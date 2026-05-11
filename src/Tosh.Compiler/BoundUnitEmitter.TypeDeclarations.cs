using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;
using Tosh.Language.Parsing;
using Tosh.Runtime;
namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private static bool CanEmitClrClassShell(BoundClassDefinition cls)
    {
        if (cls.IsPartial) return false;
        foreach (var p in cls.PrimaryConstructorParameters)
        {
            if (p.IsRest) return false;
        }
        // At most one user-declared constructor is supported. Its
        // parameters drive the shell ctor signature when no primary
        // ctor is declared; otherwise the primary ctor wins and the
        // explicit ctor's body still gets lowered into the shell
        // ctor IL after field copies.
        var ctorCount = 0;
        foreach (var m in cls.Members)
        {
            switch (m)
            {
                case BoundClassPropertyMember prop:
                    if (prop.IsStatic) return false;
                    if (prop.IsLazy) return false;
                    if (prop.GetterBody is not null) return false;
                    if (prop.SetterBody is not null) return false;
                    continue;
                case BoundClassMethodMember:
                    // Methods (including override and abstract) are handled
                    // in DeclareClrClassShell — their presence doesn't
                    // disqualify the type from having a CLR shell.
                    continue;
                case BoundClassConstructorMember ctor:
                    if (++ctorCount > 1) return false;
                    foreach (var p in ctor.Parameters)
                    {
                        if (p.IsRest) return false;
                    }
                    continue;
                case BoundClassEventMember:
                    // Event members are emitted as EventBuilder infrastructure
                    // on the shell — they don't disqualify the type.
                    continue;
                default:
                    return false;
            }
        }
        // If both a primary ctor and an explicit ctor exist, only
        // the primary ctor's parameters drive the shell signature;
        // we don't try to model overloaded CLR ctors yet.
        return true;
    }

    /// <summary>
    /// True if <paramref name="rec"/> is a plain record (no
    /// <c>partial</c>); records are pure data shapes so almost any
    /// declaration qualifies. Default-value initializers are
    /// intentionally not lowered \u2014 the shell exposes the field
    /// names, source-replay still owns initial-value semantics.
    /// </summary>
    private static bool CanEmitClrRecordShell(BoundRecordDefinition rec)
    {
        return !rec.IsPartial;
    }

    /// <summary>
    /// True if <paramref name="st"/> can be emitted as a real CLR value-type
    /// shell: the struct must not be <c>partial</c> and must not contain
    /// members that require interpreter semantics (lazy props, getter/setter
    /// bodies, abstract props, rest params). Partial structs remain Tier 3
    /// because the full field set is not known at parse time.
    /// </summary>
    private static bool CanEmitClrStructShell(BoundStructDefinition st)
    {
        if (st.IsPartial) return false;
        foreach (var m in st.Members)
        {
            switch (m)
            {
                case BoundClassPropertyMember prop:
                    if (prop.IsLazy) return false;
                    if (prop.GetterBody is not null) return false;
                    if (prop.SetterBody is not null) return false;
                    if (prop.IsAbstract) return false;
                    continue;
                case BoundClassMethodMember:
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }


    /// the underlying type is one of the integral CLR enum primitives, every
    /// explicit value is a compile-time integral literal, and mangled member
    /// names stay unique. Dynamic/non-integral enum shapes remain Tier 3
    /// source replay so permissive builds keep the interpreter semantics.
    /// </summary>
    private static bool CanEmitClrEnumType(BoundEnumDefinition en)
    {
        if (!TryResolveClrEnumUnderlyingType(en.UnderlyingTypeName, out var underlying))
            return false;
        if (!TryBuildClrEnumLiteralValues(en, underlying, out _))
            return false;

        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in en.Members)
        {
            if (!memberNames.Add(MangleClrIdentifier(member.Name)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True if <paramref name="stmt"/> is a type definition that
    /// the emitter has produced a CLR shell for. The Tier-3
    /// diagnostic is suppressed for these.
    /// </summary>
    private bool IsClrShellEmittedTypeDefinition(BoundStatement stmt) =>
        stmt switch
        {
            BoundClassDefinition c => _clrTypeShells.ContainsKey(c.Name),
            BoundRecordDefinition r => _clrTypeShells.ContainsKey(r.Name),
            BoundEnumDefinition e => _clrEnumTypes.Contains(e.Name) || _clrEnumStaticShells.ContainsKey(e.Name),
            BoundInterfaceDefinition i => _clrTypeShells.ContainsKey(i.Name),
            BoundTraitDefinition t => _clrTypeShells.ContainsKey(t.Name),
            BoundStructDefinition s => _clrTypeShells.ContainsKey(s.Name),
            BoundEventDefinition ev => _clrTypeShells.ContainsKey(ev.Name),
            BoundUnionDefinition un => _clrUnionShells.ContainsKey(un.Name),
            BoundTypeAliasStatement ta => _clrAliasTypes.Contains(ta.Name),
            _ => false,
        };

    /// <summary>
    /// Emit a real CLR <c>enum</c> for a tosh enum definition. Member
    /// literals are defined with CLR-safe names; any renamed member gets
    /// <see cref="global::Tosh.Runtime.ToshOriginalNameAttribute"/> so tools
    /// can recover the source spelling.
    /// </summary>
    private void DeclareClrEnumType(BoundEnumDefinition en)
    {
        if (_clrEnumTypes.Contains(en.Name)) return;
        if (!TryResolveClrEnumUnderlyingType(en.UnderlyingTypeName, out var underlying))
            return;
        if (!TryBuildClrEnumLiteralValues(en, underlying, out var values))
            return;

        var enumBuilder = _moduleBuilder.DefineEnum(
            $"{_assemblyName}.{MangleClrIdentifier(en.Name)}",
            TypeAttributes.Public,
            MetadataType(underlying));
        StampToshTypeAttribute(enumBuilder, "enum", en.Span);
        StampOriginalNameIfMangled(enumBuilder, en.Name);

        for (var i = 0; i < en.Members.Count; i++)
        {
            var member = en.Members[i];
            var field = enumBuilder.DefineLiteral(MangleClrIdentifier(member.Name), values[i]);
            StampOriginalNameIfMangled(field, member.Name);
        }

        enumBuilder.CreateType();
        _clrEnumTypes.Add(en.Name);
    }

    /// <summary>
    /// Predicate matching enum declarations that cannot be expressed as a real
    /// CLR <c>enum</c> but can be represented as a static class with one
    /// <c>public static readonly object</c> field per member. Used as a Tier-2
    /// fallback so non-integral / dynamic-value enums no longer need source
    /// replay. Every member must carry an explicit literal value (auto-incrementing
    /// only makes sense for integral underlyings, which would have already been
    /// caught by <see cref="CanEmitClrEnumType"/>).
    /// </summary>
    private static bool CanEmitClrEnumStaticShell(BoundEnumDefinition en)
    {
        if (en.Members.Count == 0) return false;

        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in en.Members)
        {
            if (!memberNames.Add(MangleClrIdentifier(member.Name))) return false;
            // Every member needs a literal value. Auto-incrementing isn't
            // meaningful for non-integral underlyings.
            if (member.Value is null) return false;
            if (!TryGetLiteralDefaultValue(member.Value, out _)) return false;
        }

        return true;
    }

    /// <summary>
    /// Emit a CLR static class shell (<c>public sealed abstract class</c>) for an
    /// enum declaration whose members cannot fit a real CLR <c>enum</c>. Each
    /// member becomes a <c>public static readonly object</c> field initialised in
    /// the type's <c>.cctor</c>. Member access (<c>EnumName.Member</c>) is lowered
    /// to a direct <c>ldsfld</c> via <see cref="_clrEnumStaticShells"/>.
    /// </summary>
    private void DeclareClrEnumStaticShell(BoundEnumDefinition en)
    {
        if (_clrEnumStaticShells.ContainsKey(en.Name)) return;

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(en.Name)}",
            TypeAttributes.Public
                | TypeAttributes.Sealed
                | TypeAttributes.Abstract
                | TypeAttributes.Class
                | TypeAttributes.AutoLayout
                | TypeAttributes.AnsiClass
                | TypeAttributes.BeforeFieldInit);
        StampToshTypeAttribute(typeBuilder, "enum", en.Span);
        StampOriginalNameIfMangled(typeBuilder, en.Name);

        var fields = new Dictionary<string, FieldBuilder>(StringComparer.Ordinal);
        var literalValues = new object?[en.Members.Count];
        for (var i = 0; i < en.Members.Count; i++)
        {
            var member = en.Members[i];
            // CanEmitClrEnumStaticShell guarantees every Value is a literal.
            TryGetLiteralDefaultValue(member.Value!, out literalValues[i]);

            var field = typeBuilder.DefineField(
                MangleClrIdentifier(member.Name),
                typeof(object),
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
            StampOriginalNameIfMangled(field, member.Name);
            fields[member.Name] = field;
        }

        // Static constructor: initialise each field with its literal value.
        var cctor = typeBuilder.DefineTypeInitializer();
        var il = cctor.GetILGenerator();
        for (var i = 0; i < en.Members.Count; i++)
        {
            var member = en.Members[i];
            EmitConstantOnIL(il, literalValues[i]);
            il.Emit(OpCodes.Stsfld, fields[member.Name]);
        }
        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        _clrEnumStaticShells[en.Name] = new ClrEnumStaticShell(typeBuilder, fields);
    }

    /// <summary>
    /// Push a constant value onto an arbitrary <see cref="ILGenerator"/> as an
    /// <c>object</c>-typed stack slot. Used by the .cctor emitter for
    /// non-integral enum static shells.
    /// </summary>
    private static void EmitConstantOnIL(ILGenerator il, object? value)
    {
        switch (value)
        {
            case null:
                il.Emit(OpCodes.Ldnull);
                return;
            case string s:
                il.Emit(OpCodes.Ldstr, s);
                return;
            case bool b:
                il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Box, typeof(bool));
                return;
            case int i:
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Box, typeof(int));
                return;
            case long l:
                il.Emit(OpCodes.Ldc_I8, l);
                il.Emit(OpCodes.Box, typeof(long));
                return;
            case double d:
                il.Emit(OpCodes.Ldc_R8, d);
                il.Emit(OpCodes.Box, typeof(double));
                return;
            default:
                // Unknown literal type — fall back to null so .cctor still
                // emits valid IL. CanEmitClrEnumStaticShell shouldn't admit
                // anything not handled above.
                il.Emit(OpCodes.Ldnull);
                return;
        }
    }

    /// <summary>
    /// Emit a real CLR type hierarchy for a tosh <c>union</c> declaration.
    /// The shape is:
    /// <list type="bullet">
    ///   <item>An abstract base class (<c>public abstract class Result</c>)
    ///     with a public <c>string Variant</c> field and a protected
    ///     constructor <c>(string variant)</c> that sets it.</item>
    ///   <item>One sealed variant class per union variant
    ///     (<c>public sealed class Result_Ok</c> extending the base).
    ///     Each variant class has a public <c>object</c> field per
    ///     variant field and a constructor that chains the base ctor with
    ///     the variant name and fills the fields.</item>
    ///   <item>Unit variants (no fields) also get a sealed class plus a
    ///     <c>public static readonly</c> field on the base class and a
    ///     static initializer (<c>.cctor</c>) that pre-creates the
    ///     singleton.</item>
    /// </list>
    /// All types are registered in <see cref="_clrTypeShells"/> /
    /// <see cref="_clrShellsByType"/> so that <see cref="EmitMemberAccess"/>
    /// can lower <c>$r.Variant</c> to a direct <c>ldfld</c>, and the
    /// union-specific dispatch data goes into <see cref="_clrUnionShells"/>
    /// so <see cref="EmitStaticMethodCall"/> /
    /// <see cref="EmitExpression"/> can lower <c>Result.Ok(v)</c> /
    /// <c>Color.Red</c> to direct <c>newobj</c> / <c>ldsfld</c>.
    /// </summary>
    private void DeclareClrUnionShell(BoundUnionDefinition union)
    {
        if (_clrUnionShells.ContainsKey(union.Name)) return;

        // ── 1. Abstract base class ────────────────────────────────────
        var baseAttrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract;
        var baseType = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(union.Name)}",
            baseAttrs,
            MetadataType(typeof(object)));
        StampToshTypeAttribute(baseType, "union", union.Span);
        StampOriginalNameIfMangled(baseType, union.Name);

        // Public read-only "Variant" string field on the base.
        var variantField = baseType.DefineField(
            "Variant",
            MetadataType(typeof(string)),
            FieldAttributes.Public | FieldAttributes.InitOnly);

        // Protected ctor: base(object) + Variant = variant
        var baseCtor = baseType.DefineConstructor(
            MethodAttributes.Family | MethodAttributes.HideBySig
                | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            new[] { MetadataType(typeof(string)) });
        baseCtor.DefineParameter(1, ParameterAttributes.None, "variant");
        var baseCtorIl = baseCtor.GetILGenerator();
        baseCtorIl.Emit(OpCodes.Ldarg_0);
        baseCtorIl.Emit(OpCodes.Call, MetadataType(typeof(object)).GetConstructor(Type.EmptyTypes)!);
        baseCtorIl.Emit(OpCodes.Ldarg_0);
        baseCtorIl.Emit(OpCodes.Ldarg_1);
        baseCtorIl.Emit(OpCodes.Stfld, variantField);
        baseCtorIl.Emit(OpCodes.Ret);

        // ── 2. Variant classes ────────────────────────────────────────
        var variants = new Dictionary<string, ClrUnionVariantInfo>(StringComparer.OrdinalIgnoreCase);

        // Unit-variant singletons: we need the variant ctor before we can
        // emit the .cctor IL, so we collect them here and emit after the loop.
        var unitVariants = new List<(FieldBuilder SingletonField, ConstructorBuilder VariantCtor)>();

        foreach (var variant in union.Variants)
        {
            var mangledVariant = MangleClrIdentifier(variant.Name);
            var variantType = _moduleBuilder.DefineType(
                $"{_assemblyName}.{MangleClrIdentifier(union.Name)}_{mangledVariant}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                baseType);
            StampToshTypeAttribute(variantType, "union_variant", variant.Span);
            StampOriginalNameIfMangled(variantType, $"{union.Name}.{variant.Name}");

            // Variant-specific data fields
            var variantFields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in variant.Fields)
            {
                var fb = variantType.DefineField(
                    MangleClrIdentifier(field.Name),
                    MetadataType(typeof(object)),
                    FieldAttributes.Public);
                StampOriginalNameIfMangled(fb, field.Name);
                variantFields[field.Name] = fb;
            }

            // Constructor: (object f1, ...) → base("VariantName"), fill fields
            var isUnit = variant.Fields.Count == 0;
            var ctorParamTypes = new Type[variant.Fields.Count];
            for (var i = 0; i < ctorParamTypes.Length; i++) ctorParamTypes[i] = MetadataType(typeof(object));
            var variantCtor = variantType.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                ctorParamTypes);
            for (var i = 0; i < variant.Fields.Count; i++)
                variantCtor.DefineParameter(i + 1, ParameterAttributes.None, variant.Fields[i].Name);
            var variantCtorIl = variantCtor.GetILGenerator();
            variantCtorIl.Emit(OpCodes.Ldarg_0);
            variantCtorIl.Emit(OpCodes.Ldstr, variant.Name);
            variantCtorIl.Emit(OpCodes.Call, baseCtor);
            for (var i = 0; i < variant.Fields.Count; i++)
            {
                variantCtorIl.Emit(OpCodes.Ldarg_0);
                variantCtorIl.Emit(OpCodes.Ldarg, i + 1);
                variantCtorIl.Emit(OpCodes.Stfld, variantFields[variant.Fields[i].Name]);
            }
            variantCtorIl.Emit(OpCodes.Ret);

            // Unit variants: static readonly singleton field on the base class
            FieldBuilder? unitSingletonField = null;
            if (isUnit)
            {
                unitSingletonField = baseType.DefineField(
                    $"_unit_{mangledVariant}",
                    baseType,  // typed as abstract base (widened)
                    FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
                unitVariants.Add((unitSingletonField, variantCtor));
            }

            // Register variant as a ClrTypeShell for field-access dispatch
            var variantParamNames = new string[variant.Fields.Count];
            for (var i = 0; i < variantParamNames.Length; i++) variantParamNames[i] = variant.Fields[i].Name;
            var variantShell = new ClrTypeShell(
                $"{union.Name}.{variant.Name}",
                variantType,
                variantCtor,
                ctorParamTypes,
                variantParamNames,
                variantFields,
                supportsDirectNewObj: false);  // conservative: always use newobj path explicitly
            _clrTypeShells[$"{union.Name}.{variant.Name}"] = variantShell;
            _clrShellsByType[variantType] = variantShell;

            variants[variant.Name] = new ClrUnionVariantInfo(
                variant.Name, variantType, variantCtor, variantFields, unitSingletonField);
        }

        // Base class .cctor to pre-create unit-variant singletons
        if (unitVariants.Count > 0)
        {
            var cctor = baseType.DefineTypeInitializer();
            var cctorIl = cctor.GetILGenerator();
            foreach (var (singletonField, variantCtor) in unitVariants)
            {
                cctorIl.Emit(OpCodes.Newobj, variantCtor);
                cctorIl.Emit(OpCodes.Stsfld, singletonField);
            }
            cctorIl.Emit(OpCodes.Ret);
        }

        // Base class shell — interface-style ctor (no primary ctor, just the
        // Variant field for direct ldfld dispatch)
        var baseShell = new ClrTypeShell(union.Name, baseType,
            methods: new Dictionary<string, MethodBuilder>());
        baseShell.Fields["Variant"] = variantField;
        _clrTypeShells[union.Name] = baseShell;
        _clrShellsByType[baseType] = baseShell;

        _clrUnionShells[union.Name] = new ClrUnionShell(union.Name, baseType, variantField, variants);
    }

    /// <summary>
    /// Emit a real CLR <c>interface</c> for one tosh <c>interface</c>
    /// declaration. Each method signature becomes a public abstract
    /// virtual method on the interface type. All parameters and return
    /// types are typed <c>object</c> — tosh interfaces are structurally
    /// untyped at the CLR level. Method bodies are not emitted (abstract
    /// contract only). The interface is stored in <see cref="_clrTypeShells"/>
    /// so callers can resolve it by name and
    /// <see cref="IsClrShellEmittedTypeDefinition"/> suppresses the
    /// Tier-3 source-replay diagnostic for these.
    /// </summary>
    private void DeclareClrInterfaceShell(BoundInterfaceDefinition iface)
    {
        if (_clrTypeShells.ContainsKey(iface.Name)) return;

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(iface.Name)}",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        StampToshTypeAttribute(typeBuilder, "interface", iface.Span);
        StampOriginalNameIfMangled(typeBuilder, iface.Name);

        var methods = new Dictionary<string, MethodBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in iface.Methods)
        {
            var paramTypes = new Type[sig.Parameters.Count];
            for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = MetadataType(typeof(object));
            var mb = typeBuilder.DefineMethod(
                MangleClrIdentifier(sig.Name),
                MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
                    | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                MetadataType(typeof(object)),
                paramTypes);
            for (var i = 0; i < sig.Parameters.Count; i++)
            {
                mb.DefineParameter(i + 1, ParameterAttributes.None, sig.Parameters[i].Name);
            }
            StampOriginalNameIfMangled(mb, sig.Name);
            methods[sig.Name] = mb;
        }

        typeBuilder.CreateType();
        _clrTypeShells[iface.Name] = new ClrTypeShell(
            iface.Name,
            typeBuilder,
            methods);
    }

    /// <summary>
    /// Emit a CLR interface for one tosh <c>trait</c> declaration.
    /// Methods without a <c>DefaultBody</c> become abstract interface
    /// method signatures. Methods with a <c>DefaultBody</c> are emitted
    /// as Default Interface Methods (DIM) — their IL bodies are queued
    /// for deferred emission via <see cref="_clrClassMethodBodies"/> so
    /// they run after <c>Program</c> is finalized.
    /// Trait properties are structural hints only; they are not promoted
    /// to CLR methods by this pass.
    /// </summary>
    private void DeclareClrTraitShell(BoundTraitDefinition trait)
    {
        if (_clrTypeShells.ContainsKey(trait.Name)) return;

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(trait.Name)}",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        StampToshTypeAttribute(typeBuilder, "trait", trait.Span);
        StampOriginalNameIfMangled(typeBuilder, trait.Name);

        var methods = new Dictionary<string, MethodBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in trait.Methods)
        {
            var paramTypes = new Type[sig.Parameters.Count];
            for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = MetadataType(typeof(object));

            if (sig.DefaultBody is null)
            {
                // Abstract method — implementing class must provide a body.
                var mb = typeBuilder.DefineMethod(
                    MangleClrIdentifier(sig.Name),
                    MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
                        | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    MetadataType(typeof(object)),
                    paramTypes);
                for (var i = 0; i < sig.Parameters.Count; i++)
                    mb.DefineParameter(i + 1, ParameterAttributes.None, sig.Parameters[i].Name);
                StampOriginalNameIfMangled(mb, sig.Name);
                methods[sig.Name] = mb;
            }
            else
            {
                // Default Interface Method (DIM) — body emitted in deferred pass.
                var mb = typeBuilder.DefineMethod(
                    MangleClrIdentifier(sig.Name),
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot
                        | MethodAttributes.HideBySig,
                    CallingConventions.HasThis,
                    MetadataType(typeof(object)),
                    paramTypes);
                for (var i = 0; i < sig.Parameters.Count; i++)
                    mb.DefineParameter(i + 1, ParameterAttributes.None, sig.Parameters[i].Name);
                StampOriginalNameIfMangled(mb, sig.Name);
                methods[sig.Name] = mb;
            }
        }

        var traitShell = new ClrTypeShell(trait.Name, typeBuilder, methods);
        _clrTypeShells[trait.Name] = traitShell;
        _clrShellsByType[typeBuilder] = traitShell;

        // Queue DIM bodies (methods with a DefaultBody) for deferred IL emission.
        foreach (var sig in trait.Methods)
        {
            if (sig.DefaultBody is null) continue;
            if (!methods.TryGetValue(sig.Name, out var mb)) continue;
            // Wrap the bound trait method in a synthetic BoundFunctionDefinition so
            // EmitClrClassMethodBodies can drive the body via the shared IL emitter.
            var syntheticFn = new BoundFunctionDefinition(
                Name: sig.Name,
                Symbol: new BoundSymbol(sig.Name, BoundSymbolKind.Parameter, ScopeDepth: 0, DeclaredType: BoundType.Dynamic),
                Parameters: sig.Parameters,
                ReturnTypeName: sig.ReturnTypeName,
                Body: sig.DefaultBody,
                Captures: Array.Empty<BoundSymbol>(),
                IsCommandWrapper: false,
                Modifier: trait.Modifier,
                Span: sig.Span);
            _clrClassMethodBodies.Add(new ClrClassMethodPending(traitShell, mb, syntheticFn));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> for any tosh <c>type</c> alias that can
    /// be promoted to a real CLR sealed-class shell. Non-generic aliases
    /// (with or without a refinement predicate) are eligible — generic
    /// aliases require open CLR generic types which are deferred to Tier-3
    /// source replay.
    /// </summary>
    private static bool CanEmitClrAliasShell(BoundTypeAliasStatement ta)
        => true;

    /// <summary>
    /// Emit a <c>public sealed class</c> for one tosh <c>type</c> alias that
    /// implements <see cref="global::Tosh.Runtime.IShellRefinementTypeDescriptor"/>
    /// and is stamped with <c>[ToshTypeAttribute("alias")]</c>. The class is a
    /// metadata-only carrier — it is never instantiated by the runtime; its
    /// purpose is to make the alias discoverable via CLR reflection (e.g. from
    /// <c>DotNetTypeResolver</c> and tooling). For refinement aliases the engine
    /// still registers a source-replay slice so that <c>ToshHost.CheckType</c>
    /// can evaluate the predicate; for simple (non-refinement) aliases the CLR
    /// shell is the complete representation.
    /// </summary>
    private void DeclareClrAliasShell(BoundTypeAliasStatement ta)
    {
        if (_clrAliasTypes.Contains(ta.Name)) return;

        var ifaceType = MetadataType(typeof(global::Tosh.Runtime.IShellRefinementTypeDescriptor));

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(ta.Name)}",
            TypeAttributes.Public | TypeAttributes.Sealed,
            MetadataType(typeof(object)));

        if (ta.TypeParameters.Count > 0)
        {
            var genericNames = new string[ta.TypeParameters.Count];
            for (var i = 0; i < genericNames.Length; i++)
                genericNames[i] = MangleClrIdentifier(ta.TypeParameters[i]);
            typeBuilder.DefineGenericParameters(genericNames);
        }

        typeBuilder.AddInterfaceImplementation(ifaceType);

        StampToshTypeAttribute(typeBuilder, "alias", ta.Span);
        StampOriginalNameIfMangled(typeBuilder, ta.Name);

        // Explicit interface implementation for IShellRefinementTypeDescriptor.Name
        var getNameGetter = ifaceType.GetProperty(nameof(global::Tosh.Runtime.IShellRefinementTypeDescriptor.Name))!.GetGetMethod()!;
        var nameMethod = typeBuilder.DefineMethod(
            $"{ifaceType.FullName}.get_Name",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Final,
            typeof(string), Type.EmptyTypes);
        var nameIl = nameMethod.GetILGenerator();
        nameIl.Emit(OpCodes.Ldstr, ta.Name);
        nameIl.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(nameMethod, getNameGetter);

        // Explicit interface implementation for IShellRefinementTypeDescriptor.BaseTypeName
        var getBaseGetter = ifaceType.GetProperty(nameof(global::Tosh.Runtime.IShellRefinementTypeDescriptor.BaseTypeName))!.GetGetMethod()!;
        var baseMethod = typeBuilder.DefineMethod(
            $"{ifaceType.FullName}.get_BaseTypeName",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Final,
            typeof(string), Type.EmptyTypes);
        var baseIl = baseMethod.GetILGenerator();
        baseIl.Emit(OpCodes.Ldstr, ta.BaseTypeName);
        baseIl.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(baseMethod, getBaseGetter);

        // Explicit interface implementation for IShellRefinementTypeDescriptor.Description (returns null)
        var getDescGetter = ifaceType.GetProperty(nameof(global::Tosh.Runtime.IShellRefinementTypeDescriptor.Description))!.GetGetMethod()!;
        var descMethod = typeBuilder.DefineMethod(
            $"{ifaceType.FullName}.get_Description",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.Final,
            typeof(string), Type.EmptyTypes);
        var descIl = descMethod.GetILGenerator();
        descIl.Emit(OpCodes.Ldnull);
        descIl.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(descMethod, getDescGetter);

        typeBuilder.CreateType();
        _clrAliasTypes.Add(ta.Name);
    }

    private bool ProgramHasCompiledAliasRegistration()
    {
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundTypeAliasStatement ta
                && _clrAliasTypes.Contains(ta.Name)
                && (ta.Refinement is not null || ta.TypeParameters.Count > 0))
                return true;
        }

        return false;
    }

    /// <summary>
    /// First-class .NET plan, step 7. Maps a tosh native type name
    /// to the CLR primitive (or <see cref="string"/>) used by P/Invoke
    /// marshaling. <c>string</c>/<c>cstring</c>/<c>cstr</c> all map to
    /// <see cref="string"/>; the caller is responsible for applying
    /// the right <c>MarshalAs</c> on the parameter. Returns <c>null</c>
    /// for shapes the emitter doesn't handle yet (custom marshaling,
    /// struct-by-value), which causes the bind statement to fall back
    /// to source replay.
    /// </summary>
    private static Type? TryMapNativeBindType(string? name)
    {
        if (string.IsNullOrEmpty(name)) return typeof(void);
        return name.ToLowerInvariant() switch
        {
            "int" => typeof(int),
            "uint" => typeof(uint),
            "long" => typeof(long),
            "ulong" => typeof(ulong),
            "short" => typeof(short),
            "ushort" => typeof(ushort),
            "byte" => typeof(byte),
            "sbyte" => typeof(sbyte),
            "double" => typeof(double),
            "float" => typeof(float),
            "bool" => typeof(bool),
            "nint" or "ptr" => typeof(IntPtr),
            "nuint" or "uptr" => typeof(UIntPtr),
            "string" or "cstring" or "cstr" => typeof(string),
            "void" => typeof(void),
            _ => null,
        };
    }

    private static bool IsNativeBindStringTypeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.ToLowerInvariant() is "string" or "cstring" or "cstr";
    }

    private static System.Runtime.InteropServices.CallingConvention ParseNativeBindCallConv(string? name)
    {
        if (string.IsNullOrEmpty(name)) return System.Runtime.InteropServices.CallingConvention.Cdecl;
        return name.ToLowerInvariant() switch
        {
            "cdecl" => System.Runtime.InteropServices.CallingConvention.Cdecl,
            "stdcall" => System.Runtime.InteropServices.CallingConvention.StdCall,
            "winapi" => System.Runtime.InteropServices.CallingConvention.Winapi,
            "thiscall" => System.Runtime.InteropServices.CallingConvention.ThisCall,
            "fastcall" => System.Runtime.InteropServices.CallingConvention.FastCall,
            _ => System.Runtime.InteropServices.CallingConvention.Cdecl,
        };
    }

    /// <summary>
    /// Predicate: every function in the bind block must use
    /// parameter and return types the emitter knows how to lower
    /// directly into a CLR P/Invoke method. Phase 1 covered
    /// primitive scalars only; phase 2 adds <c>string</c>/<c>cstring</c>
    /// (<c>In</c> only) and <c>ref</c>/<c>out</c> on primitive scalars.
    /// Anything else (<c>ref</c>/<c>out</c> string, struct-by-value,
    /// unknown type names) still routes to source replay.
    /// </summary>
    private bool CanEmitNativeBindShell(BoundBindStatement bind)
    {
        if (bind.NativeTarget is null) return false;
        if (string.IsNullOrEmpty(bind.ModuleName)) return false;
        if (_clrTypeShells.ContainsKey(bind.ModuleName)) return false;
        if (_clrModules.ContainsKey(bind.ModuleName)) return false;
        foreach (var fn in bind.Functions)
        {
            if (TryMapNativeBindType(fn.ReturnTypeName) is null) return false;
            foreach (var p in fn.Parameters)
            {
                if (TryMapNativeBindType(p.TypeName) is null) return false;
                if (p.PassingMode != NativeParameterPassingMode.In)
                {
                    // by-ref string marshaling needs explicit
                    // pointer types; mirror the engine's rejection.
                    if (IsNativeBindStringTypeName(p.TypeName)) return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Emit a public sealed abstract CLR class with one
    /// <c>[DllImport]</c> static method per native function in the
    /// bind block. The class is stamped with
    /// <see cref="ToshModuleShellAttribute"/> so
    /// <c>ToshHost.RegisterCompiledAssembly</c> wires it up for
    /// qualified-method dispatch (<c>LibC.abs(-5)</c>).
    /// </summary>
    private void DeclareNativeBindShell(BoundBindStatement bind)
    {
        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(bind.ModuleName)}",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract,
            MetadataType(typeof(object)));
        StampOriginalNameIfMangled(typeBuilder, bind.ModuleName);

        var moduleShellAttrCtor = typeof(global::Tosh.Runtime.ToshModuleShellAttribute)
            .GetConstructor(new[] { typeof(string) })!;
        typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            moduleShellAttrCtor, new object[] { bind.ModuleName }));

        var marshalAsCtor = typeof(System.Runtime.InteropServices.MarshalAsAttribute)
            .GetConstructor(new[] { typeof(System.Runtime.InteropServices.UnmanagedType) })!;

        foreach (var fn in bind.Functions)
        {
            var returnElement = TryMapNativeBindType(fn.ReturnTypeName) ?? typeof(void);
            var returnIsString = IsNativeBindStringTypeName(fn.ReturnTypeName);
            var paramTypes = new Type[fn.Parameters.Count];
            for (var i = 0; i < fn.Parameters.Count; i++)
            {
                var element = TryMapNativeBindType(fn.Parameters[i].TypeName)!;
                paramTypes[i] = fn.Parameters[i].PassingMode == NativeParameterPassingMode.In
                    ? element
                    : element.MakeByRefType();
            }

            var entryPoint = string.IsNullOrEmpty(fn.SymbolName) ? fn.Name : fn.SymbolName;
            var pinvoke = typeBuilder.DefinePInvokeMethod(
                MangleClrIdentifier(fn.Name),
                bind.NativeTarget!,
                entryPoint,
                MethodAttributes.Public | MethodAttributes.Static
                    | MethodAttributes.HideBySig | MethodAttributes.PinvokeImpl,
                CallingConventions.Standard,
                MetadataType(returnElement),
                MetadataTypes(paramTypes),
                ParseNativeBindCallConv(fn.CallingConventionName),
                System.Runtime.InteropServices.CharSet.Ansi);
            pinvoke.SetImplementationFlags(
                pinvoke.GetMethodImplementationFlags() | MethodImplAttributes.PreserveSig);
            StampOriginalNameIfMangled(pinvoke, fn.Name);

            if (returnIsString)
            {
                // [return: MarshalAs(UnmanagedType.LPStr)] — treat
                // tosh `string`/`cstring`/`cstr` returns as ANSI/UTF-8
                // C strings, matching the engine's default.
                var returnParam = pinvoke.DefineParameter(
                    0, ParameterAttributes.None, null);
                returnParam.SetCustomAttribute(new CustomAttributeBuilder(
                    marshalAsCtor,
                    new object[] { System.Runtime.InteropServices.UnmanagedType.LPStr }));
            }

            for (var i = 0; i < fn.Parameters.Count; i++)
            {
                var p = fn.Parameters[i];
                var paramAttrs = p.PassingMode switch
                {
                    NativeParameterPassingMode.Out => ParameterAttributes.Out,
                    NativeParameterPassingMode.Ref => ParameterAttributes.In | ParameterAttributes.Out,
                    _ => ParameterAttributes.None,
                };
                var pb = pinvoke.DefineParameter(i + 1, paramAttrs,
                    string.IsNullOrEmpty(p.Name) ? $"arg{i}" : p.Name);

                if (IsNativeBindStringTypeName(p.TypeName))
                {
                    pb.SetCustomAttribute(new CustomAttributeBuilder(
                        marshalAsCtor,
                        new object[] { System.Runtime.InteropServices.UnmanagedType.LPStr }));
                }
            }
        }

        typeBuilder.CreateType();
        _clrNativeBinds.Add(bind);
    }

    /// <summary>
    /// Emit a real CLR value-type shell for one tosh <c>struct</c>
    /// declaration. The CLR type inherits from <see cref="System.ValueType"/>
    /// and is <c>public sealed</c>. Fields from the struct's primary
    /// constructor parameters become public instance fields typed
    /// <c>object</c>. A positional constructor is emitted that copies
    /// each argument into the matching field. Member properties become
    /// additional public fields. Method bodies are not lowered — callers
    /// go through <c>ToshHost</c> for behavior; the CLR shell is
    /// reflectable for shape inspection.
    /// </summary>
    private void DeclareClrStructShell(BoundStructDefinition st)
    {
        if (_clrTypeShells.ContainsKey(st.Name)) return;

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(st.Name)}",
            TypeAttributes.Public | TypeAttributes.Sealed,
            MetadataType(typeof(ValueType)));
        StampToshTypeAttribute(typeBuilder, "struct", st.Span);
        StampOriginalNameIfMangled(typeBuilder, st.Name);

        // Primary constructor fields.
        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in st.Fields)
        {
            if (fields.ContainsKey(f.Name)) continue;
            var fb = typeBuilder.DefineField(
                MangleClrIdentifier(f.Name),
                MetadataType(typeof(object)),
                FieldAttributes.Public);
            StampOriginalNameIfMangled(fb, f.Name);
            fields[f.Name] = fb;
        }

        // Additional storage properties from member declarations.
        foreach (var m in st.Members)
        {
            if (m is BoundClassPropertyMember prop && !fields.ContainsKey(prop.Name))
            {
                var fieldAttrs = MapPropertyVisibility(prop);
                if (prop.IsFixed) fieldAttrs |= FieldAttributes.InitOnly;
                var fb = typeBuilder.DefineField(
                    MangleClrIdentifier(prop.Name),
                    MetadataType(typeof(object)),
                    fieldAttrs);
                StampOriginalNameIfMangled(fb, prop.Name);
                fields[prop.Name] = fb;
            }
        }

        // Positional constructor: one `object` parameter per primary field.
        // Value types must NOT call base..ctor() — the runtime zero-initialises.
        var paramTypes = new Type[st.Fields.Count];
        var paramNames = new string[st.Fields.Count];
        for (var i = 0; i < paramTypes.Length; i++)
        {
            paramTypes[i] = MetadataType(typeof(object));
            paramNames[i] = st.Fields[i].Name;
        }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < st.Fields.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, st.Fields[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        // No base..ctor call for value types.
        for (var i = 0; i < st.Fields.Count; i++)
        {
            if (!fields.TryGetValue(st.Fields[i].Name, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }
        ctorIl.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        var shell = new ClrTypeShell(st.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: false);
        _clrTypeShells[st.Name] = shell;
        _clrShellsByType[typeBuilder] = shell;
    }

    /// <summary>
    /// Emit a real CLR <c>public sealed class</c> for one tosh top-level
    /// <c>event</c> declaration. Each field becomes a public mutable
    /// instance field typed <c>object</c>. A positional constructor
    /// matching the field order is emitted so compiled call sites can
    /// construct event payloads directly. The type is stamped with
    /// <c>[ToshTypeAttribute("event")]</c> so runtime tooling can
    /// distinguish event payloads from plain records.
    /// </summary>
    private void DeclareClrEventTypeShell(BoundEventDefinition ev)
    {
        if (_clrTypeShells.ContainsKey(ev.Name)) return;

        var attrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed;
        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(ev.Name)}",
            attrs,
            MetadataType(typeof(object)));
        StampToshTypeAttribute(typeBuilder, "event", ev.Span);
        StampOriginalNameIfMangled(typeBuilder, ev.Name);

        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ev.Fields)
        {
            if (fields.ContainsKey(f.Name)) continue;
            var fb = typeBuilder.DefineField(
                MangleClrIdentifier(f.Name),
                MetadataType(typeof(object)),
                FieldAttributes.Public);
            StampOriginalNameIfMangled(fb, f.Name);
            fields[f.Name] = fb;
        }

        // Positional ctor: one `object` parameter per field.
        var paramTypes = new Type[ev.Fields.Count];
        var paramNames = new string[ev.Fields.Count];
        for (var i = 0; i < paramTypes.Length; i++)
        {
            paramTypes[i] = MetadataType(typeof(object));
            paramNames[i] = ev.Fields[i].Name;
        }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < ev.Fields.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, ev.Fields[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, MetadataType(typeof(object)).GetConstructor(System.Type.EmptyTypes)!);
        for (var i = 0; i < ev.Fields.Count; i++)
        {
            if (!fields.TryGetValue(ev.Fields[i].Name, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }
        ctorIl.Emit(OpCodes.Ret);

        var evShell = new ClrTypeShell(ev.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: true);
        _clrTypeShells[ev.Name] = evShell;
        _clrShellsByType[typeBuilder] = evShell;
    }

    /// <summary>
    /// Emit a real CLR class shell for one tosh
    /// <c>class</c> declaration. Storage properties become
    /// public mutable instance fields typed <c>object</c>; the
    /// constructor takes one <c>object</c> parameter per primary
    /// constructor parameter and copies each one into the matching-
    /// name field (case-insensitive) when one exists. Method bodies
    /// are not lowered \u2014 callers go through <c>ToshHost</c> for
    /// behavior, the CLR type is reflectable for shape only.
    /// </summary>
    /// <summary>
    /// Ensures the base class shell for <paramref name="baseName"/> is
    /// declared before the derived class attempts to reference its
    /// <see cref="TypeBuilder"/> as a parent. Scans the top-level unit
    /// statements for the matching <see cref="BoundClassDefinition"/> and
    /// recursively calls <see cref="DeclareClrClassShell"/> — the guard
    /// at the top of that method prevents infinite loops on circular
    /// references (which the binder would have rejected anyway).
    /// </summary>
}
