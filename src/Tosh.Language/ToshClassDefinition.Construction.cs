using System.Globalization;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshClassDefinition
{
    internal object? GetInitialPropertyValue(ToshClassInstance instance, ToshClassPropertyDefinition property, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        if (property.Initializer is null)
        {
            return null;
        }

        var locals = CreateLocals(instance, constructorLocals);
        var value = _engine.EvaluateClassPipelineValueSync(this, SourceName, SourceText, property.Initializer, locals, CapturedScopes);
        return ConvertPropertyValue(instance, property, value);
    }

    internal async ValueTask<object?> GetInitialPropertyValueAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        if (property.Initializer is null)
        {
            return null;
        }

        var locals = CreateLocals(instance, constructorLocals);
        var value = await _engine.EvaluateClassPipelineValueAsync(
            this,
            SourceName,
            SourceText,
            property.Initializer,
            locals,
            CapturedScopes,
            cancellationToken);
        return await ConvertPropertyValueAsync(
            instance,
            property,
            value,
            cancellationToken);
    }


    internal async Task RunConstructorAsync(
        ToshClassInstance instance,
        ToshClassConstructorDefinition constructor,
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, constructorLocals);
        await _engine.ExecuteClassBlockAsync(
            this,
            constructor.SourceName,
            constructor.SourceText,
            constructor.Body,
            locals,
            constructor.CapturedScopes,
            $"{Name}()",
            cancellationToken);
    }

    /// <summary>
    /// Separates a leading <c>$this(...)</c> chain call from the rest of a constructor body.
    /// </summary>
    /// <remarks>
    /// Same rules as the base-constructor initializer: at most one, and it must come first, so
    /// the primary parameters are bound before anything can read them.
    /// </remarks>
    private (PipelineStatementSyntax? Chain, BlockSyntax Body)
        SplitPrimaryConstructorChain(ToshClassConstructorDefinition constructor)
    {
        var chains = constructor.Body.Statements
            .Select((statement, index) => (statement, index))
            .Where(pair => IsDirectPrimaryConstructorCall(pair.statement))
            .ToArray();

        if (chains.Length == 0)
        {
            return (null, constructor.Body);
        }

        if (chains.Length > 1)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.duplicate_primary_constructor_chain",
                title: $"Constructor '{Name}()' calls '$this(...)' more than once.",
                span: chains[1].statement.Span,
                label: "remove this duplicate primary-constructor call",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        var (statement, index) = chains[0];

        if (index != 0)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.primary_chain_must_be_first",
                title: "'$this(...)' must be the first executable statement in a constructor.",
                span: statement.Span,
                label: "move this call to the start of the constructor",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        if (_primaryConstructorParameters.Count == 0)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.primary_chain_without_primary",
                title: $"Class '{Name}' cannot call '$this(...)' because it has no primary constructor.",
                span: statement.Span,
                label: $"declare parameters on the class — 'class {Name}(...)' — or remove this call",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        return (
            (PipelineStatementSyntax)statement,
            new BlockSyntax(constructor.Body.Statements.Skip(1).ToArray(), constructor.Body.Span));
    }

    private (PipelineStatementSyntax? Initializer, BlockSyntax Body)
        SplitConstructorInitializer(ToshClassConstructorDefinition constructor)
    {
        var initializerIndices = constructor.Body.Statements
            .Select((statement, index) => (statement, index))
            .Where(pair => IsDirectSuperConstructorCall(pair.statement))
            .ToArray();

        if (initializerIndices.Length > 1)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.duplicate_base_constructor_initializer",
                title: $"Constructor '{Name}()' calls '$super(...)' more than once.",
                span: initializerIndices[1].statement.Span,
                label: "remove this duplicate base-constructor initializer",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        if (initializerIndices.Length == 0)
        {
            return (null, constructor.Body);
        }

        var (statement, index) = initializerIndices[0];
        if (index != 0)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.super_initializer_must_be_first",
                title: "'$super(...)' must be the first executable statement in a constructor.",
                span: statement.Span,
                label: "move this call to the start of the constructor",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        var initializer = (PipelineStatementSyntax)statement;
        var body = new BlockSyntax(
            constructor.Body.Statements.Skip(1).ToArray(),
            constructor.Body.Span);
        return (initializer, body);
    }

    private static bool IsDirectSuperConstructorCall(StatementSyntax statement)
    {
        if (statement is not PipelineStatementSyntax
            {
                Pipeline:
                {
                    Stages:
                    [
                        ExpressionPipelineStageSyntax
                        {
                            Expression: CallableInvocationArgumentSyntax
                            {
                                Target: VariableReferenceArgumentSyntax { Name: var name },
                            },
                        },
                    ],
                    IsBackground: false,
                },
            } pipelineStatement)
        {
            return false;
        }

        if (pipelineStatement.Pipeline.Redirections is { Count: > 0 }
            || pipelineStatement.Pipeline.InputRedirection is not null)
        {
            return false;
        }

        return string.Equals(name, "super", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="statement"/> is a leading <c>$this(...)</c> — an explicit
    /// constructor chaining to its class's primary constructor.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="IsDirectSuperConstructorCall"/> exactly, because the two are the same
    /// idea pointed at different constructors: <c>$super(...)</c> initializes the base class,
    /// <c>$this(...)</c> initializes this class's own primary parameters. Spelled with the
    /// existing <c>$this</c> rather than a new keyword for that reason (<c>TS-P1-37</c>).
    /// </remarks>
    private static bool IsDirectPrimaryConstructorCall(StatementSyntax statement)
    {
        if (statement is not PipelineStatementSyntax
            {
                Pipeline:
                {
                    Stages:
                    [
                        ExpressionPipelineStageSyntax
                        {
                            Expression: CallableInvocationArgumentSyntax
                            {
                                Target: VariableReferenceArgumentSyntax { Name: var name },
                            },
                        },
                    ],
                    IsBackground: false,
                },
            } pipelineStatement)
        {
            return false;
        }

        if (pipelineStatement.Pipeline.Redirections is { Count: > 0 }
            || pipelineStatement.Pipeline.InputRedirection is not null)
        {
            return false;
        }

        return string.Equals(name, "this", StringComparison.OrdinalIgnoreCase);
    }


    private async Task RunConstructorInitializerAsync(
        ToshClassInstance instance,
        ToshClassConstructorDefinition constructor,
        IReadOnlyDictionary<string, object?> constructorLocals,
        PipelineStatementSyntax initializer,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, constructorLocals);
        var block = new BlockSyntax([initializer], initializer.Span);
        await _engine.ExecuteClassBlockAsync(
            this,
            constructor.SourceName,
            constructor.SourceText,
            block,
            locals,
            constructor.CapturedScopes,
            $"{Name}.base()",
            cancellationToken);
    }


    /// <summary>
    /// Runs an instance method to completion on the calling thread.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ToshBoundMethodReference"/> rather than reaching into the
    /// engine directly, so a method invoked from the platform takes exactly the path a method
    /// invoked from a script does — overloads, visibility and all.
    /// </remarks>
    /// <summary>
    /// Constructs the CLR base object for an instance — as a real subclass where it can be.
    /// </summary>
    /// <remarks>
    /// The object the platform receives should be one of its own kind, overrides and all;
    /// see <see cref="Bridge.ToshClrSubclassFactory"/>. Where it cannot be, the plain base is
    /// constructed exactly as before, because a class that loses its overrides at the
    /// boundary is what this replaces and is better than a class that will not construct.
    /// </remarks>
    internal static async Task<object> CreateClrBaseObjectAsync(
        ToshEngine engine,
        ToshClassInstance instance,
        Type clrBaseType,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var subclass = Bridge.ToshClrSubclassFactory.TryGetSubclass(
            clrBaseType,
            DeclaredMethodNames(instance.Definition),
            DeclaredPropertyNames(instance.Definition));

        var created = await engine.LanguageRuntime.Invoker.CreateInstanceAsync(
            subclass ?? clrBaseType,
            arguments,
            cancellationToken);

        // After construction, never before: a base constructor that calls a virtual — and
        // plenty do — would otherwise dispatch into a tōsh object whose own construction has
        // not begun. The emitted overrides call base while this is unset.
        if (created is Bridge.IToshClrBackedObject backed)
        {
            backed.ToshInstance = instance;
        }

        return created;
    }

    /// <summary>
    /// Every instance method name declared anywhere in a class's own chain.
    /// </summary>
    /// <remarks>
    /// The whole chain, because a method declared on a middle class overrides the CLR base
    /// just as surely as one declared on the leaf. Static methods are excluded: they are not
    /// dispatched through an instance and cannot override anything.
    /// </remarks>
    private static IReadOnlyCollection<string> DeclaredMethodNames(ToshClassDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var current = definition; current is not null; current = current.BaseClass)
        {
            foreach (var method in current.Methods)
            {
                if (!method.IsStatic)
                {
                    names.Add(method.Name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Every instance property name declared anywhere in a class's own chain.
    /// </summary>
    /// <remarks>
    /// A widget says what it is through properties as much as through methods, and the
    /// framework reads them off the object it holds rather than asking the language. Without
    /// these, <c>prop IsFocusable = true</c> was true to a script and false to the thing
    /// deciding whether the widget could be focused.
    /// </remarks>
    private static IReadOnlyCollection<string> DeclaredPropertyNames(ToshClassDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var current = definition; current is not null; current = current.BaseClass)
        {
            foreach (var property in current.Properties)
            {
                if (!property.IsStatic)
                {
                    names.Add(property.Name);
                }
            }
        }

        return names;
    }

    private async Task InitializeClrBaseAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var clrObject = await CreateClrBaseObjectAsync(
            _engine,
            instance,
            ClrBaseType!,
            arguments,
            cancellationToken);
        if (instance.TryInitializeClrBase(clrObject))
        {
            return;
        }

        throw CreateConstructionDiagnostic(
            code: "tosh.runtime.base_constructor_already_initialized",
            title: $"CLR base class '{ClrBaseType!.FullName}' has already been initialized for this instance.",
            span: Span,
            label: "each base class can be constructed only once");
    }

    private ToshDiagnosticException CreateConstructionDiagnostic(
        string code,
        string title,
        TextSpan span,
        string label,
        string? help = null,
        string? sourceName = null,
        string? sourceText = null) =>
        ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName ?? SourceName,
            SourceText: sourceText ?? SourceText,
            Span: span,
            Label: label,
            Help: help));


    internal async Task<IReadOnlyList<object?>> EvaluateBaseConstructorArgsAsync(
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        if (BaseConstructorArgs is null or { Count: 0 })
        {
            return Array.Empty<object?>();
        }

        var args = new List<object?>();
        foreach (var argPipeline in BaseConstructorArgs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await _engine.EvaluateClassPipelineValueAsync(
            this,
                SourceName,
                SourceText,
                argPipeline,
                constructorLocals,
                CapturedScopes,
                cancellationToken);
            args.Add(value);
        }

        return args;
    }


    private IReadOnlyDictionary<string, object?> CreateLocals(ToshClassInstance? instance, IReadOnlyDictionary<string, object?> locals)
    {
        var result = new Dictionary<string, object?>(locals, StringComparer.Ordinal)
        {
            ["args"] = locals.Values.ToArray(),
        };

        if (instance is not null)
        {
            result["this"] = new ToshClassSelfReference(instance, accessor: this);

            if (BaseClass is not null)
            {
                result["super"] = new ToshClassSuperReference(instance, BaseClass, accessor: this);
            }
            else if (ClrBaseType is not null)
            {
                result["super"] = new ToshClassClrSuperReference(instance, ClrBaseType, _engine);
            }
        }

        return result;
    }

    /// <summary>
    /// The `this`/`super` bindings an instance member sees. Used to make
    /// them visible inside method parameter defaults (TS-P1-21).
    /// Returns null for static members and for constructors, where the
    /// instance is not yet initialised.
    /// </summary>
    private IReadOnlyDictionary<string, object?>? CreateSelfBindings(ToshClassInstance? instance)
    {
        if (instance is null)
        {
            return null;
        }

        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = new ToshClassSelfReference(instance, accessor: this),
        };

        if (BaseClass is not null)
        {
            bindings["super"] = new ToshClassSuperReference(instance, BaseClass, accessor: this);
        }
        else if (ClrBaseType is not null)
        {
            bindings["super"] = new ToshClassClrSuperReference(instance, ClrBaseType, _engine);
        }

        return bindings;
    }


    private async ValueTask<(
        ToshClassConstructorDefinition Constructor,
        Dictionary<string, object?> Locals)> SelectConstructorAsync(
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var constructors = GetConstructorDefinitions();
        // `TOAST-0122`. A parameter annotation is written inside the class body, so it
        // resolves against the module that body lives in — not against wherever the call
        // happens to be made from. The return annotation already did this; parameters did
        // not, and overload *scoring* is where a parameter annotation is read.
        //
        // Under `require … as M` the caller's scope has no `ToastLib`, so
        // `amount: ToastLib.Math.Vector2D<T>` resolved to nothing, every argument scored as
        // a mismatch, and it surfaced as "no overload matched with 1 argument(s)" — a
        // message about arity for a problem about names.
        using var annotationScope = new AnnotationScope(_engine, DeclaringExports);

        var matches = await _engine.SelectBestCallableMatchesAsync(
            constructors,
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            if (constructors.Count == 1)
            {
                await ThrowDetailedSingleConstructorMismatchAsync(
                    constructors[0],
                    arguments,
                    cancellationToken);
            }

            throw new InvalidOperationException($"No constructor matched class '{Name}' with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatConstructorSignature(match.Candidate.Parameters)));
            throw new InvalidOperationException(
                $"Multiple constructor overloads matched class '{Name}' with {arguments.Count} argument(s): {signatures}.");
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
            $"{Name}()",
            cancellationToken,
            ambient: null,
            selfUnavailable: true);
        return (winner, locals);
    }

    /// <summary>
    /// Works out which argument each constructor parameter would receive, so the caller can
    /// re-run the conversions and surface the first one that fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what the two <c>ThrowDetailedSingleConstructorMismatch</c> twins
    /// duplicated: named-versus-positional sorting, the required-argument count, the arity
    /// bail-out, and the rest-parameter tail. Both copies then walked the same plan, one calling
    /// the synchronous converter and the other awaiting the asynchronous one — the only genuine
    /// difference between them (<c>TS-P1-24</c>).
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when the argument count cannot fit the signature at all.
    /// In that case there is no per-argument conversion to blame, and the caller falls back to
    /// the generic "no constructor matched" message.
    /// </para>
    /// </remarks>
    private static List<(FunctionParameterDefinition Parameter, object? Value)>?
        PlanConstructorArgumentConversions(
            ToshClassConstructorDefinition constructor,
            IReadOnlyList<object?> arguments)
    {
        var parameters = constructor.Parameters;
        var hasRestParameter = parameters.Count > 0 && parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? parameters.Count - 1 : parameters.Count;

        var namedArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var positionalArgs = new List<object?>();

        foreach (var argument in arguments)
        {
            if (argument is NamedArgument named)
            {
                namedArgs[named.Name] = named.Value;
            }
            else
            {
                positionalArgs.Add(argument);
            }
        }

        var requiredCount = parameters.Count(parameter =>
            !parameter.IsOptional &&
            !parameter.IsRest &&
            parameter.DefaultValue is null &&
            !namedArgs.ContainsKey(parameter.Name));

        if (positionalArgs.Count < requiredCount ||
            (!hasRestParameter && positionalArgs.Count > positionalCount - namedArgs.Count))
        {
            return null;
        }

        var plan = new List<(FunctionParameterDefinition, object?)>();
        var positionalIndex = 0;

        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = parameters[index];

            if (namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                plan.Add((parameter, namedValue));
                continue;
            }

            if (positionalIndex >= positionalArgs.Count)
            {
                continue;
            }

            plan.Add((parameter, positionalArgs[positionalIndex++]));
        }

        if (hasRestParameter)
        {
            var restParameter = parameters[^1];

            for (var index = positionalCount; index < arguments.Count; index++)
            {
                plan.Add((restParameter, arguments[index]));
            }
        }

        return plan;
    }

    private async Task ThrowDetailedSingleConstructorMismatchAsync(
        ToshClassConstructorDefinition constructor,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (PlanConstructorArgumentConversions(constructor, arguments) is not { } plan)
        {
            return;
        }

        foreach (var (parameter, value) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ConvertConstructorParameterValueAsync(constructor, parameter, value, cancellationToken);
        }
    }
    private Exception? TranslateConstructorParameterConversionFailure(
        ToshClassConstructorDefinition constructor,
        FunctionParameterDefinition parameter,
        ToshDiagnosticException exception)
    {
        var alreadyPrecise = exception.Diagnostics.Any(diagnostic =>
            string.Equals(diagnostic.Code, "tosh.runtime.annotation_unknown_type", StringComparison.Ordinal) ||
            string.Equals(diagnostic.Code, "tosh.runtime.refinement_failed", StringComparison.Ordinal) ||
            string.Equals(diagnostic.Code, "tosh.runtime.expression_failed", StringComparison.Ordinal));

        if (alreadyPrecise || parameter.Refinement is not null)
        {
            // Null means "rethrow the original", so the call site can use a bare `throw;` and
            // keep the stack trace. Returning the exception to be thrown would have reset it —
            // a real difference from the code this replaced, and the kind of detail a
            // convergence is supposed to preserve rather than quietly change.
            return null;
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.constructor_parameter_type_conversion_failed",
            Title: $"Constructor argument '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
            SourceName: constructor.SourceName,
            SourceText: constructor.SourceText,
            Span: parameter.Span,
            Label: $"'{parameter.Name}' expects {parameter.TypeName}"));
    }
    /// <summary>
    /// Evaluates a <c>$this(...)</c> chain call and binds its arguments to this class's primary
    /// constructor parameters, returning the constructor locals with those bindings merged in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The explicit constructor's own parameters stay visible — they are what the chain
    /// arguments are usually computed from, as in <c>C(a, b) { $this($a + $b) }</c> — and the
    /// primary parameters are added alongside. A name declared by both belongs to the explicit
    /// constructor, since that is the one the caller actually invoked.
    /// </para>
    /// <para>
    /// Arguments are converted against the primary parameters' annotations by the same binder
    /// that a direct <c>new C(...)</c> would use, so a chain cannot smuggle in a value the
    /// primary constructor would have rejected.
    /// </para>
    /// </remarks>
    private async ValueTask<Dictionary<string, object?>> BindPrimaryConstructorChainAsync(
        ToshClassConstructorDefinition constructor,
        PipelineStatementSyntax chain,
        Dictionary<string, object?> constructorLocals,
        ToshClassInstance instance,
        CancellationToken cancellationToken)
    {
        var invocation = (CallableInvocationArgumentSyntax)
            ((ExpressionPipelineStageSyntax)chain.Pipeline.Stages[0]).Expression;

        var arguments = new List<object?>(invocation.Arguments.Count);

        foreach (var argument in invocation.Arguments)
        {
            // Wrapped as a one-stage pipeline so the existing class-context evaluator can run
            // it, rather than adding a second way to evaluate an argument.
            var argumentPipeline = new PipelineSyntax(
                [new ExpressionPipelineStageSyntax(argument, argument.Span)]);

            arguments.Add(await _engine.EvaluateClassPipelineValueAsync(
            this,
                constructor.SourceName,
                constructor.SourceText,
                argumentPipeline,
                CreateLocals(instance, constructorLocals),
                constructor.CapturedScopes,
                cancellationToken,
                $"{Name}.$this()"));
        }

        var binding = await _engine.TryBindCallableParametersAsync(
            _primaryConstructorParameters,
            arguments,
            cancellationToken);

        if (!binding.Success)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.primary_chain_arguments",
                title: $"'$this(...)' does not match the primary constructor of '{Name}'.",
                span: chain.Span,
                label: $"expected {FormatConstructorSignature(_primaryConstructorParameters)}",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        var merged = new Dictionary<string, object?>(binding.Locals, StringComparer.Ordinal);

        foreach (var local in constructorLocals)
        {
            merged[local.Key] = local.Value;
        }

        return merged;
    }

    internal Task InvokeConstructorOnInstanceAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken) =>
        ConstructOnInstanceAsync(instance, arguments, cancellationToken);

    private async Task ConstructOnInstanceAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        bool isImplicitBaseCall = false,
        ToshClassDefinition? requestedBy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Property initialisers and the constructor body are this class's own code, so its
        // nested types are in scope by name there as they are inside a method.
        using var executingClass = _engine.EnterClass(this);
        using var nestedTypeScope = _engine.PushNestedTypeScope(this);

        if (instance.IsConstructionLayerComplete(this))
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.base_constructor_already_initialized",
                title: $"Base class '{Name}' has already been initialized for this instance.",
                span: requestedBy?.Span ?? Span,
                label: "each class layer can be constructed only once",
                sourceName: requestedBy?.SourceName,
                sourceText: requestedBy?.SourceText);
        }

        if (!instance.TryBeginConstructionLayer(this))
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.constructor_cycle",
                title: $"Constructor cycle detected while initializing class '{Name}'.",
                span: requestedBy?.Span ?? Span,
                label: "this class layer is already being initialized",
                sourceName: requestedBy?.SourceName,
                sourceText: requestedBy?.SourceText);
        }

        try
        {
            ToshClassConstructorDefinition constructor;
            Dictionary<string, object?> constructorLocals;

            try
            {
                (constructor, constructorLocals) = await SelectConstructorAsync(
                    arguments,
                    cancellationToken);
            }
            catch (InvalidOperationException) when (isImplicitBaseCall && requestedBy is not null)
            {
                throw requestedBy.CreateConstructionDiagnostic(
                    code: "tosh.runtime.missing_base_constructor_initializer",
                    title: $"Class '{requestedBy.Name}' must initialize base class '{Name}' with constructor arguments.",
                    span: requestedBy.Span,
                    label: $"add 'extends {Name}(...)' or a leading '$super(...)'",
                    help: $"'{Name}' has no constructor that can be called without arguments.");
            }

            ValidateConstructorTypeArguments(constructor, constructorLocals, instance);

            // A leading `$this(...)` binds this class's primary-constructor parameters, so a
            // property initializer that reads one sees a value even though construction came in
            // through an explicit constructor. Split and applied *before* the initializer loop
            // below, which is the whole point: that loop is where the missing binding used to
            // surface as "Variable 'x' was not found" (TS-P1-37).
            var (primaryChain, chainedBody) = SplitPrimaryConstructorChain(constructor);

            if (primaryChain is not null)
            {
                constructorLocals = await BindPrimaryConstructorChainAsync(
                    constructor,
                    primaryChain,
                    constructorLocals,
                    instance,
                    cancellationToken);
            }

            var (superInitializer, constructorBody) =
                SplitConstructorInitializer(constructor with { Body = chainedBody });

            if (BaseConstructorArgs is not null && superInitializer is not null)
            {
                throw CreateConstructionDiagnostic(
                    code: "tosh.runtime.duplicate_base_constructor_initializer",
                    title: $"Class '{Name}' initializes its base class more than once.",
                    span: superInitializer.Span,
                    label: "remove this '$super(...)' call or remove the 'extends Base(...)' arguments",
                    help: "Use exactly one base-constructor initializer.");
            }

            if (BaseClass is not null)
            {
                if (BaseConstructorArgs is not null)
                {
                    var baseArguments = await EvaluateBaseConstructorArgsAsync(
                        constructorLocals,
                        cancellationToken);
                    await BaseClass.ConstructOnInstanceAsync(
                        instance,
                        baseArguments,
                        cancellationToken);
                }
                else if (superInitializer is not null)
                {
                    await RunConstructorInitializerAsync(
                        instance,
                        constructor,
                        constructorLocals,
                        superInitializer,
                        cancellationToken);
                }
                else
                {
                    await BaseClass.ConstructOnInstanceAsync(
                        instance,
                        Array.Empty<object?>(),
                        cancellationToken,
                        isImplicitBaseCall: true,
                        requestedBy: this);
                }
            }
            else if (ClrBaseType is not null)
            {
                if (BaseConstructorArgs is not null)
                {
                    var baseArguments = await EvaluateBaseConstructorArgsAsync(
                        constructorLocals,
                        cancellationToken);
                    await InitializeClrBaseAsync(instance, baseArguments, cancellationToken);
                }
                else if (superInitializer is not null)
                {
                    await RunConstructorInitializerAsync(
                        instance,
                        constructor,
                        constructorLocals,
                        superInitializer,
                        cancellationToken);
                }
                else
                {
                    try
                    {
                        await InitializeClrBaseAsync(
                            instance,
                            Array.Empty<object?>(),
                            cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is not ToshDiagnosticException and not OperationCanceledException)
                    {
                        throw CreateConstructionDiagnostic(
                            code: "tosh.runtime.missing_base_constructor_initializer",
                            title: $"Class '{Name}' must initialize CLR base class '{ClrBaseType.FullName}' with constructor arguments.",
                            span: Span,
                            label: $"add 'extends {ClrBaseType.Name}(...)' or a leading '$super(...)'",
                            help: exception.Message);
                    }
                }
            }
            else if (superInitializer is not null)
            {
                throw CreateConstructionDiagnostic(
                    code: "tosh.runtime.super_without_base_class",
                    title: $"Class '{Name}' cannot call '$super(...)' because it has no base class.",
                    span: superInitializer.Span,
                    label: "remove this base-constructor initializer");
            }

            foreach (var property in Properties)
            {
                if (property.IsComputed || property.IsStatic || property.IsLazy || property.IsAbstract)
                {
                    continue;
                }

                var initialValue = await GetInitialPropertyValueAsync(
                    instance,
                    property,
                    constructorLocals,
                    cancellationToken);
                instance.SetStoredValue(property.Name, initialValue);
            }

            await RunConstructorAsync(
                instance,
                constructor with { Body = constructorBody },
                constructorLocals,
                cancellationToken);
            instance.CompleteConstructionLayer(this);
        }
        catch
        {
            instance.AbortConstructionLayer(this);
            throw;
        }
    }

    private async ValueTask<object?> ConvertConstructorParameterValueAsync(
        ToshClassConstructorDefinition constructor,
        FunctionParameterDefinition parameter,
        object? value,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _engine.ConvertAnnotatedValueAsync(
                parameter.TypeName,
                parameter.Refinement,
                value,
                parameter.Span,
                constructor.SourceName,
                constructor.SourceText,
                $"{Name}.{parameter.Name}",
                cancellationToken);
        }
        catch (ToshDiagnosticException exception)
            when (TranslateConstructorParameterConversionFailure(constructor, parameter, exception)
                  is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// After a constructor has been selected, walk its parameters and for any
    /// whose original (un-erased) annotation referred to a class type-parameter,
    /// substitute the bound CLR type and validate the supplied argument. Mutates
    /// <paramref name="locals"/> in place to reflect any coercion that
    /// <see cref="ToshEngine.ConvertAnnotatedValue(string, RefinementAnnotation?, object?, TextSpan, string, string, string)"/>
    /// performed.
    /// </summary>
    private void ValidateConstructorTypeArguments(
        ToshClassConstructorDefinition constructor,
        Dictionary<string, object?> locals,
        ToshClassInstance instance)
    {
        var bindings = instance.GetBindingsFor(this);
        if (bindings is null || bindings.Count == 0)
        {
            return;
        }

        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RawTypeName is null)
            {
                continue;
            }

            // Only revalidate parameters whose RawTypeName is actually a
            // class type-parameter (otherwise the standard conversion in
            // SelectBestCallableMatches has already enforced the annotation).
            if (!bindings.TryGetValue(parameter.RawTypeName, out var boundType))
            {
                continue;
            }

            if (boundType is null)
            {
                // Unresolved type-parameter binding — accept any value.
                continue;
            }

            if (!locals.TryGetValue(parameter.Name, out var value))
            {
                continue;
            }

            if (parameter.IsRest && value is System.Collections.IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    list[i] = CoerceStrictBinding(
                        boundType,
                        list[i],
                        parameter.Span,
                        constructor.SourceName,
                        constructor.SourceText,
                        $"{Name}.{parameter.Name}");
                }
                continue;
            }

            locals[parameter.Name] = CoerceStrictBinding(
                boundType,
                value,
                parameter.Span,
                constructor.SourceName,
                constructor.SourceText,
                $"{Name}.{parameter.Name}");
        }
    }

    /// <summary>
    /// Strict (no-coercion) check used when a parameter or return type was
    /// declared as a class type-parameter (e.g. <c>x: T</c>) and is being
    /// re-validated against the concrete CLR type bound at instantiation
    /// (e.g. <c>T = int</c>). Unlike
    /// <see cref="ToshEngine.ConvertAnnotatedValue(string, RefinementAnnotation?, object?, TextSpan, string, string, string)"/>
    /// this does not run <see cref="TypeConversion"/>; it only accepts a
    /// value that is already an instance of the bound type. This prevents
    /// surprising widening (e.g. <c>int</c> → <c>double</c>) and
    /// stringification (e.g. <c>4</c> → <c>"4"</c>) that would
    /// otherwise silently succeed for <c>new Box&lt;string&gt;(4)</c>.
    /// </summary>
    /// <summary>
    /// Checks a value against the type its parameter is bound to, widening it where C# would
    /// — <c>TOAST-0124</c>. Returns the value to store.
    /// </summary>
    /// <remarks>
    /// It used to only check, which made <c>new Vector2D&lt;double&gt;(0, 0)</c> impossible:
    /// <c>0</c> is an <c>Int32</c>, the bound <c>T</c> is a <c>Double</c>, and nothing is
    /// lost by widening it. A generic factory could not be written at all, because there is
    /// no way to spell "zero of T" when the literal has to already be the right type.
    ///
    /// The strictness itself is right and is kept: it is what stops a <c>Point2D&lt;int&gt;</c>
    /// taking 3.5. Only the *direction* was undistinguished. The permitted set is exactly
    /// C#'s implicit numeric conversions, which is the set that cannot fail — deliberately
    /// not <c>TypeConversion.TryConvert</c>, which also parses strings and truncates
    /// doubles and would take the 3.5.
    /// </remarks>
    private object? CoerceStrictBinding(
        Type boundType,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (value is null)
        {
            if (!boundType.IsValueType || Nullable.GetUnderlyingType(boundType) is not null)
            {
                return null;
            }
        }
        else if (boundType.IsInstanceOfType(value))
        {
            return value;
        }
        else if (TryWidenImplicitly(value, boundType, out var widened))
        {
            return widened;
        }

        var typeName = boundType.FullName ?? boundType.Name;
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced {DescribeConversionSource(value)}, which could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"{DescribeConversionSource(value)} does not become '{typeName}'"));
    }


    /// <summary>
    /// Whether a value is an instance of the ToastScript type named <paramref name="typeName"/>
    /// — <c>TOAST-0125</c>.
    /// </summary>
    /// <remarks>
    /// Asks the value, through the same contract `is` uses, so a subclass and an implemented
    /// interface both satisfy the name. A value that cannot answer is accepted: the binding is
    /// nominal, and refusing what we cannot check would turn an unenforced annotation into a
    /// wrong error.
    /// </remarks>
    private bool NominalValueMatches(object? value, string typeName)
    {
        if (value is null)
        {
            return true;
        }

        // A nested generic is checked by its open name: `Holder<int>` asks whether the value
        // is a `Holder`. Checking the closure as well would mean matching `int` against
        // `Int32` and recursing, which belongs with making annotations check closures
        // generally rather than here.
        var open = typeName;
        var angle = open.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0)
        {
            open = open[..angle];
        }

        // Only a name the script itself *declared* is enforced. `array`, `list` and `dict`
        // are named shell types too, but they name a CLR shape rather than a declaration, and
        // whether a value has one is not a nominal question — refusing what cannot be checked
        // that way would turn an unenforced annotation into a wrong error, which is worse
        // than the hole it closes.
        if (!_engine.TryGetNamedType(open, out var named) || !IsScriptDeclaredType(named))
        {
            return true;
        }

        return value is IShellTypeCheckable checkable && checkable.IsInstanceOf(open);
    }

    /// <summary>
    /// Checks a value against a type parameter that was bound nominally — <c>TOAST-0125</c>.
    /// </summary>
    /// <remarks>
    /// Does nothing when the parameter was bound to a CLR type, which the strict check
    /// already covers, or when there is no instance to read the binding from — a static
    /// method has no receiver and so no nominal argument to check against.
    /// </remarks>
    private void EnforceNominalBinding(
        ToshClassInstance? instance,
        string rawTypeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        var nominal = instance?.GetNominalBindingsFor(this);

        if (nominal is null ||
            !nominal.TryGetValue(rawTypeName, out var nominalName) ||
            NominalValueMatches(value, nominalName))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that is not a '{nominalName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{nominalName}'"));
    }

    /// <summary>
    /// Resolves a written type name to the CLR type a bound argument would hold, for
    /// comparison — <c>TOAST-0125</c>.
    /// </summary>
    internal Type? ResolveComparisonType(string typeName) => _engine.TryResolveTypeName(typeName);

    /// <summary>
    /// Whether a named type is one the script declared, rather than a built-in or an alias.
    /// </summary>
    private static bool IsScriptDeclaredType(IShellNamedType named) =>
        named is ToshClassDefinition
            or ToshInterfaceDefinition
            or ToshRecordDefinition
            or ToshUnionDefinition
            or ToshStructDefinition;

    /// <summary>
    /// C#'s implicit numeric conversions, by source type — <c>TOAST-0124</c>.
    /// </summary>
    /// <remarks>
    /// Listed rather than computed so that what is permitted is readable and auditable.
    /// <c>int</c>→<c>float</c> and <c>long</c>→<c>double</c> lose precision and are implicit
    /// in C# all the same; this follows C# rather than inventing a stricter rule, so that a
    /// reader who knows one knows the other.
    /// </remarks>
    private IReadOnlyList<ToshClassConstructorDefinition> GetConstructorDefinitions()
    {
        var constructors = new List<ToshClassConstructorDefinition>(_constructors);

        if (_primaryConstructorParameters.Count > 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                _primaryConstructorParameters,
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        if (constructors.Count == 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                Array.Empty<FunctionParameterDefinition>(),
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        return constructors;
    }

    private IReadOnlyList<ShellConstructorDescriptor> GetConstructorMetadata()
    {
        return GetConstructorDefinitions()
            .Select(constructor => new ShellConstructorDescriptor(
                constructor.Parameters.Count,
                FormatConstructorSignature(constructor.Parameters)))
            .ToArray();
    }

    private static string DescribeConversionSource(object? value) =>
        value is null ? "null" : $"a {value.GetType().FullName ?? value.GetType().Name}";


}
