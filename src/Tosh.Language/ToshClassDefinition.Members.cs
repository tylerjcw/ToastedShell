using System.Globalization;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshClassDefinition
{
    /// <summary>
    /// Types declared inside this class body, keyed by their own name.
    /// </summary>
    /// <remarks>
    /// A nested type is reached as a static member of the class that declares it, so
    /// <c>Reactor.Fuel</c> resolves through the ordinary static lookup and
    /// <c>Reactor.Fuel.Mox</c> follows by member access on the type it returns. Nothing about
    /// the nested type itself differs from an outer one; only where its name lives.
    /// </remarks>
    private readonly Dictionary<string, (IShellNamedType Type, bool IsShy)> _nestedTypes =
        new(StringComparer.OrdinalIgnoreCase);

    internal void SetNestedType(string name, IShellNamedType type, bool isShy)
        => _nestedTypes[name] = (type, isShy);

    /// <summary>
    /// Every nested type visible to code inside this class, including those inherited, keyed by
    /// name. A nearer declaration wins, which is the same rule members follow.
    /// </summary>
    internal IReadOnlyList<(string Name, IShellNamedType Type)> NestedTypesForScope()
    {
        List<(string, IShellNamedType)>? collected = null;
        HashSet<string>? seen = null;

        for (var current = this; current is not null; current = current.BaseClass)
        {
            foreach (var (name, entry) in current._nestedTypes)
            {
                seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!seen.Add(name))
                {
                    continue;
                }

                (collected ??= []).Add((name, entry.Type));
            }
        }

        return collected ?? (IReadOnlyList<(string, IShellNamedType)>)Array.Empty<(string, IShellNamedType)>();
    }

    internal bool TryGetNestedType(string name, out IShellNamedType type)
    {
        for (var current = this; current is not null; current = current.BaseClass)
        {
            if (current._nestedTypes.TryGetValue(name, out var entry))
            {
                type = entry.Type;
                return true;
            }
        }

        type = null!;
        return false;
    }

    internal void SetNativeMember(IShellCommand command, bool isShy)
    {
        _nativeMembers[command.Name] = (command, isShy);
    }

    internal bool TryGetNativeMember(string memberName, out IShellCommand command)
    {
        if (_nativeMembers.TryGetValue(memberName, out var entry))
        {
            if (entry.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Native method", memberName);
            }

            command = entry.Command;
            return true;
        }

        command = null!;
        return false;
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;

        // A nested type is a static member of its declaring class. `shy` hides it from outside.
        if (_nestedTypes.TryGetValue(memberName, out var nested))
        {
            if (nested.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Type", memberName);
            }

            value = nested.Type;
            return true;
        }

        // Check static properties
        if (_propertiesByName.TryGetValue(memberName, out var property) && property.IsStatic)
        {
            // `TS-P2-61`. `shy` was honoured for a nested type and ignored for a property, so
            // `class B { shy static prop S = 1 }` then `B.S` answered 1 from anywhere. One
            // modifier cannot mean two things depending on which kind of static member wears it.
            if (property.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Static property", memberName);
            }

            // A computed static property has to be *evaluated*; it has no stored
            // value and never will. Static properties were only ever initialized —
            // both initialization sites read `IsStatic && Initializer is not null &&
            // !IsComputed` — so a computed one had no `_staticValues` entry and fell
            // to the null default below, silently. `static prop Y => 7` answered
            // null, and so did an accessor-block form, with no diagnostic at all.
            if (property.IsComputed && property.GetterBody is not null)
            {
                value = EvaluateStaticPropertyGetter(property);
                return true;
            }

            if (_staticValues.TryGetValue(memberName, out var stored))
            {
                value = stored;
                return true;
            }

            return true; // null default
        }

        if (_methodsByName.TryGetValue(memberName, out var candidates))
        {
            var isStatic = candidates.Any(c => c.IsStatic);
            var hint = isStatic
                ? $"'{memberName}' is a method on class '{Name}'. Call it with parentheses: {Name}.{memberName}(...)"
                : $"'{memberName}' is an instance method on class '{Name}'. Create an instance first: var obj = new {Name}(); $obj.{memberName}(...)";
            throw new InvalidOperationException(hint);
        }

        // Same courtesy for native bindings — without this the diagnostic was a
        // bare "not found", which reads as if the binding had failed.
        if (_nativeMembers.TryGetValue(memberName, out var native))
        {
            if (native.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Native method", memberName);
            }

            throw new InvalidOperationException(
                $"'{memberName}' is a native binding on class '{Name}'. Call it with parentheses: {native.Command.Usage}");
        }

        // Nothing by that name here, so the base is asked — a static property declared on a base
        // is readable through a derived class, and reading it through the declaring class is what
        // keeps one shared value rather than a copy per subclass.
        return BaseClass is not null && BaseClass.TryGetStaticMember(memberName, out value);
    }

    public bool TrySetStaticMember(string memberName, object? value)
    {
        if (_propertiesByName.TryGetValue(memberName, out var property) && property.IsStatic)
        {
            // Writes answer to `shy` exactly as reads do. Enforcing one and not the other would
            // be a worse asymmetry than the leak this closes.
            if (property.IsShy && !CanSeeShyStatic())
            {
                throw ShyStaticMemberIsHidden("Static property", memberName);
            }

            // The same three routes an instance assignment takes, decided the same way: a
            // custom setter runs, a getter-only property refuses, and `fixed` refuses once
            // the declaration's own initializer has run. A static that answered differently
            // from an instance property would be one more rule to remember for no reason.
            if (property.SetterBody is not null)
            {
                ExecuteStaticPropertySetter(property, value);
                return true;
            }

            if (property.GetterBody is not null)
            {
                throw new InvalidOperationException(
                    $"Static property '{property.Name}' on class '{Name}' is read-only.");
            }

            if (property.IsFixed)
            {
                throw new InvalidOperationException(
                    $"Static property '{property.Name}' on class '{Name}' is fixed and cannot be reassigned after initialization.");
            }

            _staticValues[memberName] = value;
            return true;
        }

        // Assignment follows the read: an inherited static is stored on the class that declared
        // it, so `D.S = 1` and `B.S` refer to the same slot instead of silently diverging.
        return BaseClass is not null && BaseClass.TrySetStaticMember(memberName, value);
    }

    /// <summary>
    /// Stores a declared static property's initial value, bypassing the rules that govern a
    /// later assignment.
    /// </summary>
    /// <remarks>
    /// Declaration-time initialization is not an assignment and must not be judged as one:
    /// <c>fixed static prop S = 1</c> has to be allowed to reach 1 before <c>fixed</c> starts
    /// refusing writes. Kept as its own method rather than a flag on
    /// <see cref="TrySetStaticMember"/> because the two callers mean genuinely different
    /// things, and a flag invites passing the wrong one.
    /// </remarks>
    internal void InitializeStaticMember(string memberName, object? value)
    {
        _staticValues[memberName] = value;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("Namespace", ShellNamespace),
            new KeyValuePair<string, object?>("Assembly", ShellAssemblyName),
            new KeyValuePair<string, object?>("BaseType", ShellBaseTypeName),
            new KeyValuePair<string, object?>("IsClass", ShellIsClass),
            new KeyValuePair<string, object?>("IsInterface", ShellIsInterface),
            new KeyValuePair<string, object?>("IsEnum", ShellIsEnum),
            new KeyValuePair<string, object?>("IsValueType", ShellIsValueType),
            new KeyValuePair<string, object?>("IsAbstract", ShellIsAbstract),
            new KeyValuePair<string, object?>("IsSealed", IsSealed),
            new KeyValuePair<string, object?>("IsHermit", IsHermit),
            new KeyValuePair<string, object?>("IsStrict", IsStrict),
            new KeyValuePair<string, object?>("IsGenericType", ShellIsGenericType),
            new KeyValuePair<string, object?>("IsArray", ShellIsArray),
            new KeyValuePair<string, object?>("IsPublic", ShellIsPublic),
            new KeyValuePair<string, object?>("PropertyCount", GetShellMembers(includeHidden).Count(member => !member.IsStatic)),
            new KeyValuePair<string, object?>("StaticPropertyCount", GetShellMembers(includeHidden).Count(member => member.IsStatic)),
            new KeyValuePair<string, object?>("MethodCount", GetShellMethods(includeHidden).Count(method => !method.IsStatic)),
            new KeyValuePair<string, object?>("StaticMethodCount", GetShellMethods(includeHidden).Count(method => method.IsStatic)),
            new KeyValuePair<string, object?>("ConstructorCount", GetShellConstructors().Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        // The same visibility rule member access applies, rather than a weaker copy of it.
        // Testing only `IsShy` advertised `local` and `guarded` properties that `$c.Local`
        // then refused with "Member not found" — the type descriptor promising what the
        // instance denies (`TS-P2-47`). A static property is listed either way, since it is a
        // real member of the type even though it is not an instance one.
        return Properties
            .Where(property =>
                property.IsStatic
                    ? includeHidden || !property.IsShy
                    : IsVisibleInstanceProperty(property, this, includeHidden, accessor: null))
            .Select(property => new ShellMemberDescriptor(
                property.Name,
                Kind: "Property",
                TypeName: GetAnnotationDisplayName(property.TypeName),
                IsStatic: property.IsStatic,
                IsWritable: property.IsWritable,
                IsHidden: property.IsShy,
                Documentation: property.Documentation?.Description))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        var declared = Methods
            .Where(method => includeHidden || !method.IsShy)
            .Select(method => new ShellMethodDescriptor(
                method.Name,
                ReturnTypeName: GetAnnotationDisplayName(method.ReturnTypeName),
                IsStatic: method.IsStatic,
                ParameterCount: method.Parameters.Count,
                Signature: FormatMethodSignature(method),
                IsHidden: method.IsShy,
                Documentation: method.Documentation?.Description));

        // Native bindings are real callable members, so `methods` must show the
        // proud ones — otherwise `proud bind` and `shy bind` would look identical
        // from outside and the distinction would be decorative.
        var native = _nativeMembers.Values
            .Where(entry => includeHidden || !entry.IsShy)
            .Select(entry => new ShellMethodDescriptor(
                entry.Command.Name,
                ReturnTypeName: (entry.Command as Bridge.NativeFunctionCommand)?.ReturnTypeName ?? "any",
                IsStatic: true,
                ParameterCount: (entry.Command as Bridge.NativeFunctionCommand)?.CallableParameterCount ?? 0,
                Signature: entry.Command.Usage,
                IsHidden: entry.IsShy));

        return declared
            .Concat(native)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
    {
        return GetConstructorMetadata()
            .DistinctBy(constructor => constructor.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>How a member name is served, once the class has decided which route applies.</summary>
    private enum InstanceMemberRoute
    {
        /// <summary>No property of that name is visible here.</summary>
        NotDeclared,

        /// <summary>A computed property: run its getter.</summary>
        Computed,

        /// <summary>A lazy property: initialize once, then share.</summary>
        Lazy,

        /// <summary>A stored property: read the instance's slot.</summary>
        Stored,
    }

    /// <summary>
    /// Decides which route serves <paramref name="name"/> on this class, and emits the
    /// deprecation warning if the property is fading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the two <c>TryGetInstanceMember</c> twins duplicated: the visibility test
    /// (static, shy, guarded and local members are hidden unless <paramref name="includeHidden"/>),
    /// the fading-property warning, and the computed/lazy/stored precedence. Both then did the same
    /// thing with the answer, differing only in whether the getter call was awaited
    /// (<c>TS-P1-24</c>).
    /// </para>
    /// <para>
    /// The warning belongs here rather than in the callers precisely because it is a *side effect*
    /// tied to the decision: duplicated, it could fire once on one surface and twice — or not at
    /// all — on the other, and nothing would fail.
    /// </para>
    /// </remarks>
    private InstanceMemberRoute ResolveInstanceMemberRoute(
        string name,
        bool includeHidden,
        ToshClassDefinition? accessor,
        out ToshClassPropertyDefinition? property)
    {
        if (!TryGetVisibleInstanceProperty(name, includeHidden, accessor, out property))
        {
            return InstanceMemberRoute.NotDeclared;
        }

        if (property.IsFading)
        {
            _engine.WriteWarning(
                code: "tosh.runtime.fading_member",
                title: $"Property '{property.Name}' on class '{Name}' is fading (deprecated).",
                help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
        }

        if (property.GetterBody is not null)
        {
            return InstanceMemberRoute.Computed;
        }

        return property.IsLazy ? InstanceMemberRoute.Lazy : InstanceMemberRoute.Stored;
    }

    /// <summary>
    /// Reads <paramref name="name"/> off the CLR base object, if there is one and it has such a
    /// member. Shared because "swallow the lookup failure" is a decision, and a silent catch is
    /// the last place a divergence would be noticed.
    /// </summary>
    private bool TryGetClrBaseMember(
        ToshClassInstance instance,
        string name,
        out object? value,
        CancellationToken cancellationToken = default)
    {
        if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            // Checked *outside* the try, so a cancellation cannot be mistaken for "no such
            // member" and swallowed. The asynchronous twin achieved this with an explicit
            // `catch (OperationCanceledException) { throw; }` ahead of its blanket catch; moving
            // the check out is the same guarantee with one fewer thing to remember.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                value = _engine.LanguageRuntime.ObjectAccessor.GetValue(instance.ClrBaseObject, name);
                return true;
            }
            catch { /* member not found on CLR base */ }
        }

        value = null;
        return false;
    }

    internal bool TryGetInstanceMember(
        ToshClassInstance instance,
        string name,
        bool includeHidden,
        ToshClassDefinition? accessor,
        out object? value)
    {
        switch (ResolveInstanceMemberRoute(name, includeHidden, accessor, out var property))
        {
            case InstanceMemberRoute.Computed:
                value = EvaluatePropertyGetter(instance, property!);
                return true;

            case InstanceMemberRoute.Lazy:
                value = GetOrInitializeLazyProperty(instance, property!);
                return true;

            case InstanceMemberRoute.Stored:
                return instance.TryGetStoredValue(property!.Name, out value);
        }

        if (BaseClass is not null)
        {
            return BaseClass.TryGetInstanceMember(instance, name, includeHidden, accessor, out value);
        }

        if (TryGetClrBaseMember(instance, name, out value))
        {
            return true;
        }

        if (IsFluid)
        {
            if (instance.TryGetDynamicProperty(name, out var dynamicProp))
            {
                if (!includeHidden && dynamicProp.IsShy)
                {
                    value = null;
                    return false;
                }

                if (dynamicProp.IsComputed)
                {
                    value = EvaluateDynamicPropertyGetter(instance, dynamicProp);
                    return true;
                }

                return instance.TryGetStoredValue(name, out value);
            }

            return instance.TryGetStoredValue(name, out value);
        }

        value = null;
        return false;
    }

    internal async ValueTask<(bool Found, object? Value)> TryGetInstanceMemberAsync(
        ToshClassInstance instance,
        string name,
        bool includeHidden,
        ToshClassDefinition? accessor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (ResolveInstanceMemberRoute(name, includeHidden, accessor, out var property))
        {
            case InstanceMemberRoute.Computed:
                return (true, await EvaluatePropertyGetterAsync(instance, property!, cancellationToken));

            case InstanceMemberRoute.Lazy:
                return (true, await GetOrInitializeLazyPropertyAsync(instance, property!, cancellationToken));

            case InstanceMemberRoute.Stored:
                return instance.TryGetStoredValue(property!.Name, out var stored)
                    ? (true, stored)
                    : (false, null);
        }

        if (BaseClass is not null)
        {
            return await BaseClass.TryGetInstanceMemberAsync(
                instance,
                name,
                includeHidden,
                accessor,
                cancellationToken);
        }

        if (TryGetClrBaseMember(instance, name, out var clrValue, cancellationToken))
        {
            return (true, clrValue);
        }

        if (IsFluid)
        {
            if (instance.TryGetDynamicProperty(name, out var dynamicProp))
            {
                if (!includeHidden && dynamicProp.IsShy)
                {
                    return (false, null);
                }

                if (dynamicProp.IsComputed)
                {
                    var val = await EvaluateDynamicPropertyGetterAsync(instance, dynamicProp, cancellationToken);
                    return (true, val);
                }

                if (instance.TryGetStoredValue(name, out var fluidValue))
                {
                    return (true, fluidValue);
                }
            }
            else if (instance.TryGetStoredValue(name, out var fluidValue))
            {
                return (true, fluidValue);
            }
        }

        return (false, null);
    }

    private object? GetOrInitializeLazyProperty(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property)
    {
        ThrowIfRecursiveLazyInitialization(instance, property);
        var initialization = instance.GetOrCreateLazyInitialization(property.Name);
        if (!initialization.IsOwner)
        {
            return initialization.Completion.GetAwaiter().GetResult();
        }

        var previous = instance.EnterLazyInitializationContext(property.Name);
        try
        {
            var value = GetInitialPropertyValue(
                instance,
                property,
                new Dictionary<string, object?>(StringComparer.Ordinal));
            instance.CompleteLazyInitialization(property.Name, value);
            return value;
        }
        catch (Exception exception)
        {
            instance.FailLazyInitialization(property.Name, exception);
            throw;
        }
        finally
        {
            instance.ExitLazyInitializationContext(previous);
        }
    }

    private async ValueTask<object?> GetOrInitializeLazyPropertyAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        CancellationToken cancellationToken)
    {
        ThrowIfRecursiveLazyInitialization(instance, property);
        var initialization = instance.GetOrCreateLazyInitialization(property.Name);
        if (!initialization.IsOwner)
        {
            return await initialization.Completion.WaitAsync(cancellationToken);
        }

        var previous = instance.EnterLazyInitializationContext(property.Name);
        try
        {
            var value = await GetInitialPropertyValueAsync(
                instance,
                property,
                new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);
            instance.CompleteLazyInitialization(property.Name, value);
            return value;
        }
        catch (Exception exception)
        {
            instance.FailLazyInitialization(property.Name, exception);
            throw;
        }
        finally
        {
            instance.ExitLazyInitializationContext(previous);
        }
    }

    private void ThrowIfRecursiveLazyInitialization(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property)
    {
        if (instance.IsLazyInitializationActiveInCurrentContext(property.Name))
        {
            throw new InvalidOperationException(
                $"Lazy property '{property.Name}' on class '{Name}' recursively reads itself while initializing.");
        }
    }

    /// <summary>
    /// Finds the instance property <paramref name="name"/> refers to on this class, if it is
    /// visible from the caller's position.
    /// </summary>
    /// <remarks>
    /// The rule — static members are never instance members, and shy, guarded and local ones are
    /// hidden unless <paramref name="includeHidden"/> — was written out four times: once per
    /// surface for reads and once per surface for writes. Four copies of a visibility rule is
    /// four chances for a new modifier to be honoured in three places (<c>TS-P1-24</c>).
    /// </remarks>
    private bool TryGetVisibleInstanceProperty(
        string name,
        bool includeHidden,
        ToshClassDefinition? accessor,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ToshClassPropertyDefinition? property)
    {
        if (!_propertiesByName.TryGetValue(name, out property) ||
            !IsVisibleInstanceProperty(property, this, includeHidden, accessor))
        {
            property = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="property"/> is reachable on an instance from a caller with the
    /// given visibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static members are never instance members; shy, guarded and local ones are hidden unless
    /// <paramref name="includeHidden"/>.
    /// </para>
    /// <para>
    /// <c>GetInstanceMembers</c> — what <c>members</c> reports — used to apply a *weaker* rule,
    /// testing only <c>IsShy || IsGuarded</c>. So `members` listed `local` and `shared`
    /// properties that member access then refused: `$c.Local` and `$c.Shared` both failed with
    /// "Member not found" while appearing in the listing. Introspection contradicting behaviour
    /// is the <c>TS-P1-33</c> family, and here it was caused by the same rule existing twice with
    /// different contents — which is what <c>TS-P1-24</c> is about. One predicate now serves
    /// member lookup and both member listings.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Why <paramref name="name"/> could not be reached on an instance of this class, or
    /// <see langword="null"/> when the class simply has no such member — <c>TS-P2-18</c>.
    /// </summary>
    /// <remarks>
    /// The visibility rules answer a single question — can this caller see it? — so a refusal and
    /// an absence arrive identically at the accessor, which then said "was not found" for both.
    /// Saying which one it was is the difference between checking the modifier and hunting a
    /// typo. Walks the inheritance chain, because a member declared on a base class is what the
    /// reader wrote even though this class does not list it.
    /// </remarks>
    internal string? ExplainHiddenInstanceMember(string name)
    {
        for (var current = this; current is not null; current = current.BaseClass)
        {
            foreach (var property in current.Properties)
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.IsStatic)
                {
                    return $"'{name}' is a static property of '{current.Name}'. " +
                           $"Reach it through the class: '{current.Name}.{property.Name}'.";
                }

                if (property.IsShy)
                {
                    return $"Property '{property.Name}' is private to '{current.Name}'.";
                }

                if (property.IsGuarded)
                {
                    return $"Property '{property.Name}' is guarded and is not readable from here.";
                }

                if (property.IsLocal)
                {
                    return $"Property '{property.Name}' is local to '{current.Name}'.";
                }

                return null;
            }

            foreach (var method in current.Methods)
            {
                if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (method.IsShy)
                {
                    return $"Method '{method.Name}' is private to '{current.Name}'.";
                }

                return null;
            }
        }

        return null;
    }

    private static bool IsVisibleInstanceProperty(
        ToshClassPropertyDefinition property,
        ToshClassDefinition declaring,
        bool includeHidden,
        ToshClassDefinition? accessor) =>
        !property.IsStatic
        && (!property.IsShy || CanSeeShy(declaring, includeHidden, accessor))
        && (!property.IsGuarded || CanSeeGuarded(declaring, includeHidden, accessor))
        && (!property.IsLocal || includeHidden);

    /// <summary>
    /// Whether a <c>shy</c> member of <paramref name="declaring"/> is visible to code running in
    /// <paramref name="accessor"/>. Private means private to the class that declared it, so only
    /// that same class qualifies — a subclass does not.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="accessor"/> means the caller did not say whose code is running, and
    /// falls back to <paramref name="includeHidden"/> — the behaviour every surface had before
    /// the accessing class was carried. That is what keeps introspection surfaces such as
    /// completion working unchanged while <c>$this</c> and <c>$super</c>, which do know, get the
    /// real rule.
    /// </remarks>
    /// <summary>
    /// Whether the code currently running may see this class's <c>shy</c> static members.
    /// </summary>
    /// <remarks>
    /// Only the declaring class itself, matching <see cref="CanSeeShy"/> for instance members —
    /// <c>shy</c> is private, so a subclass does not qualify and neither does anything outside.
    /// The engine answers "who is asking?" because a static access carries no <c>$this</c> to
    /// ask (<c>TS-P2-61</c>).
    /// </remarks>
    private bool CanSeeShyStatic() => ReferenceEquals(_engine.CurrentClass, this);

    private InvalidOperationException ShyStaticMemberIsHidden(string kind, string memberName) =>
        new($"{kind} '{memberName}' on class '{Name}' is shy and cannot be reached from outside the class.");

    private static bool CanSeeShy(
        ToshClassDefinition declaring,
        bool includeHidden,
        ToshClassDefinition? accessor) =>
        accessor is null ? includeHidden : ReferenceEquals(accessor, declaring);

    /// <summary>
    /// Whether a <c>guarded</c> member of <paramref name="declaring"/> is visible to code running
    /// in <paramref name="accessor"/>. Protected reaches down the chain, so the declaring class
    /// and anything derived from it qualify — which is the whole difference from <c>shy</c>.
    /// </summary>
    private static bool CanSeeGuarded(
        ToshClassDefinition declaring,
        bool includeHidden,
        ToshClassDefinition? accessor)
    {
        if (accessor is null)
        {
            return includeHidden;
        }

        for (var current = accessor; current is not null; current = current.BaseClass)
        {
            if (ReferenceEquals(current, declaring))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How an assignment to a visible property is served.</summary>
    private enum InstanceMemberAssignment
    {
        /// <summary>No visible property of that name; try the base class, then the CLR base.</summary>
        NotDeclared,

        /// <summary>A custom setter: run its body.</summary>
        Setter,

        /// <summary>Computed with no setter: assignment is an error.</summary>
        ReadOnly,

        /// <summary>Declared <c>fixed</c> and already initialized: assignment is an error.</summary>
        Fixed,

        /// <summary>An ordinary stored property: convert and store.</summary>
        Stored,
    }

    /// <summary>
    /// Decides how an assignment to <paramref name="name"/> is served, and throws for the two
    /// cases that are errors rather than routes.
    /// </summary>
    /// <remarks>
    /// The read-only and fixed messages were written out once per surface, word for word. Keeping
    /// the throws here rather than returning a route to the callers means neither surface can
    /// reword one of them alone, which is the same failure the <c>$env</c> message had.
    /// </remarks>
    private InstanceMemberAssignment ResolveInstanceMemberAssignment(
        ToshClassInstance instance,
        string name,
        bool includeHidden,
        ToshClassDefinition? accessor,
        out ToshClassPropertyDefinition? property)
    {
        if (!TryGetVisibleInstanceProperty(name, includeHidden, accessor, out property))
        {
            return InstanceMemberAssignment.NotDeclared;
        }

        if (property!.SetterBody is not null)
        {
            return InstanceMemberAssignment.Setter;
        }

        if (property.GetterBody is not null)
        {
            throw new InvalidOperationException(
                $"Property '{property.Name}' on class '{Name}' is read-only.");
        }

        if (property.IsFixed && !instance.IsInitializing)
        {
            throw new InvalidOperationException(
                $"Property '{property.Name}' on class '{Name}' is fixed and cannot be reassigned after initialization.");
        }

        return InstanceMemberAssignment.Stored;
    }

    /// <summary>Writes <paramref name="name"/> to the CLR base object, if it will take it.</summary>
    private bool TrySetClrBaseMember(
        ToshClassInstance instance,
        string name,
        object? value,
        CancellationToken cancellationToken = default)
    {
        if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _engine.LanguageRuntime.ObjectAccessor.SetValue(instance.ClrBaseObject, name, value);
                return true;
            }
            catch { /* member not found or read-only on CLR base */ }
        }

        return false;
    }

    internal bool TrySetInstanceMember(
        ToshClassInstance instance,
        string name,
        object? value,
        bool includeHidden,
        ToshClassDefinition? accessor)
    {
        switch (ResolveInstanceMemberAssignment(instance, name, includeHidden, accessor, out var property))
        {
            case InstanceMemberAssignment.Setter:
                ExecutePropertySetter(instance, property!, value);
                return true;

            case InstanceMemberAssignment.Stored:
                instance.SetStoredValue(property!.Name, ConvertPropertyValue(instance, property, value));
                return true;
        }

        if (BaseClass is not null)
        {
            return BaseClass.TrySetInstanceMember(instance, name, value, includeHidden, accessor);
        }

        if (TrySetClrBaseMember(instance, name, value))
        {
            return true;
        }

        if (IsFluid)
        {
            if (instance.TryGetDynamicProperty(name, out var dynamicProp))
            {
                if (dynamicProp.IsFixed)
                {
                    throw new InvalidOperationException($"Cannot modify readonly dynamic property '{name}' on '{Name}'.");
                }

                if (dynamicProp.IsComputed)
                {
                    if (dynamicProp.Setter is null)
                    {
                        throw new InvalidOperationException($"Dynamic property '{name}' on '{Name}' has no setter.");
                    }

                    ExecuteDynamicPropertySetter(instance, dynamicProp, value);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(dynamicProp.TypeName))
                {
                    value = ConvertDynamicPropertyValue(instance, dynamicProp.TypeName, value);
                }

                instance.SetStoredValue(name, value);
                return true;
            }

            instance.SetStoredValue(name, value);
            return true;
        }

        return false;
    }

    internal async ValueTask<bool> TrySetInstanceMemberAsync(
        ToshClassInstance instance,
        string name,
        object? value,
        bool includeHidden,
        ToshClassDefinition? accessor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (ResolveInstanceMemberAssignment(instance, name, includeHidden, accessor, out var property))
        {
            case InstanceMemberAssignment.Setter:
                await ExecutePropertySetterAsync(instance, property!, value, cancellationToken);
                return true;

            case InstanceMemberAssignment.Stored:
                instance.SetStoredValue(
                    property!.Name,
                    await ConvertPropertyValueAsync(instance, property, value, cancellationToken));
                return true;
        }

        if (BaseClass is not null)
        {
            return await BaseClass.TrySetInstanceMemberAsync(
                instance,
                name,
                value,
                includeHidden,
                accessor,
                cancellationToken);
        }

        if (TrySetClrBaseMember(instance, name, value, cancellationToken))
        {
            return true;
        }

        if (IsFluid)
        {
            if (instance.TryGetDynamicProperty(name, out var dynamicProp))
            {
                if (dynamicProp.IsFixed)
                {
                    throw new InvalidOperationException($"Cannot modify readonly dynamic property '{name}' on '{Name}'.");
                }

                if (dynamicProp.IsComputed)
                {
                    if (dynamicProp.Setter is null)
                    {
                        throw new InvalidOperationException($"Dynamic property '{name}' on '{Name}' has no setter.");
                    }

                    await ExecuteDynamicPropertySetterAsync(instance, dynamicProp, value, cancellationToken);
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(dynamicProp.TypeName))
                {
                    value = await ConvertDynamicPropertyValueAsync(instance, dynamicProp.TypeName, value, cancellationToken);
                }

                instance.SetStoredValue(name, value);
                return true;
            }

            instance.SetStoredValue(name, value);
            return true;
        }

        return false;
    }

    internal IReadOnlyList<KeyValuePair<string, object?>> GetInstanceMembers(
        ToshClassInstance instance,
        bool includeHidden,
        ToshClassDefinition? accessor)
    {
        var members = new List<KeyValuePair<string, object?>>();

        // Include base class members first
        if (BaseClass is not null)
        {
            foreach (var baseMember in BaseClass.GetInstanceMembers(instance, includeHidden, accessor))
            {
                members.Add(baseMember);
            }
        }
        else if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            foreach (var prop in ClrBaseType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try { members.Add(new KeyValuePair<string, object?>(prop.Name, prop.GetValue(instance.ClrBaseObject))); }
                catch { members.Add(new KeyValuePair<string, object?>(prop.Name, null)); }
            }
        }

        foreach (var property in Properties)
        {
            if (!IsVisibleInstanceProperty(property, this, includeHidden, accessor))
            {
                continue;
            }

            // Skip if already provided by a base class (overridden)
            if (members.Any(m => string.Equals(m.Key, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryGetInstanceMember(instance, property.Name, includeHidden, accessor, out var value);
            members.Add(new KeyValuePair<string, object?>(property.Name, value));
        }

        if (IsFluid)
        {
            if (instance.HasDynamicProperties)
            {
                foreach (var dynamicProp in instance.GetDynamicProperties())
                {
                    if (!includeHidden && dynamicProp.IsShy) continue;
                    if (members.Any(m => string.Equals(m.Key, dynamicProp.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    if (dynamicProp.IsComputed)
                    {
                        try
                        {
                            var val = EvaluateDynamicPropertyGetter(instance, dynamicProp);
                            members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, val));
                        }
                        catch
                        {
                            members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, null));
                        }
                    }
                    else
                    {
                        instance.TryGetStoredValue(dynamicProp.Name, out var val);
                        members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, val));
                    }
                }
            }

            foreach (var (key, value) in instance.GetStoredValues())
            {
                if (!members.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
                {
                    members.Add(new KeyValuePair<string, object?>(key, value));
                }
            }
        }

        return members;
    }

    internal async ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetInstanceMembersAsync(
        ToshClassInstance instance,
        ToshClassDefinition? accessor,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = new List<KeyValuePair<string, object?>>();

        if (BaseClass is not null)
        {
            members.AddRange(await BaseClass.GetInstanceMembersAsync(
                instance,
                accessor,
                includeHidden,
                cancellationToken));
        }
        else if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            foreach (var property in ClrBaseType.GetProperties(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    members.Add(new KeyValuePair<string, object?>(
                        property.Name,
                        property.GetValue(instance.ClrBaseObject)));
                }
                catch
                {
                    members.Add(new KeyValuePair<string, object?>(
                        property.Name,
                        null));
                }
            }
        }

        foreach (var property in Properties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsVisibleInstanceProperty(property, this, includeHidden, accessor))
            {
                continue;
            }

            if (members.Any(member =>
                    string.Equals(
                        member.Key,
                        property.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var lookup = await TryGetInstanceMemberAsync(
                instance,
                property.Name,
                includeHidden,
                accessor,
                cancellationToken);
            members.Add(new KeyValuePair<string, object?>(
                property.Name,
                lookup.Found ? lookup.Value : null));
        }

        if (IsFluid)
        {
            if (instance.HasDynamicProperties)
            {
                foreach (var dynamicProp in instance.GetDynamicProperties())
                {
                    if (!includeHidden && dynamicProp.IsShy) continue;
                    if (members.Any(m => string.Equals(m.Key, dynamicProp.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    if (dynamicProp.IsComputed)
                    {
                        try
                        {
                            var val = await EvaluateDynamicPropertyGetterAsync(instance, dynamicProp, cancellationToken);
                            members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, val));
                        }
                        catch
                        {
                            members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, null));
                        }
                    }
                    else
                    {
                        instance.TryGetStoredValue(dynamicProp.Name, out var val);
                        members.Add(new KeyValuePair<string, object?>(dynamicProp.Name, val));
                    }
                }
            }

            foreach (var (key, value) in instance.GetStoredValues())
            {
                if (!members.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
                {
                    members.Add(new KeyValuePair<string, object?>(key, value));
                }
            }
        }

        return members;
    }

    /// <summary>
    /// Evaluates a computed <c>static</c>/<c>shared</c> property's getter. Identical to
    /// the instance form except that there is no instance, so the locals carry no
    /// <c>$this</c> — <see cref="CreateLocals"/> already accepts a null instance and
    /// omits it.
    /// </summary>
    private object? EvaluateStaticPropertyGetter(ToshClassPropertyDefinition property)
    {
        var locals = CreateLocals(null, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = _engine.ExecuteClassBlockSync(
            this,
            SourceName,
            SourceText,
            property.GetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.get");

        return FlattenCallResult(values);
    }

    private void ExecuteStaticPropertySetter(ToshClassPropertyDefinition property, object? value)
    {
        var locals = CreateLocals(null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        _engine.ExecuteClassBlockSync(
            this,
            SourceName,
            SourceText,
            property.SetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.set");
    }

    private object? EvaluatePropertyGetter(ToshClassInstance instance, ToshClassPropertyDefinition property)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = _engine.ExecuteClassBlockSync(this, SourceName, SourceText, property.GetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.get");
        return FlattenCallResult(values);
    }

    private async ValueTask<object?> EvaluatePropertyGetterAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = await _engine.ExecuteClassBlockAsync(
            this,
            SourceName,
            SourceText,
            property.GetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.get",
            cancellationToken);
        return FlattenCallResult(values);
    }

    private void ExecutePropertySetter(ToshClassInstance instance, ToshClassPropertyDefinition property, object? value)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        _engine.ExecuteClassBlockSync(this, SourceName, SourceText, property.SetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.set");
    }

    private async ValueTask ExecutePropertySetterAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        await _engine.ExecuteClassBlockAsync(
            this,
            SourceName,
            SourceText,
            property.SetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.set",
            cancellationToken);
    }

    /// <summary>
    /// The part of property conversion that is identical on both surfaces: everything up to
    /// the point where the engine's annotated-value conversion has to be called, which is the
    /// only step that differs between the synchronous and asynchronous paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Substitutes generic type-parameter names (e.g. <c>T1</c>) against the instance's resolved
    /// type-argument bindings. When the property's declared type is itself a class type parameter
    /// a strict no-coercion check applies, matching constructor and method parameter behaviour;
    /// otherwise the caller goes through the engine's standard conversion path.
    /// </para>
    /// <para>
    /// Extracted for <c>TS-P1-24</c>. The two twins had already drifted: the asynchronous copy
    /// had lost these explanatory comments entirely, which is the documented failure mode of a
    /// parallel implementation showing up before any behavioural divergence did.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the value is settled here and <paramref name="result"/> holds
    /// it; <see langword="false"/> when the caller must run the annotated-value conversion.
    /// </returns>
    private bool TryResolvePropertyValueByBinding(
        ToshClassInstance? instance,
        ToshClassPropertyDefinition property,
        object? value,
        out object? result)
    {
        result = value;

        if (property.TypeName is null)
        {
            return true;
        }

        if (instance is null)
        {
            return false;
        }

        var bindings = instance.GetBindingsFor(this);

        if (bindings is null || !bindings.TryGetValue(property.TypeName, out var boundType))
        {
            return false;
        }

        if (boundType is null)
        {
            // `TOAST-0125`. Nominal-only used to mean "accept anything", which made
            // `Holder<Circle>` unenforceable: a ToastScript type argument has no CLR type, so
            // every one of them landed here. The name it was closed over is now kept, and a
            // value is checked against *that* — nominally, against this instance's own
            // declaration, rather than against whatever type the CLR happens to have loaded
            // under the same name, which is what `TS-P2-39` was.
            EnforceNominalBinding(
                instance,
                property.TypeName,
                value,
                property.Span,
                SourceName,
                SourceText,
                $"{Name}.{property.Name}");

            return true;
        }

        result = CoerceStrictBinding(
            boundType,
            value,
            property.Span,
            SourceName,
            SourceText,
            $"{Name}.{property.Name}");

        return true;
    }

    /// <summary>
    /// The export table of the module this class was declared in, or null at top
    /// level. A class body is written inside its module, so an unqualified
    /// annotation naming a sibling — or the class itself — has to resolve
    /// against that module. Annotations are checked when a member runs, by which
    /// time the module scope is long gone from the engine's stack, so the class
    /// carries the table rather than relying on where the call happens to be.
    /// </summary>
    internal ModuleExportTable? DeclaringExports { get; set; }

    /// <summary>
    /// Makes this class's declaring module visible to annotation resolution for
    /// the duration of a conversion, then restores whatever was there. Nested —
    /// a member of one class calling a member of another must not leave the
    /// wrong module installed.
    /// </summary>
    private readonly struct AnnotationScope : IDisposable
    {
        private readonly ToshEngine _engine;
        private readonly ModuleExportTable? _previous;

        public AnnotationScope(ToshEngine engine, ModuleExportTable? exports)
        {
            _engine = engine;
            _previous = engine.AnnotationResolutionExports;
            if (exports is not null) engine.AnnotationResolutionExports = exports;
        }

        public void Dispose() => _engine.AnnotationResolutionExports = _previous;
    }

    private object? ConvertPropertyValue(ToshClassInstance? instance, ToshClassPropertyDefinition property, object? value)
    {
        if (TryResolvePropertyValueByBinding(instance, property, value, out var resolved))
        {
            return resolved;
        }

        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        return _engine.ConvertAnnotatedValue(
            property.TypeName!,
            property.Refinement,
            value,
            property.Span,
            SourceName,
            SourceText,
            $"{Name}.{property.Name}");
    }

    private async ValueTask<object?> ConvertPropertyValueAsync(
        ToshClassInstance? instance,
        ToshClassPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken)
    {
        if (TryResolvePropertyValueByBinding(instance, property, value, out var resolved))
        {
            return resolved;
        }

        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        return await _engine.ConvertAnnotatedValueAsync(
            property.TypeName!,
            property.Refinement,
            value,
            property.Span,
            SourceName,
            SourceText,
            $"{Name}.{property.Name}",
            cancellationToken);
    }

    internal object? EvaluateDynamicPropertyGetter(ToshClassInstance instance, DynamicPropertyDescriptor dynamicProp)
    {
        if (dynamicProp.Getter is BlockSyntax block)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
            var values = _engine.ExecuteClassBlockSync(
                this,
                dynamicProp.SourceName ?? SourceName,
                dynamicProp.SourceText ?? SourceText,
                block,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.get");
            return FlattenCallResult(values);
        }

        if (dynamicProp.Getter is ShellBlock shellBlock && shellBlock.Syntax is BlockSyntax syntaxBlock)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
            var values = _engine.ExecuteClassBlockSync(
                this,
                shellBlock.SourceName,
                shellBlock.SourceText,
                syntaxBlock,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.get");
            return FlattenCallResult(values);
        }

        return EvaluateDynamicPropertyGetterAsync(instance, dynamicProp, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    internal async ValueTask<object?> EvaluateDynamicPropertyGetterAsync(
        ToshClassInstance instance,
        DynamicPropertyDescriptor dynamicProp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (dynamicProp.Getter is BlockSyntax block)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
            var values = await _engine.ExecuteClassBlockAsync(
                this,
                dynamicProp.SourceName ?? SourceName,
                dynamicProp.SourceText ?? SourceText,
                block,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.get",
                cancellationToken);
            return FlattenCallResult(values);
        }

        if (dynamicProp.Getter is ShellBlock shellBlock)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
            if (shellBlock.Syntax is BlockSyntax syntaxBlock)
            {
                var values = await _engine.ExecuteClassBlockAsync(
                    this,
                    shellBlock.SourceName,
                    shellBlock.SourceText,
                    syntaxBlock,
                    locals,
                    CapturedScopes,
                    $"{Name}.{dynamicProp.Name}.get",
                    cancellationToken);
                return FlattenCallResult(values);
            }

            var executor = _engine.LanguageRuntime.BlockExecutor;
            if (executor is null)
            {
                throw new InvalidOperationException("Block execution is not available in this runtime.");
            }

            var results = new List<object?>();
            await foreach (var val in executor.ExecuteAsync(shellBlock, locals, cancellationToken).WithCancellation(cancellationToken))
            {
                results.Add(val);
            }
            return FlattenCallResult(results);
        }

        if (dynamicProp.Getter is IShellCallable callable)
        {
            var context = new CommandContext(
                LanguageRuntime: _engine.LanguageRuntime,
                Input: System.Linq.AsyncEnumerable.Empty<object?>(),
                Arguments: Array.Empty<object?>(),
                CancellationToken: cancellationToken);
            var results = await AsyncEnumerableExtensions.ToListAsync(callable.InvokeAsync(context), cancellationToken);
            return FlattenCallResult(results);
        }

        return dynamicProp.Value;
    }

    internal void ExecuteDynamicPropertySetter(
        ToshClassInstance instance,
        DynamicPropertyDescriptor dynamicProp,
        object? value)
    {
        if (dynamicProp.Setter is BlockSyntax block)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["_"] = value,
            });
            _engine.ExecuteClassBlockSync(
                this,
                dynamicProp.SourceName ?? SourceName,
                dynamicProp.SourceText ?? SourceText,
                block,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.set");
            return;
        }

        if (dynamicProp.Setter is ShellBlock shellBlock && shellBlock.Syntax is BlockSyntax syntaxBlock)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["_"] = value,
            });
            _engine.ExecuteClassBlockSync(
                this,
                shellBlock.SourceName,
                shellBlock.SourceText,
                syntaxBlock,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.set");
            return;
        }

        ExecuteDynamicPropertySetterAsync(instance, dynamicProp, value, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    internal async ValueTask ExecuteDynamicPropertySetterAsync(
        ToshClassInstance instance,
        DynamicPropertyDescriptor dynamicProp,
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (dynamicProp.Setter is BlockSyntax block)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["_"] = value,
            });
            await _engine.ExecuteClassBlockAsync(
                this,
                dynamicProp.SourceName ?? SourceName,
                dynamicProp.SourceText ?? SourceText,
                block,
                locals,
                CapturedScopes,
                $"{Name}.{dynamicProp.Name}.set",
                cancellationToken);
            return;
        }

        if (dynamicProp.Setter is ShellBlock shellBlock)
        {
            var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = value,
                ["_"] = value,
            });
            if (shellBlock.Syntax is BlockSyntax syntaxBlock)
            {
                await _engine.ExecuteClassBlockAsync(
                    this,
                    shellBlock.SourceName,
                    shellBlock.SourceText,
                    syntaxBlock,
                    locals,
                    CapturedScopes,
                    $"{Name}.{dynamicProp.Name}.set",
                    cancellationToken);
                return;
            }

            var executor = _engine.LanguageRuntime.BlockExecutor;
            if (executor is null)
            {
                throw new InvalidOperationException("Block execution is not available in this runtime.");
            }

            await foreach (var _ in executor.ExecuteAsync(shellBlock, locals, cancellationToken).WithCancellation(cancellationToken))
            {
            }
            return;
        }

        if (dynamicProp.Setter is IShellCallable callable)
        {
            var context = new CommandContext(
                LanguageRuntime: _engine.LanguageRuntime,
                Input: System.Linq.AsyncEnumerable.Empty<object?>(),
                Arguments: [value],
                CancellationToken: cancellationToken);
            await foreach (var _ in callable.InvokeAsync(context).WithCancellation(cancellationToken))
            {
            }
        }
    }

    internal async ValueTask<object?> EvaluateDynamicInitializerAsync(
        ToshClassInstance instance,
        PipelineSyntax initializer,
        CancellationToken cancellationToken,
        string? sourceName = null,
        string? sourceText = null)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        return await _engine.EvaluateClassPipelineValueAsync(
            this,
            sourceName ?? SourceName,
            sourceText ?? SourceText,
            initializer,
            locals,
            CapturedScopes,
            cancellationToken);
    }

    internal object? EvaluateDynamicInitializer(
        ToshClassInstance instance,
        PipelineSyntax initializer,
        string? sourceName = null,
        string? sourceText = null)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        return _engine.EvaluateClassPipelineValueSync(
            this,
            sourceName ?? SourceName,
            sourceText ?? SourceText,
            initializer,
            locals,
            CapturedScopes);
    }

    internal async ValueTask<object?> ConvertDynamicPropertyValueAsync(
        ToshClassInstance instance,
        string typeName,
        object? value,
        CancellationToken cancellationToken)
    {
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);
        return await _engine.ConvertAnnotatedValueAsync(
            typeName,
            null,
            value,
            new TextSpan(0, 0),
            SourceName,
            SourceText,
            $"{Name}.<dynamic>",
            cancellationToken);
    }

    internal object? ConvertDynamicPropertyValue(
        ToshClassInstance instance,
        string typeName,
        object? value)
    {
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);
        return _engine.ConvertAnnotatedValue(
            typeName,
            null,
            value,
            new TextSpan(0, 0),
            SourceName,
            SourceText,
            $"{Name}.<dynamic>");
    }

}
