using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;
namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private static readonly ConstructorInfo s_paramArrayCtor =
        typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!;

    /// <summary>
    /// Stamp ABI-relevant metadata on a typed top-level function's
    /// parameter:
    /// <list type="bullet">
    ///   <item><c>name</c> — always (for tooling and reflection).</item>
    ///   <item><see cref="ParameterAttributes.HasDefault"/> +
    ///       <see cref="ParameterAttributes.Optional"/> — when the parameter
    ///       has a literal default expression. C# / F# / VB consumers can
    ///       then call the function omitting trailing arguments, and
    ///       reflection-driven tooling sees the default constant.</item>
    ///   <item><see cref="ParamArrayAttribute"/> — when the parameter is
    ///       declared as a rest parameter and the resolved CLR type is an
    ///       array type. C# consumers can then call with <c>params</c>-style
    ///       variadic argument lists.</item>
    /// </list>
    /// Part of the public CLR ABI v1 (see <c>docs/CLR_ABI_v1.md</c>).
    /// </summary>
    private static void StampTypedParameterAbi(
        MethodBuilder method,
        int index,
        BoundParameter param,
        Type paramClrType)
    {
        var attrs = ParameterAttributes.None;
        object? literalDefault = null;
        var hasLiteralDefault = false;
        if (param.Default is not null && TryGetLiteralDefaultValue(param.Default, out literalDefault))
        {
            attrs |= ParameterAttributes.HasDefault | ParameterAttributes.Optional;
            hasLiteralDefault = true;
        }
        else if (param.IsOptional)
        {
            // Optional but with a non-literal default: surface as Optional
            // so language tooling treats trailing args as omittable, but
            // do not stamp HasDefault — there's no constant to record.
            attrs |= ParameterAttributes.Optional;
        }

        var pb = method.DefineParameter(index + 1, attrs, param.Name);
        if (hasLiteralDefault)
        {
            try
            {
                pb.SetConstant(literalDefault);
            }
            catch (ArgumentException)
            {
                // SetConstant only accepts certain primitive / string /
                // null shapes. If the literal is something else, leave
                // HasDefault unstamped silently — the body still applies
                // it dynamically at runtime.
            }
        }

        if (param.IsRest && paramClrType.IsArray)
        {
            pb.SetCustomAttribute(new CustomAttributeBuilder(s_paramArrayCtor, Array.Empty<object>()));
        }
    }

    /// <summary>
    /// Promotes every <see cref="BoundScriptInputStatement"/> parameter
    /// symbol in <paramref name="stmts"/> (and recursively in nested
    /// <see cref="BoundSubcommandStatement"/> bodies) to a static field
    /// on <see cref="_program"/>.  Registered in
    /// <see cref="_staticFields"/> so that <see cref="EmitVariableReference"/>
    /// emits <c>ldsfld</c> for them without further changes.
    /// </summary>
    private void PromoteSubcommandInputsAsStaticFields(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                {
                    if (_staticFields.ContainsKey(param.Symbol)) continue;
                    var field = _program.DefineField(
                        $"_scriptinput_{param.Name}_{_staticFields.Count}",
                        MetadataType(typeof(object)),
                        FieldAttributes.Private | FieldAttributes.Static);
                    _staticFields[param.Symbol] = field;
                }
            }
            else if (stmt is BoundSubcommandStatement sub)
            {
                PromoteSubcommandInputsAsStaticFields(sub.Body.Statements);
            }
        }
    }

    /// <summary>
    /// Emits the compiled subcommand dispatch path in <c>Main</c>:
    /// for each subcommand a private static body method is defined,
    /// then a <see cref="CompiledSubcommandNode"/> tree is built
    /// inline and passed to
    /// <see cref="ToshHost.RunCompiledSubcommandDispatch"/>.
    /// Also emits any top-level user-function bodies needed by the
    /// subcommand bodies.
    /// </summary>
    private void EmitCompiledSubcommandDispatch()
    {
        // Emit top-level function definitions (callable from bodies).
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundFunctionDefinition func)
                EmitUserFunctionBody(func);
        }

        // Require Tier 2 for the ToshHost dispatch call.
        RequireTier(2, "subcommand-tree dispatch (compiled)");

        // Build the root CompiledSubcommandNode on the IL stack,
        // then call ToshHost.RunCompiledSubcommandDispatch(argv, root).
        var rootSubcommands = _unit.Root.Statements
            .OfType<BoundSubcommandStatement>()
            .ToList();
        var rootInputs = GetScriptInputParams(_unit.Root.Statements);

        // Build child nodes into IL locals (bottom-up).
        var childLocals = new Dictionary<BoundSubcommandStatement, LocalBuilder>();
        foreach (var sub in rootSubcommands)
        {
            var local = _il.DeclareLocal(s_compiledSubcommandNodeType);
            EmitSubcommandNodeToLocal(sub, sub.Name, local);
            childLocals[sub] = local;
        }

        // Root body method (binds root flags + runs root setup).
        MethodBuilder? rootBodyMethod = EmitSubcommandBodyMethodForStatements(
            rootInputs.flags, rootInputs.args,
            _unit.Root.Statements
                .Where(static s => s is not BoundSubcommandStatement
                                  && s is not BoundScriptInputStatement
                                  && s is not BoundFunctionDefinition
                                  && !IsTypeDefinitionStatement(s, out _)
                                  && s is not BoundModuleDefinition)
                .ToList(),
            qualName: "__subcommand_root");

        // Build root node on IL stack.
        // argv
        _il.Emit(OpCodes.Ldarg_0);
        // root node
        EmitSubcommandNodeExpression(
            name: null,
            modifiers: SubcommandModifier.None,
            userDeclaredHelpFlag: false,
            flags: rootInputs.flags,
            args: rootInputs.args,
            childNames: rootSubcommands.Select(s => s.Name).ToArray(),
            childLocals: childLocals.Values.ToArray(),
            bodyMethod: rootBodyMethod);

        _il.Emit(OpCodes.Call, s_hostRunCompiledSubcommandDispatch);
    }

    /// <summary>
    /// Emits a <see cref="CompiledSubcommandNode"/> for a nested
    /// <paramref name="sub"/> into a fresh IL local (so it can be
    /// consumed by the parent's <c>children[]</c> array).  Recurses
    /// into any nested subcommands first.
    /// </summary>
    private void EmitSubcommandNodeToLocal(
        BoundSubcommandStatement sub,
        string qualName,
        LocalBuilder targetLocal)
    {
        var inputs = GetScriptInputParams(sub.Body.Statements);
        var children = sub.Body.Statements.OfType<BoundSubcommandStatement>().ToList();

        // Recurse first (bottom-up construction).
        var childLocals = new Dictionary<BoundSubcommandStatement, LocalBuilder>();
        foreach (var child in children)
        {
            var childLocal = _il.DeclareLocal(s_compiledSubcommandNodeType);
            EmitSubcommandNodeToLocal(child, $"{qualName}_{child.Name}", childLocal);
            childLocals[child] = childLocal;
        }

        // Emit body method.
        MethodBuilder? bodyMethod = EmitSubcommandBodyMethodForStatements(
            inputs.flags, inputs.args,
            sub.Body.Statements
                .Where(static s => s is not BoundSubcommandStatement
                                  && s is not BoundScriptInputStatement)
                .ToList(),
            qualName: $"__subcommand_{qualName}_{_subcommandBodyCounter++}");

        // Determine if user declared their own --help flag.
        var userDeclaredHelp = inputs.flags.Any(
            static p => string.Equals(p.Name, "help", StringComparison.OrdinalIgnoreCase));

        // Push node onto stack, then store in targetLocal.
        EmitSubcommandNodeExpression(
            name: sub.Name,
            modifiers: sub.Modifiers,
            userDeclaredHelpFlag: userDeclaredHelp,
            flags: inputs.flags,
            args: inputs.args,
            childNames: children.Select(c => c.Name).ToArray(),
            childLocals: childLocals.Values.ToArray(),
            bodyMethod: bodyMethod);
        _il.Emit(OpCodes.Stloc, targetLocal);
    }

    /// <summary>
    /// Extracts all <see cref="BoundScriptInputStatement"/> parameters
    /// from <paramref name="stmts"/>, returning flags (Kind=Flag) and
    /// args (Kind=Argument) in declaration order.
    /// </summary>
    private static (List<BoundParameter> flags, List<BoundParameter> args)
        GetScriptInputParams(IReadOnlyList<BoundStatement> stmts)
    {
        var flags = new List<BoundParameter>();
        var args = new List<BoundParameter>();
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                if (input.Kind == ScriptInputDeclarationKind.Flag)
                    flags.AddRange(input.Parameters);
                else
                    args.AddRange(input.Parameters);
            }
        }
        return (flags, args);
    }

    /// <summary>
    /// Emits a private static method
    /// <c>__subcommand_&lt;qualName&gt;(object?[] bindings)</c>
    /// that initialises each flag/arg static field from the bindings
    /// array and then runs <paramref name="bodyStatements"/>.
    /// Returns the <see cref="MethodBuilder"/> if any real work is
    /// needed (flags/args to bind OR statements to execute), else
    /// <c>null</c> (caller may pass a null body to
    /// <see cref="ToshHost.MakeSubcommandNode"/>).
    /// </summary>
    private MethodBuilder? EmitSubcommandBodyMethodForStatements(
        IReadOnlyList<BoundParameter> flags,
        IReadOnlyList<BoundParameter> args,
        IReadOnlyList<BoundStatement> bodyStatements,
        string qualName)
    {
        var hasBindings = flags.Count > 0 || args.Count > 0;
        var hasBody = bodyStatements.Count > 0;
        if (!hasBindings && !hasBody) return null;

        // Save emitter state (mirrors EmitBlockBodyMethod pattern).
        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedParams = _typedParamLocals;
        var savedBlockCaptureIndices = _blockCaptureIndices;
        var savedBlockOutputLocal = _blockOutputLocal;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        var savedThisType = _currentThisType;
        var savedUnderscoreStack = _underscoreStack;
        var savedLoopStack = _loopStack;

        var method = _program.DefineMethod(
            qualName,
            MethodAttributes.Private | MethodAttributes.Static,
            MetadataType(typeof(void)),
            MetadataTypes(typeof(object?[])));

        _il = method.GetILGenerator();
        _locals = new Dictionary<BoundSymbol, LocalSlot>();
        _paramSlots = new Dictionary<BoundSymbol, int>();
        _typedParamLocals = new Dictionary<BoundSymbol, LocalBuilder>();
        _blockCaptureIndices = new Dictionary<BoundSymbol, int>();
        _blockOutputLocal = null;
        _currentFunctionReturnType = null;
        _currentFunctionReturnRefinement = null;
        _currentThisType = null;
        _underscoreStack = new Stack<LocalBuilder>();
        _loopStack = new Stack<LoopFrame>();

        // 1. Bind flags (indices 0..flags.Count-1).
        for (var i = 0; i < flags.Count; i++)
        {
            var param = flags[i];
            if (!_staticFields.TryGetValue(param.Symbol, out var field)) continue;
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Stsfld, field);
        }

        // 2. Bind args (indices flags.Count..flags.Count+args.Count-1).
        for (var i = 0; i < args.Count; i++)
        {
            var param = args[i];
            if (!_staticFields.TryGetValue(param.Symbol, out var field)) continue;
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, flags.Count + i);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Stsfld, field);
        }

        // 3. Emit body statements.
        foreach (var stmt in bodyStatements)
            EmitStatement(stmt);

        _il.Emit(OpCodes.Ret);

        // Restore emitter state.
        _il = savedIl;
        _locals = savedLocals;
        _paramSlots = savedParams;
        _typedParamLocals = savedTypedParams;
        _blockCaptureIndices = savedBlockCaptureIndices;
        _blockOutputLocal = savedBlockOutputLocal;
        _currentFunctionReturnType = savedReturnType;
        _currentFunctionReturnRefinement = savedReturnRefinement;
        _currentThisType = savedThisType;
        _underscoreStack = savedUnderscoreStack;
        _loopStack = savedLoopStack;

        return method;
    }

    /// <summary>
    /// Pushes a <see cref="CompiledSubcommandNode"/> onto the IL
    /// evaluation stack via a call to
    /// <see cref="ToshHost.MakeSubcommandNode"/>.
    /// </summary>
    private void EmitSubcommandNodeExpression(
        string? name,
        SubcommandModifier modifiers,
        bool userDeclaredHelpFlag,
        IReadOnlyList<BoundParameter> flags,
        IReadOnlyList<BoundParameter> args,
        string[] childNames,
        LocalBuilder[] childLocals,
        MethodBuilder? bodyMethod)
    {
        // arg 0: name (string? or null)
        if (name is null)
            _il.Emit(OpCodes.Ldnull);
        else
            _il.Emit(OpCodes.Ldstr, name);

        // arg 1: modifiers (int)
        _il.Emit(OpCodes.Ldc_I4, (int)modifiers);

        // arg 2: userDeclaredHelpFlag (bool)
        _il.Emit(userDeclaredHelpFlag ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);

        // arg 3: flags (CompiledSubcommandParam[])
        EmitSubcommandParamArray(flags);

        // arg 4: args (CompiledSubcommandParam[])
        EmitSubcommandParamArray(args);

        // arg 5: childNames (string[])
        _il.Emit(OpCodes.Ldc_I4, childNames.Length);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (var i = 0; i < childNames.Length; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, childNames[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // arg 6: children (CompiledSubcommandNode[])
        _il.Emit(OpCodes.Ldc_I4, childLocals.Length);
        _il.Emit(OpCodes.Newarr, s_compiledSubcommandNodeType);
        for (var i = 0; i < childLocals.Length; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldloc, childLocals[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // arg 7: body (Action<object?[]>? or null)
        if (bodyMethod is null)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);                       // target = null (static method)
            _il.Emit(OpCodes.Ldftn, bodyMethod);
            _il.Emit(OpCodes.Newobj, s_actionOfObjArrayCtor);
        }

        _il.Emit(OpCodes.Call, s_hostMakeSubcommandNode);
    }

    /// <summary>
    /// Pushes a <c>CompiledSubcommandParam[]</c> onto the stack for
    /// <paramref name="params"/>, using
    /// <see cref="ToshHost.MakeSubcommandParam"/> per element.
    /// </summary>
    private void EmitSubcommandParamArray(IReadOnlyList<BoundParameter> @params)
    {
        _il.Emit(OpCodes.Ldc_I4, @params.Count);
        _il.Emit(OpCodes.Newarr, s_compiledSubcommandParamType);

        for (var i = 0; i < @params.Count; i++)
        {
            var p = @params[i];
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);

            // name
            _il.Emit(OpCodes.Ldstr, p.Name);
            // typeName
            if (p.TypeName is null)
                _il.Emit(OpCodes.Ldnull);
            else
                _il.Emit(OpCodes.Ldstr, p.TypeName);
            // isOptional
            _il.Emit(p.IsOptional ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // isRest
            _il.Emit(p.IsRest ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // isBool
            var isBool = IsBoolTypeName(p.TypeName);
            _il.Emit(isBool ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // hasDefault + defaultValue
            if (p.Default is not null && TryGetLiteralDefaultValue(p.Default, out var defaultVal))
            {
                _il.Emit(OpCodes.Ldc_I4_1); // hasDefault = true
                EmitObjectLiteral(defaultVal);
            }
            else
            {
                _il.Emit(OpCodes.Ldc_I4_0); // hasDefault = false
                _il.Emit(OpCodes.Ldnull);   // defaultValue = null
            }

            _il.Emit(OpCodes.Call, s_hostMakeSubcommandParam);
            _il.Emit(OpCodes.Stelem_Ref);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="typeName"/> resolves
    /// to <c>bool</c> (handles nullable suffix and common aliases).
    /// </summary>
    private static bool IsBoolTypeName(string? typeName)
    {
        if (typeName is null) return false;
        var t = typeName.TrimEnd('?');
        return t is "bool" or "Boolean" or "System.Boolean";
    }

}
