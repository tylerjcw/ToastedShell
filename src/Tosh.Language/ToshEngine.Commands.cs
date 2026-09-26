using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Bridge;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    private void ValidateScriptInputs(
        string sourceName,
        string sourceText,
        IReadOnlyList<FunctionParameterSyntax> flagParameters,
        IReadOnlyList<FunctionParameterSyntax> argumentParameters)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var allParameters = flagParameters.Concat(argumentParameters).ToArray();

        foreach (var parameter in allParameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");

            if (!seenNames.Add(parameter.Name))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.duplicate_script_input",
                    Title: $"Script input '{parameter.Name}' is declared more than once.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: "use each script input name only once"));
            }
        }

        foreach (var flag in flagParameters)
        {
            if (flag.IsRest)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.script_flag_cannot_be_rest",
                    Title: "Script flags cannot use rest parameters.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: flag.Span,
                    Label: "use 'arg rest...' for positional rest arguments"));
            }
        }

        for (var index = 0; index < argumentParameters.Count; index++)
        {
            var argument = argumentParameters[index];

            if (argument.IsRest && index != argumentParameters.Count - 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.script_rest_argument_must_be_last",
                    Title: "A script rest argument must be the last argument.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: argument.Span,
                    Label: "move this rest argument after the other script arguments"));
            }
        }
    }

    /// <summary>
    /// Whether the script was invoked with <c>--help</c> (or <c>-h</c>) and should describe itself
    /// instead of running.
    /// </summary>
    /// <remarks>
    /// A script that declares its own <c>help</c> or <c>h</c> flag keeps it: the built-in answer
    /// is a default for scripts that have not said otherwise, never an override of one that has.
    /// Arguments after a bare <c>--</c> are the script's data and are not scanned, for the same
    /// reason the ordinary flag parser stops there.
    /// </remarks>
    private static bool ScriptHelpWasRequested(
        IReadOnlyList<object?> arguments,
        IReadOnlyList<FunctionParameterSyntax> flagParameters)
    {
        foreach (var flag in flagParameters)
        {
            if (string.Equals(flag.Name, "help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(flag.Name, "h", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var argument in arguments)
        {
            if (argument is not string text)
            {
                continue;
            }

            if (text == "--")
            {
                return false;
            }

            if (text is "--help" or "-h")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the usage a script's own declarations describe: its doc-comment summary, a usage
    /// line, and every argument and flag with the description written above it.
    /// </summary>
    private async Task WriteScriptUsageAsync(
        string sourceName,
        DocComment? scriptDoc,
        IReadOnlyList<FunctionParameterSyntax> argumentParameters,
        IReadOnlyList<FunctionParameterSyntax> flagParameters,
        IReadOnlyDictionary<string, string> documented,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(sourceName);
        var output = LanguageRuntime.Output;

        // The file-level doc-block, which the parser separates from a declaration's own block by
        // requiring a blank line between them. Taking the first declaration's description instead
        // printed "Who to greet." as the summary of a script that greets people.
        if (scriptDoc?.Description is { Length: > 0 } summary && !string.IsNullOrWhiteSpace(summary))
        {
            await output.WriteTextLineAsync(summary.Trim(), cancellationToken);
            await output.WriteTextLineAsync(string.Empty, cancellationToken);
        }

        var usage = new StringBuilder($"Usage: {name}");
        if (flagParameters.Count > 0)
        {
            usage.Append(" [options]");
        }

        foreach (var argument in argumentParameters)
        {
            usage.Append(argument switch
            {
                { IsRest: true } => $" [{argument.Name}...]",
                { IsOptional: true } => $" [{argument.Name}]",
                _ => $" <{argument.Name}>",
            });
        }

        await output.WriteTextLineAsync(usage.ToString(), cancellationToken);

        // `TS-P2-67`. The script's own `@arg` / `@flag` tags describe its inputs, exactly as a
        // subcommand block's do. Without this a subcommand-free script showed its summary as the
        // description of every argument, so three documented arguments read the same sentence
        // three times — and the tags a reader had written were parsed and thrown away.
        await WriteScriptUsageSectionAsync(
            output,
            "Arguments",
            ApplyDocumentedDescriptions(argumentParameters, documented),
            isFlag: false,
            cancellationToken: cancellationToken);
        await WriteScriptUsageSectionAsync(
            output,
            "Options",
            ApplyDocumentedDescriptions(flagParameters, documented),
            isFlag: true,
            cancellationToken: cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task WriteScriptUsageSectionAsync(
        IToastStream output,
        string heading,
        IReadOnlyList<FunctionParameterSyntax> parameters,
        bool isFlag,
        CancellationToken cancellationToken)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        await output.WriteTextLineAsync(string.Empty, cancellationToken);
        await output.WriteTextLineAsync($"{heading}:", cancellationToken);

        var labels = parameters
            .Select(parameter => isFlag ? $"--{parameter.Name}" : parameter.Name)
            .ToArray();
        var width = labels.Max(static label => label.Length);

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var line = new StringBuilder("  ").Append(labels[index].PadRight(width));

            if (parameter.TypeName is { Length: > 0 } typeName)
            {
                line.Append("  ").Append(typeName);
            }

            // The description is the doc-comment written above the declaration, which is the
            // whole point of the exercise: it is why the comment was written.
            if (parameter.Description is { Length: > 0 } description)
            {
                line.Append("  ").Append(description.Trim());
            }

            await output.WriteTextLineAsync(line.ToString(), cancellationToken);
        }
    }

    private (IReadOnlyDictionary<string, ScriptArgumentValue> Flags, IReadOnlyList<ScriptArgumentValue> Arguments) ParseScriptArgumentValues(
        string sourceName,
        string sourceText,
        IReadOnlyList<FunctionParameterSyntax> flagParameters,
        IReadOnlyList<object?> arguments)
    {
        var optionLookup = BuildScriptFlagOptionLookup(sourceName, sourceText, flagParameters);
        var flagValues = new Dictionary<string, ScriptArgumentValue>(StringComparer.OrdinalIgnoreCase);
        var argumentValues = new List<ScriptArgumentValue>();
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                argumentValues.Add(new ScriptArgumentValue(argument, index));
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (!text.StartsWith("--", StringComparison.Ordinal) || text.Length <= 2)
            {
                argumentValues.Add(new ScriptArgumentValue(argument, index));
                continue;
            }

            var optionText = text[2..];
            string optionName;
            string? inlineValue = null;
            var equalsIndex = optionText.IndexOf('=');

            if (equalsIndex >= 0)
            {
                optionName = optionText[..equalsIndex];
                inlineValue = optionText[(equalsIndex + 1)..];
            }
            else
            {
                optionName = optionText;
            }

            if (!optionLookup.TryGetValue(optionName, out var parameter))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_script_flag",
                    Title: $"Unknown script flag '--{optionName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: null,
                    Label: "this script does not declare a matching flag",
                    Help: BuildUnknownScriptFlagHelp(flagParameters)));
            }

            object? value;

            if (IsBooleanScriptInput(parameter))
            {
                value = inlineValue is null ? true : inlineValue;
            }
            else if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else
            {
                if (index + 1 >= arguments.Count)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.script_option_requires_value",
                        Title: $"Option '--{optionName}' requires a value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: parameter.Span,
                        Label: $"'{parameter.Name}' expects a value"));
                }

                value = arguments[++index];
            }

            flagValues[parameter.Name] = new ScriptArgumentValue(value, index);
        }

        return (flagValues, argumentValues);
    }

    private Dictionary<string, FunctionParameterSyntax> BuildScriptFlagOptionLookup(
        string sourceName,
        string sourceText,
        IReadOnlyList<FunctionParameterSyntax> flags)
    {
        var options = new Dictionary<string, FunctionParameterSyntax>(StringComparer.OrdinalIgnoreCase);

        foreach (var flag in flags)
        {
            if (flag.IsRest)
            {
                continue;
            }

            AddScriptFlagOption(sourceName, sourceText, options, flag, flag.Name);

            var optionName = GetPrimaryScriptOptionName(flag.Name);
            if (!string.Equals(optionName, flag.Name, StringComparison.OrdinalIgnoreCase))
            {
                AddScriptFlagOption(sourceName, sourceText, options, flag, optionName);
            }
        }

        return options;
    }

    private static string? BuildUnknownScriptFlagHelp(IReadOnlyList<FunctionParameterSyntax> flagParameters)
    {
        var options = flagParameters
            .Where(static flag => !flag.IsRest && !string.IsNullOrWhiteSpace(flag.Name))
            .Select(static flag => $"--{GetPrimaryScriptOptionName(flag.Name)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static option => option, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return options.Length == 0
            ? "this script does not declare any flags."
            : $"available flags: {string.Join(", ", options)}";
    }

    private static void AddScriptFlagOption(
        string sourceName,
        string sourceText,
        Dictionary<string, FunctionParameterSyntax> options,
        FunctionParameterSyntax flag,
        string optionName)
    {
        if (options.TryGetValue(optionName, out var existing) &&
            !string.Equals(existing.Name, flag.Name, StringComparison.Ordinal))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_script_flag",
                Title: $"Script flag '--{optionName}' is inferred for more than one flag.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: flag.Span,
                Label: "rename one of these flags before using inferred long options"));
        }

        options[optionName] = flag;
    }

    private object? ConvertScriptInputValue(
        string sourceName,
        string sourceText,
        FunctionParameterSyntax parameter,
        object? value,
        string inputKind)
    {
        if (parameter.TypeName is null)
        {
            return value;
        }

        return ConvertAnnotatedValue(
            parameter.TypeName,
            CreateRefinementAnnotation(sourceName, sourceText, parameter.Refinement),
            value,
            parameter.Span,
            sourceName,
            sourceText,
            $"script {inputKind} '{parameter.Name}'");
    }

    private bool IsBooleanScriptInput(FunctionParameterSyntax parameter)
    {
        if (parameter.TypeName is null)
        {
            return false;
        }

        var effectiveTypeName = GetEffectiveAnnotatedTypeName(parameter.TypeName);
        var typeName = effectiveTypeName.EndsWith("?", StringComparison.Ordinal)
            ? effectiveTypeName[..^1]
            : effectiveTypeName;

        if (typeName is "bool" or "Boolean" or "System.Boolean")
        {
            return true;
        }

        return ResolveTypeName(typeName) == typeof(bool);
    }

    private static string GetPrimaryScriptOptionName(string parameterName) => parameterName;


    private async IAsyncEnumerable<object?> ExecuteCommandSyntaxAsync(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax,
        IAsyncEnumerable<object?> input,
        IReadOnlyList<object?>? additionalArguments,
        bool isPipelined,
        PipelineExitStatusTracker? pipelineExitStatusTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyList<object?>? prependedArguments = null,
        bool outputIsCaptured = false,
        bool hasUpstream = false,
        RawByteHandoff? rawInput = null,
        RawByteHandoff? rawOutput = null)
    {
        var command = ResolveCommand(sourceName, sourceText, commandSyntax);

        // Hard-error when a [ShellOnly] command runs outside an interactive
        // session. These commands depend on REPL state (history, prompt,
        // directory stack, TUI) and have no meaning in scripts / -c / pipelines.
        // The diagnostic surfaces in script mode; the REPL never trips it.
        EnforceShellOnlyOutsideInteractive(command, sourceName, sourceText, commandSyntax);

        // Rune (macro) expansion: intercept before argument evaluation
        if (command is RuneCommand runeCommand)
        {
            await foreach (var item in ExpandRuneAsync(
                runeCommand.Definition,
                commandSyntax.Arguments,
                sourceName,
                sourceText,
                input,
                cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        IReadOnlyList<object?> arguments;

        try
        {
            var evaluatedArguments = await EvaluateCommandArgumentsAsync(sourceName, sourceText, command, commandSyntax, cancellationToken);
            arguments = ExpandCommandArguments(command, evaluatedArguments, sourceName, sourceText);

            if (prependedArguments is { Count: > 0 })
            {
                arguments = prependedArguments.Concat(arguments).ToArray();
            }

            if (additionalArguments is { Count: > 0 })
            {
                arguments = arguments.Concat(additionalArguments).ToArray();
            }
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
        }

        var invocation = new CommandInvocation(
            sourceName,
            sourceText,
            commandSyntax.Name,
            commandSyntax.Span,
            commandSyntax.Arguments.Select(argument => argument.Span).ToArray(),
            commandSyntax.ExplicitTypeArguments,
            _targetTypeAnnotation.Value);
        var context = new CommandContext(
            LanguageRuntime,
            input,
            arguments,
            cancellationToken,
            invocation,
            isPipelined,
            CreateScopedTypeResolver(),
            pipelineExitStatusTracker,
            BlockExecutor: _ownBlockExecutor,
            OutputIsCaptured: outputIsCaptured,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this)
        {
            HasUpstream = hasUpstream,
            RawInput = rawInput,
            RawOutput = rawOutput,
        };

        // `TOSH-0012`. Bound to this context rather than to the command: a registered command is
        // one object shared by every stage that names it, and a context a command derives with
        // `with` — for a renderer, a callback — must not inherit the right to claim or offer.
        rawInput?.BindConsumer(context);
        rawOutput?.BindProducer(context);

        if (LanguageRuntime.Options.Trace)
        {
            var traceArgs = string.Join(" ", arguments.Select(FormatTraceArgument));
            var traceLine = string.IsNullOrEmpty(traceArgs)
                ? $"+ {commandSyntax.Name}"
                : $"+ {commandSyntax.Name} {traceArgs}";
            await Diagnostics.TraceAsync(traceLine, cancellationToken);
        }

        var exitCodeCountBefore = pipelineExitStatusTracker?.ExitCodeCount ?? 0;

        await using var enumerator = command.ExecuteAsync(context).GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            object? item;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                item = enumerator.Current;
            }
            catch (ToshDiagnosticException)
            {
                throw;
            }
            catch (ReturnSignalException)
            {
                throw;
            }
            catch (BreakSignalException)
            {
                throw;
            }
            catch (ContinueSignalException)
            {
                throw;
            }
            catch (ThrowSignalException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an execution outcome, not a command
                // failure diagnostic. In particular, a surrounding defer
                // scope must be able to retain it as the primary exit while
                // cleanup runs with its shielded token.
                throw;
            }
            catch (Exception exception) when (IsToshThrown(exception))
            {
                // A user `throw (new MyError(...))` directly raised a CLR
                // exception (not the wrapper signal); let it propagate so
                // a higher-level catch / diagnostic stage handles it,
                // rather than rewrapping it as a runtime command failure.
                throw;
            }
            catch (Exception exception)
            {
                throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
            }

            yield return item;
        }

        // If the command didn't record its own exit code (shell commands),
        // record 0 so the pipeline tracker has complete stage information.
        if (pipelineExitStatusTracker is not null && pipelineExitStatusTracker.ExitCodeCount == exitCodeCountBefore)
        {
            pipelineExitStatusTracker.Record(0);
        }
    }

    private IShellCommand ResolveCommand(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Commands.TryGetValue(commandSyntax.Name, out var scopedCommand))
            {
                return scopedCommand;
            }
        }

        if (LanguageRuntime.Commands.TryGet(commandSyntax.Name, out var command))
        {
            return command;
        }

        if (commandSyntax.Name.Contains('.') &&
            TryResolveModuleQualifiedCommand(commandSyntax.Name, out var moduleCommand))
        {
            return moduleCommand;
        }

        // `TS-P2-60`. A command head is a word like any other, so a leading tilde expands here
        // too. Without this, a bare `~` reached external resolution as two characters and came
        // back "Command '~' was not found", while the equivalent `/home/ada` reported that it
        // is a directory — the same input, described two different ways depending on how it was
        // spelled. Nothing is registered under a name starting with `~`, so this runs after the
        // builtin lookups and cannot shadow one.
        var externalName = ExpandCommandNameTilde(commandSyntax.Name);
        var external = ExternalCommandResolver.Resolve(LanguageRuntime.CurrentDirectory, externalName);

        if (external.Status is not ExternalCommandLookupStatus.Found &&
            TryBuildVariableReferenceHint(commandSyntax.Name, out var suggestedReference, out var variableName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.variable_reference_requires_dollar",
                Title: $"Variable '{variableName}' exists, but variable references must start with '$'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: commandSyntax.Span,
                Label: $"did you mean '{suggestedReference}'?",
                Help: "declare variables with 'var name', then use '$name' everywhere else in ToSh."));
        }

        // Auto-source Tosh scripts instead of trying to exec them as native processes.
        if (external.Status is ExternalCommandLookupStatus.Found or ExternalCommandLookupStatus.NotExecutable &&
            external.ResolvedPath is not null &&
            ScriptFileDetection.IsToshScript(external.ResolvedPath))
        {
            return new ToshScriptCommand(commandSyntax.Name, external.ResolvedPath, this);
        }

        return external.Status switch
        {
            // The shell supplies the factory; the language only decides that the name
            // is not anything it owns and so must be a program (`TOAST-0004`).
            ExternalCommandLookupStatus.Found when external.ResolvedPath is not null =>
                LanguageRuntime.ExternalCommands?.CreateExternalProcess(commandSyntax.Name, external.ResolvedPath)
                    ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.external_commands_unavailable",
                        Title: $"'{commandSyntax.Name}' is a program on disk, and this host does not run external programs.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: commandSyntax.Span,
                        Label: $"resolved to '{external.ResolvedPath}', but nothing is registered to launch it",
                        Help: "reference Tosh.Stdlib, which registers a launcher automatically, or set ToastRuntime.ExternalCommands to your own IExternalCommandFactory.")),
            ExternalCommandLookupStatus.NotExecutable =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.external_command_not_executable",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is not executable.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' cannot be launched as a program",
                    Help: external.IsExplicitPath
                        ? $"make it executable, for example with `chmod +x {commandSyntax.Name}`, or run it with an interpreter."
                        : "check the file permissions or invoke it through an interpreter.")),
            ExternalCommandLookupStatus.IsDirectory when LanguageRuntime.Options.AutoCd =>
                CreateAutoCdCommand(
                    external.ResolvedPath ?? commandSyntax.Name,
                    sourceName,
                    sourceText,
                    commandSyntax.Span),
            ExternalCommandLookupStatus.IsDirectory =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.external_command_is_directory",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is a directory, not an executable file.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' does not refer to a runnable program")),
            _ when LanguageRuntime.Options.AutoCd && TryResolveAutoCdDirectory(commandSyntax.Name, out var autoCdPath) =>
                CreateAutoCdCommand(autoCdPath, sourceName, sourceText, commandSyntax.Span),
            // `TS-P2-41`. A word that names a member of the running class is not an unknown
            // command, and saying so — then suggesting `bg` — was the whole complaint. Placed
            // after the external lookup above, so a real program of the same name still wins.
            _ when TryDescribeEnclosingMember(commandSyntax.Name) is { } memberForm =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_command",
                    Title: EnclosingMemberSuggestion.Title(commandSyntax.Name, CurrentClass!.Name),
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: EnclosingMemberSuggestion.Label(memberForm),
                    Help: EnclosingMemberSuggestion.Help(CurrentClass!.Name))),
            _ =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_command",
                    Title: $"Command '{commandSyntax.Name}' was not found.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' is not a built-in, function, executable, or $-prefixed variable reference",
                    Help: ResolveUnknownCommandHelp(commandSyntax.Name))),
        };
    }

    /// <summary>
    /// Expands a leading tilde in a command head, leaving an unresolvable <c>~name</c> alone.
    /// </summary>
    /// <remarks>
    /// An argument refuses an unknown <c>~name</c>; a command head does not, because the
    /// resolution that follows has its own account of a name it cannot find, and two diagnostics
    /// competing to explain one word is worse than either.
    /// </remarks>
    private static string ExpandCommandNameTilde(string name)
    {
        var expansion = PathUtilities.ExpandTilde(name);
        return expansion.Kind == PathUtilities.TildeExpansionKind.Expanded ? expansion.Path : name;
    }

    /// <summary>
    /// The qualified spelling of <paramref name="name"/> when it names a member of the class
    /// whose code is running — <c>TS-P2-41</c>.
    /// </summary>
    /// <remarks>
    /// The engine's half of the answer. The binder reaches this case first for most names, but
    /// gives up silently when nothing in the registry resembles the word, and a member name
    /// usually resembles nothing — which is how <c>zog()</c> beside <c>func zog()</c> came to be
    /// answered "did you mean 'bg'?" by the runtime instead.
    /// </remarks>
    private string? TryDescribeEnclosingMember(string name)
    {
        if (CurrentClass is not { } cls) return null;

        foreach (var method in cls.Methods)
        {
            if (method.Name == name)
            {
                return EnclosingMemberSuggestion.Qualify(cls.Name, name, method.IsStatic, isMethod: true);
            }
        }

        foreach (var property in cls.Properties)
        {
            if (property.Name == name)
            {
                return EnclosingMemberSuggestion.Qualify(cls.Name, name, property.IsStatic, isMethod: false);
            }
        }

        return null;
    }

    private string ResolveUnknownCommandHelp(string name)
    {
        // Suggest well-known corrections for common mistakes from other shells.
        var suggestion = name switch
        {
            "alias" or "unalias" => "ToSh uses functions instead of aliases. Use 'func name => command' for a one-liner alias.",
            "set" => "use 'var name = value' for variables, or 'export NAME = \"value\"' for environment variables.",
            "local" or "declare" or "typeset" => "use '$name = value' — variables are local by default in ToSh.",
            "readonly" => "use 'const $name = value' for constants.",
            "test" or "[" => "use 'if condition { ... }' with expression syntax instead of test/[.",
            "source" or "." => "use 'source path' to load a script file.",
            _ => null
        };

        if (suggestion is not null)
        {
            return suggestion;
        }

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return "check that the path exists and points to an executable file.";
        }

        // Levenshtein nearest-match against builtins — asked only about words that could be
        // misspelled names. This is the second suggestion machine in the shell; the binder has
        // the other, and the guard had to be added to both or `~` would keep coming back as a
        // possible `bg` from whichever one answered (`TS-P1-24`).
        var bestMatch = (Name: (string?)null, Distance: int.MaxValue);

        if (!ShellCommandRegistry.IsNameShaped(name))
        {
            return $"use 'which {name}' to inspect how Tosh resolves this command.";
        }

        foreach (var command in LanguageRuntime.Commands.All)
        {
            var distance = LevenshteinDistance(name, command.Name);
            if (distance < bestMatch.Distance)
            {
                bestMatch = (command.Name, distance);
            }
        }

        if (bestMatch.Name is not null && bestMatch.Distance <= Math.Max(2, Math.Max(name.Length, bestMatch.Name.Length) * 2 / 5))
        {
            return $"did you mean '{bestMatch.Name}'?";
        }

        return $"use 'which {name}' to inspect how Tosh resolves this command.";
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var costs = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) costs[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            var previousDiag = costs[0];
            costs[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var temp = costs[j];
                costs[j] = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1])
                    ? previousDiag
                    : Math.Min(Math.Min(costs[j - 1], costs[j]), previousDiag) + 1;
                previousDiag = temp;
            }
        }

        return costs[b.Length];
    }

    /// <summary>
    /// A number the operator evaluator handles without reaching for a class overload,
    /// a quantity, a vector or a string.
    /// </summary>
    private static bool IsPrimitiveNumber(object? value)
        => value is int or long or double or float or decimal
            or short or ushort or byte or sbyte or uint or ulong;

    private string FormatCommandSubstitutionValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            ShellTextLine textLine => textLine.Text,
            string text => text,
            _ => ToastRenderer.Render(value),
        };
    }

    private async Task<bool> EvaluateGuardWithCurrentItemAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax guard,
        object? currentItem,
        CancellationToken cancellationToken)
    {
        using var scope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_"] = currentItem,
        });

        var guardValue = await EvaluateArgumentAsync(sourceName, sourceText, guard, cancellationToken);
        return OperatorEvaluator.ToBoolean(guardValue);
    }

    private static int ConvertToInt(object? value, string label)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d when d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
            _ => throw new InvalidOperationException($"The {label} of a range must be an integer, got '{value}'.")
        };
    }


    private void DeclareCommand(IShellCommand command, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(command.Name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            var registered = RegisterCommand(moduleScope.Commands, command);
            moduleScope.Exports!.Commands[command.Name] = registered;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            var registered = RegisterCommand(exportScope.Commands, command);
            exportScope.Exports!.Commands[command.Name] = registered;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy aliases and functions require a function, block, or module scope.");
            }

            RegisterCommand(_scopes.Peek().Commands, command);
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            WarnIfShadowingBuiltin(command.Name);
            RegisterCommand(LanguageRuntime.Commands, command);
            return;
        }

        if (_scopes.Count > 0)
        {
            RegisterCommand(_scopes.Peek().Commands, command);
            return;
        }

        WarnIfShadowingBuiltin(command.Name);
        RegisterCommand(LanguageRuntime.Commands, command);
    }

    private IShellCommand RegisterCommand(Dictionary<string, IShellCommand> commands, IShellCommand command)
    {
        if (TryMergeFunctionOverload(commands.TryGetValue(command.Name, out var existing) ? existing : null, command, out var merged))
        {
            commands[command.Name] = merged;
            return merged;
        }

        commands[command.Name] = command;
        return command;
    }

    private IShellCommand RegisterCommand(ICommandTable commands, IShellCommand command)
    {
        if (commands.TryGet(command.Name, out var existing) &&
            TryMergeFunctionOverload(existing, command, out var merged))
        {
            commands.RegisterOrReplace(merged);
            return merged;
        }

        commands.RegisterOrReplace(command);
        return command;
    }

    private bool TryMergeFunctionOverload(IShellCommand? existing, IShellCommand incoming, out IShellCommand merged)
    {
        merged = incoming;

        if (incoming is not FunctionCommand incomingFunction)
        {
            return false;
        }

        switch (existing)
        {
            case FunctionCommand existingFunction:
                merged = new OverloadedFunctionCommand(this, [existingFunction.Definition, incomingFunction.Definition]);
                return true;
            case OverloadedFunctionCommand overloadGroup:
                overloadGroup.AddOrReplace(incomingFunction.Definition);
                merged = overloadGroup;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Emits <c>tosh.shell_only</c> when a command marked
    /// <see cref="ShellOnlyAttribute"/> is invoked outside an interactive
    /// REPL session. Throws a <see cref="ToshDiagnosticException"/> with code
    /// <c>tosh.shell_only</c> in script / -c / pipeline mode; no-op in the REPL.
    /// Errors are not hushable — these commands depend on REPL state (history,
    /// directory stack, prompt rendering, TUI) and cannot meaningfully run in
    /// non-interactive contexts.
    /// </summary>
    private void EnforceShellOnlyOutsideInteractive(IShellCommand command, string sourceName, string sourceText, CommandSyntax commandSyntax)
    {
        if (IsInteractiveSession)
        {
            return;
        }

        var attribute = command.GetType().GetCustomAttribute<ShellOnlyAttribute>();
        if (attribute is null)
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(attribute.Reason)
            ? "It depends on interactive-shell state (history, prompt, directory stack, TUI)."
            : attribute.Reason;

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.shell_only",
            Title: $"Command '{command.Name}' is shell-only and cannot be used outside an interactive session.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: commandSyntax.Span,
            Label: $"'{command.Name}' is REPL-only",
            Help: reason));
    }


    public string ResolveSourcePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || Path.IsPathRooted(rawPath))
        {
            return rawPath;
        }

        var scriptDirectory = GetExecutionDirectory(GetCurrentScriptPath());

        if (string.Equals(scriptDirectory, LanguageRuntime.CurrentDirectory, StringComparison.Ordinal))
        {
            return rawPath;
        }

        var candidate = PathUtilities.ResolvePath(scriptDirectory, rawPath);

        return File.Exists(candidate) ? candidate : rawPath;
    }

    private string GetExecutionDirectory(string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName) &&
            !sourceName.StartsWith('<') &&
            !sourceName.StartsWith("repl_entry", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSource = PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, sourceName);
            var directory = Path.GetDirectoryName(resolvedSource);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return LanguageRuntime.CurrentDirectory;
    }

    /// <summary>
    /// The build configuration this host was compiled in, used when `require` has to
    /// build a project.
    /// </summary>
    /// <remarks>
    /// Previously no configuration was passed at all, so MSBuild applied its default and
    /// `require` always resolved and built **Debug** — including from a published
    /// Release shell, which would silently build and load Debug output. Matching the
    /// host is the least surprising rule: the assembly you are about to load into this
    /// process is built the same way this process was.
    ///
    /// It also removes a cost that looked like flakiness. A Release test run used to
    /// trigger a from-scratch Debug build of the whole dependency chain, because the
    /// Debug output it asked for did not exist; the Release output already did
    /// (`PLAN-0002`).
    /// </remarks>
    private static readonly string HostBuildConfiguration =
        typeof(ToshEngine).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            is { Length: > 0 } configuration
            ? configuration
            : "Debug";

    private static async Task<string> BuildProjectAndResolveAssemblyPathAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Project '{projectPath}' was not found.", projectPath);
        }

        // `-p:Configuration=` rather than `-c`: the latter is a `dotnet build` shorthand
        // and `dotnet msbuild` rejects it outright ("Switch: -c"), while both accept the
        // property form.
        var configuration = $"-p:Configuration={QuoteArgument(HostBuildConfiguration)}";

        var targetPath = await RunDotNetForOutputAsync(
            $"msbuild {QuoteArgument(projectPath)} -nologo {configuration} -getProperty:TargetPath",
            Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
            cancellationToken);

        if (!File.Exists(targetPath))
        {
            await RunDotNetAsync(
                $"build {QuoteArgument(projectPath)} -nologo {configuration} -clp:ErrorsOnly",
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
                cancellationToken);

            targetPath = await RunDotNetForOutputAsync(
                $"msbuild {QuoteArgument(projectPath)} -nologo {configuration} -getProperty:TargetPath",
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
                cancellationToken);
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Built project '{projectPath}' did not produce a loadable assembly.", targetPath);
        }

        return targetPath;
    }

    private static async Task RunDotNetAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // `PLAN-0003`. MSBuild keeps its worker nodes alive after a build so the
                // next one starts faster, and nothing reclaims them: `dotnet build-server
                // shutdown` reports success and leaves them running. One build of a
                // moderate project leaves ~`nproc` of them behind, each a couple of
                // hundred megabytes, for fifteen minutes.
                //
                // `scripts/build.tosh` already makes this trade and states the reason:
                // TōSh is somebody's logon shell, and leaking gigabytes into their session
                // to save a second is not a bargain. That argument is *stronger* here,
                // because this is the shipped path — `require <project.csproj>` in a
                // user's script — rather than a build script they ran deliberately.
                //
                // The environment variable rather than `-nr:false`: measured on a full
                // solution build, the switch alone leaves four nodes behind and the
                // variable leaves none, because the variable reaches the nested `dotnet`
                // processes this one spawns and the switch only reaches the invocation it
                // is written on.
                Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" },
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException((await standardError).Trim().Length > 0
            ? (await standardError).Trim()
            : (await standardOutput).Trim());
    }

    private static async Task<string> RunDotNetForOutputAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // `PLAN-0003`. MSBuild keeps its worker nodes alive after a build so the
                // next one starts faster, and nothing reclaims them: `dotnet build-server
                // shutdown` reports success and leaves them running. One build of a
                // moderate project leaves ~`nproc` of them behind, each a couple of
                // hundred megabytes, for fifteen minutes.
                //
                // `scripts/build.tosh` already makes this trade and states the reason:
                // TōSh is somebody's logon shell, and leaking gigabytes into their session
                // to save a second is not a bargain. That argument is *stronger* here,
                // because this is the shipped path — `require <project.csproj>` in a
                // user's script — rather than a build script they ran deliberately.
                //
                // The environment variable rather than `-nr:false`: measured on a full
                // solution build, the switch alone leaves four nodes behind and the
                // variable leaves none, because the variable reaches the nested `dotnet`
                // processes this one spawns and the switch only reaches the invocation it
                // is written on.
                Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" },
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await standardOutput).Trim();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        var error = (await standardError).Trim();
        throw new InvalidOperationException(error.Length > 0 ? error : output);
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string ExtractSourceText(string sourceText, int start, int end)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return string.Empty;
        }

        var boundedStart = Math.Clamp(start, 0, sourceText.Length);
        var boundedEnd = Math.Clamp(end, boundedStart, sourceText.Length);
        return sourceText[boundedStart..boundedEnd];
    }

    private bool TryResolveAutoCdDirectory(string name, out string resolvedPath)
    {
        if (name.StartsWith('~'))
        {
            var expanded = PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, name);

            if (Directory.Exists(expanded))
            {
                resolvedPath = expanded;
                return true;
            }
        }

        var candidate = Path.Combine(LanguageRuntime.CurrentDirectory, name);

        if (Directory.Exists(candidate))
        {
            resolvedPath = Path.GetFullPath(candidate);
            return true;
        }

        resolvedPath = string.Empty;
        return false;
    }

    private IShellCommand CreateAutoCdCommand(
        string resolvedPath,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        return LanguageRuntime.AutoCdCommandFactory?.CreateAutoCdCommand(resolvedPath)
            ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.auto_cd_not_supported",
                Title: "This host does not support AutoCd navigation.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "AutoCd resolved this name to a directory, but the host cannot navigate there"));
    }

    internal sealed class EngineBlockExecutor : IShellBlockExecutor
    {
        private readonly ToshEngine _engine;

        internal ToshEngine Engine => _engine;

        public EngineBlockExecutor(ToshEngine engine)
        {
            _engine = engine;
        }

        public async IAsyncEnumerable<object?> ExecuteAsync(
            ShellBlock block,
            IReadOnlyDictionary<string, object?> locals,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (block.Syntax is not BlockSyntax syntax)
            {
                throw new InvalidOperationException("This runtime cannot execute the provided block.");
            }

            await foreach (var value in _engine.ExecuteBlockAsync(block.SourceName, block.SourceText, syntax, cancellationToken, locals)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }

        public IAsyncEnumerable<object?> InvokeCallableAsync(IShellCallable callable, CommandContext context)
            // Engine-bound callables (ToshLambda, FunctionCommand, OverloadedFunctionCommand)
            // redirect themselves to the fork engine when they see context.BlockExecutor is
            // a different EngineBlockExecutor — so the default interface implementation
            // (callable.InvokeAsync) is sufficient here.
            => callable.InvokeAsync(context);

        public IShellBlockExecutor Fork()
        {
            var snapshot = _engine.CaptureVisibleScopes();
            return new EngineBlockExecutor(_engine.Fork(snapshot));
        }
    }

    private sealed class ScopeFrame : IDisposable
    {
        private readonly Stack<LexicalScope> _scopes;
        private readonly ShellEventBus? _eventBus;

        public ScopeFrame(Stack<LexicalScope> scopes, ShellEventBus? eventBus = null)
        {
            _scopes = scopes;
            _eventBus = eventBus;
        }

        public void Dispose()
        {
            var scope = _scopes.Pop();

            if (_eventBus is not null && scope.LocalEventNames.Count > 0)
            {
                foreach (var eventName in scope.LocalEventNames)
                {
                    _eventBus.RemoveAll(eventName);
                }
            }
        }
    }

    private sealed class ScopeFrames : IDisposable
    {
        public static readonly ScopeFrames Empty = new(Array.Empty<IDisposable>());

        private readonly IReadOnlyList<IDisposable> _frames;

        public ScopeFrames(IReadOnlyList<IDisposable> frames)
        {
            _frames = frames;
        }

        public void Dispose()
        {
            for (var index = _frames.Count - 1; index >= 0; index--)
            {
                _frames[index].Dispose();
            }
        }
    }

    private enum RequireTargetKind
    {
        Script,
        Assembly,
        Project,
    }


}
