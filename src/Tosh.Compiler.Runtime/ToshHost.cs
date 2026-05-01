using Tosh.Runtime;
using Tosh.Language;
using Tosh.Language.Parsing;

namespace Tosh.Compiler.Runtime;

/// <summary>
/// Host shim that toshc-emitted assemblies call into for any builtin
/// command other than the inlined fast paths (echo etc.).
///
/// The shim deliberately exposes a tiny, named, synchronous surface:
/// <see cref="Initialize"/> wires up a default <see cref="ToshRuntime"/>
/// once, and <see cref="InvokeStatement"/> / <see cref="InvokeValue"/>
/// dispatch a single command call by name. The async-enumerable
/// pipeline machinery is hidden — emitted IL never sees an
/// <see cref="IAsyncEnumerable{T}"/>.
///
/// An emitted Main prologue calls <see cref="Initialize"/> exactly
/// once before any builtin dispatch, so callers running a
/// <c>dotnet emitted.dll</c> get a working stdlib-backed shell with
/// no extra setup. Embedders may pre-populate the runtime by
/// supplying their own <see cref="ToshRuntime"/> before the emitted
/// program starts.
/// </summary>
public static class ToshHost
{
    private static ToshRuntime? s_runtime;
    private static readonly object s_lock = new();

    /// <summary>
    /// The ambient runtime backing every <see cref="InvokeStatement"/>
    /// / <see cref="InvokeValue"/> call. Lazily wired the first time
    /// it's accessed if <see cref="Initialize"/> hasn't been called.
    /// </summary>
    public static ToshRuntime Runtime
    {
        get
        {
            if (s_runtime is null) Initialize();
            return s_runtime!;
        }
    }

    /// <summary>
    /// Wires the ambient runtime. Idempotent — only the first call
    /// takes effect. If <paramref name="runtime"/> is null, builds a
    /// default runtime via <see cref="ToshRuntime.CreateDefault"/>
    /// with stdout/stderr pointed at <see cref="Console.Out"/> /
    /// <see cref="Console.Error"/>.
    /// </summary>
    public static void Initialize(ToshRuntime? runtime = null)
    {
        if (s_runtime is not null) return;
        lock (s_lock)
        {
            if (s_runtime is not null) return;
            var rt = runtime ?? ToshRuntime.CreateDefault(Console.Out, Console.Error);
            // Eagerly build a ToshEngine so rt.BlockExecutor is wired.
            // Discarding the engine reference is fine — the executor
            // captured by the runtime keeps it alive.
            _ = new ToshEngine(rt);
            s_runtime = rt;
        }
    }

    /// <summary>
    /// Statement-context invocation: drains the command's
    /// async-enumerable output, formats every yielded item with the
    /// runtime's formatter, and writes each one as a line to
    /// <see cref="Console.Out"/>. Returns the last yielded item (or
    /// null) so the emitter can choose to surface it; the IL emitter
    /// pops the return value in statement context.
    /// </summary>
    public static object? InvokeStatement(string name, object?[] args)
    {
        var (last, _) = InvokeAndDrain(name, args, printItems: true);
        return last;
    }

    /// <summary>
    /// Value-context invocation: drains the command's
    /// async-enumerable output without printing. Returns:
    /// <list type="bullet">
    /// <item><c>null</c> when nothing was yielded.</item>
    /// <item>the single item when exactly one was yielded.</item>
    /// <item>the full <see cref="List{Object}"/> otherwise.</item>
    /// </list>
    /// Callers that need uniform list semantics can wrap the
    /// returned value themselves.
    /// </summary>
    public static object? InvokeValue(string name, object?[] args)
    {
        var (_, all) = InvokeAndDrain(name, args, printItems: false);
        if (all.Count == 0) return null;
        if (all.Count == 1) return all[0];
        return all;
    }

