using System.Collections;
using System.Text;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Arguments: evaluating them at a call site, expanding splats and tildes, and binding
/// them to a callable's parameters — including script inputs, flags and arguments.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// This file contains `EvaluateArgumentSlowAsync`, which is **1,030 lines on its own**
/// — the largest member in the engine, and the one `TOAST-0009` identifies as the
/// allocation problem: a single `async` method covering thirty-nine node shapes, whose
/// state-machine box therefore carries every branch's locals, about 2,545 bytes per
/// entry whatever the expression was. Moving it here changes none of that. It is
/// measured in `TOAST-0013` and belongs to the evaluator rewrite.
///
/// `TryEvaluateSimpleArgument` beside it is the synchronous pre-dispatch that keeps
/// literals, variables and simple arithmetic out of that method. Reading the two
/// together is the point of putting them in one file: the fast path only makes sense
/// against the cost of the slow one.
/// </summary>
public sealed partial class ToshEngine
{

    private readonly record struct EvaluatedCommandArgument(ArgumentSyntax Syntax, object? Value);

    private readonly record struct ScriptArgumentValue(object? Value, int Index);

    private async Task BindScriptInputsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ScriptInputStatementSyntax> declarations,
        DocComment? scriptDoc,
        CancellationToken cancellationToken)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        var flagParameters = declarations
            .Where(static declaration => declaration.Kind == ScriptInputDeclarationKind.Flag)
            .SelectMany(static declaration => declaration.Parameters)
            .Where(static parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .ToArray();

        var argumentParameters = declarations
            .Where(static declaration => declaration.Kind == ScriptInputDeclarationKind.Argument)
            .SelectMany(static declaration => declaration.Parameters)
            .Where(static parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .ToArray();

        if (flagParameters.Length == 0 && argumentParameters.Length == 0)
        {
            return;
        }

        ValidateScriptInputs(sourceName, sourceText, flagParameters, argumentParameters);

        var scriptArguments = GetCurrentScriptArguments();

        // A script that declares its inputs answers `--help` with them. Previously the flag fell
        // through to the ordinary lookup and was refused as an unknown one — a script documented
        // its arguments and the documentation had nowhere to appear.
        if (ScriptHelpWasRequested(scriptArguments, flagParameters))
        {
            await WriteScriptUsageAsync(
                sourceName,
                scriptDoc,
                argumentParameters,
                flagParameters,
                CollectDocumentedNames(declarations, scriptDoc));

            // Answered, so the body does not run. This asks to exit rather than throwing a
            // signal of its own: `exit` now stops execution, which is the whole reason the
            // signal existed (`TS-P2-52`). Binding runs before the statement loop, so the loop
            // sees the request on its first statement and stops there.
            Host.RequestExit();
            return;
        }

        var (flagValues, argumentValues) = ParseScriptArgumentValues(
            sourceName,
            sourceText,
            flagParameters,
            scriptArguments);

        await BindScriptFlagsAsync(sourceName, sourceText, flagParameters, flagValues, cancellationToken);
        await BindScriptArgumentsAsync(sourceName, sourceText, argumentParameters, argumentValues, cancellationToken);
    }

    private async Task BindScriptFlagsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<FunctionParameterSyntax> flagParameters,
        IReadOnlyDictionary<string, ScriptArgumentValue> flagValues,
        CancellationToken cancellationToken)
    {
        foreach (var parameter in flagParameters)
        {
            object? value;

            if (flagValues.TryGetValue(parameter.Name, out var argumentValue))
            {
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, argumentValue.Value, "flag");
            }
            else if (parameter.DefaultValue is not null)
            {
                var defaultValue = await EvaluatePipelineAsync(
                    sourceName,
                    sourceText,
                    parameter.DefaultValue,
                    cancellationToken).FirstOrDefaultAsync(cancellationToken);
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, defaultValue, "flag");
            }
            else if (parameter.IsOptional)
            {
                value = null;
            }
            else
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_script_flag",
                    Title: $"Missing required script flag '{parameter.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: $"provide --{GetPrimaryScriptOptionName(parameter.Name)}"));
            }

            DeclareVariable(parameter.Name, ToVariableBinding(value), DeclarationModifier.Default);
        }
    }

    private async Task BindScriptArgumentsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<FunctionParameterSyntax> argumentParameters,
        IReadOnlyList<ScriptArgumentValue> argumentValues,
        CancellationToken cancellationToken)
    {
        var positionalIndex = 0;
        var restParameter = argumentParameters.LastOrDefault(static parameter => parameter.IsRest);

        foreach (var parameter in argumentParameters)
        {
            if (parameter.IsRest)
            {
                var restValues = argumentValues
                    .Skip(positionalIndex)
                    .Select(static argument => argument.Value)
                    .ToList();
                DeclareVariable(parameter.Name, ToVariableBinding(restValues), DeclarationModifier.Default);
                continue;
            }

            object? value;

            if (positionalIndex < argumentValues.Count)
            {
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, argumentValues[positionalIndex++].Value, "argument");
            }
            else if (parameter.DefaultValue is not null)
            {
                var defaultValue = await EvaluatePipelineAsync(
                    sourceName,
                    sourceText,
                    parameter.DefaultValue,
                    cancellationToken).FirstOrDefaultAsync(cancellationToken);
                value = ConvertScriptInputValue(sourceName, sourceText, parameter, defaultValue, "argument");
            }
            else if (parameter.IsOptional)
            {
                value = null;
            }
            else
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_script_argument",
                    Title: $"Missing required script argument '{parameter.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: "provide a positional argument"));
            }

            DeclareVariable(parameter.Name, ToVariableBinding(value), DeclarationModifier.Default);
        }

        if (restParameter is null && positionalIndex < argumentValues.Count)
        {
            var unexpected = argumentValues[positionalIndex];
            var span = argumentParameters.Count > 0 ? argumentParameters[^1].Span : new TextSpan(0, 0);
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unexpected_script_argument",
                Title: $"Unexpected script argument '{FormatScriptArgumentForDiagnostic(unexpected.Value)}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"argument #{unexpected.Index + 1} does not match any declared script argument"));
        }
    }

    private FunctionParameterDefinition CreateParameterDefinition(
        FunctionParameterSyntax parameter,
        string sourceName,
        string sourceText,
        IReadOnlyList<string>? typeParameters = null)
    {
        var erased = EraseTypeParameter(parameter.TypeName, typeParameters);
        return new FunctionParameterDefinition(
            parameter.Name,
            erased,
            parameter.IsOptional,
            parameter.IsRest,
            parameter.DefaultValue,
            parameter.Span,
            CreateRefinementAnnotation(sourceName, sourceText, parameter.Refinement),
            // Preserve the original (un-erased) annotation so generic
            // classes can re-validate against substituted type parameters
            // at construction / call time.
            RawTypeName: parameter.TypeName);
    }

    /// <summary>
    /// Compares two parameter lists by arity and declared type name, the same test the
    /// constructor-collision rule applies (<c>TS-P1-18</c>).
    /// </summary>
    private static bool ParameterListsMatch(
        IReadOnlyList<FunctionParameterDefinition> left,
        IReadOnlyList<FunctionParameterDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].TypeName, right[index].TypeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<EvaluatedCommandArgument>> EvaluateCommandArgumentsAsync(
        string sourceName,
        string sourceText,
        IShellCommand command,
        CommandSyntax commandSyntax,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Name, "offset-of", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.Name, "native-offsetof", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = new List<EvaluatedCommandArgument>(commandSyntax.Arguments.Count);

            for (var index = 0; index < commandSyntax.Arguments.Count; index++)
            {
                if (index == 0 && commandSyntax.Arguments[index] is StaticMemberAccessArgumentSyntax staticAccess)
                {
                    arguments.Add(new EvaluatedCommandArgument(staticAccess, staticAccess.Path));
                    continue;
                }

                await EvaluateCommandArgumentAsync(arguments, sourceName, sourceText, commandSyntax.Arguments[index], cancellationToken);
            }

            return arguments;
        }

        var results = new List<EvaluatedCommandArgument>(commandSyntax.Arguments.Count);

        foreach (var argument in commandSyntax.Arguments)
        {
            if (command is ICurrentItemMemberPathCommand &&
                TryGetCurrentItemMemberPath(argument, out var memberPath))
            {
                results.Add(new EvaluatedCommandArgument(argument, memberPath));
                continue;
            }

            await EvaluateCommandArgumentAsync(results, sourceName, sourceText, argument, cancellationToken);
        }

        return results;
    }

    /// <summary>
    /// Applies the shell's own word expansions to a command's evaluated arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tilde expansion runs for <em>every</em> command; globbing only for one that asks for it.
    /// That asymmetry is `TS-P2-60`: a `~` reached a command only if that command happened to
    /// path-resolve its own arguments, so `cd ~`, `ls ~` and `read-file ~/x` worked while
    /// `echo ~` and `/bin/echo ~` both received a literal tilde. Whether `~` means the home
    /// directory is not something each command should get to decide separately.
    /// </para>
    /// <para>
    /// Only barewords are expanded, so `echo "~"` stays literal — quoting is how a tilde is
    /// written when a tilde is what is wanted — and so does a `$variable` holding one.
    /// </para>
    /// </remarks>
    private IReadOnlyList<object?> ExpandCommandArguments(
        IShellCommand command,
        IReadOnlyList<EvaluatedCommandArgument> evaluatedArguments,
        string sourceName,
        string sourceText)
    {
        if (evaluatedArguments.Count == 0)
        {
            return [];
        }

        var allowGlobs = command is IImplicitGlobCommand;
        var expanded = new List<object?>(evaluatedArguments.Count);

        for (var index = 0; index < evaluatedArguments.Count; index++)
        {
            var evaluatedArgument = evaluatedArguments[index];

            // A word that was quoted is not a glob candidate, and the check is against the
            // *syntax* rather than the value: `TOSH-0001` strips the quotes during
            // evaluation, so by here `x"*"y` and `x*y` are the same string and only the
            // written form still knows which one suppressed expansion.
            var wasQuoted = evaluatedArgument.Syntax is BarewordArgumentSyntax quotedWord &&
                            ShellWordQuoting.ContainsQuote(quotedWord.Value);

            if (evaluatedArgument.Syntax is BarewordArgumentSyntax or SplatArgumentSyntax &&
                evaluatedArgument.Value is string text &&
                !string.IsNullOrWhiteSpace(text) &&
                !wasQuoted &&
                !text.StartsWith("-", StringComparison.Ordinal))
            {
                // Tilde before glob: `~/*.tosh` has to name a real directory before there is
                // anywhere to look.
                text = ExpandArgumentTilde(text, sourceName, sourceText, evaluatedArgument.Syntax.Span);

                if (allowGlobs && PathUtilities.ContainsGlobPattern(text))
                {
                    var matches = PathUtilities.ExpandGlob(Runtime.CurrentDirectory, text);

                    if (matches.Count > 0)
                    {
                        expanded.AddRange(matches.Select(static match => (object?)match.ArgumentText));
                        continue;
                    }
                }

                expanded.Add(text);
                continue;
            }

            expanded.Add(evaluatedArgument.Value);
        }

        return expanded;
    }

    /// <summary>
    /// Expands a leading tilde in one argument, refusing a <c>~name</c> that names nothing.
    /// </summary>
    /// <remarks>
    /// Passing an unresolved <c>~name</c> through unchanged is the behaviour that made this hard
    /// to see: the command received two characters and a name, and reported whatever it made of
    /// them. Saying so here names the real problem once, in the one place that knows a tilde was
    /// written.
    /// </remarks>
    private string ExpandArgumentTilde(string text, string sourceName, string sourceText, TextSpan span)
    {
        var expansion = PathUtilities.ExpandTilde(text);

        return expansion.Kind switch
        {
            PathUtilities.TildeExpansionKind.Expanded => expansion.Path,
            PathUtilities.TildeExpansionKind.UnknownName => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unknown_tilde_target",
                Title: $"'~{expansion.Name}' names neither a directory alias nor a user.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"no user '{expansion.Name}' and no directory alias '{expansion.Name}'",
                Help: $"'~name' expands to that user's home directory. Write \"~{expansion.Name}\" in quotes for a literal tilde, "
                    + $"or set a directory alias with '$tosh.Config.Shell.Dirs.{expansion.Name} = \"/some/path\"'.")),
            _ => text,
        };
    }

    private async Task EvaluateCommandArgumentAsync(
        ICollection<EvaluatedCommandArgument> arguments,
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        CancellationToken cancellationToken)
    {
        if (argument is SplatArgumentSyntax splat)
        {
            var splatValue = await EvaluateArgumentAsync(sourceName, sourceText, splat.Value, cancellationToken);

            foreach (var item in ExpandSplatValues(sourceName, sourceText, splat, splatValue))
            {
                arguments.Add(new EvaluatedCommandArgument(splat, item));
            }

            return;
        }

        // Named argument passed directly (from function-call invocation syntax)
        if (argument is NamedArgumentSyntax namedArgDirect)
        {
            var value = await EvaluateArgumentAsync(sourceName, sourceText, namedArgDirect.Value, cancellationToken);
            arguments.Add(new EvaluatedCommandArgument(namedArgDirect, new NamedArgument(namedArgDirect.Name, value)));
            return;
        }

        // Expand tuples with named arguments into individual call arguments
        if (argument is TupleLiteralArgumentSyntax tupleLiteral &&
            tupleLiteral.Items.Any(static item => item is NamedArgumentSyntax))
        {
            foreach (var item in tupleLiteral.Items)
            {
                if (item is NamedArgumentSyntax namedArg)
                {
                    var value = await EvaluateArgumentAsync(sourceName, sourceText, namedArg.Value, cancellationToken);
                    arguments.Add(new EvaluatedCommandArgument(namedArg, new NamedArgument(namedArg.Name, value)));
                }
                else
                {
                    arguments.Add(new EvaluatedCommandArgument(item,
                        await EvaluateArgumentAsync(sourceName, sourceText, item, cancellationToken)));
                }
            }

            return;
        }

        arguments.Add(new EvaluatedCommandArgument(
            argument,
            await EvaluateArgumentAsync(sourceName, sourceText, argument, cancellationToken)));
    }

    private IReadOnlyList<object?> ExpandSplatValues(
        string sourceName,
        string sourceText,
        SplatArgumentSyntax splat,
        object? value)
    {
        if (value is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.splat_requires_collection",
                Title: "Argument splatting requires a non-null collection value.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: splat.Span,
                Label: "this splat target is null",
                Help: "use a list, array, range, or tuple value with '...'."));
        }

        if (value is string || ShellRecordUtilities.IsRecordLike(value))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.splat_requires_collection",
                Title: "Argument splatting requires an array, list, range, tuple, or similar collection.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: splat.Span,
                Label: "this value expands as a single argument, not a collection"));
        }

        if (value is ToshRange range)
        {
            if (range.IsInfinite)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.splat_infinite_range",
                    Title: "Cannot splat an infinite range.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: splat.Span,
                    Label: "this range has no upper bound",
                    Help: "add an end value to the range, e.g. 1..10 instead of 1.."));
            }

            return range.Enumerate().Cast<object?>().ToArray();
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().ToArray();
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.splat_requires_collection",
            Title: "Argument splatting requires a collection value.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: splat.Span,
            Label: $"'{value.GetType().Name}' does not expand into multiple arguments",
            Help: "wrap multiple values in an array or list before splatting them."));
    }

    /// <summary>
    /// Evaluates a call's arguments — constructors, instance methods, static and
    /// qualified calls.
    /// </summary>
    /// <remarks>
    /// `TS-P2-104`. This used to walk the arguments itself, one value per syntax
    /// node, and so had no notion of a splat: every dotted call —
    /// `$obj.Sum(...$xs)`, `C.SSum(...$xs)`, `M.MSum(...$xs)`,
    /// `String.Join("-", ...$xs)` — reported "Unsupported argument syntax:
    /// SplatArgumentSyntax", while the bare-name form worked because it went
    /// through the other evaluator. Two argument evaluators, and only one of them
    /// knew the language had spreading.
    ///
    /// It now delegates rather than duplicating. The two names are kept because
    /// the call sites differ in intent, but there is one implementation, so a
    /// future argument form cannot reach half the language again.
    /// </remarks>
    /// <summary>
    /// Whether <paramref name="target"/> declares anything reachable by
    /// <paramref name="name"/> — asked, never invoked.
    /// </summary>
    /// <remarks>
    /// Every receiver that cannot answer honestly answers "yes", so the only names that
    /// reach the function fallback are the ones a receiver has positively disclaimed.
    /// </remarks>
    private bool ReceiverHasInstanceMember(object target, string name) => target switch
    {
        IShellInvocableObject invocable => invocable.HasInstanceMember(name),
        IShellStaticType or Type => true,
        _ => Runtime.Invoker.HasInstanceMethod(target, name),
    };

    /// <summary>
    /// Whether an <c>extend</c> method would supply <paramref name="name"/> for this
    /// receiver.
    /// </summary>
    /// <remarks>
    /// Asked so the fallback sits *after* extension dispatch rather than beside it. An
    /// extension already resolves only where the receiver has no such member
    /// (<c>TS-P3-27</c>); a free function is one step further out again, which keeps a
    /// single order — member, extension, function — instead of a third rule.
    /// </remarks>
    private ValueTask<bool> HasExtensionMethodAsync(object receiver, string name)
    {
        if (_extensionMethods.Count == 0)
        {
            return ValueTask.FromResult(false);
        }

        foreach (var typeName in EnumerateReceiverTypeNames(receiver))
        {
            if (_extensionMethods.TryGetValue(typeName, out var methods) && methods.ContainsKey(name))
            {
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// Calls the function the bare name meant, or reports that neither reading resolved.
    /// </summary>
    /// <remarks>
    /// The diagnostic names both readings deliberately. At this point the name is known not
    /// to be a member, not to be an extension, and not to be in scope — and a reader who
    /// meant either one is helped only by being told about the other.
    /// </remarks>
    private async Task<object?> InvokeImplicitItemFallbackAsync(
        string sourceName,
        string sourceText,
        MethodCallArgumentSyntax methodCall,
        object target,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (CreateScopedCommandView().TryGet(methodCall.MethodName, out var command) &&
            command is IShellCallable callable)
        {
            return await InvokeCallableInExpressionAsync(
                callable,
                arguments,
                sourceName,
                sourceText,
                methodCall.Span,
                methodCall.Arguments.Select(argument => argument.Span).ToArray(),
                cancellationToken);
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unknown_implicit_call",
            Title: $"Nothing named '{methodCall.MethodName}' is in scope, and the current item has no such method.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: methodCall.Span,
            Label: $"'{target.GetType().Name}' has no '{methodCall.MethodName}'",
            Help: "define a function of that name, or write '$_.<member>' for a member of the current item."));
    }

    private Task<IReadOnlyList<object?>> EvaluateArgumentsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ArgumentSyntax> arguments,
        CancellationToken cancellationToken)
        => EvaluateCallableInvocationArgumentsAsync(sourceName, sourceText, arguments, cancellationToken);

    private async Task<IReadOnlyList<object?>> EvaluateCallableInvocationArgumentsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ArgumentSyntax> arguments,
        CancellationToken cancellationToken)
    {
        var values = new List<object?>(arguments.Count);

        foreach (var argument in arguments)
        {
            if (argument is SplatArgumentSyntax splat)
            {
                var splatValue = await EvaluateArgumentAsync(sourceName, sourceText, splat.Value, cancellationToken);
                values.AddRange(ExpandSplatValues(sourceName, sourceText, splat, splatValue));
                continue;
            }

            values.Add(await EvaluateArgumentAsync(sourceName, sourceText, argument, cancellationToken));
        }

        return values;
    }

    /// <summary>
    /// Evaluates an argument, taking a synchronous path for the shapes that cannot
    /// suspend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EvaluateArgumentSlowAsync"/> handles thirty-nine argument shapes in
    /// one <c>async</c> method, and an async method's state-machine box carries the
    /// locals of every branch — so entering it cost about 2,545 bytes whatever the
    /// expression was. A literal paid for the largest case in the switch
    /// (<c>TS-P2-125</c>).
    /// </para>
    /// <para>
    /// This wrapper is deliberately not <c>async</c>: that is the whole point, since
    /// an <c>async</c> wrapper would allocate the very box it exists to avoid.
    /// </para>
    /// </remarks>
    private ValueTask<object?> EvaluateArgumentAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        CancellationToken cancellationToken)
        => TryEvaluateSimpleArgument(sourceName, sourceText, argument, out var value)
            ? new ValueTask<object?>(value)
            : EvaluateArgumentSlowAsync(sourceName, sourceText, argument, cancellationToken);

    /// <summary>
    /// The argument shapes with no suspension point, evaluated in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every case here is a second copy of one in <see cref="EvaluateArgumentSlowAsync"/>,
    /// which is where the risk lives: a copy that is *nearly* the same silently drops
    /// whatever the original did in addition. So each one declines rather than guesses,
    /// and anything needing the original's diagnostics falls through to it.
    /// </para>
    /// <para>
    /// A variable falls through when its binding is declared-but-unassigned, holds a
    /// rune thunk, or names an out-of-scope constructor parameter — each of which the
    /// full case reports specifically. An operator falls through unless it is pure
    /// arithmetic over primitive numbers: comparison, membership and string
    /// concatenation all await, and either operand being a class instance means a
    /// user-defined overload that can await too.
    /// </para>
    /// </remarks>
    private bool TryEvaluateSimpleArgument(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        out object? value)
    {
        switch (argument)
        {
            case LiteralArgumentSyntax literal:
                value = literal.Value;
                return true;

            case VariableReferenceArgumentSyntax variable
                when TryGetVariableBinding(variable.Name, out var binding)
                     && !binding.IsAllocatedOnly
                     && binding.Value is not RuneThunk:
                value = binding.Value;
                return true;

            // `(expr)` is the value of `expr`; the parentheses should not cost a
            // pipeline and two state machines.
            case SubexpressionArgumentSyntax subexpression
                when subexpression.Pipeline.Stages.Count == 1 &&
                     subexpression.Pipeline.Stages[0] is ExpressionPipelineStageSyntax stage:
                return TryEvaluateSimpleArgument(sourceName, sourceText, stage.Expression, out value);

            // Already proved constant by the lowering pass.
            case OperatorArgumentSyntax { FoldedConstant: { } folded }:
                value = folded.Value;
                return true;

            case OperatorArgumentSyntax operation
                when IsSynchronousArithmeticOperator(operation.Operator) &&
                     TryEvaluateSimpleArgument(sourceName, sourceText, operation.Left, out var left) &&
                     IsPrimitiveNumber(left) &&
                     TryEvaluateSimpleArgument(sourceName, sourceText, operation.Right, out var right) &&
                     IsPrimitiveNumber(right):
                try
                {
                    value = OperatorEvaluator.EvaluateBinary(left, operation.Operator, right);
                    return true;
                }
                catch (ToshDiagnosticException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // The same wrapping the full path applies, so an overflow or a
                    // division by zero still underlines the operator.
                    throw CreateExpressionDiagnostic(sourceName, sourceText, operation.Span, exception);
                }

            default:
                value = null;
                return false;
        }
    }

    private async ValueTask<object?> EvaluateArgumentSlowAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (argument)
            {
                case BarewordArgumentSyntax bareword:
                    // `TOSH-0001`. A word beginning with a quote is lexed as a string and
                    // arrives here unquoted; one that merely *contains* a quote is lexed
                    // whole and kept its quote characters all the way to the callee.
                    return ShellWordQuoting.StripBalancedQuotes(bareword.Value);

                case LiteralArgumentSyntax literal:
                    return literal.Value;

                case VariableReferenceArgumentSyntax variableReference:
                    {
                        if (TryGetVariableBinding(variableReference.Name, out var binding))
                        {
                            if (binding.IsAllocatedOnly)
                            {
                                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                    Code: "tosh.runtime.uninitialized_variable",
                                    Title: $"Variable '{variableReference.Name}' has been declared but not assigned yet.",
                                    SourceName: sourceName,
                                    SourceText: sourceText,
                                    Span: variableReference.Span,
                                    Label: $"assign a value to '{variableReference.Name}' before using it",
                                    Help: $"try '${variableReference.Name} = ...' or assign a member like '${variableReference.Name}.Name = ...'."));
                            }

                            // Rune thunk: transparently evaluate the deferred argument
                            if (binding.Value is RuneThunk thunk)
                            {
                                return await EvaluateRuneThunkAsync(thunk, cancellationToken);
                            }

                            return binding.Value;
                        }

                        // `TS-P2-81`. A primary-constructor parameter used outside a stored
                        // property initializer is not an undeclared variable — it is a name that
                        // was in scope while the value was being built and is gone once it is.
                        // "declare it first with 'var x = …'" is the wrong advice for it, and the
                        // bare spelling of the same mistake already says so (`TS-P2-41`).
                        if (DescribeOutOfScopeConstructorParameter(variableReference.Name) is { } parameterHelp)
                        {
                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                Code: "tosh.runtime.unknown_variable",
                                Title: $"'${variableReference.Name}' is a constructor parameter of "
                                     + $"'{CurrentClass!.Name}' and is not in scope here.",
                                SourceName: sourceName,
                                SourceText: sourceText,
                                Span: variableReference.Span,
                                Label: $"'${variableReference.Name}' is not available once the value is built",
                                Help: parameterHelp));
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.unknown_variable",
                            Title: $"Variable '{variableReference.Name}' was not found.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: variableReference.Span,
                            Label: $"'{variableReference.Name}' is not defined in this scope",
                            Help: DescribeUnknownVariable(variableReference.Name)));
                    }

                case NewObjectArgumentSyntax newObject:
                    {
                        var constructorArguments = await EvaluateArgumentsAsync(sourceName, sourceText, newObject.Arguments, cancellationToken);

                        var bareName = newObject.EffectiveBareName;
                        var typeArgList = newObject.EffectiveTypeArguments;
                        var hasAngles = newObject.HasExplicitTypeArgumentList;

                        // Reject empty `<>` early — it's never useful and
                        // is almost always a typo for an inferred-args
                        // attempt that we don't yet support.
                        if (hasAngles && typeArgList.Count == 0)
                        {
                            throw new InvalidOperationException(
                                $"Empty type-argument list '<>' is not allowed on 'new {bareName}'. Either omit the angle brackets or supply concrete type arguments.");
                        }

                        if (TryResolveShellStaticType(bareName, out var shellType))
                        {
                            if (shellType is ToshClassDefinition classDef)
                            {
                                if (classDef.TypeParameterNames.Count == 0)
                                {
                                    if (hasAngles)
                                    {
                                        throw new InvalidOperationException(
                                            $"Class '{bareName}' is not generic and does not accept type arguments.");
                                    }

                                    return await classDef.CreateInstanceAsync(
                                        constructorArguments,
                                        cancellationToken);
                                }

                                // Generic class — must have matching type-arg list
                                if (!hasAngles)
                                {
                                    if (TryInferTypeArgumentsFromCtorArgs(
                                            classDef.TypeParameterNames,
                                            classDef.PrimaryConstructorParameters,
                                            constructorArguments,
                                            out var inferredResolved,
                                            out var inferredDisplay))
                                    {
                                        return await classDef.CreateGenericInstanceAsync(
                                            inferredResolved,
                                            inferredDisplay,
                                            constructorArguments,
                                            cancellationToken);
                                    }

                                    throw new InvalidOperationException(
                                        $"Generic class '{bareName}' requires type arguments, e.g. 'new {bareName}<{string.Join(", ", classDef.TypeParameterNames)}>(…)'.");
                                }

                                if (typeArgList.Count != classDef.TypeParameterNames.Count)
                                {
                                    throw new InvalidOperationException(
                                        $"Generic class '{bareName}' expects {classDef.TypeParameterNames.Count} type argument(s) " +
                                        $"<{string.Join(", ", classDef.TypeParameterNames)}> but received {typeArgList.Count}: <{string.Join(", ", typeArgList)}>.");
                                }

                                var resolved = new Type?[typeArgList.Count];
                                for (int i = 0; i < typeArgList.Count; i++)
                                {
                                    resolved[i] = ResolveTypeArgument(typeArgList[i]);
                                }
                                return await classDef.CreateGenericInstanceAsync(
                                    resolved,
                                    typeArgList,
                                    constructorArguments,
                                    cancellationToken);
                            }

                            if (shellType is ToshRecordDefinition recordDef)
                            {
                                if (recordDef.TypeParameterNames.Count == 0)
                                {
                                    if (hasAngles)
                                    {
                                        throw new InvalidOperationException(
                                            $"Record '{bareName}' is not generic and does not accept type arguments.");
                                    }

                                    return recordDef.CreateInstance(constructorArguments);
                                }

                                if (!hasAngles)
                                {
                                    if (TryInferTypeArgumentsFromRecordFields(
                                            recordDef.TypeParameterNames,
                                            recordDef.Fields,
                                            constructorArguments,
                                            out var inferredResolvedRec,
                                            out var inferredDisplayRec))
                                    {
                                        return recordDef.CreateGenericInstance(inferredResolvedRec, inferredDisplayRec, constructorArguments);
                                    }

                                    throw new InvalidOperationException(
                                        $"Generic record '{bareName}' requires type arguments, e.g. 'new {bareName}<{string.Join(", ", recordDef.TypeParameterNames)}>(\u2026)'.");
                                }

                                if (typeArgList.Count != recordDef.TypeParameterNames.Count)
                                {
                                    throw new InvalidOperationException(
                                        $"Generic record '{bareName}' expects {recordDef.TypeParameterNames.Count} type argument(s) " +
                                        $"<{string.Join(", ", recordDef.TypeParameterNames)}> but received {typeArgList.Count}: <{string.Join(", ", typeArgList)}>.");
                                }

                                var resolvedRec = new Type?[typeArgList.Count];
                                for (int i = 0; i < typeArgList.Count; i++)
                                {
                                    resolvedRec[i] = ResolveTypeName(typeArgList[i]);
                                }
                                return recordDef.CreateGenericInstance(resolvedRec, typeArgList, constructorArguments);
                            }

                            // Non-Tosh-class shell static type
                            // (built-in collection alias such as
                            // 'list', 'array', 'dict', or a CLR-backed
                            // descriptor) — these accept type
                            // arguments cosmetically and infer
                            // element types from the constructor
                            // arguments. Forward as-is.
                            return await Runtime.Invoker.CreateInstanceAsync(
                                shellType,
                                constructorArguments,
                                cancellationToken);
                        }

                        // Fall back to CLR resolution — pass the original
                        // (concatenated) type name including any generic
                        // suffix so reflection can find e.g. 'List`1'.
                        var lookupName = newObject.TypeName;
                        var type = ResolveTypeName(lookupName)
                                   ?? throw new InvalidOperationException($"Unable to resolve type '{lookupName}'.");
                        return await Runtime.Invoker.CreateInstanceAsync(
                            type,
                            constructorArguments,
                            cancellationToken);
                    }

                case StaticMethodCallArgumentSyntax staticMethodCall:
                    {
                        var methodArguments = await EvaluateArgumentsAsync(sourceName, sourceText, staticMethodCall.Arguments, cancellationToken);

                        // `TS-P2-82`. Resolved here rather than in the invoker: the names need the
                        // scope, aliases and declared types the engine holds, and passing names
                        // down would mean a second, weaker resolver living there.
                        var typeArguments = staticMethodCall.ExplicitTypeArguments is null
                            ? null
                            : ResolveExplicitTypeArguments(
                                staticMethodCall.ExplicitTypeArguments,
                                sourceName,
                                sourceText,
                                staticMethodCall.Span);

                        // `TS-P2-114`. A bare name here may be a function in scope
                        // rather than a type. In statement position `Helper(3)`
                        // parses as a command and resolves against the scope; an
                        // interpolation hole is re-parsed as a pure expression, where
                        // the same text becomes a qualified path and went straight to
                        // CLR resolution — so a module's sibling function was
                        // unreachable from a hole while `{M.Helper(3)}`, `{$f(3)}` and
                        // a top-level `{Plain(3)}` all worked.
                        //
                        // Resolved through the same scoped view a command call uses,
                        // which is the rule `TS-P2-01` already established for
                        // `f() + 1`. Only single-segment paths are considered: a
                        // dotted path is a qualified name and belongs below.
                        if (!staticMethodCall.Path.Contains('.', StringComparison.Ordinal) &&
                            CreateScopedCommandView().TryGet(staticMethodCall.Path, out var scopedCommand) &&
                            scopedCommand is IShellCallable scopedCallable)
                        {
                            return await InvokeCallableInExpressionAsync(
                                scopedCallable,
                                methodArguments,
                                sourceName,
                                sourceText,
                                staticMethodCall.Span,
                                staticMethodCall.Arguments.Select(argument => argument.Span).ToArray(),
                                cancellationToken);
                        }

                        return await InvokeQualifiedMethodAsync(
                            staticMethodCall.Path,
                            methodArguments,
                            cancellationToken,
                            typeArguments);
                    }

                case StaticMemberAccessArgumentSyntax staticMemberAccess:
                    {
                        return ResolveQualifiedAccessOrFallback(staticMemberAccess.Path);
                    }

                case ArrayLiteralArgumentSyntax listLiteral:
                    {
                        var items = new List<object?>();

                        foreach (var element in listLiteral.Items)
                        {
                            if (element is SpreadElementArgumentSyntax spread)
                            {
                                var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

                                if (spreadValue is string)
                                {
                                    items.Add(spreadValue);
                                }
                                else if (spreadValue is IEnumerable enumerable)
                                {
                                    foreach (var item in enumerable)
                                    {
                                        items.Add(item);
                                    }
                                }
                                else
                                {
                                    items.Add(spreadValue);
                                }
                            }
                            else
                            {
                                items.Add(await EvaluateArgumentAsync(sourceName, sourceText, element, cancellationToken));
                            }
                        }

                        return CreateTypedArray(items);
                    }

                case DictLiteralArgumentSyntax dictLiteral:
                    {
                        var dict = new Dictionary<object, object?>();

                        foreach (var entry in dictLiteral.Entries)
                        {
                            var key = await EvaluateArgumentAsync(sourceName, sourceText, entry.Key, cancellationToken);
                            var value = await EvaluateArgumentAsync(sourceName, sourceText, entry.Value, cancellationToken);
                            dict[key ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                Code: "tosh.runtime.null_dict_key",
                                Title: "Dict keys cannot be null.",
                                SourceName: sourceName,
                                SourceText: sourceText,
                                Span: entry.Key.Span,
                                Label: "this key evaluated to null"))] = value;
                        }

                        return CreateTypedDictionary(dict);
                    }

                case RecordLiteralArgumentSyntax recordLiteral:
                    {
                        IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();

                        foreach (var entry in recordLiteral.Fields)
                        {
                            switch (entry)
                            {
                                case RecordFieldSyntax field:
                                    record[field.Name] = await EvaluateArgumentAsync(sourceName, sourceText, field.Value, cancellationToken);
                                    break;

                                case ComputedRecordFieldSyntax computed:
                                    {
                                        var key = await EvaluateArgumentAsync(sourceName, sourceText, computed.NameExpression, cancellationToken);
                                        var value = await EvaluateArgumentAsync(sourceName, sourceText, computed.Value, cancellationToken);
                                        record[key?.ToString() ?? string.Empty] = value;
                                    }
                                    break;

                                case SpreadRecordEntrySyntax spread:
                                    {
                                        var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

                                        if (spreadValue is IDictionary<string, object?> dict)
                                        {
                                            foreach (var kvp in dict)
                                            {
                                                record[kvp.Key] = kvp.Value;
                                            }
                                        }
                                        else if (spreadValue is IShellRecordObject shellRecord)
                                        {
                                            foreach (var member in await shellRecord.GetMembersAsync(
                                                         includeHidden: false,
                                                         cancellationToken))
                                            {
                                                record[member.Key] = member.Value;
                                            }
                                        }
                                        else
                                        {
                                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                                Code: "tosh.runtime.spread_requires_record",
                                                Title: "Spread in a record literal requires a record or dictionary value.",
                                                SourceName: sourceName,
                                                SourceText: sourceText,
                                                Span: spread.Span,
                                                Label: "this value is not a record"));
                                        }
                                    }
                                    break;
                            }
                        }

                        return record;
                    }

                case TupleLiteralArgumentSyntax tupleLiteral:
                    {
                        var items = new object?[tupleLiteral.Items.Count];

                        for (var i = 0; i < tupleLiteral.Items.Count; i++)
                        {
                            items[i] = await EvaluateArgumentAsync(sourceName, sourceText, tupleLiteral.Items[i], cancellationToken);
                        }

                        return new ToshTuple(items);
                    }

                case SetLiteralArgumentSyntax setLiteral:
                    {
                        var set = new HashSet<object?>();

                        foreach (var element in setLiteral.Items)
                        {
                            var value = await EvaluateArgumentAsync(sourceName, sourceText, element, cancellationToken);
                            set.Add(value);
                        }

                        return set;
                    }

                case ListComprehensionArgumentSyntax listComp:
                    {
                        var items = new List<object?>();
                        await EvaluateComprehensionClauseAsync(
                            sourceName, sourceText, listComp.Clause,
                            async ct =>
                            {
                                items.Add(await EvaluateArgumentAsync(sourceName, sourceText, listComp.Body, ct));
                            },
                            cancellationToken);
                        return CreateTypedArray(items);
                    }

                case SetComprehensionArgumentSyntax setComp:
                    {
                        var set = new HashSet<object?>();
                        await EvaluateComprehensionClauseAsync(
                            sourceName, sourceText, setComp.Clause,
                            async ct =>
                            {
                                set.Add(await EvaluateArgumentAsync(sourceName, sourceText, setComp.Body, ct));
                            },
                            cancellationToken);
                        return set;
                    }

                case DictComprehensionArgumentSyntax dictComp:
                    {
                        var dict = new Dictionary<object, object?>();
                        await EvaluateComprehensionClauseAsync(
                            sourceName, sourceText, dictComp.Clause,
                            async ct =>
                            {
                                var key = await EvaluateArgumentAsync(sourceName, sourceText, dictComp.Key, ct);
                                var value = await EvaluateArgumentAsync(sourceName, sourceText, dictComp.Value, ct);
                                dict[key ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                    Code: "tosh.runtime.null_dict_key",
                                    Title: "Dict keys cannot be null.",
                                    SourceName: sourceName,
                                    SourceText: sourceText,
                                    Span: dictComp.Key.Span,
                                    Label: "this key evaluated to null"))] = value;
                            },
                            cancellationToken);
                        return CreateTypedDictionary(dict);
                    }

                case GeneratorComprehensionArgumentSyntax genComp:
                    {
                        // Evaluate the source eagerly so it captures current scope
                        var genSourceValue = await EvaluateArgumentAsync(sourceName, sourceText, genComp.Clause.Source, cancellationToken);

                        // Produce a lazy sequence that evaluates body items on demand
                        return new LazySequence(
                            EnumerateComprehensionLazily(sourceName, sourceText, genComp.Clause, genComp.Body, genSourceValue),
                            label: null);
                    }

                case BlockArgumentSyntax blockArgument:
                    {
                        return new ShellBlock(blockArgument.Block, sourceName, sourceText, blockArgument.Span);
                    }

                case QuoteArgumentSyntax quoteArgument:
                    {
                        // If the inner expression references a rune parameter (RuneThunk),
                        // return the thunk's AST wrapped as a QuotedSyntax.
                        if (quoteArgument.Inner is VariableReferenceArgumentSyntax varRef &&
                            TryGetVariableBinding(varRef.Name, out var quoteBinding) &&
                            quoteBinding.Value is RuneThunk quotedThunk)
                        {
                            return new QuotedSyntax(
                                quotedThunk.Syntax,
                                quotedThunk.SourceName,
                                quotedThunk.SourceText);
                        }

                        // Otherwise, capture the inner expression as-is
                        return new QuotedSyntax(quoteArgument.Inner, sourceName, sourceText);
                    }

                case AnonymousFunctionArgumentSyntax anonymousFunction:
                    {
                        var definition = CreateFunctionDefinition(
                            "<lambda>",
                            anonymousFunction.Parameters,
                            returnTypeName: anonymousFunction.ReturnTypeName,
                            anonymousFunction.Body,
                            isCommandWrapper: false,
                            sourceName,
                            sourceText,
                            anonymousFunction.Span);

                        return new ToshLambda(this, definition);
                    }

                case MemberProjectionArgumentSyntax projection:
                    {
                        return new ProjectedMemberSelection(projection.MemberPaths);
                    }

                case MemberAccessArgumentSyntax memberAccess:
                    {
                        var target = await EvaluateArgumentAsync(sourceName, sourceText, memberAccess.Target, cancellationToken);

                        if (memberAccess.NullSafe && target is null)
                        {
                            return null;
                        }

                        return await Runtime.ObjectAccessor.GetValueAsync(
                            target,
                            memberAccess.MemberPath,
                            cancellationToken);
                    }

                case IndexAccessArgumentSyntax indexAccess:
                    {
                        var target = await EvaluateArgumentAsync(sourceName, sourceText, indexAccess.Target, cancellationToken);
                        var index = await EvaluateArgumentAsync(sourceName, sourceText, indexAccess.Index, cancellationToken);
                        return await ShellIndexingUtilities.GetIndexedValueAsync(
                            target,
                            index,
                            indexAccess.LookupKind,
                            cancellationToken);
                    }

                case MethodCallArgumentSyntax methodCall:
                    {
                        var target = await ResolveMethodCallTargetAsync(sourceName, sourceText, methodCall, cancellationToken);

                        if (target is ShellTextLine textLine)
                        {
                            target = textLine.Text;
                        }

                        if (target is null)
                        {
                            if (methodCall.NullSafe)
                            {
                                return null;
                            }

                            throw new InvalidOperationException("Cannot invoke an instance method on null.");
                        }

                        var methodArguments = await EvaluateArgumentsAsync(sourceName, sourceText, methodCall.Arguments, cancellationToken);

                        // Names are resolved here rather than passed down: scope, aliases and
                        // ToastScript-declared types are all knowledge the engine holds and the
                        // invoker does not.
                        var methodTypeArguments = ResolveCallSiteTypeArguments(
                            methodCall.ExplicitTypeArguments,
                            methodCall.MethodName,
                            sourceName,
                            sourceText,
                            methodCall.Span);

                        // `TOAST-0001`. Inside a closure a bare name is implicit member
                        // access, so `where { double($_) }` arrives here as `$_.double($_)`
                        // and reported a missing method on Int32 — an error about a
                        // construct the reader had not written. Only the *synthesized*
                        // receiver may mean something else, and only once the item has been
                        // asked first.
                        if (methodCall.ImplicitCurrentItem &&
                            !ReceiverHasInstanceMember(target, methodCall.MethodName) &&
                            !await HasExtensionMethodAsync(target, methodCall.MethodName))
                        {
                            return await InvokeImplicitItemFallbackAsync(
                                sourceName,
                                sourceText,
                                methodCall,
                                target,
                                methodArguments,
                                cancellationToken);
                        }

                        var invocation = await Runtime.Invoker.InvokeInstanceMethodAsync(
                            target,
                            methodCall.MethodName,
                            methodArguments,
                            methodTypeArguments,
                            cancellationToken);
                        return invocation.ReturnedVoid ? target : invocation.Value;
                    }

                case CallableInvocationArgumentSyntax callableInvocation:
                    {
                        IShellCallable? callable = null;

                        // `TS-P2-01`. A bareword target names a function rather than
                        // holding one: in `(f() + 1)` the target is the word `f`, which
                        // evaluates to the *string* "f" and is not callable. Resolved
                        // through the same scoped view a command call uses, so a function
                        // composes in an operator expression the way it already did as a
                        // statement — `f() + 1` used to report "this value is not
                        // callable" once the parser learned to build the invocation.
                        if (callableInvocation.Target is BarewordArgumentSyntax { Value.Length: > 0 } named &&
                            CreateScopedCommandView().TryGet(named.Value, out var namedCommand) &&
                            namedCommand is IShellCallable namedCallable)
                        {
                            callable = namedCallable;
                        }

                        if (callable is null)
                        {
                            var target = await EvaluateArgumentAsync(sourceName, sourceText, callableInvocation.Target, cancellationToken);

                            if (target is not IShellCallable evaluated)
                            {
                                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                    Code: "tosh.runtime.value_not_callable",
                                    Title: "The provided value is not callable.",
                                    SourceName: sourceName,
                                    SourceText: sourceText,
                                    Span: callableInvocation.Target.Span,
                                    Label: "this value cannot be invoked",
                                    Help: "pass a lambda like 'func(x) => ...' or another callable shell value."));
                            }

                            callable = evaluated;
                        }

                        var callArguments = await EvaluateCallableInvocationArgumentsAsync(sourceName, sourceText, callableInvocation.Arguments, cancellationToken);

                        return await InvokeCallableInExpressionAsync(
                            callable,
                            callArguments,
                            sourceName,
                            sourceText,
                            callableInvocation.Span,
                            callableInvocation.Arguments.Select(argument => argument.Span).ToArray(),
                            cancellationToken);
                    }

                case SubexpressionArgumentSyntax subexpression:
                    {
                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, subexpression.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            return raw.Value;
                        }

                        var results = await AsyncEnumerableExtensions.ToListAsync(
                            EvaluatePipelineAsync(sourceName, sourceText, subexpression.Pipeline, cancellationToken, outputIsCaptured: true),
                            cancellationToken);

                        if (results.Count <= 1)
                        {
                            return results.Count == 1 ? results[0] : null;
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.subexpression_requires_single_value",
                            Title: "Subexpressions used as arguments must produce exactly one value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: argument.Span,
                            Label: $"this subexpression produced {results.Count} values",
                            Help: "ensure the parenthesized pipeline returns exactly one object."));
                    }

                case CommandSubstitutionArgumentSyntax commandSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, commandSubstitution.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            results = [raw.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, commandSubstitution.Pipeline, cancellationToken, outputIsCaptured: true),
                                cancellationToken);
                        }

                        return string.Join(Environment.NewLine, results.Select(FormatCommandSubstitutionValue));
                    }

                case InputProcessSubstitutionArgumentSyntax processSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, processSubstitution.Pipeline, cancellationToken) is { Matched: true } raw)
                        {
                            results = [raw.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, processSubstitution.Pipeline, cancellationToken, outputIsCaptured: true),
                                cancellationToken);
                        }

                        return await PipelineFileMaterializer.MaterializeAsync("text", results, cancellationToken);
                    }

                case OutputProcessSubstitutionArgumentSyntax outputProcessSubstitution:
                    {
                        IReadOnlyList<object?> results;

                        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, outputProcessSubstitution.Pipeline, cancellationToken) is { Matched: true } rawOutput)
                        {
                            results = [rawOutput.Value];
                        }
                        else
                        {
                            results = await AsyncEnumerableExtensions.ToListAsync(
                                EvaluatePipelineAsync(sourceName, sourceText, outputProcessSubstitution.Pipeline, cancellationToken, outputIsCaptured: true),
                                cancellationToken);
                        }

                        return await PipelineFileMaterializer.MaterializeAsync("text", results, cancellationToken);
                    }

                case ChainedComparisonArgumentSyntax chain:
                    {
                        // TS-P1-22: `a < b < c` means `(a < b) and (b < c)`
                        // with each operand evaluated exactly once and
                        // short-circuit preserved, so a failing earlier
                        // comparison never evaluates later operands.
                        var current = await EvaluateArgumentAsync(
                            sourceName, sourceText, chain.Operands[0], cancellationToken);

                        for (var i = 0; i < chain.Operators.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var next = await EvaluateArgumentAsync(
                                sourceName, sourceText, chain.Operands[i + 1], cancellationToken);

                            var comparison = await EvaluateBinaryOperatorAsync(
                                sourceName,
                                sourceText,
                                chain.OperatorSpans[i],
                                current,
                                chain.Operators[i],
                                next,
                                cancellationToken);

                            if (!OperatorEvaluator.ToBoolean(comparison))
                            {
                                return false;
                            }

                            current = next;
                        }

                        return true;
                    }

                case OperatorArgumentSyntax operation:
                    {
                        // Constant-folded by the lowering pass: skip both
                        // sub-evaluations and return the precomputed value.
                        if (operation.FoldedConstant is { } cachedBinary)
                        {
                            return cachedBinary.Value;
                        }

                        var left = await EvaluateArgumentAsync(sourceName, sourceText, operation.Left, cancellationToken);

                        // Short-circuit: do not evaluate the right side if unnecessary.
                        if (operation.Operator == "and")
                        {
                            return OperatorEvaluator.ToBoolean(left)
                                && OperatorEvaluator.ToBoolean(await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken));
                        }

                        if (operation.Operator == "or")
                        {
                            return OperatorEvaluator.ToBoolean(left)
                                || OperatorEvaluator.ToBoolean(await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken));
                        }

                        if (operation.Operator == "??")
                        {
                            return left ?? await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken);
                        }

                        var right = await EvaluateArgumentAsync(sourceName, sourceText, operation.Right, cancellationToken);

                        return await EvaluateBinaryOperatorAsync(
                            sourceName,
                            sourceText,
                            operation.Span,
                            left,
                            operation.Operator,
                            right,
                            cancellationToken);
                    }

                case ConditionalArgumentSyntax conditional:
                    {
                        var condition = await EvaluateArgumentAsync(sourceName, sourceText, conditional.Condition, cancellationToken);
                        return OperatorEvaluator.ToBoolean(condition)
                            ? await EvaluateArgumentAsync(sourceName, sourceText, conditional.WhenTrue, cancellationToken)
                            : await EvaluateArgumentAsync(sourceName, sourceText, conditional.WhenFalse, cancellationToken);
                    }

                case ThrowArgumentSyntax throwArg:
                    {
                        object? raised;
                        if (throwArg.Value is null)
                        {
                            raised = new CommandFailure("An error was thrown.");
                        }
                        else
                        {
                            raised = await EvaluateArgumentAsync(sourceName, sourceText, throwArg.Value, cancellationToken);
                        }
                        await RaiseThrownValueAsync(throwArg.Span, raised, cancellationToken);
                        return null; // unreachable; satisfies the compiler
                    }

                case MatchArgumentSyntax match:
                    {
                        var arm = await ResolveMatchArmAsync(sourceName, sourceText, match, cancellationToken);
                        return await EvaluateMatchArmValueAsync(sourceName, sourceText, arm, cancellationToken);
                    }

                case IfExpressionArgumentSyntax ifExpression:
                    {
                        var condition = await EvaluateConditionAsync(sourceName, sourceText, ifExpression.Condition, cancellationToken);
                        var block = condition ? ifExpression.ThenBlock : ifExpression.ElseBlock;
                        var values = await AsyncEnumerableExtensions.ToListAsync(
                            ExecuteBlockAsync(sourceName, sourceText, block, cancellationToken),
                            cancellationToken);

                        return values.Count switch
                        {
                            0 => null,
                            1 => values[0],
                            _ => values.ToArray(),
                        };
                    }

                case UnaryOperatorArgumentSyntax unaryOperation:
                    {
                        // Constant-folded by the lowering pass.
                        if (unaryOperation.FoldedConstant is { } cachedUnary)
                        {
                            return cachedUnary.Value;
                        }

                        var operand = await EvaluateArgumentAsync(sourceName, sourceText, unaryOperation.Operand, cancellationToken);

                        if (operand is ToshClassInstance unaryInst)
                        {
                            var unaryOverload = await TryInvokeClassUnaryOperatorAsync(
                                unaryInst,
                                unaryOperation.Operator,
                                cancellationToken);
                            if (unaryOverload.Matched)
                            {
                                return unaryOverload.Value;
                            }
                        }

                        return OperatorEvaluator.EvaluateUnary(unaryOperation.Operator, operand);
                    }

                case InterpolatedStringArgumentSyntax interpolated:
                    {
                        var builder = new System.Text.StringBuilder();

                        foreach (var part in interpolated.Parts)
                        {
                            switch (part)
                            {
                                case InterpolatedStringLiteralPart literal:
                                    builder.Append(literal.Text);
                                    break;

                                case InterpolatedStringExpressionPart expression:
                                    {
                                        // The hole's value is being consumed into a string, so an
                                        // external command inside it must have its stdout piped
                                        // rather than inherited — `echo $"{git rev-parse …}"` used
                                        // to print the branch to the terminal and interpolate the
                                        // empty string (TS-P1-32).
                                        var results = await AsyncEnumerableExtensions.ToListAsync(
                                            EvaluateParseResultAsync(
                                                PrepareInterpolationHole(expression, sourceName),
                                                cancellationToken,
                                                outputIsCaptured: true),
                                            cancellationToken);

                                        if (results.Count == 1)
                                        {
                                            builder.Append(ApplyInterpolationClauses(
                                                await FormatInterpolatedValueAsync(
                                                    results[0],
                                                    cancellationToken,
                                                    expression.Format,
                                                    sourceName,
                                                    sourceText,
                                                    expression.ExpressionSpan),
                                                expression.Alignment));
                                        }
                                        else if (results.Count > 1)
                                        {
                                            var formatted = new string[results.Count];
                                            for (var index = 0; index < results.Count; index++)
                                            {
                                                formatted[index] = await FormatInterpolatedValueAsync(
                                                    results[index],
                                                    cancellationToken,
                                                    expression.Format,
                                                    sourceName,
                                                    sourceText,
                                                    expression.ExpressionSpan);
                                            }

                                            // Alignment pads the joined text, not each item:
                                            // the clause describes the field the hole occupies.
                                            builder.Append(ApplyInterpolationClauses(
                                                string.Join(" ", formatted),
                                                expression.Alignment));
                                        }

                                        break;
                                    }
                            }
                        }

                        return builder.ToString();
                    }

                case NameOfArgumentSyntax nameOf:
                    {
                        // If not a $-prefixed variable reference, check if the bare identifier
                        // actually refers to a variable — if so, require '$'.
                        //
                        // `TS-P2-20`. A member or type path is exempt: its last segment is a
                        // member name, and a variable that happens to share it says nothing about
                        // what was written. Without this, `nameof(K.S)` beside a variable `$S`
                        // would demand `nameof($S)` — a name the operand never mentioned.
                        if (!nameOf.IsVariableReference && !nameOf.IsMemberChain &&
                            TryGetVariableBinding(nameOf.Identifier, out _))
                        {
                            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                                Code: "tosh.runtime.nameof_requires_dollar",
                                Title: $"Variable references in nameof require '$'. Use nameof(${nameOf.Identifier}).",
                                SourceName: sourceName,
                                SourceText: sourceText,
                                Span: nameOf.Span,
                                Label: $"did you mean '${nameOf.Identifier}'?",
                                Help: $"try nameof(${nameOf.Identifier}) to get the variable name."));
                        }

                        return nameOf.Identifier;
                    }

                case FunctionReferenceArgumentSyntax funcRef:
                    {
                        // Look up the function/command by name and return it as a callable value.
                        foreach (var scope in _scopes)
                        {
                            if (scope.Commands.TryGetValue(funcRef.Name, out var scopedCommand))
                            {
                                return scopedCommand;
                            }
                        }

                        if (Runtime.Commands.TryGet(funcRef.Name, out var registeredCommand))
                        {
                            return registeredCommand;
                        }

                        // `TS-P2-94`. A dotted name is in neither flat table, so it is
                        // resolved here.
                        //
                        // A static method is tried first, and the order matters: it is
                        // not a value — reading `C.Method` deliberately raises "call it
                        // with parentheses" — so letting the value path run first threw
                        // that error before this branch was ever reached.
                        if (funcRef.Name.LastIndexOf('.') is var dot and > 0 &&
                            TryResolveShellStaticType(funcRef.Name[..dot], out var ownerType) &&
                            ownerType is ToshClassDefinition ownerClass &&
                            ownerClass.HasStaticMethod(funcRef.Name[(dot + 1)..]))
                        {
                            return new ToshStaticMethodReference(ownerClass, funcRef.Name[(dot + 1)..]);
                        }

                        // `&$obj.Method` — bound to a receiver. Tried before the
                        // qualified-value path because `$obj.Method` on a class instance
                        // is not a value either: reading a method without parentheses
                        // raises the same "call it" error a static does, so the value
                        // path would throw before this branch was reached.
                        if (funcRef.Name.StartsWith('$') &&
                            funcRef.Name.IndexOf('.', StringComparison.Ordinal) is var receiverDot and > 0 &&
                            TryGetVariableValue(funcRef.Name[1..receiverDot], out var receiver) &&
                            receiver is not null)
                        {
                            return new ToshBoundMethodReference(
                                receiver,
                                funcRef.Name[(receiverDot + 1)..],
                                Runtime.Invoker);
                        }

                        // A module-qualified function, by contrast, already evaluates
                        // to a callable through ordinary member access — `var f = M.E`
                        // has always worked — so it needs only that same resolution.
                        if (funcRef.Name.Contains('.', StringComparison.Ordinal) &&
                            TryResolveQualifiedAccess(funcRef.Name, out var qualifiedValue, out _) &&
                            qualifiedValue is IShellCallable qualifiedCallable)
                        {
                            return qualifiedCallable;
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.unknown_function_reference",
                            Title: $"Function '{funcRef.Name}' was not found.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: funcRef.Span,
                            Label: $"'{funcRef.Name}' is not defined in this scope",
                            Help: "define the function first or check the spelling."));
                    }

                case RangeArgumentSyntax range:
                    {
                        var startValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Start, cancellationToken);
                        var start = ConvertToInt(startValue, "range start");

                        int? end = null;
                        if (range.End is not null)
                        {
                            var endValue = await EvaluateArgumentAsync(sourceName, sourceText, range.End, cancellationToken);
                            end = ConvertToInt(endValue, "range end");
                        }

                        int? step = null;
                        if (range.Step is not null)
                        {
                            var stepValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Step, cancellationToken);
                            step = ConvertToInt(stepValue, "range step");
                        }

                        return new ToshRange(start, step, end);
                    }

                case NamedArgumentSyntax namedArg:
                    {
                        var value = await EvaluateArgumentAsync(sourceName, sourceText, namedArg.Value, cancellationToken);
                        return new NamedArgument(namedArg.Name, value);
                    }

                default:
                    throw new InvalidOperationException($"Unsupported argument syntax: {argument.GetType().Name}.");
            }
        }
        // A native binding is invoked through a module or class member, neither
        // of which carries a CommandSpan, so the failure arrives spanless and the
        // renderer would underline the start of the script. This is the first
        // frame that knows where the call was written.
        catch (NativeError native) when (native.Span.Length == 0 && native.Span.Start == 0)
        {
            native.Span = argument.Span;
            throw;
        }
        catch (Exception exception) when (exception is not ToshDiagnosticException && exception is not OperationCanceledException && exception is not Tosh.Runtime.ShellControlFlowException && !IsToshThrown(exception))
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, argument, exception);
        }
    }

    /// <summary>
    /// Rejects a call that supplies the same parameter name twice
    /// (TS-P1-06). A duplicate is invalid for every candidate, so it is
    /// diagnosed once at the call site rather than being treated as an
    /// overload mismatch — otherwise the caller would see a misleading
    /// "no overload matched" failure.
    /// </summary>
    internal static void ValidateNamedArgumentUniqueness(
        IReadOnlyList<object?> arguments,
        string calleeLabel)
    {
        HashSet<string>? seen = null;

        foreach (var argument in arguments)
        {
            if (argument is not NamedArgument named)
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(named.Name))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.duplicate_named_argument",
                    Title: $"Named argument '{named.Name}' was supplied more than once to {calleeLabel}.",
                    Label: $"'{named.Name}' is already bound by an earlier named argument",
                    Help: "each parameter may be bound by at most one named argument."));
            }
        }
    }

    /// <summary>
    /// True when every named argument matches a declared parameter.
    /// Used by overload selection, where an unmatched name must make the
    /// candidate lose rather than fail the whole call (TS-P1-06): a
    /// sibling overload may well declare that parameter.
    /// </summary>
    private static bool AllNamedArgumentsMatchParameters(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyDictionary<string, object?> namedArgs)
    {
        foreach (var name in namedArgs.Keys)
        {
            var matched = false;
            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryBindCallableParameters(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments,
        out Dictionary<string, object?> locals,
        out int score)
        => TryBindCallableParameters(parameters, arguments, out locals, out score, out _);

    /// <summary>
    /// One parameter slot's outcome from binding, before any type conversion is attempted.
    /// </summary>
    /// <param name="Parameter">The parameter this step fills.</param>
    /// <param name="Value">The argument it receives; meaningless when <paramref name="IsMissing"/>.</param>
    /// <param name="IsRest">Whether this step contributes to the trailing rest collection.</param>
    /// <param name="IsMissing">
    /// No argument was supplied, so the slot takes its default. Defaults are deliberately *not*
    /// evaluated here: a losing overload candidate must never run a default's side effects, so the
    /// winner's are applied afterwards by <c>ApplyPendingParameterDefaults</c>.
    /// </param>
    private readonly record struct CallableBindingStep(
        FunctionParameterDefinition Parameter,
        object? Value,
        bool IsRest,
        bool IsMissing);

    /// <summary>
    /// Works out which argument fills which parameter, without converting anything.
    /// </summary>
    /// <remarks>
    /// This is everything the two <c>TryBindCallableParameters</c> twins duplicated: splitting
    /// named from positional arguments, rejecting names that match no parameter, counting required
    /// parameters, the arity check, named-takes-priority ordering, and the rest-parameter tail.
    /// Both then walked the same slots, one converting synchronously and the other awaiting — the
    /// only genuine difference between them (<c>TS-P1-24</c>).
    ///
    /// Deliberately *not* converged by making the synchronous form block on the asynchronous one,
    /// which is the pattern used elsewhere in this file. Overload resolution runs on the command
    /// dispatch path, and turning it into sync-over-async would change threading behaviour under
    /// load to remove a duplication that this planner removes anyway.
    /// </remarks>
    /// <returns>The ordered steps, or <see langword="null"/> when the arguments cannot fit.</returns>
    private static List<CallableBindingStep>? PlanCallableParameterBinding(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments)
    {
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

        if (!AllNamedArgumentsMatchParameters(parameters, namedArgs))
        {
            return null;
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

        var steps = new List<CallableBindingStep>(parameters.Count);
        var positionalIndex = 0;

        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = parameters[index];

            if (namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                steps.Add(new CallableBindingStep(parameter, namedValue, IsRest: false, IsMissing: false));
                continue;
            }

            if (positionalIndex >= positionalArgs.Count)
            {
                steps.Add(new CallableBindingStep(parameter, null, IsRest: false, IsMissing: true));
                continue;
            }

            steps.Add(new CallableBindingStep(
                parameter,
                positionalArgs[positionalIndex++],
                IsRest: false,
                IsMissing: false));
        }

        if (hasRestParameter)
        {
            var restParameter = parameters[^1];

            for (var index = positionalCount; index < arguments.Count; index++)
            {
                steps.Add(new CallableBindingStep(restParameter, arguments[index], IsRest: true, IsMissing: false));
            }
        }

        return steps;
    }

    /// <summary>
    /// Records a slot that had no argument: the local is null, a default is queued rather than
    /// run, and the score reflects how good a match this is. Shared so the two twins cannot
    /// disagree about scoring, which is what decides overload resolution.
    /// </summary>
    private static void ApplyMissingArgumentStep(
        CallableBindingStep step,
        Dictionary<string, object?> locals,
        ref int score,
        ref List<FunctionParameterDefinition>? pendingDefaults)
    {
        locals[step.Parameter.Name] = null;

        if (step.Parameter.DefaultValue is not null)
        {
            (pendingDefaults ??= new List<FunctionParameterDefinition>()).Add(step.Parameter);
        }

        score += step.Parameter.DefaultValue is not null ? 1 : 4;
    }

    internal bool TryBindCallableParameters(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments,
        out Dictionary<string, object?> locals,
        out int score,
        out List<FunctionParameterDefinition>? pendingDefaults)
    {
        locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        score = 0;
        pendingDefaults = null;

        if (PlanCallableParameterBinding(parameters, arguments) is not { } steps)
        {
            return false;
        }

        locals["args"] = arguments.ToArray();
        List<object?>? restArguments = null;

        foreach (var step in steps)
        {
            if (step.IsMissing)
            {
                ApplyMissingArgumentStep(step, locals, ref score, ref pendingDefaults);
                continue;
            }

            if (!TryConvertParameterValue(step.Parameter, step.Value, out var converted, out var failure))
            {
                if (failure is not null)
                {
                    throw failure;
                }

                locals = new Dictionary<string, object?>(StringComparer.Ordinal);
                score = 0;
                return false;
            }

            if (step.IsRest)
            {
                (restArguments ??= []).Add(converted);
                continue;
            }

            if (!ReferenceEquals(converted, step.Value))
            {
                score += 1;
            }

            locals[step.Parameter.Name] = converted;
        }

        if (restArguments is not null)
        {
            locals[parameters[^1].Name] = restArguments;
        }
        else if (parameters.Count > 0 && parameters[^1].IsRest)
        {
            // A rest parameter with no arguments still binds, to an empty list.
            locals[parameters[^1].Name] = new List<object?>();
        }

        return true;
    }

    private bool TryConvertParameterValue(
        FunctionParameterDefinition parameter,
        object? value,
        out object? converted,
        out ToshDiagnosticException? failure)
    {
        converted = value;
        failure = null;

        if (parameter.TypeName is not null &&
            !TryConvertAnnotatedValue(parameter.TypeName, value, out converted))
        {
            failure = DescribeAnnotationFailure(converted);
            return false;
        }

        return TryApplyRefinementWithOptionalCoercion(parameter.Refinement, converted, out converted, out failure);
    }

    /// <summary>
    /// When no candidate bound, a named argument that matches no
    /// parameter of any candidate is definitively wrong, so it gets a
    /// precise diagnostic instead of the caller's generic "no overload
    /// matched" message (TS-P1-06).
    /// </summary>
    private static void ThrowIfNamedArgumentMatchesNoCandidate(
        IReadOnlyList<IReadOnlyList<FunctionParameterDefinition>> candidateParameters,
        IReadOnlyList<object?> arguments)
    {
        if (candidateParameters.Count == 0)
        {
            return;
        }

        foreach (var argument in arguments)
        {
            if (argument is not NamedArgument named)
            {
                continue;
            }

            var matchedAnywhere = false;
            foreach (var parameters in candidateParameters)
            {
                foreach (var parameter in parameters)
                {
                    if (string.Equals(parameter.Name, named.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAnywhere = true;
                        break;
                    }
                }

                if (matchedAnywhere)
                {
                    break;
                }
            }

            if (matchedAnywhere)
            {
                continue;
            }

            var declaredNames = candidateParameters
                .SelectMany(parameters => parameters.Select(parameter => parameter.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var declared = declaredNames.Length == 0
                ? "no parameters are declared"
                : $"declared parameters: {string.Join(", ", declaredNames)}";

            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unknown_named_argument",
                Title: $"There is no parameter named '{named.Name}'.",
                Label: $"'{named.Name}' does not match a parameter",
                Help: $"{declared}."));
        }
    }

    internal void ApplyPendingParameterDefaults(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        Dictionary<string, object?> locals,
        IReadOnlyList<FunctionParameterDefinition>? pendingDefaults,
        string sourceName,
        string sourceText,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName,
        IReadOnlyDictionary<string, object?>? ambient = null,
        bool selfUnavailable = false)
    {
        if (pendingDefaults is null || pendingDefaults.Count == 0)
        {
            return;
        }

        var pendingNames = CollectPendingDefaultNames(pendingDefaults);
        var visible = SeedDefaultScope(ambient);

        foreach (var parameter in parameters)
        {
            if (!NeedsPendingDefault(parameter, pendingNames, locals, visible))
            {
                continue;
            }

            object? value;
            try
            {
                value = EvaluateClassPipelineValueSync(
                    null,
                    sourceName,
                    sourceText,
                    parameter.DefaultValue!,
                    visible,
                    capturedScopes,
                    callName);
            }
            catch (ToshDiagnosticException selfFailure) when (selfUnavailable && ReferencesUnavailableSelf(selfFailure))
            {
                throw CreateSelfUnavailableInDefaultDiagnostic(parameter, sourceName, sourceText);
            }

            if (!TryConvertParameterValue(parameter, value, out var converted, out var failure))
            {
                throw failure ?? CreateParameterDefaultConversionDiagnostic(parameter, sourceName, sourceText);
            }

            locals[parameter.Name] = converted;
            visible[parameter.Name] = converted;
        }
    }

    /// <inheritdoc cref="ApplyPendingParameterDefaults"/>
    internal async ValueTask ApplyPendingParameterDefaultsAsync(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        Dictionary<string, object?> locals,
        IReadOnlyList<FunctionParameterDefinition>? pendingDefaults,
        string sourceName,
        string sourceText,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? ambient = null,
        bool selfUnavailable = false)
    {
        if (pendingDefaults is null || pendingDefaults.Count == 0)
        {
            return;
        }

        var pendingNames = CollectPendingDefaultNames(pendingDefaults);
        var visible = SeedDefaultScope(ambient);

        foreach (var parameter in parameters)
        {
            if (!NeedsPendingDefault(parameter, pendingNames, locals, visible))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            object? value;
            try
            {
                value = await EvaluateClassPipelineValueAsync(
                    null,
                    sourceName,
                    sourceText,
                    parameter.DefaultValue!,
                    visible,
                    capturedScopes,
                    cancellationToken,
                    callName);
            }
            catch (ToshDiagnosticException failure) when (selfUnavailable && ReferencesUnavailableSelf(failure))
            {
                throw CreateSelfUnavailableInDefaultDiagnostic(parameter, sourceName, sourceText);
            }

            var conversion = await TryConvertParameterValueAsync(parameter, value, cancellationToken);
            if (!conversion.Success)
            {
                throw conversion.Failure ?? CreateParameterDefaultConversionDiagnostic(parameter, sourceName, sourceText);
            }

            locals[parameter.Name] = conversion.Converted;
            visible[parameter.Name] = conversion.Converted;
        }
    }

    internal void EnsureFunctionArgumentsMatch(FunctionDefinition definition, CommandContext context)
    {
        _ = BindFunctionParameters(definition, context, Array.Empty<object?>());
    }

    private Dictionary<string, object?> BindFunctionParametersForLambdaCheck(
        FunctionDefinition definition,
        CommandContext context)
    {
        return BindFunctionParameters(definition, context, Array.Empty<object?>()).Locals;
    }

    private (Dictionary<string, object?> Locals, Dictionary<string, Type>? TypeBindings) BindFunctionParameters(
        FunctionDefinition definition,
        CommandContext context,
        IReadOnlyList<object?> inputItems)
    {
        Dictionary<string, Type>? typeBindings = null;
        if (definition.TypeParameters is { Count: > 0 } typeParamsForSeed)
        {
            typeBindings = new Dictionary<string, Type>(StringComparer.Ordinal);

            // Phase 3.3 — seed explicit call-site type arguments
            // (e.g. `box<int> 42`). These bindings are authoritative:
            // later inference must agree with them, otherwise the
            // strict-mismatch path fires.
            var explicitArgs = context.Invocation?.ExplicitTypeArguments;
            if (explicitArgs is { Count: > 0 } explicitList)
            {
                if (explicitList.Count != typeParamsForSeed.Count)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.generic_type_argument_count_mismatch",
                        title: $"Function '{definition.Name}' has {typeParamsForSeed.Count} type parameter(s) but received {explicitList.Count} type argument(s).",
                        label: $"'{definition.Name}' takes <{string.Join(", ", typeParamsForSeed)}>");
                }
                for (var i = 0; i < typeParamsForSeed.Count; i++)
                {
                    var typeName = explicitList[i];
                    var resolved = TryResolveTypeName(typeName);
                    if (resolved is null)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.unknown_type_name",
                            title: $"Type '{typeName}' could not be resolved for type parameter '{typeParamsForSeed[i]}' of function '{definition.Name}'.",
                            label: $"unknown type '{typeName}'");
                    }
                    typeBindings[typeParamsForSeed[i]] = resolved;
                }
            }

            // Phase 3.2 — seed bindings from the LHS target-type
            // annotation when explicit type args weren't supplied.
            // Example: `var x: int = identity<T> 42` — the target type
            // `int` propagates into `T` via the function's return
            // annotation. Annotation-vs-annotation unification handles
            // nested shapes (e.g. `var xs: list<int> = make<list<T>>()`).
            if ((typeBindings.Count == 0)
                && context.Invocation?.TargetTypeAnnotation is { Length: > 0 } targetAnnot
                && definition.RawReturnTypeName is { Length: > 0 } returnAnnot)
            {
                var seedTarget = new GenericInferenceTarget(
                    OwnerLabel: $"function '{definition.Name}'",
                    TypeParameters: typeParamsForSeed,
                    TypeParameterConstraints: definition.TypeParameterConstraints);
                UnifyAnnotationWithAnnotation(seedTarget, returnAnnot, targetAnnot, typeBindings);
            }
        }

        var hasRestParameter = definition.Parameters.Count > 0 && definition.Parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? definition.Parameters.Count - 1 : definition.Parameters.Count;
        var allowsImplicitWrapperArguments = definition.IsCommandWrapper && definition.Parameters.Count == 0;

        // Separate named and positional arguments
        var namedArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var positionalArgs = new List<object?>();

        // A duplicate name is invalid regardless of the parameter list,
        // so it is reported before any binding decision (TS-P1-06).
        ValidateNamedArgumentUniqueness(context.Arguments, $"function '{definition.Name}'");

        foreach (var arg in context.Arguments)
        {
            if (arg is NamedArgument named)
            {
                namedArgs[named.Name] = named.Value;
            }
            else
            {
                positionalArgs.Add(arg);
            }
        }

        // This binder serves one concrete definition, so an unmatched
        // name cannot be another overload's parameter: report it instead
        // of silently dropping the argument (TS-P1-06).
        if (!allowsImplicitWrapperArguments && !AllNamedArgumentsMatchParameters(definition.Parameters, namedArgs))
        {
            var unknown = namedArgs.Keys.First(name =>
                !definition.Parameters.Any(parameter =>
                    string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)));
            var declared = definition.Parameters.Count == 0
                ? "it declares no parameters"
                : $"declared parameters: {string.Join(", ", definition.Parameters.Select(parameter => parameter.Name))}";

            throw context.CreateDiagnostic(
                code: "tosh.runtime.unknown_named_argument",
                title: $"Function '{definition.Name}' has no parameter named '{unknown}'.",
                label: $"'{unknown}' does not match a parameter",
                help: $"{declared}.");
        }

        var requiredCount = definition.Parameters.Count(p =>
            !p.IsOptional && !p.IsRest && p.DefaultValue is null && !namedArgs.ContainsKey(p.Name));

        if (positionalArgs.Count < requiredCount ||
            (!allowsImplicitWrapperArguments && !hasRestParameter && positionalArgs.Count > positionalCount - namedArgs.Count))
        {
            var totalRequired = definition.Parameters.Count(p => !p.IsOptional && !p.IsRest && p.DefaultValue is null);
            var expected = totalRequired == positionalCount
                ? $"{positionalCount}"
                : $"{totalRequired}-{positionalCount}";
            if (hasRestParameter)
            {
                expected = $"at least {totalRequired}";
            }
            throw context.CreateDiagnostic(
                code: "tosh.runtime.function_argument_count_mismatch",
                title: $"Function '{definition.Name}' expects {expected} argument(s) but received {context.Arguments.Count}.",
                label: $"'{definition.Name}' requires {expected} argument(s)");
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var positionalIndex = 0;

        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = definition.Parameters[index];

            // Named argument takes priority
            if (namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                // Bind generics from the pre-conversion value so
                // element-type info isn't widened to object by the
                // erased-annotation conversion path.
                ApplyGenericBinding(definition, parameter, namedValue, context, index, typeBindings);
                var converted = ConvertFunctionParameterValue(definition, context, parameter, namedValue, index);
                locals[parameter.Name] = converted;
                continue;
            }

            if (positionalIndex >= positionalArgs.Count)
            {
                // Optional parameter with no argument — bind as null
                locals[parameter.Name] = null;
                continue;
            }

            var value = positionalArgs[positionalIndex++];

            ApplyGenericBinding(definition, parameter, value, context, index, typeBindings);
            var convertedValue = ConvertFunctionParameterValue(definition, context, parameter, value, index);
            locals[parameter.Name] = convertedValue;
        }

        if (hasRestParameter)
        {
            var restParam = definition.Parameters[^1];
            var restArgs = new List<object?>();
            for (var i = positionalCount; i < context.Arguments.Count; i++)
            {
                var rawRest = context.Arguments[i];
                ApplyGenericBinding(definition, restParam, rawRest, context, i, typeBindings);
                var convertedRest = ConvertFunctionParameterValue(definition, context, restParam, rawRest, i);
                restArgs.Add(convertedRest);
            }
            locals[restParam.Name] = restArgs;
        }

        return (locals, typeBindings);
    }

    private object? ConvertFunctionParameterValue(
        FunctionDefinition definition,
        CommandContext context,
        FunctionParameterDefinition parameter,
        object? value,
        int argumentIndex)
    {
        try
        {
            return ConvertAnnotatedValue(
                parameter.TypeName,
                parameter.Refinement,
                value,
                parameter.Span,
                definition.SourceName,
                definition.SourceText,
                $"{definition.Name}.{parameter.Name}");
        }
        catch (ToshDiagnosticException exception)
        {
            if (exception.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "tosh.runtime.annotation_unknown_type", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code, "tosh.runtime.refinement_failed", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code, "tosh.runtime.expression_failed", StringComparison.Ordinal)))
            {
                throw;
            }

            if (parameter.Refinement is not null)
            {
                throw;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.parameter_type_conversion_failed",
                title: $"Argument '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
                argumentIndex: argumentIndex,
                label: $"'{parameter.Name}' expects {parameter.TypeName}");
        }
    }
}
