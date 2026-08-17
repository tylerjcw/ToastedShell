using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Diagnostics: constructing them, deciding whether one is hushed, and rendering a
/// thrown value into something a reader can act on.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// Two members whose names put them here were left behind on reading:
/// `RedirectionIncludesError` asks whether a redirection covers the error stream, which
/// is stream plumbing, and `CreateCaughtErrorValue` builds the value `catch` binds,
/// which is exception semantics rather than diagnostic emission. Both would have been
/// swept in by a grep for "Error".
/// </summary>
public sealed partial class ToshEngine
{

    private void RegisterLineHushDirectives(string sourceName, IReadOnlyList<LineHushDirective>? directives)
    {
        if (directives is null || directives.Count == 0)
        {
            return;
        }

        if (!_lineHushBySource.TryGetValue(sourceName, out var byLine))
        {
            byLine = new Dictionary<int, HashSet<string>>();
            _lineHushBySource[sourceName] = byLine;
        }

        foreach (var directive in directives)
        {
            if (!byLine.TryGetValue(directive.Line, out var codes))
            {
                codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byLine[directive.Line] = codes;
            }
            codes.Add(directive.Code);
        }
    }

    private bool IsLineHushed(string code, string? sourceName, int line)
    {
        if (sourceName is null || line <= 0)
        {
            return false;
        }
        if (!_lineHushBySource.TryGetValue(sourceName, out var byLine))
        {
            return false;
        }
        // Honor a directive on the line itself (trailing comment) or the line
        // immediately above (leading comment on the previous line).
        return (byLine.TryGetValue(line, out var hereCodes) && hereCodes.Contains(code)) ||
               (line > 1 && byLine.TryGetValue(line - 1, out var aboveCodes) && aboveCodes.Contains(code));
    }

