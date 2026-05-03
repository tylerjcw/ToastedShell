using System.Reflection;
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
    private static ToshEngine? s_engine;
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
            // We also keep a reference so the host bridge can resolve
            // user-defined types for compiled `new`-expressions.
            s_engine = new ToshEngine(rt);
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

    /// <summary>
    /// Block-context invocation: drains the command's output silently
    /// (without printing to stdout) and returns all yielded items as an
    /// array. Used by compiled block-body methods to collect command
    /// output as pipeline values instead of writing to the console.
    /// </summary>
    public static object?[] InvokeCollect(string name, object?[] args)
    {
        var (_, all) = InvokeAndDrain(name, args, printItems: false);
        return all.ToArray();
    }

    /// <summary>
    /// Creates a <see cref="CompiledBlockCallable"/> wrapping a compiled
    /// block-body delegate and its captured variable values. Called from
    /// compiled-program Main instead of <see cref="MakeBlock"/> when the
    /// block body was successfully lowered to CLR IL.
    /// </summary>
    public static global::Tosh.Runtime.CompiledBlockCallable MakeCompiledBlock(
        Func<object?, object[], List<object?>> body,
        object[] captureValues)
        => new global::Tosh.Runtime.CompiledBlockCallable(body, captureValues);

    public static object? GetMember(object? target, string memberPath, bool nullSafe)
    {
        if (target is null) return nullSafe ? null : throw new NullReferenceException(
            $"member access '{memberPath}' on null target");
        return Runtime.ObjectAccessor.GetValue(target, memberPath);
    }

    /// <summary>
    /// Invokes <paramref name="methodName"/> on <paramref name="target"/>
    /// with <paramref name="args"/>. Routes through
    /// <see cref="IShellInvocableObject"/> for tosh-defined types and
    /// falls back to <see cref="ToshRuntime.Invoker"/> for CLR objects.
    /// </summary>
    public static object? InvokeMember(object? target, string methodName, object?[] args, bool nullSafe)
    {
        if (target is null)
        {
            if (nullSafe) return null;
            throw new NullReferenceException(
                $"method call '{methodName}' on null target");
        }
        var argList = (IReadOnlyList<object?>)args;
        if (target is IShellInvocableObject inv)
        {
            var result = inv.InvokeInstanceMethod(methodName, argList);
            return result.ReturnedVoid ? null : result.Value;
        }
        // Fallback: plain CLR reflection.
        var type = target.GetType();
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (method is null)
        {
            throw new MissingMethodException(
                $"method '{methodName}' not found on {type.Name}");
        }
        return method.Invoke(target, args);
    }

    /// <summary>
    /// Performs an index/key lookup on <paramref name="target"/>,
    /// matching the runtime evaluator's <c>$x[idx]</c> semantics.
    /// </summary>
    public static object? GetIndex(object? target, object? index)
        => ShellIndexingUtilities.GetIndexedValue(target, index);

    /// <summary>
    /// Throws a <see cref="ThrowSignalException"/> wrapping
    /// <paramref name="value"/>, matching the interpreter's
    /// <c>throw</c> semantics. The default <see cref="TextSpan"/> is
    /// fine here — compiled programs surface the IL stack trace
    /// rather than a source span.
    /// </summary>
    public static void ThrowValue(object? value)
    {
        throw new ThrowSignalException(default, value);
    }

    /// <summary>
    /// Reads <see cref="ThrowSignalException.Value"/> off a caught
    /// exception. The IL emitter stores the caught instance into a
    /// local and then calls this to bind the catch variable.
    /// </summary>
    public static object? ThrownValueOf(ThrowSignalException ex) => ex.Value;

    /// <summary>
    /// Splats <paramref name="value"/> into <paramref name="bag"/>:
    /// each element of the collection becomes its own positional
    /// argument. Mirrors the interpreter's
    /// <c>EvaluateCommandArgumentsAsync</c> splat path: nulls,
    /// strings, and record-like values reject; ranges enumerate;
    /// any other <see cref="System.Collections.IEnumerable"/> walks
    /// element-by-element.
    /// </summary>
    public static void SpreadArgs(List<object?> bag, object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                "Argument splatting requires a non-null collection value.");
        }
        if (value is string)
        {
            throw new InvalidOperationException(
                "Argument splatting requires a collection, not a string.");
        }
        if (value is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq) bag.Add(item);
            return;
        }
        throw new InvalidOperationException(
            $"Argument splatting requires a collection; got {value.GetType().Name}.");
    }

    /// <summary>
    /// Echoes <paramref name="args"/> with the same shape the
    /// inlined fast path uses for fixed-arity calls: stringifies
    /// every element and joins them with a single space, then
    /// writes the joined line to <see cref="Console.Out"/>. Used
    /// by the splat path where the argument count isn't known at
    /// compile time.
    /// </summary>
    public static void EchoArgs(object?[] args)
    {
        if (args is null || args.Length == 0)
        {
            Console.WriteLine();
            return;
        }
        var parts = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            parts[i] = args[i]?.ToString() ?? string.Empty;
        }
        Console.WriteLine(string.Join(" ", parts));
    }

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
    /// Seeds a multi-stage pipeline from an arbitrary expression
    /// value (Phase 3). Lists/arrays/IEnumerables are walked
    /// element-by-element; strings are passed through as a single
    /// value (NOT char-at-a-time, unlike <see cref="ToEnumerable"/>,
    /// because pipelines treat strings as scalar items); scalars
    /// become a single-element sequence; null becomes empty. If the
    /// value is already an <see cref="IAsyncEnumerable{T}"/> of
    /// <see cref="object"/>, it is returned as-is.
    /// </summary>
    public static IAsyncEnumerable<object?> SeedFromValue(object? value)
    {
        if (value is null) return EmptyAsync();
        if (value is IAsyncEnumerable<object?> asyncSeq) return asyncSeq;
        if (value is string) return SingletonAsync(value);
        if (value is System.Collections.IEnumerable seq) return SyncToAsync(seq);
        return SingletonAsync(value);
    }

    private static async IAsyncEnumerable<object?> SingletonAsync(object? item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<object?> SyncToAsync(
        System.Collections.IEnumerable source)
    {
        foreach (var item in source) yield return item;
        await Task.CompletedTask;
    }

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
    /// Runs an emitted user function as a pipeline stage. Dispatches
    /// by parameter count vs. callsite argument count:
    /// <list type="bullet">
    /// <item><c>paramCount == args.Length</c>: ignore input;
    /// invoke once with <paramref name="args"/>; yield the return
    /// value.</item>
    /// <item><c>paramCount == args.Length + 1</c>: walk
    /// <paramref name="input"/>; for each item invoke
    /// <c>fn(item, ...args)</c>; yield each return value.</item>
    /// <item>Anything else: throws — the emitter validates arity at
    /// compile time so this is a defensive guard.</item>
    /// </list>
    /// Reflection invocation is used here rather than a strongly
    /// typed delegate because the emitted method may not yet be a
    /// runtime-loaded method when the pipeline is constructed.
    /// </summary>
    public static async IAsyncEnumerable<object?> RunUserFuncStage(
        System.Reflection.MethodInfo fn,
        int paramCount,
        IAsyncEnumerable<object?> input,
        object?[] args)
    {
        if (paramCount == args.Length)
        {
            await foreach (var _ in input) { /* drain & discard */ }
            yield return InvokeUserFunc(fn, args);
            yield break;
        }
        if (paramCount == args.Length + 1)
        {
            var slot = new object?[paramCount];
            for (var i = 0; i < args.Length; i++) slot[i + 1] = args[i];
            await foreach (var item in input)
            {
                slot[0] = item;
                yield return InvokeUserFunc(fn, slot);
            }
            yield break;
        }
        throw new InvalidOperationException(
            $"user function '{fn.Name}' as a pipeline stage expects "
            + $"{args.Length} or {args.Length + 1} parameters, got {paramCount}.");
    }

    private static object? InvokeUserFunc(
        System.Reflection.MethodInfo fn, object?[] args)
    {
        try
        {
            // Coerce each arg to the declared parameter type. Required
            // because typed user-function primaries declare typed
            // signatures (e.g. `int add(int, int)`) but pipeline /
            // host dispatch always passes object?[] of the runtime
            // values, which may be boxed long when the param is int.
            // MethodInfo.Invoke is strict about the boxed type.
            var ps = fn.GetParameters();
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                args[i] = CoerceForParameter(args[i], ps[i].ParameterType);
            }
            return fn.Invoke(null, args);
        }
        catch (System.Reflection.TargetInvocationException tie)
            when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    private static object? CoerceForParameter(object? value, Type target)
    {
        if (target == typeof(object)) return value;
        if (value is null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
            {
                return Activator.CreateInstance(target);
            }
            return null;
        }
        var actual = value.GetType();
        if (target.IsAssignableFrom(actual)) return value;
        if (target.IsValueType || target == typeof(string))
        {
            try { return Convert.ChangeType(value, Nullable.GetUnderlyingType(target) ?? target); }
            catch { return value; }
        }
        return value;
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

    /// <summary>
    /// Compiled-IL refinement-check bridge. Converts and validates
    /// <paramref name="value"/> against the named annotated type
    /// (e.g. a refinement type alias), throwing a tosh diagnostic
    /// exception on failure. Returns the (possibly coerced) value.
    /// Argument order is value-first so the IL emitter can leave the
    /// value on the stack and push the metadata directly after.
    /// </summary>
    public static object? CheckType(
        object? value,
        string typeName,
        int spanStart,
        int spanLength,
        string owner)
    {
        if (s_engine is null) Initialize();
        return s_engine!.ConvertValueToAnnotatedType(
            typeName,
            value,
            spanStart,
            spanLength,
            s_sourceName ?? "<compiled>",
            s_sourceText ?? string.Empty,
            owner);
    }

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

    /// <summary>
    /// Registers a user-defined type (class / record / struct / enum
    /// / union / interface / trait) by replaying its source slice
    /// through the engine. Compiled tosh emits a call here in module
    /// initialization for every type-definition statement so that
    /// later <see cref="NewObject"/> calls and member accesses can
    /// resolve the type. The slice is taken from the source text
    /// previously wired via <see cref="RegisterSource"/>.
    /// </summary>
    public static void RegisterTypeFromSource(int spanStart, int spanLength)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                "ToshHost.RegisterTypeFromSource called before RegisterSource");
        }
        if (s_engine is null) Initialize();
        var slice = s_sourceText.Substring(spanStart, spanLength);
        // Drain the async sequence; type-definition statements yield
        // nothing but register the type as a side-effect.
        var task = s_engine!.ExecuteToListAsync(slice, s_sourceName ?? "<compiled>");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Registers a top-level <c>module Foo.Bar { ... }</c>
    /// declaration by replaying its source slice through the
    /// engine. Mirrors <see cref="RegisterTypeFromSource"/> but for
    /// modules: the slice executes the module body in an isolated
    /// module scope and registers the resulting
    /// <c>ToshModuleObject</c> in the engine's runtime so that later
    /// dotted-name lookups (e.g. <c>Foo.Bar.greet</c>) resolve
    /// without requiring re-parsing.
    /// </summary>
    public static void RegisterModuleFromSource(int spanStart, int spanLength)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                "ToshHost.RegisterModuleFromSource called before RegisterSource");
        }
        if (s_engine is null) Initialize();
        var slice = s_sourceText.Substring(spanStart, spanLength);
        var task = s_engine!.ExecuteToListAsync(slice, s_sourceName ?? "<compiled>");
        task.GetAwaiter().GetResult();
    }

    // ─── Compiled subcommand dispatch ─────────────────────────────

    /// <summary>
    /// Factory for <see cref="CompiledSubcommandParam"/> called from
    /// emitted IL to avoid complex object-initializer IL sequences.
    /// </summary>
    public static CompiledSubcommandParam MakeSubcommandParam(
        string name,
        string? typeName,
        bool isOptional,
        bool isRest,
        bool isBool,
        bool hasDefault,
        object? defaultValue)
        => new(name, typeName, isOptional, isRest, isBool, hasDefault, defaultValue);

    /// <summary>
    /// Factory for <see cref="CompiledSubcommandNode"/> called from
    /// emitted IL. Builds the child dictionary from parallel arrays.
    /// </summary>
    public static CompiledSubcommandNode MakeSubcommandNode(
        string? name,
        int modifiers,
        bool userDeclaredHelpFlag,
        CompiledSubcommandParam[] flags,
        CompiledSubcommandParam[] args,
        string[] childNames,
        CompiledSubcommandNode[] children,
        Action<object?[]>? body)
    {
        var childDict = new Dictionary<string, CompiledSubcommandNode>(StringComparer.Ordinal);
        for (var i = 0; i < childNames.Length; i++)
            childDict[childNames[i]] = children[i];
        return new CompiledSubcommandNode
        {
            Name = name,
            Modifiers = (SubcommandModifier)modifiers,
            UserDeclaredHelpFlag = userDeclaredHelpFlag,
            Flags = flags,
            Args = args,
            Children = childDict,
            Body = body,
        };
    }

    /// <summary>
    /// Entry-point for compiled scripts that use the compiled
    /// subcommand dispatch path (Family 4).  Parses <paramref name="argv"/>,
    /// builds a dispatch path through <paramref name="root"/>, calls
    /// each node's compiled body delegate (which binds flag/arg values
    /// into program-level static fields and runs the body statements),
    /// and writes auto-help when requested.  No source text is
    /// replayed; the dispatch algorithm is a pure C# port of
    /// <c>ToshEngine.EvaluateScriptWithSubcommandsAsync</c>.
    /// </summary>
    public static void RunCompiledSubcommandDispatch(string[] argv, CompiledSubcommandNode root)
    {
        if (s_runtime is null) Initialize();
        argv ??= Array.Empty<string>();

        var (path, helpLevel) = ResolveCompiledDispatch(root, argv);

        // Root body always runs (binds root-level flags + setup stmts).
        if (root.Body is not null)
        {
            var rootBindings = BuildCompiledBindings(path[0], root);
            root.Body(rootBindings);
        }

        if (helpLevel is not null)
        {
            WriteCompiledAutoHelp(helpLevel, path);
            return;
        }

        // Walk deeper levels in order.
        ExecuteCompiledDispatchPath(path, 1);

        // If we stopped at root and it has children with no body, auto-help / vital.
        if (path.Count == 1 && root.Children.Count > 0 && !CompiledNodeHasEffectiveBody(root))
        {
            if ((root.Modifiers & SubcommandModifier.Vital) != 0)
                throw CreateCompiledRequiredChildException(root, null);
            WriteCompiledAutoHelp(root, path);
        }
    }

    // ─── Compiled dispatch internals ──────────────────────────────

    private sealed class CompiledDispatchFrame
    {
        public required CompiledSubcommandNode Node;
        public readonly Dictionary<string, object?> FlagValues =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly List<object?> PositionalValues = new();
    }

    private static (List<CompiledDispatchFrame> Path, CompiledSubcommandNode? HelpLevel)
        ResolveCompiledDispatch(CompiledSubcommandNode root, string[] argv)
    {
        var path = new List<CompiledDispatchFrame> { new() { Node = root } };
        CompiledSubcommandNode? helpLevel = null;
        var parseOptions = true;

        for (var i = 0; i < argv.Length; i++)
        {
            var raw = argv[i];

            if (parseOptions && raw is { Length: > 0 })
            {
                if (raw == "--")
                {
                    parseOptions = false;
                    continue;
                }

                if (raw.StartsWith("--", StringComparison.Ordinal) && raw.Length > 2)
                {
                    var optionText = raw[2..];
                    string optionName;
                    string? inlineValue = null;
                    var eq = optionText.IndexOf('=');
                    if (eq >= 0)
                    {
                        optionName = optionText[..eq];
                        inlineValue = optionText[(eq + 1)..];
                    }
                    else
                    {
                        optionName = optionText;
                    }

                    // Auto --help unless user declared their own help flag.
                    if (string.Equals(optionName, "help", StringComparison.OrdinalIgnoreCase) &&
                        !CompiledPathHasUserHelpFlag(path))
                    {
                        helpLevel = path[^1].Node;
                        continue;
                    }

                    // Search leaf-to-root for a flag with this option name.
                    CompiledSubcommandParam? matched = null;
                    CompiledDispatchFrame? owningFrame = null;
                    for (var j = path.Count - 1; j >= 0; j--)
                    {
                        foreach (var flag in path[j].Node.Flags)
                        {
                            if (CompiledScriptOptionNameMatches(flag, optionName))
                            {
                                matched = flag;
                                owningFrame = path[j];
                                break;
                            }
                        }
                        if (matched is not null) break;
                    }

                    if (matched is null)
                    {
                        throw new InvalidOperationException(
                            $"Unknown script flag '--{optionName}'.");
                    }

                    object? value;
                    if (matched.IsBool)
                    {
                        value = inlineValue is null ? (object)true : inlineValue;
                    }
                    else if (inlineValue is not null)
                    {
                        value = inlineValue;
                    }
                    else
                    {
                        if (i + 1 >= argv.Length)
                            throw new InvalidOperationException(
                                $"Option '--{optionName}' requires a value.");
                        value = argv[++i];
                    }

                    owningFrame!.FlagValues[matched.Name] = value;
                    continue;
                }
            }

            // Positional: try to route to a child subcommand first.
            var leaf = path[^1];
            if (parseOptions &&
                leaf.PositionalValues.Count == 0 &&
                leaf.Node.Children.TryGetValue(raw, out var child))
            {
                path.Add(new CompiledDispatchFrame { Node = child });
                continue;
            }

            leaf.PositionalValues.Add(raw);
        }

        return (path, helpLevel);
    }

    private static object?[] BuildCompiledBindings(
        CompiledDispatchFrame frame,
        CompiledSubcommandNode node)
    {
        var total = node.Flags.Length + node.Args.Length;
        var bindings = new object?[total];

        for (var i = 0; i < node.Flags.Length; i++)
        {
            var p = node.Flags[i];
            if (frame.FlagValues.TryGetValue(p.Name, out var flagVal))
                bindings[i] = ConvertCompiledScriptArg(p.TypeName, flagVal);
            else if (p.HasDefault)
                bindings[i] = p.DefaultValue;
            else if (p.IsOptional)
                bindings[i] = null;
            else
                throw new InvalidOperationException(
                    $"Missing required script flag '--{GetCompiledPrimaryOptionName(p.Name)}'.");
        }

        var positionals = frame.PositionalValues;
        var positionalIndex = 0;
        var flagBase = node.Flags.Length;
        var hasRest = node.Args.Length > 0 && node.Args[^1].IsRest;

        for (var i = 0; i < node.Args.Length; i++)
        {
            var p = node.Args[i];
            if (p.IsRest)
            {
                var rest = new List<object?>();
                while (positionalIndex < positionals.Count)
                    rest.Add(ConvertCompiledScriptArg(p.TypeName, positionals[positionalIndex++]));
                bindings[flagBase + i] = rest;
                continue;
            }

            if (positionalIndex < positionals.Count)
            {
                bindings[flagBase + i] = ConvertCompiledScriptArg(
                    p.TypeName, positionals[positionalIndex++]);
            }
            else if (p.HasDefault)
            {
                bindings[flagBase + i] = p.DefaultValue;
            }
            else if (p.IsOptional)
            {
                bindings[flagBase + i] = null;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Missing required script argument '{p.Name}'.");
            }
        }

        if (!hasRest && positionalIndex < positionals.Count)
            throw new InvalidOperationException(
                $"Unexpected argument '{positionals[positionalIndex]}'.");

        return bindings;
    }

    private static void ExecuteCompiledDispatchPath(
        IReadOnlyList<CompiledDispatchFrame> path, int index)
    {
        if (index >= path.Count) return;

        var frame = path[index];
        var node = frame.Node;
        var isLeaf = index == path.Count - 1;

        var shouldRunBody = isLeaf || (node.Modifiers & SubcommandModifier.Eager) != 0;

        if (shouldRunBody && node.Body is not null)
        {
            var bindings = BuildCompiledBindings(frame, node);
            node.Body(bindings);
        }

        if (isLeaf)
        {
            if (node.Children.Count > 0 && !CompiledNodeHasEffectiveBody(node))
            {
                if ((node.Modifiers & SubcommandModifier.Vital) != 0)
                    throw CreateCompiledRequiredChildException(node, null);
                WriteCompiledAutoHelp(node, path);
            }
            return;
        }

        ExecuteCompiledDispatchPath(path, index + 1);
    }

    private static bool CompiledNodeHasEffectiveBody(CompiledSubcommandNode node)
        => node.Body is not null;

    private static bool CompiledPathHasUserHelpFlag(IReadOnlyList<CompiledDispatchFrame> path)
    {
        foreach (var frame in path)
            if (frame.Node.UserDeclaredHelpFlag) return true;
        return false;
    }

    private static bool CompiledScriptOptionNameMatches(CompiledSubcommandParam flag, string optionName)
    {
        if (string.Equals(flag.Name, optionName, StringComparison.OrdinalIgnoreCase)) return true;
        var primary = GetCompiledPrimaryOptionName(flag.Name);
        return string.Equals(primary, optionName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCompiledPrimaryOptionName(string paramName)
    {
        var sb = new System.Text.StringBuilder(paramName.Length + 4);
        for (var i = 0; i < paramName.Length; i++)
        {
            var ch = paramName[i];
            if (ch == '_') { sb.Append('-'); continue; }
            if (char.IsUpper(ch) && i > 0 && sb.Length > 0 && sb[^1] != '-' &&
                (char.IsLower(paramName[i - 1]) || char.IsDigit(paramName[i - 1])))
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static object? ConvertCompiledScriptArg(string? typeName, object? value)
    {
        if (typeName is null) return value;
        var t = typeName.TrimEnd('?');
        switch (t)
        {
            case "int" or "Int32" or "System.Int32":
                if (value is int) return value;
                return int.Parse(value?.ToString()
                    ?? throw new InvalidOperationException("Expected int, got null"));
            case "long" or "Int64" or "System.Int64":
                if (value is long) return value;
                return long.Parse(value?.ToString()
                    ?? throw new InvalidOperationException("Expected long, got null"));
            case "bool" or "Boolean" or "System.Boolean":
                if (value is bool) return value;
                var s = value?.ToString() ?? "";
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(s, "1", StringComparison.Ordinal);
            case "string" or "String" or "System.String":
                return value?.ToString() ?? "";
            default:
                // For other types, return value as-is and let runtime handle it.
                return value;
        }
    }

    private static Exception CreateCompiledRequiredChildException(
        CompiledSubcommandNode node, IReadOnlyList<CompiledDispatchFrame>? path)
    {
        var visible = node.Children
            .Where(kv => (kv.Value.Modifiers & SubcommandModifier.Hidden) == 0)
            .Select(kv => kv.Key);
        var label = node.Name is null
            ? $"A subcommand is required. Pick one of: {string.Join(" | ", visible)}"
            : $"Subcommand '{node.Name}' requires a child. Pick one of: {string.Join(" | ", visible)}";
        return new InvalidOperationException(label);
    }

    private static void WriteCompiledAutoHelp(
        CompiledSubcommandNode target,
        IReadOnlyList<CompiledDispatchFrame> path)
    {
        var writer = s_runtime?.Output ?? Console.Out;
        // Usage line
        var parts = new List<string> { "<script>" };
        foreach (var frame in path)
            if (frame.Node.Name is not null) parts.Add(frame.Node.Name);
        if (!ReferenceEquals(path.Count > 0 ? path[^1].Node : null, target) && target.Name is not null)
            parts.Add(target.Name);
        var subcommandPart = target.Children.Count > 0 ? " <subcommand>" : "";
        writer.WriteLine($"Usage: {string.Join(" ", parts)}{subcommandPart}");

        if (target.Children.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Subcommands:");
            foreach (var kv in target.Children)
            {
                if ((kv.Value.Modifiers & SubcommandModifier.Hidden) != 0) continue;
                writer.WriteLine($"  {kv.Key}");
            }
        }

        var flagLines = new List<string>();
        foreach (var frame in path)
        {
            foreach (var flag in frame.Node.Flags)
                flagLines.Add($"  --{GetCompiledPrimaryOptionName(flag.Name),-16} {flag.TypeName ?? "any"}");
        }
        if (!path.Any(p => ReferenceEquals(p.Node, target)))
        {
            foreach (var flag in target.Flags)
                flagLines.Add($"  --{GetCompiledPrimaryOptionName(flag.Name),-16} {flag.TypeName ?? "any"}");
        }

        if (flagLines.Count > 0 || !CompiledPathHasUserHelpFlag(path))
        {
            writer.WriteLine();
            writer.WriteLine("Options:");
            foreach (var line in flagLines) writer.WriteLine(line);
            if (!CompiledPathHasUserHelpFlag(path) && !target.UserDeclaredHelpFlag)
                writer.WriteLine("  --help             Show this help message");
        }

        writer.Flush();
    }

    // ─── Source-replay fallback (legacy) ──────────────────────────

    /// <summary>
    /// Entry-point bridge for compiled scripts that declare
    /// <c>subcommand</c> blocks or top-level <c>flag</c>/<c>arg</c>
    /// declarations. Sets <see cref="ToshRuntime.InvocationArguments"/>
    /// from the program's argv, then replays the entire registered
    /// source through <see cref="ToshEngine.ExecuteToListAsync"/> so
    /// the engine's subcommand dispatcher (argv parsing, nested
    /// flag/arg binding, eager/hollow/vital semantics, auto-help)
    /// runs against the original tosh source. Compiled output wires
    /// this in lieu of the normal statement-by-statement Main body
    /// when the unit contains any subcommand or script-input
    /// statement; the source-replay tier is required because the
    /// dispatcher's behaviour is parameterised by argv at runtime.
    /// </summary>
    public static void RunSubcommandScript(string[] argv)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                "ToshHost.RunSubcommandScript called before RegisterSource");
        }
        if (s_engine is null) Initialize();
        Runtime.InvocationArguments = (argv ?? Array.Empty<string>()).Cast<object?>().ToArray();
        var task = s_engine!.ExecuteToListAsync(s_sourceText, s_sourceName ?? "<compiled>");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resolves a dotted-path access like <c>Foo.Bar.greet</c>
    /// against the engine's module / class / type registries.
    /// Tries compiled CLR module shells first (populated by
    /// <see cref="RegisterCompiledAssembly"/>) so that pure modules
    /// that skipped source replay resolve without an engine lookup.
    /// Mirrors the interpreter's
    /// <c>StaticMemberAccessArgumentSyntax</c> evaluation path.
    /// </summary>
    public static object? ResolveQualifiedAccess(string path)
    {
        if (s_engine is null) Initialize();
        // Try compiled module shell CLR type first: avoids source
        // replay for pure-shell modules and resolves correctly even
        // when multiple test assemblies have types with the same name.
        if (TryResolveCompiledModuleAccess(path, out var compiledValue))
            return compiledValue;
        return s_engine!.ResolveQualifiedAccess(path);
    }

    /// <summary>
    /// Invokes a dotted static method path like
    /// <c>Foo.Bar.greet</c>. Tries compiled CLR module shells first
    /// before delegating to the engine. Mirrors the interpreter's
    /// <c>StaticMethodCallArgumentSyntax</c> evaluation path and is
    /// the IL bridge for <c>BoundStaticMethodCall</c>.
    /// </summary>
    public static object? InvokeQualifiedMethod(string path, object?[] args)
    {
        if (s_engine is null) Initialize();
        // Try compiled module shell CLR type first.
        if (TryInvokeCompiledModuleMethod(path, args, out var compiledResult))
            return compiledResult;
        return s_engine!.InvokeQualifiedMethodPublic(path, args);
    }

    // ─── Compiled module CLR-shell registry ───────────────────────

    /// <summary>
    /// Per-compiled-assembly map from tosh qualified module name
    /// (e.g. "Foo.Bar") to the CLR static-class shell type that
    /// backs it. Populated by <see cref="RegisterCompiledAssembly"/>.
    /// Keyed by qualified name so dotted-path lookups are O(1).
    /// </summary>
    private static readonly Dictionary<string, Type> s_compiledModuleTypes =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Scans <paramref name="asm"/> for all types carrying
    /// <see cref="ToshModuleShellAttribute"/> and caches the
    /// <c>qualifiedName → Type</c> mapping. Called from the
    /// compiled program's Main prologue whenever the program declares
    /// any module, regardless of whether source replay is needed for
    /// that module.
    /// </summary>
    public static void RegisterCompiledAssembly(Assembly asm)
    {
        if (asm is null) return;
        IEnumerable<Type?> types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        catch { return; }

        lock (s_lock)
        {
            foreach (var type in types)
            {
                if (type is null) continue;
                var attr = type.GetCustomAttribute<ToshModuleShellAttribute>();
                if (attr is not null)
                    s_compiledModuleTypes[attr.QualifiedName] = type;
            }
        }
    }

    private static bool TryResolveCompiledModuleAccess(string path, out object? value)
    {
        // Split into module-qualified prefix and member name.
        // Try the longest prefix first (most-specific module).
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= 0) { value = null; return false; }

        for (var prefixLen = lastDot; prefixLen > 0; prefixLen = path.LastIndexOf('.', prefixLen - 1))
        {
            var modulePath = path[..prefixLen];
            var memberPath = path[(prefixLen + 1)..];

            if (!s_compiledModuleTypes.TryGetValue(modulePath, out var moduleType))
            {
                if (prefixLen == 0) break;
                continue;
            }

            // Found the module type; get the static member.
            try
            {
                value = Runtime.Invoker.GetStaticMember(moduleType, memberPath);
                return true;
            }
            catch { }
        }

        value = null;
        return false;
    }

    private static bool TryInvokeCompiledModuleMethod(string path, object?[] args, out object? value)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= 0) { value = null; return false; }

        for (var prefixLen = lastDot; prefixLen > 0; prefixLen = path.LastIndexOf('.', prefixLen - 1))
        {
            var modulePath = path[..prefixLen];
            var methodName = path[(prefixLen + 1)..];

            if (!s_compiledModuleTypes.TryGetValue(modulePath, out var moduleType))
            {
                if (prefixLen == 0) break;
                continue;
            }

            try
            {
                var invocation = Runtime.Invoker.InvokeStatic(moduleType, methodName, args);
                value = invocation.ReturnedVoid ? null : invocation.Value;
                return true;
            }
            catch { }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Constructs a new instance of a user-defined or CLR type by
    /// name. Mirrors what the interpreter does for a
    /// <c>new TypeName(args…)</c> expression: resolves the type
    /// against the engine's named-type registry first, falling back
    /// to a CLR type via the runtime invoker.
    /// </summary>
    public static object? NewObject(string typeName, object?[] args)
    {
        if (s_engine is null) Initialize();
        var argList = (IReadOnlyList<object?>)args;
        if (s_engine!.TryGetNamedType(typeName, out var named))
        {
            return named switch
            {
                ToshClassDefinition cls => cls.CreateInstance(argList),
                ToshRecordDefinition rec => rec.CreateInstance(argList),
                ToshStructDefinition st => st.CreateInstance(argList),
                ToshEnumDefinition en => en.CreateInstance(argList),
                ToshUnionDefinition un => un.CreateInstance(argList),
                _ => throw new InvalidOperationException(
                    $"type '{typeName}' is not constructable"),
            };
        }
        var compiledClr = ResolveCompiledToshClrType(typeName);
        if (compiledClr is not null)
        {
            return Runtime.Invoker.CreateInstance(compiledClr, argList);
        }
        var clrType = Type.GetType(typeName, throwOnError: false);
        if (clrType is not null)
        {
            return Runtime.Invoker.CreateInstance(clrType, argList);
        }
        throw new InvalidOperationException(
            $"unknown type '{typeName}' in `new` expression");
    }

    private static Type? ResolveCompiledToshClrType(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            IEnumerable<Type?> types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null) continue;
                if (type.GetCustomAttribute<ToshTypeAttribute>() is null) continue;
                if (IsToshTypeNameMatch(type, typeName)) return type;
            }
        }

        return null;
    }

    private static bool IsToshTypeNameMatch(Type type, string requestedName)
    {
        if (string.Equals(type.Name, requestedName, StringComparison.Ordinal)) return true;
        if (string.Equals(type.FullName, requestedName, StringComparison.Ordinal)) return true;

        var original = type.GetCustomAttribute<ToshOriginalNameAttribute>()?.OriginalName;
        if (!string.IsNullOrWhiteSpace(original)
            && string.Equals(original, requestedName, StringComparison.Ordinal))
        {
            return true;
        }

        var mangled = MangleClrIdentifierForLookup(requestedName);
        if (string.Equals(type.Name, mangled, StringComparison.Ordinal)) return true;
        if (type.FullName is { Length: > 0 }
            && type.FullName.EndsWith($".{mangled}", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string MangleClrIdentifierForLookup(string toshName)
    {
        if (string.IsNullOrEmpty(toshName)) return "_";
        var needsMangling = false;
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            if (i == 0 && char.IsDigit(c)) { needsMangling = true; break; }
            if (!(char.IsLetterOrDigit(c) || c == '_')) { needsMangling = true; break; }
        }
        if (!needsMangling) return toshName;

        var sb = new System.Text.StringBuilder(toshName.Length + 1);
        if (char.IsDigit(toshName[0])) sb.Append('_');
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.ToString();
    }
}
