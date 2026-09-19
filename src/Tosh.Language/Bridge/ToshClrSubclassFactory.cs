using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace Tosh.Language.Bridge;

/// <summary>
/// Emits a real CLR subclass for a tōsh class declared <c>extends SomeClrType</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without this, such a class <em>holds</em> an instance of its base and forwards to it.
/// That is enough to read a member and enough to override one as far as tōsh is concerned,
/// and it stops at the boundary: hand the object to .NET and the overrides vanish, because
/// what crosses is the plain base object and nothing about it has ever heard of the class.
/// A class could say <c>func ToString()</c>, see it honoured everywhere in the language, and
/// see it ignored by the first CLR caller that asked.
/// </para>
/// <para>
/// So the contained object is made a subclass instead. It overrides exactly the virtuals the
/// tōsh class declares, and each override calls back into the tōsh instance. The value moving
/// around the language is still the <see cref="ToshClassInstance"/> — changing that would mean
/// rewriting every place that tests for one — but the object handed to .NET is now genuinely
/// one of its own kind.
/// </para>
/// <para>
/// A base that cannot be subclassed, a member that cannot be expressed, or a runtime without
/// <see cref="System.Reflection.Emit"/> all answer null, and construction falls back to the
/// plain base. Losing the overrides at the boundary is the behaviour that was there before;
/// refusing to construct the object at all would be worse than what this replaces.
/// </para>
/// </remarks>
internal static class ToshClrSubclassFactory
{
    private static readonly ConcurrentDictionary<string, Type?> Cache = new(StringComparer.Ordinal);
    private static int _nextTypeId;

    /// <summary>The suffix marking the non-virtual thunk that reaches a base implementation.</summary>
    /// <remarks>
    /// <c>$super.ToString()</c> must run the base's version. Calling it on the emitted object
    /// would dispatch virtually straight back into the override and recurse until the stack
    /// ran out, so each override is paired with a non-virtual thunk that a super reference
    /// calls instead.
    /// </remarks>
    public const string BaseThunkSuffix = "__tosh_base";

    private static readonly MethodInfo DispatchMethod =
        typeof(ToshClrDispatch).GetMethod(nameof(ToshClrDispatch.Invoke))!;

    private static readonly MethodInfo GetTypeFromHandle =
        typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!;

    /// <summary>
    /// The subclass to construct in place of <paramref name="baseType"/>, or null to use it
    /// directly.
    /// </summary>
    /// <param name="overriddenNames">
    /// The method names the tōsh class declares. Only virtuals it actually names are
    /// overridden — overriding everything would route members the class never mentioned
    /// through a dispatch that would only fail to find them.
    /// </param>
    public static Type? TryGetSubclass(Type baseType, IReadOnlyCollection<string> overriddenNames)
    {
        ArgumentNullException.ThrowIfNull(baseType);

        if (overriddenNames.Count == 0)
        {
            Explain(baseType, "the class declares no instance methods");
            return null;
        }

        if (!CanSubclass(baseType))
        {
            Explain(baseType, "the type cannot be subclassed here");
            return null;
        }

        var key = $"{baseType.AssemblyQualifiedName}|{string.Join(",", overriddenNames.OrderBy(n => n, StringComparer.Ordinal))}";

        return Cache.GetOrAdd(key, _ => Build(baseType, overriddenNames));
    }

    /// <summary>
    /// Says why a class did not get a real subclass, when asked.
    /// </summary>
    /// <remarks>
    /// Falling back is silent by design and that makes it invisible: the class keeps working,
    /// its overrides simply stop at the boundary, and there is nothing to read. Setting
    /// <c>TOSH_CLR_SUBCLASS_DIAGNOSTICS=1</c> makes the reason say itself rather than having
    /// to be inferred from behaviour.
    /// </remarks>
    private static void Explain(Type baseType, string reason)
        => Report($"tosh: no CLR subclass for '{baseType.Name}': {reason}");

    private static void Report(string line)
    {
        if (Environment.GetEnvironmentVariable("TOSH_CLR_SUBCLASS_DIAGNOSTICS") == "1")
        {
            Console.Error.WriteLine(line);
        }
    }

