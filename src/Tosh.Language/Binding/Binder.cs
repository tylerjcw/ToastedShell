using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Binding;

/// <summary>
/// After the parser produces a <see cref="ParseResult"/>, the binder walks
/// the AST and resolves every <see cref="CommandSyntax"/> head against the
/// runtime command registry plus any function declarations found in the
/// same source. Successful resolutions are stamped onto
/// <see cref="CommandSyntax.BoundCommand"/> as a fast path for the evaluator.
/// Unresolved names with a close Levenshtein suggestion are surfaced as
/// diagnostics; unresolved names without a suggestion are silently deferred
/// to runtime resolution (which may locate them on <c>PATH</c>).
/// </summary>
/// <remarks>
/// Phase 1 scope (commands only): the binder does not resolve variables,
/// member accesses, or refinement type names.
/// </remarks>
public static class Binder
{
    private const int ShortNameLevenshteinThreshold = 1;
    private const int LongNameLevenshteinThreshold = 2;
    private const int ShortNameMaxLength = 4;
    private const int MaxSuggestions = 3;

    /// <summary>
    /// Binds every <see cref="CommandSyntax"/> reachable from
    /// <paramref name="parseResult"/> against <paramref name="commandRegistry"/>
    /// plus any function declarations found in the parse result.
    /// Returns the diagnostics the binder would surface; the caller decides
    /// whether to raise them based on the active <see cref="BinderStrictness"/>.
    /// </summary>
    /// <param name="isInteractive">
    /// When <c>false</c>, commands marked
    /// <see cref="ShellOnlyAttribute"/> are flagged at bind time with
    /// <c>tosh.shell_only</c> rather than waiting for the runtime to reach
    /// them. The REPL passes <c>true</c>; <c>-c</c> and script invocations
    /// pass <c>false</c>.
    /// </param>
    public static IReadOnlyList<ToshDiagnostic> Bind(
        ParseResult parseResult,
        ShellCommandRegistry commandRegistry,
        bool isInteractive = false)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(commandRegistry);

        var localFunctions = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalFunctions(parseResult.Statement, localFunctions);

        var context = new BindContext(
            parseResult.SourceName,
            parseResult.SourceText,
            commandRegistry,
            localFunctions,
            isInteractive,
            new List<ToshDiagnostic>());

        VisitStatement(parseResult.Statement, context);

        // Phase 2: variable-name scope analysis runs as a separate pass.
        // Concatenated diagnostics come back in source order across both
        // passes, since each pass walks the AST top-down.
        var variableDiagnostics = VariableBinder.Bind(parseResult);
        if (variableDiagnostics.Count == 0) return context.Diagnostics;
        if (context.Diagnostics.Count == 0) return variableDiagnostics;

