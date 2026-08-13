using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Language.Bridge;

/// <summary>
/// Why a callback that arrives on the wrong thread cannot simply throw.
///
/// The thunk runs on a native stack frame. Letting an exception unwind through
/// it is undefined behaviour, so a violation is recorded and a default value
/// returned; <see cref="NativeCallbackScope"/> rethrows once control is back on
/// managed ground. The failure is still loud — it just becomes loud at a point
/// where throwing is legal.
/// </summary>
internal sealed class NativeCallbackThreadViolationException : InvalidOperationException
{
    public NativeCallbackThreadViolationException(string callbackName, int expectedThreadId, int actualThreadId)
        : base($"Callback '{callbackName}' was invoked on thread {actualThreadId}, but the engine owns thread {expectedThreadId}. " +
               "ToSh's engine is single-threaded — its scope stack is not safe to touch from a foreign thread — so the callback " +
               "did not run. Callbacks delivered on a library's own thread (SDL audio, for example) are not supported.")
    {
        CallbackName = callbackName;
        ExpectedThreadId = expectedThreadId;
        ActualThreadId = actualThreadId;
    }

    public string CallbackName { get; }

    public int ExpectedThreadId { get; }

    public int ActualThreadId { get; }
}

/// <summary>
/// Collects failures that happened inside a callback while a native call was in
/// flight, so the outermost native call can rethrow them after the native
/// frames are gone.
///
/// One instance is pushed per native invocation and is <c>[ThreadStatic]</c>:
/// a nested native call made from inside a callback gets its own scope, and the
/// innermost one that can legally throw is the one that does.
/// </summary>
internal sealed class NativeCallbackScope : IDisposable
{
    [ThreadStatic]
    private static NativeCallbackScope? _current;

    private readonly NativeCallbackScope? _parent;
    private Exception? _failure;

    public NativeCallbackScope()
    {
        _parent = _current;
        _current = this;
    }

    /// <summary>
    /// Records the first failure only. A callback invoked in a loop — a
    /// comparator, say — would otherwise report whichever call happened to fail
    /// last, which is rarely the one that explains the problem.
    /// </summary>
    public static void RecordFailure(Exception exception)
    {
        var scope = _current;

        if (scope is not null)
        {
            scope._failure ??= exception;
        }
    }

    public static bool IsActive => _current is not null;

    public Exception? Failure => _failure;

    public void Dispose() => _current = _parent;
}

internal static class NativeCallbackThunkFactory
{
    /// <summary>
    /// Delegates handed to native code must be rooted for as long as the
    /// callee might call them. <see cref="Marshal.GetFunctionPointerForDelegate"/>
    /// does not root, and neither does passing a delegate as a P/Invoke
    /// argument beyond the duration of that one call — so a thunk stored by the
    /// callee (GLFW's window callbacks, for instance) would be collected and
    /// leave native code calling freed memory.
    ///
    /// Keyed by the library binding, so thunks live exactly as long as the
    /// library they were handed to.
    /// </summary>
    private static readonly Dictionary<string, List<Delegate>> Roots = new(StringComparer.Ordinal);

    private static readonly object RootsLock = new();

    public static void Root(NativeLibraryBinding binding, Delegate thunk)
    {
        lock (RootsLock)
        {
            if (!Roots.TryGetValue(binding.CacheKey, out var list))
            {
                list = [];
                Roots[binding.CacheKey] = list;
            }

            list.Add(thunk);
        }
    }