    private static string FormatScriptArgumentForDiagnostic(object? value)
    {
        return value switch
        {
            null => "null",
            string text => text,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static ToshDiagnosticException CreateExpressionDiagnostic(
        string sourceName,
        string sourceText,
        ArgumentSyntax argument,
        Exception exception)
        => CreateExpressionDiagnostic(
            sourceName,
            sourceText,
            argument.Span,
            exception);

    private static ToshDiagnosticException CreateExpressionDiagnostic(
        string sourceName,
        string sourceText,
        TextSpan span,
        Exception exception)
    {
        if (TryCreateNativeErrorDiagnostic(sourceName, sourceText, span, exception) is { } nativeDiagnostic)
        {
            return nativeDiagnostic;
        }

        // `TS-P2-95`. The original is kept as the inner exception so a script can
        // do more than read the sentence: match on the type, or reach the fields
        // that carry the detail — the file a missing-file error names, the probing
        // paths a missing native library lists.
        return ToshDiagnosticException.Create(
            new ToshDiagnostic(
                Code: exception is InvalidOperationException
                    ? "tosh.runtime.expression_failed"
                    : "tosh.runtime.unexpected_exception",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "while evaluating this expression"),
            exception);
    }

    private void WarnIfShadowingBuiltin(string commandName)
    {
        if (Runtime.Commands.TryGet(commandName, out var existing) &&
            existing is not ICommandResolutionMetadata)
        {
            WriteWarning(
                code: "tosh.naming.shadowed_builtin",
                title: $"Function '{commandName}' shadows built-in command '{commandName}'.",
                help: "Rename the function, or hush this code: hush tosh.naming.shadowed_builtin",
                category: ToshDiagnosticCategory.Naming);
        }
    }

    internal void WriteWarning(string title, string? help = null, string? info = null)
    {
        WriteWarning(code: null, title, help, info, ToshDiagnosticCategory.Runtime);
    }

    /// <summary>
    /// Emits a warning carrying a diagnostic <paramref name="code"/>. If the code
    /// appears in any enclosing lexical scope's <c>HushedCodes</c> set, in the
    /// global <c>$tosh.Config.Diagnostics.Hushed</c> list, or in an inline
    /// <c># hush &lt;code&gt;</c> directive on (or just above) the emit line,
    /// the warning is dropped.
    /// </summary>
    internal void WriteWarning(
        string? code,
        string title,
        string? help = null,
        string? info = null,
        ToshDiagnosticCategory category = ToshDiagnosticCategory.Runtime,
        string? sourceName = null,
        int line = 0)
    {
        if (code is not null)
        {
            if (IsCodeHushed(code, ToshDiagnosticSeverity.Warning))
            {
                return;
            }
            if (IsLineHushed(code, sourceName, line))
            {
                return;
            }
        }

        Diagnostics.ReportWarning(title, help, info);
        _ = category; // reserved for future renderer use
    }

    /// <summary>
    /// Returns <c>true</c> when a diagnostic with the given <paramref name="code"/>
    /// and <paramref name="severity"/> should be suppressed at the current scope.
    /// Errors are never suppressible. Walks the lexical scope stack from innermost
    /// out, then falls back to the global <c>$tosh.Config.Diagnostics.Hushed</c> list.
    /// </summary>
    internal bool IsCodeHushed(string code, ToshDiagnosticSeverity severity)
    {
        if (severity == ToshDiagnosticSeverity.Error)
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrEmpty(code);

        foreach (var scope in _scopes)
        {
            if (scope.HushedCodes.Contains(code))
            {
                return true;
            }
        }

        return Runtime.Options.IsHushed(code, severity);
    }

    /// <summary>
    /// Adds <paramref name="code"/> to the innermost lexical scope's hush set.
    /// If there is no active scope (e.g. top-level startup), promotes to the
    /// global <c>$tosh.Config.Diagnostics.Hushed</c> list so the suppression
    /// outlives the current call.
    /// </summary>
    internal void AddHushedCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmed = code.Trim();

        if (_scopes.Count > 0)
        {
            _scopes.Peek().HushedCodes.Add(trimmed);
            return;
        }

        Runtime.Options.HushedDiagnostics.Add(trimmed);
    }

    /// <summary>Public <see cref="IShellEvaluator"/> entry point for the <c>hush</c> builtin.</summary>
    public void HushDiagnosticCode(string code) => AddHushedCode(code);

    private static ToshDiagnosticException CreateSelfUnavailableInDefaultDiagnostic(
        FunctionParameterDefinition parameter,
        string sourceName,
        string sourceText)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.self_unavailable_in_constructor_default",
            Title: $"The default for parameter '{parameter.Name}' cannot use '$this'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: parameter.Span,
            Label: "'$this' is not available while the instance is still being constructed",
            Help: "constructor defaults bind before this layer's properties are initialised; move the logic into the constructor body, or give the parameter a value that does not depend on the instance."));
    }

    private static ToshDiagnosticException CreateParameterDefaultConversionDiagnostic(
        FunctionParameterDefinition parameter,
        string sourceName,
        string sourceText)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.parameter_default_conversion_failed",
            Title: $"The default value for parameter '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: parameter.Span,
            Label: $"this default does not satisfy ': {parameter.TypeName}'",
            Help: "change the default expression or loosen the parameter's annotation."));
    }

    /// <summary>
    /// Builds the <c>tosh.runtime.refinement_failed</c> diagnostic, including the
    /// clause summary shown as help.  Extracted from
    /// <see cref="EnsureRefinementSatisfiedAsync"/> when the synchronous copy of
    /// that method was removed (<c>TS-P1-24</c>).
    /// </summary>
    private static ToshDiagnosticException CreateRefinementFailedDiagnostic(
        RefinementAnnotation refinement,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        var helpLines = new List<string>();
        foreach (var clause in refinement.Clauses)
        {
            switch (clause)
            {
                case RefinementWhereClause whereClause
                    when TryGetRefinementSnippet(
                        refinement.SourceText,
                        whereClause.Predicate.Span,
                        out var predicateText):
                    helpLines.Add($"where: {predicateText}");
                    break;
                case RefinementCoerceClause { Guard: { } guard, Coercer: var coercer }
                    when TryGetRefinementSnippet(
                             refinement.SourceText,
                             guard.Span,
                             out var guardText) &&
                         TryGetRefinementSnippet(
                             refinement.SourceText,
                             coercer.Span,
                             out var guardedCoerceText):
                    helpLines.Add($"if {guardText} coerce: {guardedCoerceText}");
                    break;
                case RefinementCoerceClause { Guard: null, Coercer: var coercer }
                    when TryGetRefinementSnippet(
                        refinement.SourceText,
                        coercer.Span,
                        out var coerceText):
                    helpLines.Add($"coerce: {coerceText}");
                    break;
            }
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.refinement_failed",
            Title: $"Value for '{owner}' does not satisfy its refinement.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: "this value failed its 'where' predicate",
            Help: helpLines.Count > 0 ? string.Join("\n", helpLines) : null));
    }

    private static ToshDiagnosticException CreateCommandDiagnostic(
        string sourceName,
        string sourceText,
        CommandSyntax commandSyntax,
        Exception exception)
    {
        // Narrow the diagnostic span to the offending argument when possible,
        // so the renderer underlines the bad flag/value rather than the whole
        // command line. Two strategies:
        //   1. The command threw CommandArgumentException with an explicit index.
        //   2. The exception message contains a single-quoted token (e.g.
        //      "Unsupported foo option '-x'.") that matches one of the
        //      command's argument source texts verbatim.
        var span = NarrowToArgumentSpan(sourceText, commandSyntax, exception) ?? commandSyntax.Span;

        if (TryCreateNativeErrorDiagnostic(sourceName, sourceText, span, exception) is { } nativeDiagnostic)
        {
            return nativeDiagnostic;
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: exception is InvalidOperationException or CommandArgumentException
                ? "tosh.runtime.command_failed"
                : "tosh.runtime.unexpected_exception",
            Title: exception.Message,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"while executing '{commandSyntax.Name}'"));
    }

    private static ToshDiagnosticException CreateLoopControlDiagnostic(
        string sourceName,
        string sourceText,
        TextSpan span,
        string keyword,
        string code,
        string title)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"'{keyword}' does not have an enclosing loop to control"));
    }

    private ValueTask<ToshDiagnosticException> CreateThrownValueDiagnosticAsync(
        string sourceName,
        string sourceText,
        ThrowSignalException signal,
        CancellationToken cancellationToken) =>
        CreateThrownValueDiagnosticAsync(
            sourceName,
            sourceText,
            signal.Span,
            signal.Value,
            cancellationToken);

    /// <summary>
    /// Pretty-format any tosh-thrown <see cref="Exception"/> (raised
    /// either as a wrapper <see cref="ThrowSignalException"/> or as a
    /// directly thrown <see cref="Exception"/> subclass) into a
    /// <see cref="ToshDiagnosticException"/> the renderer can box-draw
    /// with a source snippet and underline. Callers from non-throw
    /// contexts (e.g. unhandled <see cref="ToshError"/> escaping a
    /// pipeline) should use this overload directly.
    /// </summary>
    private ValueTask<ToshDiagnosticException> CreateThrownValueDiagnosticAsync(
        string sourceName,
        string sourceText,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return exception switch
        {
            ThrowSignalException signal => CreateThrownValueDiagnosticAsync(
                sourceName,
                sourceText,
                signal.Span,
                signal.Value,
                cancellationToken),
            // Before the generic ToshError branch: a NativeError already carries
            // a complete diagnostic contract (symbol, returned value, errno with
            // its symbolic name). The duck-typed probe below only reads
            // ToshClassInstance members, so a CLR exception subclass would
            // otherwise render as its type name with no help line.
            NativeError native => ValueTask.FromResult(
                BuildNativeErrorDiagnostic(sourceName, sourceText, native.Span, native)),
            ToshError tosh => CreateThrownValueDiagnosticAsync(
                sourceName,
                sourceText,
                tosh.Span,
                tosh,
                cancellationToken),
            _ => CreateThrownValueDiagnosticAsync(
                sourceName,
                sourceText,
                default,
                exception,
                cancellationToken),
        };
    }

    private async ValueTask<ToshDiagnosticException> CreateThrownValueDiagnosticAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // If the thrown value is itself a diagnostic (the common `throw $err`
        // re-raise pattern from a catch block), preserve its original source,
        // span, and snippet so the renderer still points at the underlying
        // problem. The throw-site location is surfaced as an `info:` footer
        // so the user still knows where the rethrow happened.
        if (value is ToshDiagnosticException inner && inner.Diagnostics.Count > 0)
        {
            var rethrown = inner.Diagnostics[0];
            var line = LineFromOffset(sourceText, span.Start);
            var throwSite = line > 0 ? $"{sourceName}:{line}" : sourceName;
            var info = string.IsNullOrWhiteSpace(rethrown.Info)
                ? $"re-thrown at {throwSite}"
                : $"{rethrown.Info}; re-thrown at {throwSite}";
            return ToshDiagnosticException.Create(rethrown with { Info = info });
        }

        var userErrorInstance = TryGetUserErrorInstance(value);

        var title = await TryGetUserErrorDiagnosticStringAsync(
            userErrorInstance,
            cancellationToken,
            "DiagnosticTitle",
            "Message",
            "Title");
        title ??= value switch
        {
            null => "An error was thrown.",
            ICommandResult result => result.Message,
            Exception exception => exception.Message,
            _ => await FormatThrownDiagnosticValueAsync(value, cancellationToken),
        };

        // For ToshError-derived types, surface the user's class name as
        // the diagnostic code so the renderer's tail tag reads
        // `tosh.user.MyError` instead of the generic
        // `tosh.runtime.throw`. Bare strings / records / numbers fall
        // back to the generic code.
        // Diagnostic code surfaces the thrown value's identity:
        //   • user-defined tosh classes (incl. those extending Error) →
        //     bare class name, e.g. `ArgumentError`.
        //   • bare CLR exceptions thrown via `throw new System.X(...)` →
        //     full CLR type name, e.g. `System.ArgumentException`.
        //   • everything else (raw strings/records/numbers, ToshError
        //     without a user type) → generic `tosh.runtime.throw`.
        var code = await TryGetUserErrorDiagnosticStringAsync(
            userErrorInstance,
            cancellationToken,
            "Code",
            "DiagnosticCode");
        code ??= value switch
        {
            ToshError tosh when tosh.Data["tosh.user.type"] is string userType
                => userType,
            ToshError tosh when tosh.GetType() != typeof(ToshError)
                => tosh.GetType().FullName ?? tosh.GetType().Name,
            ToshClassInstance instance when DefinitionExtendsException(instance.Definition)
                => instance.Definition.Name,
            ToshError => "tosh.runtime.throw",
            Exception ex => ex.GetType().FullName ?? ex.GetType().Name,
            _ => "tosh.runtime.throw",
        };

        var label = await TryGetUserErrorDiagnosticStringAsync(
            userErrorInstance,
            cancellationToken,
            "Label");
        label ??= value switch
        {
            Exception => "an error escaped here",
            ToshClassInstance instance when DefinitionExtendsException(instance.Definition)
                => "an error escaped here",
            _ => "an unhandled value was thrown here",
        };
        var help = await TryGetUserErrorDiagnosticStringAsync(
            userErrorInstance,
            cancellationToken,
            "Help",
            "Tip",
            "Hint");
        var footerInfo = await TryGetUserErrorDiagnosticStringAsync(
            userErrorInstance,
            cancellationToken,
            "Info",
            "Information",
            "Context",
            "Details");

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: label,
            Help: help,
            Info: footerInfo));
    }

    private static ToshClassInstance? TryGetUserErrorInstance(object? value)
    {
        return value switch
        {
            ToshError { Cause: ToshClassInstance instance } => instance,
            ToshClassInstance instance when DefinitionExtendsException(instance.Definition) => instance,
            _ => null,
        };
    }

    private async ValueTask<string?> TryGetUserErrorDiagnosticStringAsync(
        ToshClassInstance? instance,
        CancellationToken cancellationToken,
        params string[] memberNames)
    {
        if (instance is null)
        {
            return null;
        }

        foreach (var memberName in memberNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            object? value;
            try
            {
                var member = await instance.TryGetMemberAsync(
                    memberName,
                    includeHidden: false,
                    cancellationToken);
                if (!member.Found || member.Value is null)
                {
                    continue;
                }

                value = member.Value;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                continue;
            }

            var text = await FormatThrownDiagnosticValueAsync(value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private async ValueTask<string> FormatThrownDiagnosticValueAsync(
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value is string text)
        {
            return text;
        }

        if (value is ToshClassInstance instance)
        {
            return await ToOperatorStringAsync(instance, cancellationToken);
        }

        return Runtime.Formatter.Format(value);
    }

    private sealed record AnnotationRefinementError(ToshDiagnosticException Exception);
}