        var combined = new List<ToshDiagnostic>(context.Diagnostics.Count + variableDiagnostics.Count);
        combined.AddRange(context.Diagnostics);
        combined.AddRange(variableDiagnostics);
        return combined;
    }

    // ──────────────────────────────────────────────────────────────────
    // Phase 1: collect same-source function declarations so that calls
    // to forward-declared (or same-file) user functions don't false-flag.
    // ──────────────────────────────────────────────────────────────────

    private static void CollectLocalFunctions(StatementSyntax statement, HashSet<string> sink)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) CollectLocalFunctions(child, sink);
                break;
            case FunctionDefinitionStatementSyntax func:
                sink.Add(func.Name);
                CollectLocalFunctionsFromBlock(func.Body, sink);
                break;
            case RuneDefinitionStatementSyntax rune:
                sink.Add(rune.Name);
                CollectLocalFunctionsFromBlock(rune.Body, sink);
                break;
            case ClassDefinitionStatementSyntax cls:
                foreach (var member in cls.Members)
                {
                    if (member is ClassMethodMemberSyntax method)
                    {
                        // Class methods are member-scoped — never invoked as bare commands.
                        // We still recurse into bodies in case they declare local funcs.
                        CollectLocalFunctionsFromBlock(method.Method.Body, sink);
                    }
                }
                break;
            case ModuleDefinitionStatementSyntax module:
                CollectLocalFunctionsFromBlock(module.Body, sink);
                break;
            case IfStatementSyntax @if:
                CollectLocalFunctionsFromBlock(@if.ThenBlock, sink);
                if (@if.ElseBlock is not null) CollectLocalFunctionsFromBlock(@if.ElseBlock, sink);
                break;
            case ForStatementSyntax @for:
                CollectLocalFunctionsFromBlock(@for.Body, sink);
                break;
            case WhileStatementSyntax @while:
                CollectLocalFunctionsFromBlock(@while.Body, sink);
                break;
            case UntilStatementSyntax @until:
                CollectLocalFunctionsFromBlock(@until.Body, sink);
                break;
            case TryStatementSyntax @try:
                CollectLocalFunctionsFromBlock(@try.TryBlock, sink);
                if (@try.CatchClause is not null) CollectLocalFunctionsFromBlock(@try.CatchClause.Body, sink);
                if (@try.FinallyBlock is not null) CollectLocalFunctionsFromBlock(@try.FinallyBlock, sink);
                break;
            case DeferStatementSyntax @defer:
                CollectLocalFunctionsFromBlock(@defer.Body, sink);
                break;
            case SwitchStatementSyntax @switch:
                foreach (var c in @switch.Cases) CollectLocalFunctionsFromBlock(c.Body, sink);
                if (@switch.DefaultBlock is not null) CollectLocalFunctionsFromBlock(@switch.DefaultBlock, sink);
                break;
            case SubcommandStatementSyntax sub:
                CollectLocalFunctionsFromBlock(sub.Body, sink);
                break;
        }
    }

    private static void CollectLocalFunctionsFromBlock(BlockSyntax block, HashSet<string> sink)
    {
        foreach (var stmt in block.Statements) CollectLocalFunctions(stmt, sink);
    }

    // ──────────────────────────────────────────────────────────────────
    // Visitor pass: bind every CommandSyntax we can reach.
    // ──────────────────────────────────────────────────────────────────

    private static void VisitStatement(StatementSyntax statement, BindContext context)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements) VisitStatement(child, context);
                break;
            case PipelineStatementSyntax pipeline:
                VisitPipeline(pipeline.Pipeline, context);
                break;
            case VariableDeclarationStatementSyntax v when v.Value is not null:
                VisitPipeline(v.Value, context);
                break;
            case DestructuringDeclarationStatementSyntax d:
                VisitPipeline(d.Value, context);
                break;
            case AllocStatementSyntax a:
                VisitPipeline(a.Value, context);
                break;
            case VariableAssignmentStatementSyntax va:
                VisitPipeline(va.Value, context);
                break;
            case MemberAssignmentStatementSyntax ma:
                VisitArgument(ma.Target, context);
                VisitPipeline(ma.Value, context);
                break;
            case ReturnStatementSyntax r when r.Value is not null:
                VisitPipeline(r.Value, context);
                break;
            case YieldStatementSyntax y when y.Value is not null:
                VisitPipeline(y.Value, context);
                break;
            case ThrowStatementSyntax t when t.Value is not null:
                VisitPipeline(t.Value, context);
                break;
            case TupleAssignmentStatementSyntax tup:
                // The Value-bearing members differ in shape; visit any pipelines we can find via reflection-free pattern.
                // For Phase 1 we stay conservative: tuple assignments with command pipelines on the RHS are uncommon
                // and will still be caught at runtime. Skip.
                break;
            case FunctionDefinitionStatementSyntax func:
                foreach (var p in func.Parameters)
                    if (p.DefaultValue is not null) VisitPipeline(p.DefaultValue, context);
                VisitBlock(func.Body, context);
                break;
            case RuneDefinitionStatementSyntax rune:
                VisitBlock(rune.Body, context);
                break;
            case ClassDefinitionStatementSyntax cls:
                foreach (var ba in cls.BaseConstructorArgs ?? Array.Empty<PipelineSyntax>())
                    VisitPipeline(ba, context);
                foreach (var member in cls.Members)
                {
                    switch (member)
                    {
                        case ClassPropertyMemberSyntax prop:
                            if (prop.Initializer is not null) VisitPipeline(prop.Initializer, context);
                            if (prop.GetterBody is not null) VisitBlock(prop.GetterBody, context);
                            if (prop.SetterBody is not null) VisitBlock(prop.SetterBody, context);
                            break;
                        case ClassMethodMemberSyntax method:
                            VisitBlock(method.Method.Body, context);
                            break;
                        case ClassConstructorMemberSyntax ctor:
                            VisitBlock(ctor.Body, context);
                            break;
                    }
                }
                break;
            case ModuleDefinitionStatementSyntax module:
                VisitBlock(module.Body, context);
                break;
            case IfStatementSyntax @if:
                VisitArgument(@if.Condition, context);
                VisitBlock(@if.ThenBlock, context);
                if (@if.ElseBlock is not null) VisitBlock(@if.ElseBlock, context);
                break;
            case ForStatementSyntax @for:
                VisitPipeline(@for.Source, context);
                VisitBlock(@for.Body, context);
                break;
            case WhileStatementSyntax @while:
                VisitArgument(@while.Condition, context);
                VisitBlock(@while.Body, context);
                break;
            case UntilStatementSyntax @until:
                VisitArgument(@until.Condition, context);
                VisitBlock(@until.Body, context);
                break;
            case TryStatementSyntax @try:
                VisitBlock(@try.TryBlock, context);
                if (@try.CatchClause is not null) VisitBlock(@try.CatchClause.Body, context);
                if (@try.FinallyBlock is not null) VisitBlock(@try.FinallyBlock, context);
                break;
            case DeferStatementSyntax @defer:
                VisitBlock(@defer.Body, context);
                break;
            case SwitchStatementSyntax @switch:
                VisitArgument(@switch.Value, context);
                foreach (var c in @switch.Cases)
                {
                    VisitArgument(c.MatchExpression, context);
                    if (c.Guard is not null) VisitArgument(c.Guard, context);
                    VisitBlock(c.Body, context);
                }
                if (@switch.DefaultBlock is not null) VisitBlock(@switch.DefaultBlock, context);
                break;
            case SubcommandStatementSyntax sub:
                VisitBlock(sub.Body, context);
                break;
                // The remaining statement variants (RequireStatementSyntax, BindStatementSyntax,
                // EnumDefinitionStatementSyntax, RecordDefinitionStatementSyntax, …) don't carry
                // CommandSyntax we need to bind. Their bodies, when present, are non-pipeline.
        }
    }

    private static void VisitBlock(BlockSyntax block, BindContext context)
    {
        foreach (var stmt in block.Statements) VisitStatement(stmt, context);
    }

    private static void VisitPipeline(PipelineSyntax pipeline, BindContext context)
    {
        foreach (var stage in pipeline.Stages)
        {
            if (stage is CommandSyntax command)
            {
                BindCommand(command, context);
                foreach (var arg in command.Arguments) VisitArgument(arg, context);
            }
        }
    }

    private static void VisitArgument(ArgumentSyntax argument, BindContext context)
    {
        switch (argument)
        {
            case BlockArgumentSyntax block:
                VisitBlock(block.Block, context);
                break;
            case NamedArgumentSyntax named:
                VisitArgument(named.Value, context);
                break;
            case SplatArgumentSyntax splat:
                VisitArgument(splat.Value, context);
                break;
            case ArrayLiteralArgumentSyntax arr:
                foreach (var item in arr.Items) VisitArgument(item, context);
                break;
            case SpreadElementArgumentSyntax spread:
                VisitArgument(spread.Value, context);
                break;
            case RecordLiteralArgumentSyntax rec:
                foreach (var field in rec.Fields)
                {
                    switch (field)
                    {
                        case RecordFieldSyntax rf: VisitArgument(rf.Value, context); break;
                        case ComputedRecordFieldSyntax crf:
                            VisitArgument(crf.NameExpression, context);
                            VisitArgument(crf.Value, context);
                            break;
                        case SpreadRecordEntrySyntax sre: VisitArgument(sre.Value, context); break;
                    }
                }
                break;
            case DictLiteralArgumentSyntax dict:
                foreach (var entry in dict.Entries)
                {
                    VisitArgument(entry.Key, context);
                    VisitArgument(entry.Value, context);
                }
                break;
            case NewObjectArgumentSyntax newObj:
                foreach (var a in newObj.Arguments) VisitArgument(a, context);
                break;
            case StaticMethodCallArgumentSyntax: break; // method-call arguments live in the same node; no Pipeline children
            case QuoteArgumentSyntax quote:
                VisitArgument(quote.Inner, context);
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Per-command resolution.
    // ──────────────────────────────────────────────────────────────────

    private static void BindCommand(CommandSyntax command, BindContext context)
    {
        var name = command.Name;

        // Variable invocation: $foo … is dispatched by the evaluator. Skip.
        if (name.StartsWith('$')) return;

        // Explicit path: ./foo, /bin/foo, ../foo. Defer entirely to runtime.
        if (LooksLikeExplicitPath(name)) return;

        if (context.CommandRegistry.TryGet(name, out var resolved))
        {
            // Phase 1 doesn't stamp BoundCommand: top-level user functions are
            // registered into the same Runtime.Commands registry the binder
            // queried, replacing aliases. A bound reference captured at parse
            // time can therefore go stale before evaluation. The binder's
            // Phase 1 value is the diagnostic below; runtime resolution stays
            // authoritative. A future phase can re-introduce the fast path
            // with a freshness check.

            // Bind-time [ShellOnly] check: in non-interactive contexts (script
            // files, `-c`, `source`) a command marked ShellOnly cannot be used.
            // The runtime check in ToshEngine still fires as a safety net for
            // any path that bypasses the binder, but raising here surfaces the
            // error before any work runs and catches shell-only commands
            // behind never-taken branches.
            if (!context.IsInteractive)
            {
                var shellOnly = resolved.GetType().GetCustomAttribute<ShellOnlyAttribute>();
                if (shellOnly is not null)
                {
                    var reason = string.IsNullOrWhiteSpace(shellOnly.Reason)
                        ? "It depends on interactive-shell state (history, prompt, directory stack, TUI)."
                        : shellOnly.Reason;
                    context.Diagnostics.Add(new ToshDiagnostic(
                        Code: "tosh.shell_only",
                        Title: $"Command '{resolved.Name}' is shell-only and cannot be used outside an interactive session.",
                        SourceName: context.SourceName,
                        SourceText: context.SourceText,
                        Span: command.NameSpan,
                        Label: $"'{resolved.Name}' is REPL-only",
                        Help: reason));
                }
            }
            return;
        }

        if (context.LocalFunctions.Contains(name))
        {
            // Recognized as a same-source declaration; suppress the diagnostic.
            return;
        }

        // Unresolved. Try Levenshtein against the registry (canonical names + aliases).
        var suggestions = FindSuggestions(name, context.CommandRegistry);
        if (suggestions.Count == 0) return; // Could be an external; defer silently.

        context.Diagnostics.Add(new ToshDiagnostic(
            Code: "tosh.bind.unknown_command",
            Title: $"Command '{name}' is not a registered builtin or function declared in this source.",
            SourceName: context.SourceName,
            SourceText: context.SourceText,
            Span: command.NameSpan,
            Label: BuildSuggestionLabel(suggestions),
            Help: "the binder flags names that look like typos for known commands. " +
                  "If '" + name + "' is meant to invoke an external program, ensure it is on $PATH; " +
                  "set TOSH_DISABLE_BINDER=1 to suppress all binder checks."));
    }

    private static bool LooksLikeExplicitPath(string name)
    {
        if (name.Length == 0) return false;
        if (name[0] is '/' or '.') return true;
        if (name.Contains('/')) return true;
        if (OperatingSystem.IsWindows() && (name.Contains('\\') || (name.Length >= 2 && name[1] == ':')))
            return true;
        return false;
    }

    private static IReadOnlyList<string> FindSuggestions(string name, ShellCommandRegistry registry)
    {
        var threshold = name.Length <= ShortNameMaxLength
            ? ShortNameLevenshteinThreshold
            : LongNameLevenshteinThreshold;

        var scored = new List<(string Candidate, int Distance)>();
        foreach (var candidate in registry.AllNames)
        {
            // Quick reject on length delta.
            if (Math.Abs(candidate.Length - name.Length) > threshold) continue;
            var distance = Levenshtein(name, candidate);
            if (distance <= threshold) scored.Add((candidate, distance));
        }

        return scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Candidate, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(s => s.Candidate)
            .ToArray();
    }

    private static string BuildSuggestionLabel(IReadOnlyList<string> suggestions)
    {
        return suggestions.Count switch
        {
            1 => $"did you mean '{suggestions[0]}'?",
            2 => $"did you mean '{suggestions[0]}' or '{suggestions[1]}'?",
            _ => "did you mean " + string.Join(", ", suggestions.Take(suggestions.Count - 1).Select(s => $"'{s}'"))
                 + $", or '{suggestions[^1]}'?",
        };
    }

    private static int Levenshtein(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];

        for (var j = 0; j <= target.Length; j++) previous[j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private sealed record BindContext(
        string SourceName,
        string SourceText,
        ShellCommandRegistry CommandRegistry,
        HashSet<string> LocalFunctions,
        bool IsInteractive,
        List<ToshDiagnostic> Diagnostics);
}