    /// <summary>
    /// Reads a (possibly dotted) member path from <paramref name="target"/>.
    /// Mirrors the runtime evaluator's <c>$x.member</c> semantics by
    /// delegating to <see cref="ToshRuntime.ObjectAccessor"/>.
    /// </summary>
    public static object? GetMember(object? target, string memberPath, bool nullSafe)
    {
        if (target is null) return nullSafe ? null : throw new NullReferenceException(
            $"member access '{memberPath}' on null target");
        return Runtime.ObjectAccessor.GetValue(target, memberPath);
    }

    /// <summary>
    /// Performs an index/key lookup on <paramref name="target"/>,
    /// matching the runtime evaluator's <c>$x[idx]</c> semantics.
    /// </summary>
    public static object? GetIndex(object? target, object? index)
        => ShellIndexingUtilities.GetIndexedValue(target, index);

    /// <summary>
    /// Coerces an arbitrary value into an <see cref="IEnumerable{T}"/>
    /// of <see cref="object"/> for <c>for x in expr</c> loops. Lists,
    /// arrays, and any <see cref="System.Collections.IEnumerable"/>
    /// (including strings — character at a time) are walked as-is;
    /// scalars are wrapped as a single-element sequence; null
    /// becomes an empty sequence.
    /// </summary>
    public static IEnumerable<object?> ToEnumerable(object? source)
    {
        if (source is null) yield break;
        if (source is string s)
        {
            foreach (var ch in s) yield return ch;
            yield break;
        }
        if (source is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq) yield return item;
            yield break;
        }
        yield return source;
    }

    private static (object? Last, List<object?> All) InvokeAndDrain(
        string name, object?[] args, bool printItems)
    {
        var command = ResolveCommand(name);
        var ctx = new CommandContext(Runtime, EmptyAsync(), args, default);
        return DrainEnumerator(command.ExecuteAsync(ctx), printItems);
    }

    // ─── Multi-stage pipeline plumbing (Phase 1) ──────────────────

    /// <summary>
    /// Resolves a builtin command by name, throwing a clear error
    /// when it isn't registered. Used by both the single-command
    /// dispatch path and the per-stage pipeline path.
    /// </summary>
    public static IShellCommand ResolveCommand(string name)
    {
        if (!Runtime.Commands.TryGet(name, out var command))
        {
            throw new InvalidOperationException($"unknown command: '{name}'");
        }
        return command;
    }

    /// <summary>
    /// Returns an empty <see cref="IAsyncEnumerable{T}"/> suitable
    /// for seeding the first stage of a pipeline.
    /// </summary>
    public static IAsyncEnumerable<object?> EmptyInput() => EmptyAsync();

    /// <summary>
    /// Invokes <paramref name="command"/> as a pipeline stage with
    /// <paramref name="input"/> piped in. Returns the lazy
    /// <see cref="IAsyncEnumerable{T}"/> the command produces — the
    /// emitter chains these stage by stage and only drains at the
    /// pipeline's end.
    /// </summary>
    public static IAsyncEnumerable<object?> RunStage(
        IShellCommand command,
        IAsyncEnumerable<object?> input,
        object?[] args)
    {
        var ctx = new CommandContext(
            Runtime,
            input,
            args,
            default,
            Invocation: null,
            IsPipelined: true,
            ScopedTypeResolver: null,
            PipelineExitStatusTracker: null,
            BlockExecutor: Runtime.BlockExecutor);
        return command.ExecuteAsync(ctx);
    }

    /// <summary>
    /// Statement-context drain for a (potentially multi-stage)
    /// pipeline. Walks <paramref name="input"/> synchronously and
    /// formats each yielded item via the runtime's formatter to
    /// <see cref="Console.Out"/>.
    /// </summary>
    public static void DrainStatement(IAsyncEnumerable<object?> input)
    {
        DrainEnumerator(input, printItems: true);
    }

    /// <summary>
    /// Value-context drain. Always materializes to a
    /// <see cref="List{T}"/>, even when nothing was yielded or when
    /// only one item was produced — the emitter relies on the list
    /// shape so call sites can treat <c>(cmd | first 3)</c> uniformly.
    /// </summary>
    public static List<object?> DrainValue(IAsyncEnumerable<object?> input)
    {
        var (_, all) = DrainEnumerator(input, printItems: false);
        return all;
    }

    private static (object? Last, List<object?> All) DrainEnumerator(
        IAsyncEnumerable<object?> source, bool printItems)
    {
        var collected = new List<object?>();
        object? last = null;
        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var current = enumerator.Current;
                last = current;
                collected.Add(current);
                if (printItems)
                {
                    Console.WriteLine(Runtime.Formatter.Format(current));
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return (last, collected);
    }

#pragma warning disable CS1998 // async without await: empty async-enumerable
    private static async IAsyncEnumerable<object?> EmptyAsync()
    {
        yield break;
    }
#pragma warning restore CS1998

    // ─── Block source registration & materialization (Phase 2) ────

    private static string? s_sourceText;
    private static string? s_sourceName;
    private static readonly Dictionary<long, BlockSyntax> s_blocksBySpan = new();

    /// <summary>
    /// Wires the source text the emitted assembly was compiled from
    /// so that <see cref="MakeBlock"/> can materialize a
    /// <see cref="ShellBlock"/> referring to the original
    /// <see cref="BlockSyntax"/> by span. Idempotent on identical
    /// source text — re-registers (replacing the span index) when
    /// the source differs.
    /// </summary>
    public static void RegisterSource(string sourceText, string sourceName)
    {
        lock (s_lock)
        {
            if (string.Equals(s_sourceText, sourceText, StringComparison.Ordinal)) return;
            var parsed = ToshParser.Parse(sourceText, sourceName);
            s_blocksBySpan.Clear();
            CollectBlocks(parsed.Statement, s_blocksBySpan);
            s_sourceText = sourceText;
            s_sourceName = sourceName;
        }
    }

    /// <summary>
    /// Builds a <see cref="ShellBlock"/> bound to the
    /// previously-registered source. <paramref name="captures"/>
    /// records local-variable bindings the IL emitter snapshotted at
    /// the point the block was constructed.
    /// </summary>
    public static ShellBlock MakeBlock(int start, int length, Dictionary<string, object?>? captures)
    {
        if (s_sourceText is null || s_sourceName is null)
        {
            throw new InvalidOperationException(
                "ToshHost.MakeBlock called before RegisterSource");
        }
        var key = ((long)start << 32) | (uint)length;
        if (!s_blocksBySpan.TryGetValue(key, out var syntax))
        {
            throw new InvalidOperationException(
                $"no block syntax found at span ({start},{length})");
        }
        return new ShellBlock(syntax, s_sourceName, s_sourceText, new TextSpan(start, length))
        {
            Captures = captures,
        };
    }

    private static void CollectBlocks(object? node, Dictionary<long, BlockSyntax> map)
    {
        if (node is null) return;
        if (node is BlockSyntax block)
        {
            var key = ((long)block.Span.Start << 32) | (uint)block.Span.Length;
            map[key] = block;
        }
        var type = node.GetType();
        if (type.Namespace is null || !type.Namespace.StartsWith("Tosh.Language", StringComparison.Ordinal)) return;

        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            object? value;
            try { value = prop.GetValue(node); }
            catch { continue; }
            if (value is null) continue;
            if (value is string) continue;
            if (value is System.Collections.IEnumerable seq && value is not BlockSyntax)
            {
                foreach (var item in seq) CollectBlocks(item, map);
                continue;
            }
            var ns = value.GetType().Namespace;
            if (ns is not null && ns.StartsWith("Tosh.Language", StringComparison.Ordinal))
            {
                CollectBlocks(value, map);
            }
        }
    }
}