    private static bool CanSubclass(Type baseType) =>
        !baseType.IsSealed &&
        !baseType.IsValueType &&
        !baseType.IsInterface &&
        !baseType.ContainsGenericParameters &&
        baseType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => !c.IsPrivate && !c.GetParameters().Any(IsAwkward));

    /// <summary>A parameter this factory will not try to forward or box.</summary>
    private static bool IsAwkward(ParameterInfo parameter) =>
        parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer;

    private static Type? Build(Type baseType, IReadOnlyCollection<string> overriddenNames)
    {
        try
        {
            var id = Interlocked.Increment(ref _nextTypeId);
            var builder = NativeInteropModule.Module.DefineType(
                $"Tosh.Derived.{baseType.Name}_{id}",
                TypeAttributes.Public | TypeAttributes.Class,
                baseType,
                [typeof(IToshClrBackedObject)]);

            var toshField = builder.DefineField("_tosh", typeof(object), FieldAttributes.Private);

            DefineToshInstanceProperty(builder, toshField);
            DefineConstructors(builder, baseType);

            var names = new List<string>();

            foreach (var method in OverridableMethods(baseType, overriddenNames))
            {
                DefineOverride(builder, method, toshField);
                DefineBaseThunk(builder, method);
                names.Add(method.Name);
            }

            var overridden = names.Count;

            // Nothing was actually overridable, so a subclass would differ from the base in
            // name only and cost an emit for it.
            if (overridden == 0)
            {
                Explain(baseType, $"none of [{string.Join(", ", overriddenNames)}] names an overridable virtual");
                return null;
            }

            var created = builder.CreateType();

            Report($"tosh: emitted a CLR subclass of '{baseType.Name}' overriding {string.Join(", ", names)}");

            return created;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Emit is best-effort by design — see the type remarks. A base this factory
            // cannot express falls back to containment, which is what happened before.
            Explain(baseType, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// The virtuals worth overriding: named by the class, overridable, and expressible.
    /// </summary>
    private static IEnumerable<MethodInfo> OverridableMethods(
        Type baseType,
        IReadOnlyCollection<string> overriddenNames)
    {
        var wanted = new HashSet<string>(overriddenNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in baseType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!wanted.Contains(method.Name) ||
                !method.IsVirtual ||
                method.IsFinal ||
                method.IsPrivate ||
                method.IsGenericMethodDefinition ||
                method.ReturnType.IsByRef ||
                method.ReturnType.IsPointer ||
                method.GetParameters().Any(IsAwkward))
            {
                continue;
            }

            // One override per signature. A base that declares several overloads of a name
            // gets each of them, but a name reachable twice through the hierarchy is one
            // method to override.
            var signature = $"{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName))})";

            if (seen.Add(signature))
            {
                yield return method;
            }
        }
    }

    private static void DefineToshInstanceProperty(TypeBuilder builder, FieldBuilder toshField)
    {
        const MethodAttributes Attributes =
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Final;

        var getter = builder.DefineMethod("get_ToshInstance", Attributes, typeof(object), Type.EmptyTypes);
        var getIl = getter.GetILGenerator();
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, toshField);
        getIl.Emit(OpCodes.Ret);

        var setter = builder.DefineMethod("set_ToshInstance", Attributes, typeof(void), [typeof(object)]);
        var setIl = setter.GetILGenerator();
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Stfld, toshField);
        setIl.Emit(OpCodes.Ret);

        var property = builder.DefineProperty("ToshInstance", PropertyAttributes.None, typeof(object), null);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        builder.DefineMethodOverride(getter, typeof(IToshClrBackedObject).GetProperty(nameof(IToshClrBackedObject.ToshInstance))!.GetGetMethod()!);
        builder.DefineMethodOverride(setter, typeof(IToshClrBackedObject).GetProperty(nameof(IToshClrBackedObject.ToshInstance))!.GetSetMethod()!);
    }

    /// <summary>
    /// One constructor per accessible base constructor, forwarding unchanged.
    /// </summary>
    /// <remarks>
    /// The signatures are the base's own, so the ordinary overload binder picks between them
    /// exactly as it did when it was constructing the base directly — the construction site
    /// swaps the type and changes nothing else.
    /// </remarks>
    private static void DefineConstructors(TypeBuilder builder, Type baseType)
    {
        foreach (var baseConstructor in baseType.GetConstructors(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (baseConstructor.IsPrivate || baseConstructor.GetParameters().Any(IsAwkward))
            {
                continue;
            }

            var parameters = baseConstructor.GetParameters();
            var constructor = builder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                [.. parameters.Select(p => p.ParameterType)]);

            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);

            for (var index = 0; index < parameters.Length; index++)
            {
                EmitLoadArgument(il, index + 1);
            }

            il.Emit(OpCodes.Call, baseConstructor);
            il.Emit(OpCodes.Ret);
        }
    }

    private static void DefineOverride(TypeBuilder builder, MethodInfo method, FieldBuilder toshField)
    {
        var parameters = method.GetParameters();
        var parameterTypes = parameters.Select(p => p.ParameterType).ToArray();

        var over = builder.DefineMethod(
            method.Name,
            (method.Attributes & ~MethodAttributes.NewSlot & ~MethodAttributes.Abstract) | MethodAttributes.Virtual,
            method.ReturnType,
            parameterTypes);

        var il = over.GetILGenerator();
        var dispatch = il.DefineLabel();

        // No tōsh object attached — during the base constructor, or on a copy the platform
        // made for itself — so behave as the base does.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, toshField);
        il.Emit(OpCodes.Brtrue, dispatch);

        if (method.IsAbstract)
        {
            // There is no base implementation to call. Answering with the type's default is
            // the only thing left, and it only happens before the instance is attached.
            EmitDefault(il, method.ReturnType);
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0);

            for (var index = 0; index < parameters.Length; index++)
            {
                EmitLoadArgument(il, index + 1);
            }

            il.Emit(OpCodes.Call, method);
        }

        il.Emit(OpCodes.Ret);

        il.MarkLabel(dispatch);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, toshField);
        il.Emit(OpCodes.Ldstr, method.Name);

        EmitArgumentArray(il, parameterTypes);

        il.Emit(OpCodes.Ldtoken, method.ReturnType);
        il.Emit(OpCodes.Call, GetTypeFromHandle);
        il.Emit(OpCodes.Call, DispatchMethod);

        if (method.ReturnType == typeof(void))
        {
            il.Emit(OpCodes.Pop);
        }
        else
        {
            il.Emit(OpCodes.Unbox_Any, method.ReturnType);
        }

        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(over, method);
    }

    /// <summary>
    /// A non-virtual way back to the base implementation, for <c>$super</c>.
    /// </summary>
    private static void DefineBaseThunk(TypeBuilder builder, MethodInfo method)
    {
        if (method.IsAbstract)
        {
            // Nothing to reach. `$super` on an abstract member is a mistake, and a thunk that
            // called it would be a way to make the stack overflow look like one.
            return;
        }

        var parameters = method.GetParameters();
        var thunk = builder.DefineMethod(
            method.Name + BaseThunkSuffix,
            MethodAttributes.Public | MethodAttributes.HideBySig,
            method.ReturnType,
            [.. parameters.Select(p => p.ParameterType)]);

        var il = thunk.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);

        for (var index = 0; index < parameters.Length; index++)
        {
            EmitLoadArgument(il, index + 1);
        }

        // Call, not Callvirt: reaching the base implementation is the entire point.
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitArgumentArray(ILGenerator il, Type[] parameterTypes)
    {
        il.Emit(OpCodes.Ldc_I4, parameterTypes.Length);
        il.Emit(OpCodes.Newarr, typeof(object));

        for (var index = 0; index < parameterTypes.Length; index++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, index);
            EmitLoadArgument(il, index + 1);

            if (parameterTypes[index].IsValueType || parameterTypes[index].IsGenericParameter)
            {
                il.Emit(OpCodes.Box, parameterTypes[index]);
            }

            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    private static void EmitDefault(ILGenerator il, Type returnType)
    {
        if (returnType == typeof(void))
        {
            return;
        }

        if (returnType.IsValueType)
        {
            var local = il.DeclareLocal(returnType);
            il.Emit(OpCodes.Ldloca_S, local);
            il.Emit(OpCodes.Initobj, returnType);
            il.Emit(OpCodes.Ldloc, local);
            return;
        }

        il.Emit(OpCodes.Ldnull);
    }

    private static void EmitLoadArgument(ILGenerator il, int position)
    {
        switch (position)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); break;
            case 1: il.Emit(OpCodes.Ldarg_1); break;
            case 2: il.Emit(OpCodes.Ldarg_2); break;
            case 3: il.Emit(OpCodes.Ldarg_3); break;
            default: il.Emit(OpCodes.Ldarg, position); break;
        }
    }
}
