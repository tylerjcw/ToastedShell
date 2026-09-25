using System.Globalization;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshClassDefinition
{
    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return InvokeStaticMethodAsync(methodName, arguments, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
    public bool HasStaticMethod(string methodName)
    {
        if (_methodsByName.TryGetValue(methodName, out var candidates) &&
            candidates.Any(method => method.IsStatic))
        {
            return true;
        }

        return BaseClass is not null && BaseClass.HasStaticMethod(methodName);
    }

    public async ValueTask<InvocationResult> InvokeStaticMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? typeArguments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Native bindings are checked before declared methods so a `bind` block
        // member is callable as `SystemInfo.sysinfo()`. They are always static,
        // so enforce the same declaring-class privacy as ordinary static methods
        // here, before dispatch; qualified/type-valued callers share this path.
        if (_nativeMembers.TryGetValue(methodName, out var nativeMember))
        {
            if (nativeMember.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Native method", methodName);
            }

            var nativeValues = await _engine.InvokeNativeMemberAsync(
                nativeMember.Command, arguments, cancellationToken);

            return new InvocationResult(FlattenMethodCallResult(nativeValues), ReturnedVoid: false);
        }

        // Statics are inherited, so a name this class does not declare is asked of its base —
        // the same walk every instance lookup already performed. The base answers for its own
        // members, which keeps the storage and the `shy` rule with the class that declared them
        // rather than copying either into the derived class.
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            if (BaseClass is not null)
            {
                return await BaseClass.InvokeStaticMethodAsync(methodName, arguments, cancellationToken, typeArguments);
            }

            // `TS-P2-93`, static side: a `shared prop` holding a callable answers
            // `C.Fn(9)` for the same reason the instance one does.
            if (TryGetStaticMember(methodName, out var heldStatic) &&
                heldStatic is IShellCallable staticCallable)
            {
                return await _engine.InvokeHeldCallableAsync(
                    staticCallable,
                    arguments,
                    cancellationToken);
            }

            throw new InvalidOperationException($"Static method '{methodName}' was not found on class '{Name}'.");
        }

        // `TS-P2-103`. `CanSeeShyStatic` answers "is this class's own code asking?"
        // and was already consulted for nested types and for static properties —
        // but not here, so a `shy shared func` was unreachable from the class that
        // declared it. A hermit class has no instance and therefore no `$this` to
        // carry the accessor, so the qualified name is the *only* spelling
        // available, and refusing it meant every helper in a hermit class had to be
        // public.
        var staticCandidates = candidates
            .Where(candidate => candidate.IsStatic && (!candidate.IsShy || CanSeeShyStatic()))
            .ToArray();

        if (staticCandidates.Length == 0)
        {
            // The name is declared here but not as a callable static — an instance method, or one
            // hidden by `shy`. A base may still offer it statically, so ask before refusing.
            if (BaseClass is not null)
            {
                return await BaseClass.InvokeStaticMethodAsync(methodName, arguments, cancellationToken, typeArguments);
            }

            // Said apart, because a `shy static` reported "is an instance method" — a description
            // of neither what was declared nor why the call was refused.
            if (candidates.Any(candidate => candidate.IsStatic && candidate.IsShy))
            {
                throw new InvalidOperationException(
                    $"Static method '{methodName}' on class '{Name}' is shy and cannot be called from outside the class.");
            }

            throw new InvalidOperationException($"'{methodName}' is an instance method on class '{Name}' and cannot be called statically. Create an instance first: var obj = new {Name}(); $obj.{methodName}(...)");
        }

        var (method, locals) = await SelectMethodAsync(
            staticCandidates,
            arguments,
            cancellationToken);
        // `TOAST-0118`. A static method's own type arguments were dropped here, so
        // `Point2D.Empty<int>()` had nothing to bind `T` from — and, worse,
        // `A.WithArg<double>(1)` silently used what inference made of the argument
        // instead of what the call site asked for.
        var values = await ExecuteMethodBlockAsync(
            method, locals, instance: null, cancellationToken, typeArguments);
        return new InvocationResult(FlattenMethodCallResult(values), ReturnedVoid: false);
    }

    internal InvocationResult InvokeInstanceMethod(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments,
        bool includeHidden,
        ToshClassDefinition? accessor)
    {
        return InvokeInstanceMethodAsync(
                instance,
                methodName,
                arguments,
                includeHidden,
                accessor,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    internal async ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments,
        bool includeHidden,
        ToshClassDefinition? accessor,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? explicitTypeArguments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The overload set is gathered across the whole chain rather than from this class alone.
        // Resolving against only the nearest class that happened to declare the name meant a
        // subclass adding one overload hid every inherited one: with `f(a: int)` on the base and
        // `f(a: string)` on the subclass, `$d.f(1)` bound the string overload by coercion, and
        // an inherited overload of another arity could not be called at all.
        var candidateSet = CollectInstanceMethodCandidates(methodName, includeHidden, accessor);

        if (candidateSet.Count == 0)
        {
            // Allow calling the constructor by class name (e.g. $super.BaseClass(args))
            if (string.Equals(methodName, Name, StringComparison.OrdinalIgnoreCase))
            {
                await ConstructOnInstanceAsync(instance, arguments, cancellationToken);
                return new InvocationResult(null, ReturnedVoid: true);
            }

            if (BaseClass is not null)
            {
                return await BaseClass.InvokeInstanceMethodAsync(
                    instance,
                    methodName,
                    arguments,
                    includeHidden,
                    accessor,
                    cancellationToken,
                    explicitTypeArguments);
            }

            if (ClrBaseType is not null && instance.ClrBaseObject is not null)
            {
                return await _engine.LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
                    instance.ClrBaseObject,
                    methodName,
                    arguments,
                    cancellationToken);
            }

            // `TS-P2-93`. A property may *hold* a callable, and `$obj.Fn(9)` is how
            // that reads in any other language. The value is already invocable —
            // assigning it to a variable and calling that has always worked — so
            // refusing here made the property spelling the one that needed a
            // temporary. Checked only after every method candidate has failed, so a
            // real method of the same name still wins.
            var heldCallable = await TryGetInstanceMemberAsync(
                instance,
                methodName,
                includeHidden,
                accessor,
                cancellationToken);

            if (heldCallable is { Found: true, Value: IShellCallable propertyCallable })
            {
                return await _engine.InvokeHeldCallableAsync(
                    propertyCallable,
                    arguments,
                    cancellationToken);
            }

            // `extend` methods, last of all: a class's own members — inherited ones
            // and a callable held in a property included — always win, so an
            // extension can only ever add a name the class did not have
            // (`TS-P3-27`).
            if (await _engine.TryInvokeExtensionAsync(instance, methodName, arguments, cancellationToken) is { } extension)
            {
                return extension;
            }

            throw new InvalidOperationException($"Method '{methodName}' was not found on class '{Name}'.");
        }

        var (method, locals) = await SelectMethodAsync(
            candidateSet.Select(entry => entry.Method).ToArray(),
            arguments,
            cancellationToken,
            instance);

        // The winner runs in the class that declared it, not in the one the call arrived at. That
        // is what gives an inherited method the `$this`/`$super` bindings of its own class — a
        // base method executed from the subclass's definition would be handed the subclass's
        // base as its `$super`, skipping a level of the chain.
        var owner = candidateSet.First(entry => ReferenceEquals(entry.Method, method)).Owner;

        // Emit deprecation warning for fading methods
        if (method.IsFading)
        {
            _engine.WriteWarning(
                code: "tosh.runtime.fading_member",
                title: $"Method '{method.Name}' on class '{owner.Name}' is fading (deprecated).",
                help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
        }

        var values = await owner.ExecuteMethodBlockAsync(method, locals, instance, cancellationToken, explicitTypeArguments);
        return new InvocationResult(FlattenMethodCallResult(values), ReturnedVoid: false);
    }

    /// <summary>
    /// The instance overload set for <paramref name="methodName"/> across this class and its
    /// bases, each method paired with the class that declared it. A nearer class replaces a base
    /// method of the same signature — that is what <c>overrule</c> means — while one with a
    /// different signature joins the set instead of hiding it.
    /// </summary>
    private List<(ToshClassMethodDefinition Method, ToshClassDefinition Owner)> CollectInstanceMethodCandidates(
        string methodName,
        bool includeHidden,
        ToshClassDefinition? accessor)
    {
        var collected = new List<(ToshClassMethodDefinition Method, ToshClassDefinition Owner)>();

        for (var current = this; current is not null; current = current.BaseClass)
        {
            if (!current._methodsByName.TryGetValue(methodName, out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.IsStatic)
                {
                    continue;
                }

                // Same rule the properties use, so a `shy` method is private to the class that
                // declared it and a `guarded` one reaches down the chain.
                if ((candidate.IsShy && !CanSeeShy(current, includeHidden, accessor)) ||
                    (candidate.IsGuarded && !CanSeeGuarded(current, includeHidden, accessor)) ||
                    (candidate.IsLocal && !includeHidden))
                {
                    continue;
                }

                if (collected.Any(existing => SignaturesCollide(existing.Method.Parameters, candidate.Parameters)))
                {
                    continue;
                }

                collected.Add((candidate, current));
            }
        }

        return collected;
    }

    internal IEnumerable<object?> EnumerateItems(ToshClassInstance instance)
    {
        if (TryInvokeEnumerator(instance, out var value))
        {
            if (value is null)
            {
                yield break;
            }

            foreach (var item in ShellIterationUtilities.ExpandCollectionLikeValue(value))
            {
                yield return item;
            }

            yield break;
        }

        yield return instance;
    }

    /// <summary>
    /// Whether this class defines any of <see cref="EnumeratorMethodNames"/>, and can therefore
    /// be iterated.
    /// </summary>
    /// <remarks>
    /// This was a *third* copy of the name list, spelled out separately from the two dispatch
    /// paths. It is the reason the list was worth extracting at all: with three copies, the odds
    /// that a new spelling reaches all of them are poor, and the symptom would be a class that
    /// reports itself iterable and then is not — or the reverse.
    /// </remarks>
    internal bool HasEnumerator =>
        EnumeratorMethodNames.Any(name => HasSpecialInstanceMethod(name, Array.Empty<object?>()));

    internal async IAsyncEnumerable<object?> EnumerateItemsAsync(
        ToshClassInstance instance,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var enumeration = await TryInvokeEnumeratorAsync(instance, cancellationToken);
        if (enumeration.Matched)
        {
            if (enumeration.Value is null)
            {
                yield break;
            }

            foreach (var item in ShellIterationUtilities.ExpandCollectionLikeValue(enumeration.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        yield return instance;
    }

    internal bool HasSpecialInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return TrySelectSpecialInstanceMethod(methodName, arguments, out _, out _);
    }

    internal bool TryInvokeSpecialInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;

        if (!TrySelectSpecialInstanceMethod(methodName, arguments, out var method, out var locals, instance))
        {
            return false;
        }

        var values = ExecuteMethodBlock(method, locals, instance);
        value = FlattenMethodCallResult(values);
        return true;
    }

    internal async ValueTask<(bool Matched, object? Value)> TryInvokeSpecialInstanceMethodAsync(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selection = await TrySelectSpecialInstanceMethodAsync(
            methodName,
            arguments,
            cancellationToken,
            instance);
        if (!selection.Matched)
        {
            return (false, null);
        }

        var values = await ExecuteMethodBlockAsync(
            selection.Method!,
            selection.Locals!,
            instance,
            cancellationToken);
        return (true, FlattenMethodCallResult(values));
    }

    internal object? InvokeInstanceMethodOnThisThread(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments)
        => _engine.InvokeCallableOnThisThread(
            new ToshBoundMethodReference(instance, methodName, _engine.LanguageRuntime.Invoker, _engine),
            arguments);

    private IReadOnlyList<object?> ExecuteMethodBlock(ToshClassMethodDefinition method, IReadOnlyDictionary<string, object?> boundLocals, ToshClassInstance? instance)
    {
        return ExecuteMethodBlockAsync(method, boundLocals, instance, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private async ValueTask<IReadOnlyList<object?>> ExecuteMethodBlockAsync(
        ToshClassMethodDefinition method,
        IReadOnlyDictionary<string, object?> boundLocals,
        ToshClassInstance? instance,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? explicitTypeArguments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Code belonging to this class names its nested types without qualifying them.
        using var executingClass = _engine.EnterClass(this);
        using var nestedTypeScope = _engine.PushNestedTypeScope(this);

        // Phase 3.4 — method-level generic inference.
        // For methods that declare their own type parameters
        // (`func describe<U>(label: U) -> U`), unify each argument
        // value against the parameter's raw annotation to populate a
        // method-scoped binding table. These bindings are merged with
        // any class-level bindings carried by the instance so that
        // `class Box<T>` + `func map<U>(transform)` both resolve.
        // `TOAST-0124`. A widening replaces the value, so the bound locals have to be
        // writable from here on. The dictionary handed in is one; the cast keeps its
        // comparer rather than guessing at a new one, and the copy is only a fallback.
        var writableLocals = boundLocals as Dictionary<string, object?>
            ?? new Dictionary<string, object?>(boundLocals);

        Dictionary<string, Type>? methodBindings = null;
        if (method.TypeParameters is { Count: > 0 })
        {
            var argumentValues = new object?[method.Parameters.Count];
            var argumentSpans = new TextSpan[method.Parameters.Count];
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var parameter = method.Parameters[i];
                argumentSpans[i] = parameter.Span;
                boundLocals.TryGetValue(parameter.Name, out var v);
                argumentValues[i] = v;
            }

            var syntheticInvocation = new CommandInvocation(
                SourceName: method.SourceName,
                SourceText: method.SourceText,
                CommandName: $"{Name}.{method.Name}",
                CommandSpan: method.Span,
                ArgumentSpans: argumentSpans);
            var syntheticContext = new CommandContext(
                LanguageRuntime: _engine.LanguageRuntime,
                Input: System.Linq.AsyncEnumerable.Empty<object?>(),
                Arguments: argumentValues,
                CancellationToken: cancellationToken,
                Invocation: syntheticInvocation);

            methodBindings = _engine.InferMethodTypeBindings(
                method,
                argumentValues,
                syntheticContext,
                ownerLabel: $"{Name}.{method.Name}");
        }

        // Type arguments written at the call site are authoritative: they replace whatever
        // inference produced, rather than being merged with it. Asking for `<int>` and getting
        // the inferred binding instead is the failure this feature exists to remove.
        if (explicitTypeArguments is { Count: > 0 } && method.TypeParameters is { Count: > 0 } declared)
        {
            if (explicitTypeArguments.Count != declared.Count)
            {
                throw new InvalidOperationException(
                    $"Method '{Name}.{method.Name}' declares {declared.Count} type parameter(s) "
                    + $"but was given {explicitTypeArguments.Count}.");
            }

            methodBindings = new Dictionary<string, Type>(StringComparer.Ordinal);
            for (var index = 0; index < declared.Count; index++)
            {
                methodBindings[declared[index]] = explicitTypeArguments[index];
            }
        }
        else if (explicitTypeArguments is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Method '{Name}.{method.Name}' is not generic and takes no type arguments.");
        }

        // For generic instance methods, validate any parameters whose original
        // (un-erased) annotation references a class type-parameter and
        // substitute the instance's binding before running the body.
        if (instance is not null && !method.IsStatic)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is { Count: > 0 } || methodBindings is { Count: > 0 })
            {
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.RawTypeName is null) continue;
                    Type? bound = null;

                    // `TOAST-0118`. The method's own type parameters first: `methodBindings`
                    // holds only names the method declared, so a hit there means the method
                    // shadows the class, which is what C# does with the same spelling. The
                    // other order converted `func Shadowed<T>(v: T)` inside `class A<T>` to
                    // the *class's* T and rejected the argument the caller actually passed.
                    if (methodBindings is not null && methodBindings.TryGetValue(parameter.RawTypeName, out var mBound)) bound = mBound;
                    if (bound is null && bindings is not null && bindings.TryGetValue(parameter.RawTypeName, out var classBound)) bound = classBound;

                    if (bound is null)
                    {
                        // Bound nominally rather than to a CLR type — `TOAST-0125`.
                        if (boundLocals.TryGetValue(parameter.Name, out var nominalValue))
                        {
                            EnforceNominalBinding(
                                instance,
                                parameter.RawTypeName,
                                nominalValue,
                                parameter.Span,
                                method.SourceName,
                                method.SourceText,
                                $"{Name}.{method.Name}.{parameter.Name}");
                        }

                        continue;
                    }
                    if (!boundLocals.TryGetValue(parameter.Name, out var value)) continue;

                    if (parameter.IsRest && value is System.Collections.IList list)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            list[i] = CoerceStrictBinding(
                                bound,
                                list[i],
                                parameter.Span,
                                method.SourceName,
                                method.SourceText,
                                $"{Name}.{method.Name}.{parameter.Name}");
                        }
                    }
                    else
                    {
                        writableLocals[parameter.Name] = CoerceStrictBinding(
                            bound,
                            value,
                            parameter.Span,
                            method.SourceName,
                            method.SourceText,
                            $"{Name}.{method.Name}.{parameter.Name}");
                    }
                }
            }
        }
        else if (methodBindings is { Count: > 0 })
        {
            // Static method (or no instance) — apply method-level bindings only.
            foreach (var parameter in method.Parameters)
            {
                if (parameter.RawTypeName is null) continue;
                if (!methodBindings.TryGetValue(parameter.RawTypeName, out var bound) || bound is null) continue;
                if (!boundLocals.TryGetValue(parameter.Name, out var value)) continue;
                if (parameter.IsRest && value is System.Collections.IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        list[i] = CoerceStrictBinding(bound, list[i], parameter.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}.{parameter.Name}");
                    }
                }
                else
                {
                    writableLocals[parameter.Name] = CoerceStrictBinding(bound, value, parameter.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}.{parameter.Name}");
                }
            }
        }

        var locals = CreateLocals(instance, writableLocals);
        var values = await _engine.ExecuteClassBlockAsync(
            this,
            method.SourceName,
            method.SourceText,
            method.Body,
            locals,
            method.CapturedScopes,
            $"{Name}.{method.Name}",
            cancellationToken,
            methodBindings);

        // Resolve the effective return-type annotation: prefer the un-erased
        // RawReturnTypeName when it names a bound class type-parameter,
        // otherwise fall through to the (possibly-erased) ReturnTypeName.
        // When the return type is bound from a type-parameter we apply a
        // strict no-coercion check; otherwise we go through the engine's
        // standard annotated-value conversion path.
        Type? strictReturnBinding = null;
        string? effectiveReturnType = method.ReturnTypeName;
        if (method.RawReturnTypeName is not null)
        {
            // Prefer instance (class-level) binding, then method-level.
            if (instance is not null)
            {
                var bindings = instance.GetBindingsFor(this);
                if (bindings is not null && bindings.TryGetValue(method.RawReturnTypeName, out var bound))
                {
                    if (bound is null)
                    {
                        effectiveReturnType = null;
                    }
                    else
                    {
                        strictReturnBinding = bound;
                        effectiveReturnType = bound.FullName ?? bound.Name;
                    }
                }
            }
            if (strictReturnBinding is null && effectiveReturnType is not null && methodBindings is not null
                && methodBindings.TryGetValue(method.RawReturnTypeName, out var mBound) && mBound is not null)
            {
                strictReturnBinding = mBound;
                effectiveReturnType = mBound.FullName ?? mBound.Name;
            }
        }

        if (strictReturnBinding is not null)
        {
            var unwrapped = UnwrapValues(values).ToArray();
            for (var i = 0; i < unwrapped.Length; i++)
            {
                unwrapped[i] = CoerceStrictBinding(
                    strictReturnBinding,
                    unwrapped[i],
                    method.Span,
                    method.SourceName,
                    method.SourceText,
                    $"{Name}.{method.Name}");
            }
            return unwrapped;
        }

        var returnValues = UnwrapValues(values);
        // A return annotation is written inside the class body, so it resolves
        // against the module that body lives in — not against wherever the call
        // happens to be made from.
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        if (effectiveReturnType is null)
        {
            return returnValues;
        }

        var convertedReturnValues = new object?[returnValues.Count];
        for (var index = 0; index < returnValues.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            convertedReturnValues[index] = await _engine.ConvertAnnotatedValueAsync(
                effectiveReturnType,
                returnValues[index],
                method.Span,
                method.SourceName,
                method.SourceText,
                $"{Name}.{method.Name}",
                cancellationToken);
        }

        return convertedReturnValues;
    }

    private static readonly IReadOnlyDictionary<Type, Type[]> ImplicitNumericWidenings =
        new Dictionary<Type, Type[]>
        {
            [typeof(sbyte)]  = [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)],
            [typeof(byte)]   = [typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)],
            [typeof(short)]  = [typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)],
            [typeof(ushort)] = [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)],
            [typeof(int)]    = [typeof(long), typeof(float), typeof(double), typeof(decimal)],
            [typeof(uint)]   = [typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)],
            [typeof(long)]   = [typeof(float), typeof(double), typeof(decimal)],
            [typeof(ulong)]  = [typeof(float), typeof(double), typeof(decimal)],
            [typeof(char)]   = [typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal)],
            [typeof(float)]  = [typeof(double)],
        };

    /// <summary>
    /// Widens <paramref name="value"/> to <paramref name="target"/> when C# would do it
    /// implicitly — <c>TOAST-0124</c>.
    /// </summary>
    /// <summary>
    /// The first parameter annotation among <paramref name="candidates"/> that names
    /// nothing resolvable, described for a diagnostic — or null when every annotation
    /// resolves and the mismatch really is about arity or values (`TOAST-0122`).
    /// </summary>
    /// <remarks>
    /// Only candidates whose arity actually admits the call are examined. A three-
    /// parameter overload is not evidence of anything when one argument was passed, and
    /// naming its types would send the reader somewhere irrelevant.
    /// </remarks>
    private string? DescribeUnresolvableParameterType(
        IReadOnlyList<ToshClassMethodDefinition> candidates,
        int argumentCount)
    {
        foreach (var candidate in candidates)
        {
            var required = candidate.Parameters.Count(p => !p.IsOptional && !p.IsRest);
            var accepts = argumentCount >= required &&
                          (argumentCount <= candidate.Parameters.Count ||
                           candidate.Parameters.Any(p => p.IsRest));

            if (!accepts)
            {
                continue;
            }

            foreach (var parameter in candidate.Parameters)
            {
                if (parameter.TypeName is { Length: > 0 } annotated &&
                    !_engine.IsAnnotatedTypeKnown(annotated))
                {
                    return $"parameter '{parameter.Name}' is annotated '{annotated}', "
                        + "which does not name a type that is visible here.";
                }
            }
        }

        return null;
    }

    private static bool TryWidenImplicitly(object value, Type target, out object? widened)
    {
        widened = null;

        var effective = Nullable.GetUnderlyingType(target) ?? target;

        if (!ImplicitNumericWidenings.TryGetValue(value.GetType(), out var permitted) ||
            Array.IndexOf(permitted, effective) < 0)
        {
            return false;
        }

        widened = Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
        return true;
    }

    private async ValueTask<(
        ToshClassMethodDefinition Method,
        Dictionary<string, object?> Locals)> SelectMethodAsync(
        IReadOnlyList<ToshClassMethodDefinition> candidates,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        ToshClassInstance? instance = null)
    {
        // `TOAST-0122`, as in `SelectMethodAsync`: a parameter annotation resolves in the
        // module its declaration was written in.
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        var matches = await _engine.SelectBestCallableMatchesAsync(
            candidates,
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            var methodDisplayName = candidates.Count > 0
                ? $"'{Name}.{candidates[0].Name}'"
                : $"'{Name}'";

            // `TOAST-0122`. Before blaming the arity, say so when it is a parameter's
            // *type* that could not be resolved. The count may be perfectly right, and
            // "no overload matched with 1 argument(s)" sends the reader to count
            // arguments for a problem about names. The annotation scope entered above is
            // still open, so this asks the same question the matcher just asked.
            if (DescribeUnresolvableParameterType(candidates, arguments.Count) is { } unresolvable)
            {
                throw new InvalidOperationException(
                    $"No overload matched {methodDisplayName}: {unresolvable}");
            }

            throw new InvalidOperationException(
                $"No overload matched {methodDisplayName} with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var methodDisplayName = candidates.Count > 0
                ? $"{Name}.{candidates[0].Name}"
                : Name;
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched method '{methodDisplayName}' with {arguments.Count} argument(s): {signatures}.");
        }

        var winner = matches[0].Candidate;
        var locals = matches[0].Locals;
        await _engine.ApplyPendingParameterDefaultsAsync(
            winner.Parameters,
            locals,
            matches[0].PendingDefaults,
            winner.SourceName,
            winner.SourceText,
            winner.CapturedScopes,
            $"{Name}.{winner.Name}",
            cancellationToken,
            ambient: CreateSelfBindings(instance));
        return (winner, locals);
    }

    /// <summary>
    /// The method names a class may define to make itself iterable, in precedence order.
    /// </summary>
    /// <remarks>
    /// The list existed once per surface. Adding a third recognised spelling would have made a
    /// class iterable in scripts but not at the prompt, or the reverse, with nothing to catch it
    /// (<c>TS-P1-24</c>).
    /// </remarks>
    private static readonly string[] EnumeratorMethodNames = ["enumerate", "GetEnumerator"];

    private bool TryInvokeEnumerator(ToshClassInstance instance, out object? value)
    {
        foreach (var methodName in EnumeratorMethodNames)
        {
            if (!TryInvokeSpecialInstanceMethod(instance, methodName, Array.Empty<object?>(), out value))
            {
                continue;
            }

            return true;
        }

        value = null;
        return false;
    }

    private async ValueTask<(bool Matched, object? Value)> TryInvokeEnumeratorAsync(
        ToshClassInstance instance,
        CancellationToken cancellationToken)
    {
        foreach (var methodName in EnumeratorMethodNames)
        {
            var invocation = await TryInvokeSpecialInstanceMethodAsync(
                instance,
                methodName,
                Array.Empty<object?>(),
                cancellationToken);
            if (invocation.Matched)
            {
                return invocation;
            }
        }

        return (false, null);
    }

    /// <summary>
    /// The instance methods that could serve a special-method call of this name, or
    /// <see langword="null"/> when the class declares none and the base class should be tried.
    /// </summary>
    /// <remarks>
    /// The <c>!IsStatic</c> filter is the whole rule, and it was applied once per surface. A
    /// static method leaking into instance dispatch on one surface only is the kind of difference
    /// that shows up as "it works in a script but not at the prompt" (<c>TS-P1-24</c>).
    /// </remarks>
    private IEnumerable<ToshClassMethodDefinition>? GetSpecialInstanceMethodCandidates(string methodName)
        => _methodsByName.TryGetValue(methodName, out var candidates)
            ? candidates.Where(candidate => !candidate.IsStatic)
            : null;

    /// <summary>
    /// The error raised when more than one special-method overload matches, listing the
    /// signatures that tied.
    /// </summary>
    /// <remarks>
    /// Written out word for word on each surface before this, down to the phrasing of
    /// "Multiple overloads matched special method". Sharing it also shares
    /// <see cref="FormatMethodSignature"/>, so the two surfaces cannot come to describe the same
    /// tie differently.
    /// </remarks>
    private InvalidOperationException AmbiguousSpecialMethod(
        string methodName,
        int argumentCount,
        IEnumerable<ToshClassMethodDefinition> tied)
    {
        var signatures = string.Join("; ", tied.Select(FormatMethodSignature));

        return new InvalidOperationException(
            $"Multiple overloads matched special method '{Name}.{methodName}' with "
            + $"{argumentCount} argument(s): {signatures}.");
    }

    private bool TrySelectSpecialInstanceMethod(
        string methodName,
        IReadOnlyList<object?> arguments,
        out ToshClassMethodDefinition method,
        out Dictionary<string, object?> locals,
        ToshClassInstance? instance = null)
    {
        if (GetSpecialInstanceMethodCandidates(methodName) is not { } candidates)
        {
            if (BaseClass is not null)
            {
                return BaseClass.TrySelectSpecialInstanceMethod(methodName, arguments, out method, out locals, instance);
            }

            method = null!;
            locals = null!;
            return false;
        }

        var matches = _engine.SelectBestCallableMatches(
            candidates,
            static candidate => candidate.Parameters,
            arguments);

        if (matches.Count == 0)
        {
            method = null!;
            locals = null!;
            return false;
        }

        if (matches.Count > 1)
        {
            throw AmbiguousSpecialMethod(
                methodName,
                arguments.Count,
                matches.Select(match => match.Candidate));
        }

        method = matches[0].Candidate;
        locals = matches[0].Locals;
        _engine.ApplyPendingParameterDefaults(
            method.Parameters,
            locals,
            matches[0].PendingDefaults,
            method.SourceName,
            method.SourceText,
            method.CapturedScopes,
            $"{Name}.{method.Name}",
            ambient: CreateSelfBindings(instance));
        return true;
    }

    private async ValueTask<(
        bool Matched,
        ToshClassMethodDefinition? Method,
        Dictionary<string, object?>? Locals)> TrySelectSpecialInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        ToshClassInstance? instance = null)
    {
        if (GetSpecialInstanceMethodCandidates(methodName) is not { } candidates)
        {
            if (BaseClass is not null)
            {
                return await BaseClass.TrySelectSpecialInstanceMethodAsync(
                    methodName,
                    arguments,
                    cancellationToken,
                    instance);
            }

            return (false, null, null);
        }

        // `TOAST-0122`, as in `SelectMethodAsync`: a parameter annotation resolves in the
        // module its declaration was written in.
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        var matches = await _engine.SelectBestCallableMatchesAsync(
            candidates,
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            return (false, null, null);
        }

        if (matches.Count > 1)
        {
            throw AmbiguousSpecialMethod(
                methodName,
                arguments.Count,
                matches.Select(match => match.Candidate));
        }

        var winner = matches[0].Candidate;
        var locals = matches[0].Locals;
        await _engine.ApplyPendingParameterDefaultsAsync(
            winner.Parameters,
            locals,
            matches[0].PendingDefaults,
            winner.SourceName,
            winner.SourceText,
            winner.CapturedScopes,
            $"{Name}.{winner.Name}",
            cancellationToken,
            ambient: CreateSelfBindings(instance));
        return (true, winner, locals);
    }

    private static IReadOnlyList<object?> UnwrapValues(IReadOnlyList<object?> values)
    {
        return values
            .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
            .ToArray();
    }

    private static object? FlattenCallResult(IReadOnlyList<object?> values)
    {
        var unwrapped = UnwrapValues(values);
        return unwrapped.Count switch
        {
            0 => null,
            1 => unwrapped[0],
            _ => unwrapped.ToArray(),
        };
    }

    /// <summary>
    /// As <see cref="FlattenCallResult"/>, but says so when the body produced nothing —
    /// <c>TOAST-0123</c>.
    /// </summary>
    /// <remarks>
    /// Only the *method* paths use this. A property getter that yields nothing is a
    /// property with no value, which is null and reads correctly as null; a method that
    /// yields nothing is a method that produced no items, and a pipeline asking it for
    /// items should receive none rather than one null.
    /// </remarks>
    private static object? FlattenMethodCallResult(IReadOnlyList<object?> values)
    {
        var unwrapped = UnwrapValues(values);
        return unwrapped.Count switch
        {
            0 => ToshEmptyCallResult.Instance,
            1 => unwrapped[0],
            _ => unwrapped.ToArray(),
        };
    }

    private string FormatMethodSignature(ToshClassMethodDefinition method)
    {
        var modifier = method.IsStatic ? "static " : string.Empty;
        return $"{modifier}{GetAnnotationDisplayName(method.ReturnTypeName)} {method.Name}({FormatParameters(method.Parameters)})";
    }

    private string FormatConstructorSignature(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return $"{Name}({FormatParameters(parameters)})";
    }

    private static string FormatParameters(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var suffix = parameter.IsOptional ? "?" : string.Empty;
                var rest = parameter.IsRest ? "..." : string.Empty;
                return parameter.TypeName is { Length: > 0 }
                    ? $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}"
                    : $"{parameter.Name}{suffix}{rest}";
            }));
    }

    private static string GetAnnotationDisplayName(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName) ? typeof(object).FullName ?? typeof(object).Name : typeName;
    }
    /// <summary>What the value was, for a message about what it could not become.</summary>
    /// <remarks>
    /// The message used to say only "a value", which is the one thing the reader already knows.
    /// Two adjacent fields annotated `System.DateOnly` and `System.TimeOnly`, constructed in the
    /// wrong order, reported that a value could not become a `DateOnly` — true, and silent about
    /// the `TimeOnly` that would have named the mistake at a glance. The truncation branch beside
    /// this has always shown the value; for a conversion failure the *type* is the part that
    /// explains it.
    /// </remarks>

}
