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
    /// Mirrors the interpreter's
    /// <c>StaticMemberAccessArgumentSyntax</c> evaluation path.
    /// </summary>
    public static object? ResolveQualifiedAccess(string path)
    {
        if (s_engine is null) Initialize();
        return s_engine!.ResolveQualifiedAccess(path);
    }

    /// <summary>
    /// Invokes a dotted static method path like
    /// <c>Foo.Bar.greet</c>. Mirrors the interpreter's
    /// <c>StaticMethodCallArgumentSyntax</c> evaluation path and is
    /// the IL bridge for <c>BoundStaticMethodCall</c>.
    /// </summary>
    public static object? InvokeQualifiedMethod(string path, object?[] args)
    {
        if (s_engine is null) Initialize();
        return s_engine!.InvokeQualifiedMethodPublic(path, args);
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
        var clrType = Type.GetType(typeName, throwOnError: false);
        if (clrType is not null)
        {
            return Runtime.Invoker.CreateInstance(clrType, argList);
        }
        throw new InvalidOperationException(
            $"unknown type '{typeName}' in `new` expression");
    }
}