    /// <summary>
    /// Builds the delegate native code will call, wrapping <paramref name="callable"/>.
    /// </summary>
    /// <param name="ownerThreadId">
    /// The managed thread the engine belongs to. Compared on every invocation:
    /// ToSh's engine keeps its scopes in a plain stack and answers concurrency
    /// by cloning per fork, so touching it from another thread is a data race
    /// rather than a slow path.
    /// </param>
    public static Delegate Create(
        ToshNativeCallbackDefinition definition,
        IShellCallable callable,
        CommandContext context,
        int ownerThreadId)
    {
        var invoke = definition.ClrType.GetMethod("Invoke")
                     ?? throw new InvalidOperationException($"Emitted callback type '{definition.Name}' has no Invoke method.");

        var handler = new ThunkHandler(definition, callable, context, ownerThreadId, invoke.ReturnType);

        // Compiled rather than reflected: the delegate handed to native code
        // must match the emitted Invoke signature exactly, and a compiled
        // lambda is what turns a fixed `object?[]` dispatcher into that
        // signature without emitting IL by hand.
        var parameters = invoke
            .GetParameters()
            .Select(static parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();

        var boxed = Expression.NewArrayInit(
            typeof(object),
            parameters.Select(static parameter => (Expression)Expression.Convert(parameter, typeof(object))));

        var dispatch = typeof(ThunkHandler)
            .GetMethod(nameof(ThunkHandler.Dispatch), BindingFlags.Instance | BindingFlags.NonPublic)!;

        var call = Expression.Call(Expression.Constant(handler), dispatch, boxed);

        // A void callback still calls Dispatch; the block discards its result
        // so the lambda's type matches a void-returning delegate.
        var body = invoke.ReturnType == typeof(void)
            ? (Expression)Expression.Block(call, Expression.Empty())
            : Expression.Convert(call, invoke.ReturnType);

        return Expression.Lambda(definition.ClrType, body, parameters).Compile();
    }

    private sealed class ThunkHandler
    {
        private readonly ToshNativeCallbackDefinition _definition;
        private readonly IShellCallable _callable;
        private readonly CommandContext _context;
        private readonly int _ownerThreadId;
        private readonly Type _returnType;

        public ThunkHandler(
            ToshNativeCallbackDefinition definition,
            IShellCallable callable,
            CommandContext context,
            int ownerThreadId,
            Type returnType)
        {
            _definition = definition;
            _callable = callable;
            _context = context;
            _ownerThreadId = ownerThreadId;
            _returnType = returnType;
        }

        internal object? Dispatch(object?[] arguments)
        {
            var currentThreadId = Environment.CurrentManagedThreadId;

            if (currentThreadId != _ownerThreadId)
            {
                NativeCallbackScope.RecordFailure(
                    new NativeCallbackThreadViolationException(_definition.Name, _ownerThreadId, currentThreadId));
                return DefaultResult();
            }

            try
            {
                return InvokeCallable(arguments);
            }
            catch (Exception exception)
            {
                // Never let this unwind into the native frames above us.
                NativeCallbackScope.RecordFailure(exception);
                return DefaultResult();
            }
        }

        /// <summary>
        /// The value returned to native code when the callback could not run.
        ///
        /// A void callback has no such value, and asking for one is fatal rather
        /// than merely wrong: <see cref="NativeInteropUtilities.CreateDefaultValue"/>
        /// reaches <c>Activator.CreateInstance(typeof(void))</c>, which throws
        /// <c>NotSupportedException</c> — from inside the catch block that exists
        /// precisely to stop an exception escaping into native frames. The
        /// process dies with an unhandled exception on a native stack.
        /// </summary>
        private object? DefaultResult() =>
            _returnType == typeof(void) ? null : NativeInteropUtilities.CreateDefaultValue(_returnType);

        private object? InvokeCallable(object?[] arguments)
        {
            var callableArguments = new List<object?>(arguments.Length);

            for (var index = 0; index < arguments.Length && index < _definition.Parameters.Count; index++)
            {
                var parameter = _definition.Parameters[index];
                var value = arguments[index];

                // A cstring arrives as a pointer; the declaration asked for a
                // string, so decode it here rather than making every callback
                // body do it.
                if (parameter.ClrType == typeof(string) && value is IntPtr pointer)
                {
                    value = pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
                }

                callableArguments.Add(value);
            }

            var results = FunctionalCommandUtilities
                .ExecuteAsync(_context, _callable, callableArguments)
                .GetAwaiter()
                .GetResult();

            return ProjectResult(results);
        }

        /// <summary>
        /// A ToSh callable yields a stream; a C callback returns one value.
        /// More than one is an error rather than a silent pick, because the
        /// failure it would otherwise cause — a mis-sorted array from a
        /// comparator, an ignored event — is invisible at the call site.
        /// </summary>
        private object? ProjectResult(IReadOnlyList<object?> results)
        {
            if (_returnType == typeof(void))
            {
                return null;
            }

            if (results.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Callback '{_definition.Name}' must produce exactly one value for its '{_definition.Return.TypeName}' " +
                    $"return, but produced {results.Count}. " +
                    (results.Count == 0
                        ? "Give the function body a single result expression."
                        : "A function that emits several values cannot be used as a callback; reduce it to one."));
            }

            var result = results[0];

            if (!TypeConversion.TryConvert(result, _returnType, out var converted))
            {
                throw new InvalidOperationException(
                    $"Callback '{_definition.Name}' returned a value that could not be converted to " +
                    $"'{_definition.Return.TypeName}'.");
            }

            return converted;
        }
    }
}
