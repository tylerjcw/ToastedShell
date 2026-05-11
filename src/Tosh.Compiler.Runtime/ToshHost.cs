using System.Reflection;
using System.Collections;
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
    private static bool s_runtimeOwnedByHost;
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
            s_runtimeOwnedByHost = runtime is null;
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

    /// <summary>
    /// Creates a <see cref="CompiledLambdaCallable"/> wrapping a compiled
    /// lambda-body delegate and its captured variable values. Called from
    /// compiled-program IL when a lambda expression is evaluated.
    /// <para>
    /// <paramref name="maxParamCount"/> is negative when the lambda has a
    /// rest/variadic parameter (no upper bound on argument count).
    /// </para>
    /// </summary>
    public static global::Tosh.Runtime.CompiledLambdaCallable MakeCompiledLambda(
        Func<object?[], object?[], List<object?>> body,
        object?[] captureValues,
        string[] parameterNames,
        bool[] optionalParameters,
        int requiredParamCount,
        int maxParamCount,
        int restParameterIndex)
        => new global::Tosh.Runtime.CompiledLambdaCallable(
            body,
            captureValues,
            parameterNames,
            optionalParameters,
            requiredParamCount,
            maxParamCount,
            restParameterIndex);

    public static object? GetMember(object? target, string memberPath, bool nullSafe)
    {
        if (target is null) return nullSafe ? null : throw new NullReferenceException(
            $"member access '{memberPath}' on null target");
        return Runtime.ObjectAccessor.GetValue(target, memberPath);
    }

    /// <summary>
    /// Writes a (possibly dotted) member path on <paramref name="target"/>,
    /// mirroring interpreter assignment semantics.
    /// </summary>
    public static object? SetMember(object? target, string memberPath, object? value, bool nullSafe)
    {
        if (target is null)
        {
            if (nullSafe) return null;
            throw new NullReferenceException($"member assignment '{memberPath}' on null target");
        }

        Runtime.ObjectAccessor.SetValue(target, memberPath, value);
        return value;
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
        // Fallback: route through ReflectionInvoker so named-argument
        // wrappers (`name = value`) are unwrapped and reordered against
        // the parameter list. This matches the engine path for plain CLR
        // and compiled-tosh-CLR-shell instances.
        var ir = Runtime.Invoker.InvokeInstance(target, methodName, argList);
        return ir.ReturnedVoid ? null : ir.Value;
    }

    /// <summary>
    /// Invokes an <see cref="IShellCallable"/> value (lambda, function reference,
    /// compiled block, etc.) with the supplied positional arguments and returns
    /// the single output value (or <c>null</c> if nothing was produced).
    /// Mirrors the interpreter's <c>CallableInvocationArgumentSyntax</c> path.
    /// </summary>
    public static object? InvokeCallable(object? target, object?[] args)
    {
        var ctx = new CommandContext(
            Runtime,
            EmptyAsync(),
            args,
            default,
            Invocation: null,
            IsPipelined: false,
            ScopedTypeResolver: null,
            PipelineExitStatusTracker: null,
            BlockExecutor: Runtime.BlockExecutor);

        if (target is not IShellCallable callable)
        {
            throw ctx.CreateDiagnostic(
                code: "tosh.runtime.not_callable",
                title: $"Value of type '{(target?.GetType().Name ?? "null")}' is not callable.",
                label: "expected a callable value such as a lambda or function reference");
        }

        IAsyncEnumerable<object?> results;
        if (callable is CompiledLambdaCallable compiledLambda)
        {
            SplitCallableArguments(args, out var positionals, out var namedArguments);
            results = compiledLambda.InvokeBoundAsync(ctx, positionals, namedArguments);
        }
        else
        {
            if (!args.Any(static arg => arg is NamedArgument))
            {
                ValidateCallableArity(ctx, callable, args.Length);
            }

            results = callable.InvokeAsync(ctx);
        }

        var (last, all) = DrainEnumerator(results, printItems: false);
        return all.Count <= 1 ? last : (object?)all;
    }

    private static void ValidateCallableArity(CommandContext context, IShellCallable callable, int argumentCount)
    {
        if (argumentCount < callable.RequiredParameterCount)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.callable_argument_count_mismatch",
                title: $"Callable '{callable.CallableName}' expects at least {callable.RequiredParameterCount} argument(s) but received {argumentCount}.",
                label: $"'{callable.CallableName}' requires at least {callable.RequiredParameterCount} argument(s)");
        }

        if (callable.MaximumParameterCount is int maximum && argumentCount > maximum)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.callable_argument_count_mismatch",
                title: $"Callable '{callable.CallableName}' accepts at most {maximum} argument(s) but received {argumentCount}.",
                label: $"'{callable.CallableName}' accepts at most {maximum} argument(s)");
        }
    }

    private static void SplitCallableArguments(
        IReadOnlyList<object?> arguments,
        out IReadOnlyList<object?> positionals,
        out IReadOnlyDictionary<string, object?> namedArguments)
    {
        var positionalList = new List<object?>(arguments.Count);
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in arguments)
        {
            if (argument is NamedArgument namedArgument)
            {
                named[namedArgument.Name] = namedArgument.Value;
            }
            else
            {
                positionalList.Add(argument);
            }
        }

        positionals = positionalList;
        namedArguments = named;
    }

    /// <summary>
    /// Performs an index/key lookup on <paramref name="target"/>,
    /// matching the runtime evaluator's <c>$x[idx]</c> semantics.
    /// </summary>
    public static object? GetIndex(object? target, object? index)
        => ShellIndexingUtilities.GetIndexedValue(target, index);

    /// <summary>
    /// Splits an array/list/enumerable into exactly <paramref name="count"/>
    /// slots for destructuring; missing elements become null.
    /// </summary>
    public static object?[] DestructureArray(object? value, int count)
    {
        object?[]? array = value switch
        {
            object?[] a => a,
            Array typedArray => Enumerable.Range(0, typedArray.Length).Select(i => typedArray.GetValue(i)).ToArray(),
            IReadOnlyList<object?> list => list.ToArray(),
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
            _ => null,
        };

        if (array is null)
        {
            throw new InvalidOperationException(
                $"Array destructuring requires an array or list value; got {(value?.GetType().Name ?? "null")}.");
        }

        var result = new object?[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i < array.Length ? array[i] : null;
        }

        return result;
    }

    /// <summary>
    /// Splits a record/dictionary by <paramref name="names"/> for
    /// destructuring; missing fields become null.
    /// </summary>
    public static object?[] DestructureRecord(object? value, string[] names)
    {
        IDictionary<string, object?>? dict = value switch
        {
            IDictionary<string, object?> d => d,
            IDictionary raw => CoerceRecordDictionary(raw),
            IShellRecordObject record => record.GetMembers().ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase),
            _ => null,
        };

        if (dict is null)
        {
            throw new InvalidOperationException(
                $"Record destructuring requires a record or dictionary value; got {(value?.GetType().Name ?? "null")}.");
        }

        var result = new object?[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            dict.TryGetValue(names[i], out var memberValue);
            result[i] = memberValue;
        }

        return result;
    }

    private static IDictionary<string, object?> CoerceRecordDictionary(IDictionary raw)
    {
        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw)
        {
            switch (entry)
            {
                case DictionaryEntry de:
                    converted[de.Key?.ToString() ?? string.Empty] = de.Value;
                    break;

                default:
                    var type = entry?.GetType();
                    var keyProp = type?.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                    var valueProp = type?.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (keyProp is not null && valueProp is not null)
                    {
                        var key = keyProp.GetValue(entry)?.ToString() ?? string.Empty;
                        converted[key] = valueProp.GetValue(entry);
                    }
                    break;
            }
        }

        return converted;
    }

    /// <summary>
    /// Performs an index/key write on <paramref name="target"/>, matching
    /// interpreter assignment behavior for arrays/lists/dictionaries/records.
    /// </summary>
    public static object? SetIndex(object? target, object? index, object? value)
    {
        if (target is null)
        {
            throw new InvalidOperationException("Cannot index-assign into null.");
        }

        if (TryGetIntegerIndex(index, out var numericIndex))
        {
            if (numericIndex < 0)
            {
                throw new InvalidOperationException("Indexes must be zero or greater.");
            }

            if (target is Array array)
            {
                if (numericIndex >= array.Length)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for array length {array.Length}.");
                }

                var elementType = array.GetType().GetElementType() ?? typeof(object);
                if (!TryConvertForAssignment(value, elementType, out var converted))
                {
                    throw new InvalidOperationException($"Cannot assign value of type '{value?.GetType().FullName ?? "null"}' to array element type '{elementType.FullName}'.");
                }

                array.SetValue(converted, numericIndex);
                return value;
            }

            if (target is IList list)
            {
                if (numericIndex >= list.Count)
                {
                    throw new InvalidOperationException($"Index {numericIndex} is out of range for list length {list.Count}.");
                }

                var elementType = ResolveListElementType(target.GetType());
                if (!TryConvertForAssignment(value, elementType, out var converted))
                {
                    throw new InvalidOperationException($"Cannot assign value of type '{value?.GetType().FullName ?? "null"}' to list element type '{elementType.FullName}'.");
                }

                list[numericIndex] = converted;
                return value;
            }
        }

        if (index is string key && ShellRecordUtilities.TrySetValue(target, key, value))
        {
            return value;
        }

        if (target is IDictionary dictionary)
        {
            var keyType = typeof(object);
            var valueType = typeof(object);
            ResolveDictionaryTypes(target.GetType(), ref keyType, ref valueType);

            if (!TryConvertForAssignment(index, keyType, out var convertedKey))
            {
                throw new InvalidOperationException($"Cannot use key of type '{index?.GetType().FullName ?? "null"}' for dictionary key type '{keyType.FullName}'.");
            }

            if (!TryConvertForAssignment(value, valueType, out var convertedValue))
            {
                throw new InvalidOperationException($"Cannot assign value of type '{value?.GetType().FullName ?? "null"}' to dictionary value type '{valueType.FullName}'.");
            }

            dictionary[convertedKey!] = convertedValue;
            return value;
        }

        if (TrySetIndexerProperty(target, index, value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Type '{target.GetType().FullName}' does not support index assignment with '{index?.GetType().FullName ?? "null"}'.");
    }

    private static bool TryGetIntegerIndex(object? index, out int numericIndex)
    {
        numericIndex = 0;

        if (index is int i)
        {
            numericIndex = i;
            return true;
        }

        if (index is long l && l is >= int.MinValue and <= int.MaxValue)
        {
            numericIndex = (int)l;
            return true;
        }

        if (index is double d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            numericIndex = (int)d;
            return true;
        }

        if (TypeConversion.TryConvert(index, typeof(int), out var converted) && converted is int convertedIndex)
        {
            numericIndex = convertedIndex;
            return true;
        }

        return false;
    }

    private static bool TryConvertForAssignment(object? value, Type targetType, out object? converted)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (value is null)
        {
            if (!targetType.IsValueType || nullableUnderlying is not null)
            {
                converted = null;
                return true;
            }

            converted = null;
            return false;
        }

        var effectiveTarget = nullableUnderlying ?? targetType;
        if (effectiveTarget.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (TypeConversion.TryConvert(value, effectiveTarget, out converted))
        {
            return true;
        }

        converted = null;
        return false;
    }

    private static Type ResolveListElementType(Type targetType)
    {
        if (targetType.IsArray)
        {
            return targetType.GetElementType() ?? typeof(object);
        }

        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return targetType.GetGenericArguments()[0];
        }

        var ilistInterface = targetType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));
        return ilistInterface?.GetGenericArguments()[0] ?? typeof(object);
    }

    private static void ResolveDictionaryTypes(Type targetType, ref Type keyType, ref Type valueType)
    {
        var dictInterface = targetType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictInterface is null)
        {
            return;
        }

        var args = dictInterface.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
    }

    private static bool TrySetIndexerProperty(object target, object? index, object? value)
    {
        var type = target.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            var parameters = property.GetIndexParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            if (!TryConvertForAssignment(index, parameters[0].ParameterType, out var convertedIndex))
            {
                continue;
            }

            if (!TryConvertForAssignment(value, property.PropertyType, out var convertedValue))
            {
                continue;
            }

            property.SetValue(target, convertedValue, new[] { convertedIndex });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Throws a tosh user error. When <paramref name="value"/> is itself
    /// an <see cref="Exception"/> (e.g. a user-defined
    /// <c>class MyError extends Error</c>), the exception is raised
    /// verbatim so cross-language consumers can <c>catch</c> it by its
    /// concrete type. Non-exception values are wrapped in a
    /// <see cref="ThrowSignalException"/> so the original payload
    /// survives unmodified through tosh's <c>catch (err)</c> binding.
    /// The exception's <c>Data["tosh.thrown"]</c> entry is set so
    /// callers (and downstream catches) can identify the throw as
    /// originating from user tosh code rather than the runtime.
    /// </summary>
    public static void ThrowValue(object? value)
    {
        if (value is ToshError tosh)
        {
            tosh.Data["tosh.thrown"] = true;
            throw tosh;
        }
        if (value is Exception ex && value is not ShellControlFlowException)
        {
            ex.Data["tosh.thrown"] = true;
            throw ex;
        }
        var signal = new ThrowSignalException(default, value);
        signal.Data["tosh.thrown"] = true;
        throw signal;
    }

    /// <summary>
    /// Reads the catch-bound value out of a tosh-thrown exception.
    /// For wrapper <see cref="ThrowSignalException"/> instances this
    /// is the original payload (string / record / number / …); for
    /// directly raised <see cref="Exception"/> subclasses (e.g.
    /// <see cref="ToshError"/>-derived user types) this is the
    /// exception itself, so user code can read its properties and
    /// call its methods inside the <c>catch</c> body.
    /// <see cref="ShellControlFlowException"/> instances are
    /// rethrown — they're internal control-flow signals, not user
    /// exceptions, and must not be observable from a tosh
    /// <c>catch</c>.
    /// </summary>
    public static object? CaughtValueOf(Exception ex)
    {
        if (ex is ShellControlFlowException) throw ex;
        return ex switch
        {
            ThrowSignalException signal => signal.Value,
            // ToshError wrapping a tosh class instance: restore the
            // original instance for `_ is FooError` pattern checks.
            // `Tosh.Language.ToshClassInstance` lives in the language
            // assembly; we test by type name to avoid an assembly
            // reference from the compiler runtime.
            ToshError tosh when tosh.Cause is { } cause
                && cause.GetType().FullName == "Tosh.Language.ToshClassInstance"
                => cause,
            _ => ex,
        };
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
    /// Spreads <paramref name="value"/> into the record literal
    /// <paramref name="bag"/>. Accepts:
    ///   * <see cref="IDictionary{TKey,TValue}"/> with string keys
    ///     (tosh records — copied directly), and
    ///   * any non-generic <see cref="System.Collections.IDictionary"/>
    ///     (object→object — keys stringified).
    /// Later spread/field entries overwrite earlier ones, mirroring
    /// the interpreter's left-to-right merge order.
    /// </summary>
    public static void SpreadRecord(Dictionary<string, object?> bag, object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                "Record spread requires a non-null record value.");
        }
        if (value is IDictionary<string, object?> typed)
        {
            foreach (var kv in typed) bag[kv.Key] = kv.Value;
            return;
        }
        if (value is System.Collections.IDictionary loose)
        {
            foreach (System.Collections.DictionaryEntry kv in loose)
            {
                var key = kv.Key?.ToString()
                    ?? throw new InvalidOperationException(
                        "Record spread: dictionary key was null.");
                bag[key] = kv.Value;
            }
            return;
        }
        throw new InvalidOperationException(
            $"Record spread requires a record/dictionary; got {value.GetType().Name}.");
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

    // ── Redirection ────────────────────────────────────────────

    /// <summary>
    /// Stream selector for <see cref="BeginRedirection"/>: matches
    /// <c>Tosh.Language.Parsing.RedirectionStream</c> integral
    /// values so the IL emitter can <c>ldc.i4</c> the constant.
    /// 0=Output, 1=Error, 2=OutputThenError, 3=ErrorThenOutput.
    /// </summary>
    /// <summary>
    /// Mode selector for <see cref="BeginRedirection"/>: 0=Truncate,
    /// 1=Append. Mirrors <c>Tosh.Language.Parsing.RedirectionMode</c>.
    /// </summary>
    public sealed class RedirectionScope : IDisposable
    {
        private readonly List<IDisposable> _restorers = new();
        private readonly List<Stream> _streams = new();
        private readonly List<TextWriter> _writers = new();
        private TextReader? _origIn;
        private Stream? _origInStream;
        private string? _activeInputPath;
        private bool _trackedInputPath;
        private bool _disposed;

        internal void TrackOriginalOut(TextWriter w) => _restorers.Add(new RestoreOut(w));
        internal void TrackOriginalError(TextWriter w) => _restorers.Add(new RestoreError(w));
        internal void TrackOriginalIn(TextReader r) => _origIn = r;
        internal void TrackInStream(Stream s) => _origInStream = s;
        internal void TrackStream(Stream s) => _streams.Add(s);
        internal void TrackWriter(TextWriter w) => _writers.Add(w);

        /// <summary>
        /// Records the input redirection path on the scope and
        /// publishes it via the thread-static
        /// <see cref="ToshHost._activeInputPath"/> so pipeline
        /// helpers (e.g. <see cref="EmptyInput"/>) can seed the
        /// first stage of a compiled pipeline from the file. The
        /// previous thread-static value is captured so nested
        /// redirections restore on dispose.
        /// </summary>
        internal void TrackInputPath(string path)
        {
            _activeInputPath = ToshHost._activeInputPath;
            _trackedInputPath = true;
            ToshHost._activeInputPath = path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Restore Console.Out/Error first.
            for (var i = _restorers.Count - 1; i >= 0; i--)
            {
                try { _restorers[i].Dispose(); } catch { }
            }
            // Restore Console.In if we changed it.
            if (_origIn is not null)
            {
                try { Console.SetIn(_origIn); } catch { }
            }
            // Flush + close writers we created.
            foreach (var w in _writers)
            {
                try { w.Flush(); } catch { }
                try { w.Dispose(); } catch { }
            }
            foreach (var s in _streams)
            {
                try { s.Dispose(); } catch { }
            }
            if (_origInStream is not null)
            {
                try { _origInStream.Dispose(); } catch { }
            }
            // Restore the thread-static input-path saved by
            // TrackInputPath, but only when this scope actually
            // installed one. An output-only nested scope must not
            // clobber an outer scope's input path on dispose.
            if (_trackedInputPath)
            {
                ToshHost._activeInputPath = _activeInputPath;
            }
        }

        private sealed class RestoreOut : IDisposable
        {
            private readonly TextWriter _orig;
            public RestoreOut(TextWriter o) { _orig = o; }
            public void Dispose() => Console.SetOut(_orig);
        }
        private sealed class RestoreError : IDisposable
        {
            private readonly TextWriter _orig;
            public RestoreError(TextWriter o) { _orig = o; }
            public void Dispose() => Console.SetError(_orig);
        }
    }

    /// <summary>
    /// Opens redirection sinks/sources and rewires
    /// <see cref="Console.Out"/>, <see cref="Console.Error"/>, and
    /// <see cref="Console.In"/> for the lifetime of the returned
    /// scope. Disposing the scope flushes and restores. The IL
    /// emitter wraps a pipeline body in
    /// <c>using (ToshHost.BeginRedirection(...)) { /* stages */ }</c>.
    /// <para>
    /// <paramref name="streams"/>/<paramref name="modes"/>/<paramref name="targets"/>
    /// are parallel arrays describing each output redirection.
    /// <paramref name="inputPath"/> is non-null when the pipeline
    /// has an input redirection.
    /// </para>
    /// </summary>
    /// <summary>
    /// Coerces a redirection target value (output target or input
    /// source path) to a non-null string. Strings pass through;
    /// other values are stringified via
    /// <see cref="object.ToString"/>; null throws.
    /// </summary>
    public static string AsRedirectionPath(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                "Redirection target evaluated to null.");
        }
        return value as string ?? value.ToString()
            ?? throw new InvalidOperationException(
                "Redirection target stringified to null.");
    }

    /// <summary>
    /// Truthiness coercion mirroring the interpreter: null is false,
    /// bool passes through, numbers are non-zero, strings/collections
    /// are non-empty, everything else is truthy.
    /// </summary>
    public static bool IsTruthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        if (value is string s) return s.Length > 0;
        if (value is System.Collections.ICollection col) return col.Count > 0;
        if (value is byte by) return by != 0;
        if (value is sbyte sb) return sb != 0;
        if (value is short sh) return sh != 0;
        if (value is ushort us) return us != 0;
        if (value is int i) return i != 0;
        if (value is uint ui) return ui != 0;
        if (value is long l) return l != 0;
        if (value is ulong ul) return ul != 0;
        if (value is float f) return f != 0f;
        if (value is double d) return d != 0d;
        if (value is decimal dec) return dec != 0m;
        return true;
    }

    /// <summary>
    /// Wraps <paramref name="value"/> as an exception and throws it.
    /// Strings throw a <see cref="ToshUserException"/>; existing
    /// exceptions are rethrown verbatim; everything else throws a
    /// <see cref="ToshUserException"/> with the value attached.
    /// </summary>
    public static object ThrowAsException(object? value)
    {
        if (value is Exception ex) throw ex;
        if (value is string msg) throw new ToshUserException(msg, value);
        throw new ToshUserException(value?.ToString() ?? "throw", value);
    }

    /// <summary>
    /// Returns a callable wrapper for a user function reference
    /// (<c>&amp;funcname</c>): the returned object is invokable via
    /// <see cref="InvokeCallable"/> and resolves the function by
    /// name through the runtime each call (allows late binding /
    /// redefinition).
    /// </summary>
    public static object MakeFunctionReference(string name)
    {
        return new FunctionReferenceValue(name);
    }

    /// <summary>
    /// Returns a callable wrapper for a user function compiled to
    /// a static <see cref="MethodInfo"/>. Unlike
    /// <see cref="MakeFunctionReference(string)"/>, this binds
    /// directly to the IL-emitted method and is therefore the
    /// preferred path inside compiled assemblies (where the
    /// runtime command table does not contain user functions).
    /// </summary>
    public static object MakeFunctionReferenceFromMethod(MethodInfo method, string name)
    {
        return new CompiledMethodReferenceValue(new[] { method }, name);
    }

    /// <summary>
    /// Returns a callable wrapper for a user function with multiple
    /// overloads compiled to static <see cref="MethodInfo"/>s. At
    /// invocation time the wrapper picks the best-fitting candidate
    /// via the same <see cref="InvokeUserOverload"/> resolution the
    /// direct overload-dispatch path uses, after first applying
    /// named-argument reordering against the chosen candidate.
    /// </summary>
    public static object MakeFunctionReferenceFromMethods(MethodInfo[] methods, string name)
    {
        return new CompiledMethodReferenceValue(methods, name);
    }

    /// <summary>
    /// Returns a callable wrapper for a member projection
    /// (<c>_.A.B.C</c>): when invoked with one argument, walks the
    /// member path on that argument and returns the leaf value.
    /// </summary>
    public static object MakeMemberProjection(string[] path)
    {
        return new MemberProjectionValue(path);
    }

    /// <summary>
    /// Materializes any value into an <c>object[]</c> for tuple
    /// destructuring. Strings are treated as scalars (single
    /// element), nulls produce an empty array, arrays return
    /// directly, and any <see cref="System.Collections.IEnumerable"/>
    /// is walked element-by-element.
    /// </summary>
    public static object[] ToArray(object? value)
    {
        if (value is null) return Array.Empty<object>();
        if (value is object[] oa) return oa;
        if (value is string) return new object[] { value };
        if (value is System.Collections.IEnumerable seq)
        {
            var list = new List<object?>();
            foreach (var item in seq) list.Add(item);
            return list.Cast<object>().ToArray()!;
        }
        return new object[] { value };
    }

    /// <summary>
    /// Returns <paramref name="array"/>[<paramref name="index"/>] or
    /// <c>null</c> when the index is out of range. Used by tuple
    /// destructuring to make <c>($a, $b) = pipeline</c> tolerate a
    /// short RHS.
    /// </summary>
    public static object? IndexOrNull(object[] array, int index)
    {
        if (array is null) return null;
        return (uint)index < (uint)array.Length ? array[index] : null;
    }

    public sealed class ToshUserException : Exception
    {
        public object? Value { get; }
        public ToshUserException(string message, object? value)
            : base(message)
        {
            Value = value;
        }
    }

    /// <summary>
    /// A late-bound reference to a user function (<c>&amp;name</c>).
    /// Implements <see cref="IShellCallable"/> by resolving
    /// <see cref="Name"/> through the ambient runtime's command
    /// table at every invocation, so that redefining the function
    /// after the reference is captured is visible — matching the
    /// interpreter's late-binding semantics.
    /// </summary>
    public sealed class FunctionReferenceValue : IShellCallable
    {
        public string Name { get; }
        public FunctionReferenceValue(string name) { Name = name; }
        public override string ToString() => $"&{Name}";

        public string CallableName => Name;

        public int RequiredParameterCount =>
            ResolveCallable() is { } c ? c.RequiredParameterCount : 0;

        public int? MaximumParameterCount =>
            ResolveCallable() is { } c ? c.MaximumParameterCount : null;

        public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            var callable = ResolveCallable()
                ?? throw new InvalidOperationException(
                    $"function reference '&{Name}' could not be resolved at invocation time.");
            return callable.InvokeAsync(context);
        }

        private IShellCallable? ResolveCallable()
        {
            if (!Runtime.Commands.TryGet(Name, out var cmd)) return null;
            return cmd as IShellCallable;
        }
    }

    /// <summary>
    /// Direct binding of a function reference to a compiled static
    /// <see cref="MethodInfo"/>. Invoking it walks the param list,
    /// coerces each argument through <see cref="CoerceForParameter"/>,
    /// invokes the method, and yields the single result. Used by
    /// <c>&amp;name</c> inside compiled assemblies, where user
    /// functions are static methods rather than runtime
    /// <see cref="IShellCommand"/> entries.
    /// </summary>
    public sealed class CompiledMethodReferenceValue : IShellCallable
    {
        private readonly MethodInfo[] _methods;
        public string Name { get; }
        public CompiledMethodReferenceValue(MethodInfo[] methods, string name)
        {
            if (methods is null || methods.Length == 0)
                throw new ArgumentException("at least one method required", nameof(methods));
            _methods = methods;
            Name = name;
        }
        public override string ToString() => $"&{Name}";

        public string CallableName => Name;

        public int RequiredParameterCount
        {
            get
            {
                int min = int.MaxValue;
                foreach (var m in _methods)
                {
                    int required = 0;
                    foreach (var p in m.GetParameters())
                    {
                        if (!p.IsOptional && !p.HasDefaultValue) required++;
                    }
                    if (required < min) min = required;
                }
                return min == int.MaxValue ? 0 : min;
            }
        }

        public int? MaximumParameterCount
        {
            get
            {
                int max = 0;
                foreach (var m in _methods)
                {
                    var n = m.GetParameters().Length;
                    if (n > max) max = n;
                }
                return max;
            }
        }

        public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            var args = context.Arguments;
            var argArr = new object?[args.Count];
            for (int i = 0; i < args.Count; i++) argArr[i] = args[i];

            object? result;
            if (_methods.Length == 1)
            {
                // Single-overload fast path: still apply named-arg
                // reordering, then call InvokeUserFunc directly.
                result = InvokeUserFunc(_methods[0], BindNamedArguments(_methods[0], argArr));
            }
            else
            {
                // Overload set: route through InvokeUserOverload so
                // its existing scoring picks the best candidate.
                result = InvokeUserOverload(_methods, argArr);
            }
            if (result is not null) yield return result;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Reorders <paramref name="args"/> to match the parameter
        /// list of <paramref name="method"/>, honouring any
        /// <see cref="NamedArgument"/> wrappers. Positional args
        /// fill remaining slots in declaration order; missing
        /// optional/default parameters keep their default; missing
        /// required parameters are left as null and produce the
        /// usual TargetInvocationException downstream (matching
        /// the existing behaviour of an arity mismatch).
        /// </summary>
        private static object?[] BindNamedArguments(MethodInfo method, object?[] args)
        {
            var hasNamed = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is NamedArgument) { hasNamed = true; break; }
            }
            if (!hasNamed) return args;

            var ps = method.GetParameters();
            var bound = new object?[ps.Length];
            var filled = new bool[ps.Length];

            // Pass 1 — named arguments (case-insensitive match on
            // parameter name, mirroring SplitCallableArguments).
            foreach (var a in args)
            {
                if (a is NamedArgument na)
                {
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (string.Equals(ps[i].Name, na.Name,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            bound[i] = na.Value;
                            filled[i] = true;
                            break;
                        }
                    }
                }
            }

            // Pass 2 — positionals into the next unfilled slot.
            int next = 0;
            foreach (var a in args)
            {
                if (a is NamedArgument) continue;
                while (next < ps.Length && filled[next]) next++;
                if (next >= ps.Length) break;
                bound[next] = a;
                filled[next] = true;
                next++;
            }

            // Pass 3 — defaults for any remaining optional slots.
            for (int i = 0; i < ps.Length; i++)
            {
                if (!filled[i] && (ps[i].IsOptional || ps[i].HasDefaultValue))
                {
                    bound[i] = ps[i].DefaultValue;
                }
            }

            return bound;
        }
    }

    public sealed class MemberProjectionValue
    {
        public string[] Path { get; }
        public MemberProjectionValue(string[] path) { Path = path; }
        public object? Apply(object? target)
        {
            var current = target;
            foreach (var segment in Path)
            {
                if (current is null) return null;
                current = GetMember(current, segment, nullSafe: false);
            }
            return current;
        }
        public override string ToString() => "_." + string.Join('.', Path);
    }

    public static RedirectionScope BeginRedirection(
        int[] streams,
        int[] modes,
        string[] targets,
        string? inputPath)
    {
        var scope = new RedirectionScope();
        try
        {
            // Track originals so duplicates don't lose the real
            // Console.Out/Error.
            var origOut = Console.Out;
            var origError = Console.Error;
            var trackedOut = false;
            var trackedError = false;

            // Group writers by stream so multiple "out>" entries
            // each get their own file (last one wins for Console.Out
            // — same as a shell). The simple, predictable rule: each
            // entry overwrites the binding.
            for (var i = 0; i < streams.Length; i++)
            {
                var stream = streams[i];
                var mode = modes[i];
                var target = targets[i] ?? throw new InvalidOperationException(
                    "Redirection target evaluated to null.");

                var fileMode = mode == 1 ? FileMode.Append : FileMode.Create;
                var fs = new FileStream(target, fileMode, FileAccess.Write, FileShare.Read);
                scope.TrackStream(fs);
                var writer = new StreamWriter(fs) { AutoFlush = true };
                scope.TrackWriter(writer);

                switch (stream)
                {
                    case 0: // Output
                        if (!trackedOut) { scope.TrackOriginalOut(origOut); trackedOut = true; }
                        Console.SetOut(writer);
                        break;
                    case 1: // Error
                        if (!trackedError) { scope.TrackOriginalError(origError); trackedError = true; }
                        Console.SetError(writer);
                        break;
                    case 2: // OutputThenError -> both share writer
                        if (!trackedOut) { scope.TrackOriginalOut(origOut); trackedOut = true; }
                        if (!trackedError) { scope.TrackOriginalError(origError); trackedError = true; }
                        Console.SetOut(writer);
                        Console.SetError(writer);
                        break;
                    case 3: // ErrorThenOutput -> same as 2 in single-process land
                        if (!trackedOut) { scope.TrackOriginalOut(origOut); trackedOut = true; }
                        if (!trackedError) { scope.TrackOriginalError(origError); trackedError = true; }
                        Console.SetError(writer);
                        Console.SetOut(writer);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown redirection stream selector {stream}.");
                }
            }

            if (inputPath is not null)
            {
                scope.TrackOriginalIn(Console.In);
                var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                scope.TrackInStream(fs);
                var reader = new StreamReader(fs);
                Console.SetIn(reader);
                // Also publish the input path so the pipeline-input
                // path (commands that read context.Input rather than
                // Console.In, e.g. cat / wc / grep / read-lines) can
                // seed their first stage from the same file. The
                // file is opened a second time for that consumer so
                // both readers can stream independently.
                scope.TrackInputPath(inputPath);
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
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
        var ctx = new CommandContext(Runtime, EmptyInput(), args, default);
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
    /// Returns the input <see cref="IAsyncEnumerable{T}"/> for the
    /// first stage of a compiled pipeline. When an active
    /// redirection scope has set
    /// <see cref="_activeInputPath"/> (via <c>cmd in&lt; "file"</c>),
    /// returns a fresh per-call line-by-line reader of that file —
    /// each line yielded as a string. Otherwise returns an empty
    /// sequence. The reader is opened independently of the
    /// <see cref="Console.In"/> reader installed by
    /// <see cref="BeginRedirection"/> so commands that read
    /// pipeline input (cat/wc/grep/read-lines) and commands that
    /// read Console.In (read-line) both observe a fresh stream.
    /// </summary>
    public static IAsyncEnumerable<object?> EmptyInput()
    {
        var path = _activeInputPath;
        return path is null ? EmptyAsync() : ReadFileLinesAsync(path);
    }

    [ThreadStatic]
    private static string? _activeInputPath;

    private static async IAsyncEnumerable<object?> ReadFileLinesAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            yield return line;
        }
    }

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
        // Split args into positional and named. The compiler's
        // `EmitArgsArray` may produce a `NamedArgument` wrapper for any
        // `name: value` argument; we unpack those into a name-keyed
        // dictionary so they can be placed in their declared parameter
        // slot by name. Splat-expanded positional values remain in the
        // positional list as-is.
        var ps = fn.GetParameters();
        var positional = new List<object?>(args.Length);
        var named = (Dictionary<string, object?>?)null;
        foreach (var arg in args)
        {
            if (arg is global::Tosh.Language.NamedArgument na)
            {
                named ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                named[na.Name] = na.Value;
            }
            else
            {
                positional.Add(arg);
            }
        }

        var effectiveArgCount = positional.Count + (named?.Count ?? 0);
        if (paramCount == effectiveArgCount)
        {
            await foreach (var _ in input) { /* drain & discard */ }
            var slot = BuildSlot(ps, positional, named, leadingInputSlot: false, inputValue: null);
            yield return InvokeUserFunc(fn, slot);
            yield break;
        }
        if (paramCount == effectiveArgCount + 1)
        {
            await foreach (var item in input)
            {
                var slot = BuildSlot(ps, positional, named, leadingInputSlot: true, inputValue: item);
                yield return InvokeUserFunc(fn, slot);
            }
            yield break;
        }
        throw new InvalidOperationException(
            $"user function '{fn.Name}' as a pipeline stage expects "
            + $"{effectiveArgCount} or {effectiveArgCount + 1} parameters, got {paramCount}.");
    }

    /// <summary>
    /// Builds a parameter-slot array honoring named-argument placement. Named args go in the
    /// position named by their parameter; remaining slots are filled positionally in order.
    /// When <paramref name="leadingInputSlot"/> is true the first slot is reserved for the
    /// piped <paramref name="inputValue"/>.
    /// </summary>
    private static object?[] BuildSlot(
        System.Reflection.ParameterInfo[] ps,
        IReadOnlyList<object?> positional,
        IReadOnlyDictionary<string, object?>? named,
        bool leadingInputSlot,
        object? inputValue)
    {
        var slot = new object?[ps.Length];
        var sentinel = new object();
        for (var i = 0; i < slot.Length; i++) slot[i] = sentinel;

        var startIndex = leadingInputSlot ? 1 : 0;
        if (leadingInputSlot && slot.Length > 0)
        {
            slot[0] = inputValue;
        }

        if (named is not null)
        {
            for (var i = startIndex; i < ps.Length; i++)
            {
                if (named.TryGetValue(ps[i].Name!, out var v))
                {
                    slot[i] = v;
                }
            }
        }

        var posIndex = 0;
        for (var i = startIndex; i < slot.Length; i++)
        {
            if (!ReferenceEquals(slot[i], sentinel)) continue;
            if (posIndex < positional.Count)
            {
                slot[i] = positional[posIndex++];
            }
            else
            {
                slot[i] = null; // missing param; CoerceForParameter will handle defaults
            }
        }

        return slot;
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

    public static object? InvokeUserOverload(
        System.Reflection.MethodInfo[] candidates,
        object?[] args)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(args);

        MethodInfo? bestMethod = null;
        object?[]? bestArgs = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (!TryBuildOverloadInvocation(candidate, args, out var invocationArgs, out var score))
            {
                continue;
            }

            if (score > bestScore)
            {
                bestMethod = candidate;
                bestArgs = invocationArgs;
                bestScore = score;
            }
        }

        if (bestMethod is not null && bestArgs is not null)
            return InvokeUserFunc(bestMethod, bestArgs);

        throw new InvalidOperationException(
            $"No overload matched compiled user function call with {args.Length} argument(s).");
    }

    private static bool TryBuildOverloadInvocation(
        MethodInfo candidate,
        object?[] callArgs,
        out object?[] invocationArgs,
        out int score)
    {
        invocationArgs = Array.Empty<object?>();
        score = int.MinValue;

        var ps = candidate.GetParameters();
        if (ps.Length == 1 && ps[0].ParameterType == typeof(object[]))
        {
            // Packed-argument fallback should lose to any concrete same-arity match.
            invocationArgs = [callArgs];
            score = 1;
            return true;
        }

        // Split named-argument wrappers (`name = value`) from positional args
        // and bind them to parameters by name so that compiled user functions
        // accept `func(name = value)` invocations.
        Dictionary<string, object?>? named = null;
        List<object?>? positionalOnly = null;
        for (var i = 0; i < callArgs.Length; i++)
        {
            if (callArgs[i] is global::Tosh.Language.NamedArgument na)
            {
                named ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                named[na.Name] = na.Value;
                positionalOnly ??= new List<object?>(callArgs.Length);
                if (positionalOnly.Count < i)
                {
                    for (var j = 0; j < i; j++)
                    {
                        if (callArgs[j] is not global::Tosh.Language.NamedArgument)
                        {
                            positionalOnly.Add(callArgs[j]);
                        }
                    }
                }
            }
            else if (positionalOnly is not null)
            {
                positionalOnly.Add(callArgs[i]);
            }
        }
        IReadOnlyList<object?> positional = positionalOnly ?? (IReadOnlyList<object?>)callArgs;

        if (positional.Count + (named?.Count ?? 0) != ps.Length)
            return false;

        // Reject named args that don't match any parameter name.
        if (named is not null)
        {
            foreach (var k in named.Keys)
            {
                var found = false;
                foreach (var p in ps)
                {
                    if (string.Equals(p.Name, k, StringComparison.Ordinal)) { found = true; break; }
                }
                if (!found) return false;
            }
        }

        var coerced = new object?[ps.Length];
        var total = 0;
        var posIndex = 0;
        for (var i = 0; i < ps.Length; i++)
        {
            object? raw;
            if (named is not null && ps[i].Name is not null && named.TryGetValue(ps[i].Name!, out var nv))
            {
                raw = nv;
            }
            else if (posIndex < positional.Count)
            {
                raw = positional[posIndex++];
            }
            else
            {
                return false;
            }

            if (!TryCoerceForParameterStrict(raw, ps[i].ParameterType, out coerced[i], out var argScore))
                return false;
            total += argScore;
        }

        invocationArgs = coerced;
        score = total;
        return true;
    }

    private static bool TryCoerceForParameterStrict(
        object? value,
        Type target,
        out object? coerced,
        out int score)
    {
        coerced = null;
        score = 0;

        if (target == typeof(object))
        {
            coerced = value;
            score = 1;
            return true;
        }

        if (value is null)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                return false;
            coerced = null;
            score = 2;
            return true;
        }

        var actual = value.GetType();
        if (target.IsAssignableFrom(actual))
        {
            coerced = value;
            score = 6;
            return true;
        }

        try
        {
            var underlying = Nullable.GetUnderlyingType(target) ?? target;
            coerced = Convert.ChangeType(value, underlying);
            score = 3;
            return true;
        }
        catch
        {
            coerced = null;
            score = 0;
            return false;
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
        ExecuteRegisteredSourceSlice(spanStart, spanLength, nameof(RegisterTypeFromSource));
    }

    /// <summary>
    /// Registers a top-level declaration whose semantics are still owned by
    /// the interpreter: unsupported top-level function shapes, runes, events,
    /// <c>require</c>, and native <c>bind</c>. The declaration is replayed from
    /// the source text previously wired via <see cref="RegisterSource"/>.
    /// </summary>
    public static void RegisterDeclarationFromSource(int spanStart, int spanLength)
    {
        ExecuteRegisteredSourceSlice(spanStart, spanLength, nameof(RegisterDeclarationFromSource));
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
        ExecuteRegisteredSourceSlice(spanStart, spanLength, nameof(RegisterModuleFromSource));
    }

    /// <summary>
    /// Registers a compiled type alias by parsing only the alias slice and
    /// inserting its runtime definition directly, avoiding executable-source
    /// replay. This preserves refinement/generic alias behavior while keeping
    /// compiled alias metadata CLR-first.
    /// </summary>
    public static void RegisterCompiledTypeAlias(int spanStart, int spanLength)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                $"ToshHost.{nameof(RegisterCompiledTypeAlias)} called before RegisterSource");
        }

        if (s_engine is null) Initialize();

        var slice = s_sourceText.Substring(spanStart, spanLength);
        s_engine!.RegisterCompiledTypeAliasFromSource(
            s_sourceName ?? "<compiled>",
            slice);
    }

    /// <summary>
    /// Registers a compiled rune (macro) declaration by parsing the source slice and
    /// inserting its runtime definition directly, avoiding source replay.
    /// </summary>
    public static void RegisterRuneFromSource(int spanStart, int spanLength)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                $"ToshHost.{nameof(RegisterRuneFromSource)} called before RegisterSource");
        }

        if (s_engine is null) Initialize();

        var slice = s_sourceText.Substring(spanStart, spanLength);
        s_engine!.RegisterRuneFromSource(s_sourceName ?? "<compiled>", slice);
    }

    /// <summary>
    /// Loads and imports a required module from a compiled assembly context. Called by
    /// compiled assemblies at runtime to satisfy <c>require</c> statements that target
    /// external scripts or assemblies without replaying the parent script's source.
    /// </summary>
    public static void RequireModule(string target, string[] importedNames, string[] importedAliases)
    {
        if (s_engine is null) Initialize();

        var resolveFrom = s_sourceName is not null
            ? Path.GetDirectoryName(s_sourceName) ?? Runtime.CurrentDirectory
            : Runtime.CurrentDirectory;

        s_engine!.RequireModuleFromCompiled(target, importedNames, importedAliases, resolveFrom);
    }

    private static void ExecuteRegisteredSourceSlice(int spanStart, int spanLength, string caller)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                $"ToshHost.{caller} called before RegisterSource");
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
        using var _ = UseCurrentConsoleWritersForHostRuntime();
        var task = s_engine!.ExecuteToListAsync(s_sourceText, s_sourceName ?? "<compiled>");
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Replays the entire registered source through the interpreter.
    /// Used for permissive-profile language features whose semantics are
    /// expansion-oriented rather than command-oriented, such as runes.
    /// </summary>
    public static void RunScriptFromSource(string[] argv)
    {
        if (s_sourceText is null)
        {
            throw new InvalidOperationException(
                "ToshHost.RunScriptFromSource called before RegisterSource");
        }
        if (s_engine is null) Initialize();
        Runtime.InvocationArguments = (argv ?? Array.Empty<string>()).Cast<object?>().ToArray();
        using var _ = UseCurrentConsoleWritersForHostRuntime();
        var task = s_engine!.ExecuteToListAsync(s_sourceText, s_sourceName ?? "<compiled>");
        foreach (var value in task.GetAwaiter().GetResult())
        {
            Console.WriteLine(Runtime.Formatter.Format(value));
        }
    }

    private static IDisposable? UseCurrentConsoleWritersForHostRuntime()
    {
        if (!s_runtimeOwnedByHost || s_runtime is null) return null;
        var previousOutput = s_runtime.Output;
        var previousError = s_runtime.Error;
        s_runtime.Output = Console.Out;
        s_runtime.Error = Console.Error;
        return new RestoreWriters(previousOutput, previousError);
    }

    private sealed class RestoreWriters(TextWriter output, TextWriter error) : IDisposable
    {
        public void Dispose()
        {
            if (s_runtime is null) return;
            s_runtime.Output = output;
            s_runtime.Error = error;
        }
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
    /// Registration-order list of compiled tosh assemblies. Newest entries
    /// are preferred for CLR-shell type lookup to avoid cross-assembly
    /// same-name collisions in long-running hosts.
    /// </summary>
    private static readonly List<Assembly> s_compiledAssemblies = new();

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
            s_compiledAssemblies.Remove(asm);
            s_compiledAssemblies.Add(asm);

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
        return NewObjectCore(typeName, bareTypeName: null, typeArguments: null, args);
    }

    /// <summary>
    /// Generic-aware overload: <paramref name="bareTypeName"/> is the
    /// unqualified class name (e.g. <c>"Box"</c>) and
    /// <paramref name="typeArguments"/> are the verbatim
    /// type-argument strings written by the source
    /// (e.g. <c>["int", "string"]</c>). Falls back to the legacy path
    /// when <paramref name="typeArguments"/> is null/empty.
    /// </summary>
    public static object? NewObject(string typeName, string bareTypeName, string[] typeArguments, object?[] args)
    {
        return NewObjectCore(typeName, bareTypeName, typeArguments, args);
    }

    private static object? NewObjectCore(string typeName, string? bareTypeName, IReadOnlyList<string>? typeArguments, object?[] args)
    {
        if (s_engine is null) Initialize();
        var argList = (IReadOnlyList<object?>)args;

        var hasTypeArgs = typeArguments is { Count: > 0 };
        var lookupName = hasTypeArgs && bareTypeName is not null ? bareTypeName : typeName;

        if (s_engine!.TryGetNamedType(lookupName, out var named))
        {
            if (named is ToshClassDefinition cls)
            {
                if (hasTypeArgs)
                {
                    var resolved = new Type?[typeArguments!.Count];
                    for (int i = 0; i < typeArguments.Count; i++)
                    {
                        resolved[i] = s_engine.TryResolveTypeName(typeArguments[i]);
                    }
                    return cls.CreateGenericInstance(resolved, typeArguments, argList);
                }
                return cls.CreateInstance(argList);
            }
            return named switch
            {
                ToshRecordDefinition rec => rec.CreateInstance(argList),
                ToshStructDefinition st => st.CreateInstance(argList),
                ToshEnumDefinition en => en.CreateInstance(argList),
                ToshUnionDefinition un => un.CreateInstance(argList),
                _ => throw new InvalidOperationException(
                    $"type '{lookupName}' is not constructable"),
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
        lock (s_lock)
        {
            for (var asmIndex = s_compiledAssemblies.Count - 1; asmIndex >= 0; asmIndex--)
            {
                var asm = s_compiledAssemblies[asmIndex];
                IEnumerable<Type?> registeredTypes;
                try
                {
                    registeredTypes = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    registeredTypes = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in registeredTypes)
                {
                    if (type is null) continue;
                    if (type.GetCustomAttribute<ToshTypeAttribute>() is null) continue;
                    if (IsToshTypeNameMatch(type, typeName)) return type;
                }
            }
        }

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
