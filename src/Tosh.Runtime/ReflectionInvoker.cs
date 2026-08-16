using System.Runtime.ExceptionServices;
using System.Reflection;

namespace Tosh.Runtime;

public sealed class ReflectionInvoker
{
    /// <summary>
    /// The public methods of a type, by whether they are static, cached permanently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every call went through <c>GetMethods(...)</c>, which allocates an array of
    /// <em>every</em> public method the type has and then gets linear-scanned for a
    /// name match. `"abc".ToUpper()` built an array of all ~80 methods on
    /// <see cref="string"/> and compared names down it, on every call — 3,975 ns per
    /// invocation, with `StelemRef` array stores and
    /// `String.Equals(…, StringComparison)` visible in the profile (<c>TS-P2-122</c>).
    /// </para>
    /// <para>
    /// A loaded type's members cannot change, so the arrays are kept for the process.
    /// The cache is bounded by the number of distinct types a program calls into.
    /// Callers only read the arrays — <c>Invoke</c> takes an
    /// <see cref="IEnumerable{T}"/> and <c>ConstructGenericCandidates</c> builds a new
    /// list — so one shared array per type is safe.
    /// </para>
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Type, bool Static), MethodInfo[]> _methodsByType = new();

    private static MethodInfo[] PublicMethods(Type type, bool isStatic)
        => _methodsByType.GetOrAdd(
            (type, isStatic),
            static key => key.Type.GetMethods(
                BindingFlags.Public | (key.Static ? BindingFlags.Static : BindingFlags.Instance)));

    /// <summary>
    /// The overloads of one name, cached — so overload selection scans two or three
    /// methods instead of every method the type has.
    /// </summary>
    /// <remarks>
    /// <c>Invoke</c> filters the candidates by name itself and uses that same filtered
    /// set for its diagnostics, so handing it a pre-filtered array changes nothing it
    /// can observe. It was scanning ~80 methods per call on <see cref="string"/>, which
    /// left `String.Equals(…, StringComparison)` at 2% of a profile that does nothing
    /// but call `ToUpper` (<c>TS-P2-122</c>).
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Type, bool Static, string Name), MethodInfo[]> _methodsByName =
        new(MethodKeyComparer.Instance);

    private static MethodInfo[] MethodsNamed(Type type, bool isStatic, string name)
        => _methodsByName.GetOrAdd(
            (type, isStatic, name),
            static key => Array.FindAll(
                PublicMethods(key.Type, key.Static),
                candidate => string.Equals(candidate.Name, key.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Compares the name case-insensitively without lowercasing it, which would
    /// allocate a string on every lookup and undo the point of the cache.
    /// </summary>
    private sealed class MethodKeyComparer : IEqualityComparer<(Type Type, bool Static, string Name)>
    {
        public static readonly MethodKeyComparer Instance = new();

        public bool Equals((Type Type, bool Static, string Name) x, (Type Type, bool Static, string Name) y)
            => x.Type == y.Type &&
               x.Static == y.Static &&
               string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Type Type, bool Static, string Name) key)
            => HashCode.Combine(key.Type, key.Static, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    /// <summary>The public constructors of a type, cached for the same reason.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ConstructorInfo[]> _constructorsByType = new();

    private static ConstructorInfo[] PublicConstructors(Type type)
        => _constructorsByType.GetOrAdd(type, static candidate => candidate.GetConstructors());

    public object CreateInstance(Type type, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);

        CandidateBinding? bestBinding = null;
        ConstructorInfo? bestConstructor = null;

        foreach (var constructor in PublicConstructors(type))
        {
            if (!TryBindParameters(constructor.GetParameters(), arguments, out var binding))
            {
                continue;
            }

            if (bestBinding is null || binding.Score < bestBinding.Value.Score)
            {
                bestBinding = binding;
                bestConstructor = constructor;
            }
        }

        if (bestConstructor is not null && bestBinding is not null)
        {
            return InvokeUnwrapped(() => bestConstructor.Invoke(bestBinding.Value.BoundArguments));
        }

        throw new InvalidOperationException($"No constructor matched '{type.FullName}' with {arguments.Count} argument(s).");
    }

    public ValueTask<object> CreateInstanceAsync(
        Type type,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(CreateInstance(type, arguments));
    }

    public InvocationResult InvokeInstance(object target, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (target is IShellInvocableObject shellInvocable)
        {
            return shellInvocable.InvokeInstanceMethod(methodName, arguments);
        }

        if (target is IShellStaticType shellStaticType)
        {
            return shellStaticType.InvokeStaticMethod(methodName, arguments);
        }

        if (target is Type staticType)
        {
            return InvokeStatic(staticType, methodName, arguments);
        }

        return Invoke(
            MethodsNamed(target.GetType(), isStatic: false, methodName),
            target,
            methodName,
            arguments,
            $"instance method '{methodName}' on '{target.GetType().FullName}'");
    }

    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
        => InvokeInstanceMethodAsync(target, methodName, arguments, typeArguments: null, cancellationToken);

    /// <summary>
    /// Invokes an instance method, optionally with type arguments resolved from the call site.
    /// </summary>
    /// <remarks>
    /// The types arrive resolved rather than as names: resolution needs the scope, aliases and
    /// declared types the engine knows about, and none of that is reachable from here. Passing
    /// names would mean a second, weaker resolver living in the invoker.
    /// </remarks>
    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (target is IShellInvocableObject shellInvocable)
        {
            return shellInvocable.InvokeInstanceMethodAsync(methodName, arguments, typeArguments, cancellationToken);
        }

        if (target is IShellStaticType shellStaticType)
        {
            return shellStaticType.InvokeStaticMethodAsync(methodName, arguments, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (target is Type staticType)
        {
            return ValueTask.FromResult(InvokeStatic(staticType, methodName, arguments));
        }

        var candidates = MethodsNamed(target.GetType(), isStatic: false, methodName);

        if (typeArguments is { Count: > 0 })
        {
            candidates = ConstructGenericCandidates(
                candidates,
                methodName,
                typeArguments,
                $"'{target.GetType().FullName}'");
        }

        return ValueTask.FromResult(Invoke(
            candidates,
            target,
            methodName,
            arguments,
            $"instance method '{methodName}' on '{target.GetType().FullName}'"));
    }

    /// <summary>
    /// Narrows <paramref name="candidates"/> to the generic methods named
    /// <paramref name="methodName"/> whose arity matches, each constructed with
    /// <paramref name="typeArguments"/>, so ordinary overload selection runs against the
    /// constructed forms.
    /// </summary>
    private static MethodInfo[] ConstructGenericCandidates(
        MethodInfo[] candidates,
        string methodName,
        IReadOnlyList<Type> typeArguments,
        string ownerDescription)
    {
        var constructed = new List<MethodInfo>();
        var sawName = false;

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sawName = true;

            if (!candidate.IsGenericMethodDefinition ||
                candidate.GetGenericArguments().Length != typeArguments.Count)
            {
                continue;
            }

            try
            {
                constructed.Add(candidate.MakeGenericMethod([.. typeArguments]));
            }
            catch (ArgumentException)
            {
                // A constraint the arguments do not satisfy. Skipped rather than thrown, so the
                // failure reads as "no overload matched" alongside any sibling that did.
            }
        }

        if (constructed.Count == 0)
        {
            var names = string.Join(", ", typeArguments.Select(static type => type.Name));
            throw new InvalidOperationException(
                sawName
                    ? $"Method '{methodName}' on {ownerDescription} has no generic overload taking {typeArguments.Count} type argument(s) <{names}>."
                    : $"Method '{methodName}' was not found on {ownerDescription}.");
        }

        return [.. constructed];
    }

    public InvocationResult InvokeStatic(Type type, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        return Invoke(
            MethodsNamed(type, isStatic: true, methodName),
            target: null,
            methodName,
            arguments,
            $"static method '{methodName}' on '{type.FullName}'");
    }

    public ValueTask<InvocationResult> InvokeStaticMethodAsync(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
        => InvokeStaticMethodAsync(type, methodName, arguments, typeArguments: null, cancellationToken);

    /// <summary>
    /// Invokes a static method, optionally with type arguments written at the call site —
    /// <c>TS-P2-82</c>.
    /// </summary>
    /// <remarks>
    /// The instance path has taken these since generics were wired; the static path never did, so
    /// `Array.Empty&lt;int&gt;()` had nowhere to put them even once it parsed.
    /// </remarks>
    public ValueTask<InvocationResult> InvokeStaticMethodAsync(
        Type type,
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (typeArguments is not { Count: > 0 })
        {
            return ValueTask.FromResult(InvokeStatic(type, methodName, arguments));
        }

        var candidates = ConstructGenericCandidates(
            MethodsNamed(type, isStatic: true, methodName),
            methodName,
            typeArguments,
            $"'{type.FullName}'");

        return ValueTask.FromResult(Invoke(
            candidates,
            target: null,
            methodName,
            arguments,
            $"static method '{methodName}' on '{type.FullName}'"));
    }

    public object? GetStaticMember(Type type, string memberName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
        var property = type.GetProperty(memberName, flags);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(null);
        }

        var field = type.GetField(memberName, flags);

        if (field is not null)
        {
            return field.GetValue(null);
        }

        throw new InvalidOperationException($"Static member '{memberName}' was not found on type '{type.FullName}'.");
    }

    public InvocationResult InvokeStatic(IShellStaticType type, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.InvokeStaticMethod(methodName, arguments);
    }

    public ValueTask<InvocationResult> InvokeStaticMethodAsync(
        IShellStaticType type,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.InvokeStaticMethodAsync(methodName, arguments, cancellationToken);
    }

    public object CreateInstance(IShellStaticType type, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.CreateInstance(arguments);
    }

    public ValueTask<object> CreateInstanceAsync(
        IShellStaticType type,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.CreateInstanceAsync(arguments, cancellationToken);
    }

    public object? GetStaticMember(IShellStaticType type, string memberName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        if (type.TryGetStaticMember(memberName, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Static member '{memberName}' was not found on type '{type.ShellTypeName}'.");
    }

    private static InvocationResult Invoke(
        IEnumerable<MethodInfo> methods,
        object? target,
        string methodName,
        IReadOnlyList<object?> arguments,
        string description)
    {
        CandidateBinding? bestBinding = null;
        MethodInfo? bestMethod = null;

        // `TS-P2-36`. A generic method definition can never bind — its parameters are still
        // open — so the filter below used to discard `Task.FromResult<TResult>` and report the
        // overload as missing. Constructing it from the argument types first puts it in front
        // of ordinary overload selection, closed, and everything downstream is unchanged.
        var named = methods
            .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var method in named
                     .Where(candidate => !candidate.ContainsGenericParameters)
                     .Concat(InferGenericCandidates(named, arguments)))
        {
            if (!TryBindParameters(method.GetParameters(), arguments, out var binding))
            {
                continue;
            }

            if (bestBinding is null || binding.Score < bestBinding.Value.Score)
            {
                bestBinding = binding;
                bestMethod = method;
            }
        }

        if (bestMethod is null || bestBinding is null)
        {
            // `TS-P2-36`. When the only candidates were generic and inference could not close
            // them, "no overload matched" describes the wrong problem — the method exists and
            // the arguments may be fine; what is missing is the type argument. Naming it says
            // what to supply.
            var uninferable = named
                .Where(static candidate => candidate.IsGenericMethodDefinition)
                .SelectMany(static candidate => candidate.GetGenericArguments())
                .Select(static parameter => parameter.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (uninferable.Length > 0 && named.All(static candidate => candidate.IsGenericMethodDefinition))
            {
                var names = string.Join("', '", uninferable);
                throw new InvalidOperationException(
                    $"Cannot infer type argument '{names}' for {description} from {arguments.Count} argument(s).");
            }

            throw new InvalidOperationException($"No overload matched {description} with {arguments.Count} argument(s).");
        }

        var value = InvokeUnwrapped(() => bestMethod.Invoke(target, bestBinding.Value.BoundArguments));
        return new InvocationResult(value, bestMethod.ReturnType == typeof(void));
    }

    /// <summary>
    /// Invokes through reflection, letting the failure the callee actually raised
    /// escape rather than the wrapper reflection puts around it.
    /// </summary>
    /// <remarks>
    /// `TS-P2-95`. Reflection wraps whatever a target throws in a
    /// <see cref="TargetInvocationException"/>, whose own message is "Exception has
    /// been thrown by the target of an invocation" — true, and useless. That is
    /// what scripts saw for every failing CLR call, with the real cause
    /// unreachable: a `DllNotFoundException` naming the four paths it probed for
    /// `libSkiaSharp` arrived as that sentence and nothing else, and recovering it
    /// took a C# shim. Any interop deeper than one call was undebuggable.
    ///
    /// <see cref="ExceptionDispatchInfo"/> rethrows the inner exception with its
    /// original stack trace intact; a bare <c>throw inner</c> would overwrite the
    /// one place that says where the fault actually happened.
    /// </remarks>
    private static object? InvokeUnwrapped(Func<object?> invoke)
    {
        try
        {
            return invoke();
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(wrapper.InnerException).Throw();
            throw; // Unreachable; the line above always throws.
        }
    }

    /// <summary>
    /// Closes each generic method definition in <paramref name="candidates"/> whose type
    /// parameters can be inferred from <paramref name="arguments"/> — <c>TS-P2-36</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection offers no equivalent of the compiler's inference, so this unifies each
    /// parameter's declared type against the argument's runtime type and collects the bindings.
    /// It is deliberately narrower than C#: there is no lower/upper-bound lattice and no
    /// best-common-type step, so a type parameter appearing twice must be bound to the same type
    /// or a base of it. That covers the shapes the shell actually reaches — <c>FromResult(7)</c>,
    /// <c>Tuple.Create(1, 2)</c>, <c>WhenAll($a, $b)</c> — and declines rather than guesses
    /// elsewhere, which leaves the "no overload matched" message exactly as it was.
    /// </para>
    /// <para>
    /// A method whose type parameters cannot all be bound is skipped, not thrown from: a sibling
    /// non-generic overload may still match, and the caller reports the failure once.
    /// </para>
    /// </remarks>
    private static IEnumerable<MethodInfo> InferGenericCandidates(
        IReadOnlyList<MethodInfo> candidates,
        IReadOnlyList<object?> arguments)
    {
        foreach (var candidate in candidates)
        {
            if (!candidate.IsGenericMethodDefinition) continue;

            var parameters = candidate.GetParameters();
            var bindings = new Dictionary<Type, Type>();

            // A trailing `params` array takes every remaining argument, each unified against the
            // *element* type. Without this `Task.WhenAll($a, $b)` fell through to the
            // non-generic `WhenAll(params Task[])`, which returns a plain `Task` — so it
            // resolved, awaited to nothing, and reported a count of zero rather than failing.
            var isParams = parameters.Length > 0 &&
                           parameters[^1].ParameterType.IsArray &&
                           parameters[^1].IsDefined(typeof(ParamArrayAttribute), inherit: false);

            if (isParams ? arguments.Count < parameters.Length - 1 : parameters.Length != arguments.Count)
            {
                continue;
            }

            var inferred = true;
            var fixedCount = isParams ? parameters.Length - 1 : parameters.Length;

            for (var i = 0; i < fixedCount && inferred; i++)
            {
                // A null argument carries no type to infer from. If the parameter needs no
                // inference the loop simply moves on; if it does, the check below fails.
                if (arguments[i] is null) continue;

                inferred = TryUnify(parameters[i].ParameterType, arguments[i]!.GetType(), bindings);
            }

            if (inferred && isParams)
            {
                var elementType = parameters[^1].ParameterType.GetElementType();

                // One argument that is already the array itself is passed through rather than
                // wrapped, which is how `params` is also called from C#.
                if (arguments.Count == parameters.Length &&
                    arguments[^1] is not null &&
                    parameters[^1].ParameterType.IsInstanceOfType(arguments[^1]))
                {
                    inferred = TryUnify(parameters[^1].ParameterType, arguments[^1]!.GetType(), bindings);
                }
                else
                {
                    for (var i = fixedCount; i < arguments.Count && inferred; i++)
                    {
                        if (arguments[i] is null) continue;

                        inferred = elementType is not null &&
                                   TryUnify(elementType, arguments[i]!.GetType(), bindings);
                    }
                }
            }

            if (!inferred) continue;

            var typeParameters = candidate.GetGenericArguments();
            var resolved = new Type[typeParameters.Length];

            for (var i = 0; i < typeParameters.Length; i++)
            {
                if (!bindings.TryGetValue(typeParameters[i], out var bound))
                {
                    inferred = false;
                    break;
                }

                resolved[i] = bound;
            }

            if (!inferred) continue;

            MethodInfo constructed;
            try
            {
                constructed = candidate.MakeGenericMethod(resolved);
            }
            catch (ArgumentException)
            {
                // A constraint the inferred arguments do not satisfy.
                continue;
            }

            yield return constructed;
        }
    }

    /// <summary>
    /// Matches <paramref name="parameter"/>'s shape against <paramref name="argument"/>,
    /// recording what each type parameter must be — <c>TS-P2-36</c>.
    /// </summary>
    private static bool TryUnify(Type parameter, Type argument, Dictionary<Type, Type> bindings)
    {
        if (parameter.IsGenericParameter)
        {
            if (!bindings.TryGetValue(parameter, out var existing))
            {
                bindings[parameter] = argument;
                return true;
            }

            // Already bound. Widening to a shared base is the compiler's job and is not
            // attempted here; the binding has to hold.
            return existing.IsAssignableFrom(argument);
        }

        if (!parameter.ContainsGenericParameters)
        {
            return true;
        }

        if (parameter.IsArray)
        {
            var element = parameter.GetElementType();
            var argumentElement = argument.IsArray
                ? argument.GetElementType()
                : FindEnumerableElement(argument);

            return element is not null && argumentElement is not null &&
                   TryUnify(element, argumentElement, bindings);
        }

        if (!parameter.IsGenericType) return false;

        var definition = parameter.GetGenericTypeDefinition();

        if (FindConstructed(argument, definition) is not { } constructed) return false;

        var parameterArguments = parameter.GetGenericArguments();
        var constructedArguments = constructed.GetGenericArguments();

        if (parameterArguments.Length != constructedArguments.Length) return false;

        for (var i = 0; i < parameterArguments.Length; i++)
        {
            if (!TryUnify(parameterArguments[i], constructedArguments[i], bindings)) return false;
        }

        return true;
    }

    /// <summary>The form of <paramref name="definition"/> that <paramref name="type"/> is.</summary>
    /// <remarks>
    /// Walks interfaces and base types, so a <c>List&lt;int&gt;</c> argument satisfies an
    /// <c>IEnumerable&lt;T&gt;</c> parameter and binds <c>T</c> to <c>int</c>.
    /// </remarks>
    private static Type? FindConstructed(Type type, Type definition)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == definition)
            {
                return current;
            }
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == definition)
            {
                return contract;
            }
        }

        return null;
    }

    private static Type? FindEnumerableElement(Type type) =>
        FindConstructed(type, typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static bool TryBindParameters(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyList<object?> rawArguments,
        out CandidateBinding binding)
    {
        // Split out named-arg wrappers (`name = value`) so they can be placed
        // by parameter name. Positional args fill the remaining slots in order.
        Dictionary<string, object?>? namedArgs = null;
        List<object?>? positionalOnly = null;
        for (var i = 0; i < rawArguments.Count; i++)
        {
            if (rawArguments[i] is INamedArgument named)
            {
                namedArgs ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                namedArgs[named.Name] = named.Value;
                positionalOnly ??= new List<object?>(rawArguments.Count);
                // copy any earlier positional args we hadn't started buffering
                if (positionalOnly.Count < i)
                {
                    for (var j = 0; j < i; j++)
                    {
                        if (rawArguments[j] is not INamedArgument)
                        {
                            positionalOnly.Add(rawArguments[j]);
                        }
                    }
                }
            }
            else if (positionalOnly is not null)
            {
                positionalOnly.Add(rawArguments[i]);
            }
        }
        var positionalArgs = (IReadOnlyList<object?>?)positionalOnly ?? rawArguments;

        var hasParamArray = parameters.Count > 0 && IsParamArray(parameters[^1]);
        var requiredCount = parameters.Count(parameter =>
            !parameter.IsOptional && !IsParamArray(parameter)
            && (namedArgs is null || !namedArgs.ContainsKey(parameter.Name ?? string.Empty)));

        if (positionalArgs.Count < requiredCount)
        {
            binding = default;
            return false;
        }

        if (!hasParamArray && positionalArgs.Count + (namedArgs?.Count ?? 0) > parameters.Count)
        {
            binding = default;
            return false;
        }

        // Reject named args that don't match any parameter name.
        if (namedArgs is not null)
        {
            foreach (var name in namedArgs.Keys)
            {
                var found = false;
                foreach (var p in parameters)
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    binding = default;
                    return false;
                }
            }
        }

        var boundArguments = new object?[parameters.Count];
        var score = 0;
        var rawIndex = 0;

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];

            if (hasParamArray && index == parameters.Count - 1)
            {
                if (!TryBindParamArray(parameter, positionalArgs, rawIndex, out var paramArrayValue, out var paramArrayScore))
                {
                    binding = default;
                    return false;
                }

                boundArguments[index] = paramArrayValue;
                score += paramArrayScore;
                rawIndex = positionalArgs.Count;
                continue;
            }

            if (namedArgs is not null
                && parameter.Name is not null
                && namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                if (!TryConvertWithScore(namedValue, parameter.ParameterType, out var convertedNamed, out var namedScore))
                {
                    binding = default;
                    return false;
                }
                boundArguments[index] = convertedNamed;
                score += namedScore;
                continue;
            }

            if (rawIndex < positionalArgs.Count)
            {
                if (!TryConvertWithScore(positionalArgs[rawIndex], parameter.ParameterType, out var converted, out var conversionScore))
                {
                    binding = default;
                    return false;
                }

                boundArguments[index] = converted;
                score += conversionScore;
                rawIndex++;
                continue;
            }

            if (!parameter.IsOptional)
            {
                binding = default;
                return false;
            }

            boundArguments[index] = GetDefaultValue(parameter);
            score += 4;
        }

        if (rawIndex != positionalArgs.Count)
        {
            binding = default;
            return false;
        }

        binding = new CandidateBinding(boundArguments, score);
        return true;
    }

    private static bool TryBindParamArray(
        ParameterInfo parameter,
        IReadOnlyList<object?> rawArguments,
        int rawIndex,
        out object? value,
        out int score)
    {
        var parameterType = parameter.ParameterType;
        var elementType = parameterType.GetElementType()
                          ?? throw new InvalidOperationException($"Param-array parameter '{parameter.Name}' is missing an element type.");
        var remainingCount = rawArguments.Count - rawIndex;

        if (remainingCount == 0)
        {
            value = Array.CreateInstance(elementType, 0);
            score = 2;
            return true;
        }

        if (remainingCount == 1 &&
            TryConvertWithScore(rawArguments[rawIndex], parameterType, out var convertedArray, out var directScore))
        {
            value = convertedArray;
            score = directScore + 1;
            return true;
        }

        var array = Array.CreateInstance(elementType, remainingCount);
        score = 3;

        for (var index = 0; index < remainingCount; index++)
        {
            if (!TryConvertWithScore(rawArguments[rawIndex + index], elementType, out var convertedItem, out var itemScore))
            {
                value = null;
                score = 0;
                return false;
            }

            array.SetValue(convertedItem, index);
            score += itemScore;
        }

        value = array;
        return true;
    }

    private static bool TryConvertWithScore(object? value, Type targetType, out object? converted, out int score)
    {
        if (!TypeConversion.TryConvert(value, targetType, out converted))
        {
            score = 0;
            return false;
        }

        if (value is null)
        {
            score = 0;
            return true;
        }

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // `TS-P2-36`. Exact and merely-assignable used to score the same, so a tie between
        // `WhenAll(params Task[])` and the inferred `WhenAll<int>(params Task<int>[])` was
        // broken by candidate order — the non-generic won, returning a plain `Task` that
        // awaited to nothing and reported a count of zero. Ranking exact ahead of base-class
        // is what C# does when it prefers the more specific overload.
        if (effectiveType == value.GetType())
        {
            score = 0;
            return true;
        }

        if (effectiveType.IsInstanceOfType(value))
        {
            score = 1;
            return true;
        }

        score = 2;
        return true;
    }

    private static bool IsParamArray(ParameterInfo parameter)
    {
        return parameter.GetCustomAttribute<ParamArrayAttribute>() is not null;
    }

    private static object? GetDefaultValue(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue && parameter.DefaultValue is not DBNull)
        {
            return parameter.DefaultValue;
        }

        var parameterType = parameter.ParameterType;
        return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
    }

    private readonly record struct CandidateBinding(object?[] BoundArguments, int Score);
}
