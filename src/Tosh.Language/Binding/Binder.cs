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

    // Language keywords that look like bare command heads. Suggestion pool
    // expansion lets typos such as `rquire` map to `require`, even though
    // `require` is a parser keyword and never appears in ShellCommandRegistry.
    private static readonly string[] KeywordSuggestionPool = new[]
    {
        "require", "using", "export", "var", "const",
        "func", "class", "interface", "trait", "module", "enum",
        "record", "struct", "union", "rune", "event",
        "if", "else", "for", "while", "until", "return", "yield",
        "throw", "try", "catch", "finally", "switch", "case", "match",
        "default", "break", "continue", "defer", "new", "nameof",
        "alloc", "bind", "global",
    };

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
        bool isInteractive = false,
        Func<string, bool>? isExecutableOnPath = null)
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
            isExecutableOnPath ?? IsExecutableOnPath,
            new List<ToshDiagnostic>(),
            ImportsUnseenNames: ContainsRequireStatement(parseResult.Statement));

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

    /// <summary>
    /// True when the source <c>require</c>s another file, whose exports the binder cannot see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binder resolves command names against the registry plus the functions declared in this
    /// same source. A <c>require</c>d file's exports are in neither, so calling one produced
    /// <c>tosh.bind.unknown_command</c> — for a function that is present and callable. Worse, it
    /// was raised as an error, so `require "./lib.tosh"` followed by `shared` refused to run,
    /// while `echo (shared)` worked, because the binder only inspects command position.
    /// </para>
    /// <para>
    /// Suppressing the check for a source that imports is the conservative answer: a missed typo
    /// costs a worse runtime message, and a false positive costs a program that will not run.
    /// Reading the required file to learn its exports is the real fix and belongs with the same
    /// work in <c>TS-P3-12</c>, where the language server's index needs to chase the same targets.
    /// </para>
    /// </remarks>
    private static bool ContainsRequireStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case RequireStatementSyntax:
                return true;
            case ScriptStatementSyntax script:
                return script.Statements.Any(ContainsRequireStatement);
            case FunctionDefinitionStatementSyntax func:
                return BlockContainsRequire(func.Body);
            case RuneDefinitionStatementSyntax rune:
                return BlockContainsRequire(rune.Body);
            case ModuleDefinitionStatementSyntax module:
                return BlockContainsRequire(module.Body);
            case IfStatementSyntax @if:
                return BlockContainsRequire(@if.ThenBlock) ||
                       (@if.ElseBlock is not null && BlockContainsRequire(@if.ElseBlock));
            case ForStatementSyntax @for:
                return BlockContainsRequire(@for.Body);
            case WhileStatementSyntax @while:
                return BlockContainsRequire(@while.Body);
            case UntilStatementSyntax @until:
                return BlockContainsRequire(@until.Body);
            case TryStatementSyntax @try:
                return BlockContainsRequire(@try.TryBlock) ||
                       (@try.CatchClause is not null && BlockContainsRequire(@try.CatchClause.Body)) ||
                       (@try.FinallyBlock is not null && BlockContainsRequire(@try.FinallyBlock));
            case DeferStatementSyntax defer:
                return BlockContainsRequire(defer.Body);
            case SwitchStatementSyntax @switch:
                return @switch.Cases.Any(c => BlockContainsRequire(c.Body)) ||
                       (@switch.DefaultBlock is not null && BlockContainsRequire(@switch.DefaultBlock));
            case SubcommandStatementSyntax sub:
                return BlockContainsRequire(sub.Body);
            default:
                return false;
        }
    }

    private static bool BlockContainsRequire(BlockSyntax block) =>
        block.Statements.Any(ContainsRequireStatement);

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
            // `TS-P2-80`. Same rule for a struct body, which was not collected from either.
            case StructDefinitionStatementSyntax structure:
                foreach (var member in structure.Members)
                {
                    if (member is ClassMethodMemberSyntax method)
                    {
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
                context.DeferredDepth++;
                try { VisitBlock(func.Body, context); }
                finally { context.DeferredDepth--; }
                break;
            case RuneDefinitionStatementSyntax rune:
                context.DeferredDepth++;
                try { VisitBlock(rune.Body, context); }
                finally { context.DeferredDepth--; }
                break;
            case ClassDefinitionStatementSyntax cls:
                foreach (var ba in cls.BaseConstructorArgs ?? Array.Empty<PipelineSyntax>())
                    VisitPipeline(ba, context);

                VisitTypeBody(
                    new EnclosingTypeBody(cls.Name, cls.Members, cls.PrimaryConstructorParameters),
                    context);
                break;
            // `TS-P2-80`. A struct body is walked exactly as a class body is. It was not walked at
            // all, so `struct S { func g() { f } }` reported nothing where the identical typo in a
            // class method was caught — and the enclosing-member suggestion `TS-P2-41` added could
            // never fire inside one.
            case StructDefinitionStatementSyntax structure:
                VisitTypeBody(
                    new EnclosingTypeBody(
                        structure.Name,
                        structure.Members,
                        Array.Empty<FunctionParameterSyntax>()),
                    context);
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
            //
            // Exception: inside func/method/rune/ctor bodies the call is
            // deferred. If those bodies are eventually invoked from a
            // REPL session the runtime ShellOnly check passes; flagging
            // them at bind time produces noisy false positives every
            // time profile.tosh or autoload/*.tosh defines an alias
            // like `func h => history`.
            if (!context.IsInteractive && context.DeferredDepth == 0)
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

        // A source that imports may be calling something the binder cannot see.
        if (context.ImportsUnseenNames)
        {
            return;
        }

        // `TS-P2-41`. Before asking which shell command this looks like, ask whether the
        // enclosing class already declares it. A member is reported even when no command
        // resembles the name — the Levenshtein path below gives up silently in that case,
        // which left a bare sibling reference to be explained by the runtime instead, and
        // explained no better. An executable of the same name still wins: a class that
        // declares `prop git` should not stop `git status` in one of its methods.
        if (context.EnclosingClass is { } enclosing && !context.IsExecutableOnPath(name))
        {
            switch (ClassifyEnclosingName(enclosing, name, context.InPropertyInitializer, out var qualified))
            {
                case EnclosingName.Member:
                    context.Diagnostics.Add(new ToshDiagnostic(
                        Code: "tosh.bind.unknown_command",
                        Title: EnclosingMemberSuggestion.Title(name, enclosing.Name),
                        SourceName: context.SourceName,
                        SourceText: context.SourceText,
                        Span: command.NameSpan,
                        Label: EnclosingMemberSuggestion.Label(qualified),
                        Help: EnclosingMemberSuggestion.Help(enclosing.Name)));
                    return;

                case EnclosingName.ConstructorParameterInScope:
                    // It resolves here. Saying otherwise refused a working program.
                    return;

                case EnclosingName.ConstructorParameterOutOfScope:
                    context.Diagnostics.Add(new ToshDiagnostic(
                        Code: "tosh.bind.unknown_command",
                        Title: EnclosingMemberSuggestion.OutOfScopeTitle(name, enclosing.Name),
                        SourceName: context.SourceName,
                        SourceText: context.SourceText,
                        Span: command.NameSpan,
                        Label: EnclosingMemberSuggestion.OutOfScopeLabel(name),
                        Help: EnclosingMemberSuggestion.OutOfScopeHelp(name)));
                    return;
            }
        }

        // Unresolved. Try Levenshtein against the registry (canonical names + aliases).
        var suggestions = FindSuggestions(name, context.CommandRegistry);
        if (suggestions.Count == 0) return; // Could be an external; defer silently.

        // Before flagging as a possible typo, probe $PATH. If an
        // executable with this exact name exists, the user clearly
        // intends to invoke it as an external program — suppressing
        // the diagnostic prevents false positives for things like
        // `dotnet`, `git`, `tar`, `wget` that aren't builtins but are
        // perfectly valid commands on $PATH. Probing is cached so the
        // cost is paid at most once per name per process.
        if (context.IsExecutableOnPath(name)) return;

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

    /// <summary>
    /// Finds a member of <paramref name="cls"/> named <paramref name="name"/> — <c>TS-P2-41</c>.
    /// </summary>
    /// <remarks>
    /// Case-sensitive, because the qualified form it goes on to suggest has to be one the reader
    /// can paste: offering <c>$this.Count</c> for a reference written <c>count</c> would name a
    /// second problem while claiming to solve the first.
    /// </remarks>
    /// <summary>
    /// Walks the members of a class or struct body — <c>TS-P2-41</c>, <c>TS-P2-80</c>.
    /// </summary>
    /// <remarks>
    /// The members are in scope by name from here down, so an unresolved bare name can be
    /// recognised as one of them rather than guessed at.
    /// </remarks>
    private static void VisitTypeBody(EnclosingTypeBody body, BindContext context)
    {
        var enclosingBefore = context.EnclosingClass;
        context.EnclosingClass = body;

        try
        {
            foreach (var member in body.Members)
            {
                switch (member)
                {
                    case ClassPropertyMemberSyntax prop:
                        if (prop.Initializer is not null)
                        {
                            // A primary-constructor parameter is in scope here and nowhere else
                            // in the body, so the suggestion for one is offered only from inside
                            // this visit.
                            context.InPropertyInitializer = true;
                            try { VisitPipeline(prop.Initializer, context); }
                            finally { context.InPropertyInitializer = false; }
                        }
                        context.DeferredDepth++;
                        try
                        {
                            if (prop.GetterBody is not null) VisitBlock(prop.GetterBody, context);
                            if (prop.SetterBody is not null) VisitBlock(prop.SetterBody, context);
                        }
                        finally { context.DeferredDepth--; }
                        break;
                    case ClassMethodMemberSyntax method:
                        context.DeferredDepth++;
                        try { VisitBlock(method.Method.Body, context); }
                        finally { context.DeferredDepth--; }
                        break;
                    case ClassConstructorMemberSyntax ctor:
                        context.DeferredDepth++;
                        try { VisitBlock(ctor.Body, context); }
                        finally { context.DeferredDepth--; }
                        break;
                }
            }
        }
        finally { context.EnclosingClass = enclosingBefore; }
    }

    private static EnclosingName ClassifyEnclosingName(
        EnclosingTypeBody cls,
        string name,
        bool inPropertyInitializer,
        out string qualified)
    {
        foreach (var candidate in cls.Members)
        {
            switch (candidate)
            {
                case ClassMethodMemberSyntax method when method.Method.Name == name:
                    qualified = EnclosingMemberSuggestion.Qualify(cls.Name, name, method.IsStatic, isMethod: true);
                    return EnclosingName.Member;
                case ClassPropertyMemberSyntax prop when prop.Name == name:
                    qualified = EnclosingMemberSuggestion.Qualify(cls.Name, name, prop.IsStatic, isMethod: false);
                    return EnclosingName.Member;
            }
        }

        qualified = "";

        // A primary-constructor parameter is not a member — `$p.x` fails from outside — and it is
        // in scope in a property initializer and nowhere else. Inside one it *resolves*, so the
        // binder must stay quiet: `class K(name: string) { prop Y = name }` was rejected before
        // this, because `name` resembles `uname`, while the same program ran correctly under
        // TOSH_DISABLE_BINDER=1. Outside one there is no spelling that works, so the honest answer
        // is where the parameter does reach rather than a nearby command name.
        foreach (var parameter in cls.PrimaryConstructorParameters)
        {
            if (parameter.Name == name)
            {
                return inPropertyInitializer
                    ? EnclosingName.ConstructorParameterInScope
                    : EnclosingName.ConstructorParameterOutOfScope;
            }
        }

        return EnclosingName.None;
    }

    /// <summary>
    /// The declaration body a bare name sits inside — <c>TS-P2-41</c>, <c>TS-P2-80</c>.
    /// </summary>
    /// <param name="Name">The type's name, used to spell a static member's qualifier.</param>
    /// <param name="Members">Methods and properties declared in the body.</param>
    /// <param name="PrimaryConstructorParameters">
    /// Empty for a struct, which has no primary-constructor form.
    /// </param>
    /// <remarks>
    /// A class and a struct carry the same <c>ClassMemberSyntax</c> list but arrive as different
    /// statement nodes, so the walk was written for one of them and the other went unvisited
    /// entirely. Naming what the check actually needs is what lets both feed it.
    /// </remarks>
    private sealed record EnclosingTypeBody(
        string Name,
        IReadOnlyList<ClassMemberSyntax> Members,
        IReadOnlyList<FunctionParameterSyntax> PrimaryConstructorParameters);

    /// <summary>What an unresolved bare name turns out to be in the class around it.</summary>
    private enum EnclosingName
    {
        None,
        Member,
        ConstructorParameterInScope,
        ConstructorParameterOutOfScope,
    }

    private static bool LooksLikeExplicitPath(string name)
    {
        if (name.Length == 0) return false;

        // `TS-P2-60`. A leading `~` is a path just as much as a leading `/`. `~/projects`
        // already passed this test by containing a separator, so only the bare `~` and `~name`
        // forms fell through to the typo machinery — which answered a bare `~` with
        // "did you mean 'f'?".
        if (name[0] is '/' or '.' or '~') return true;
        if (name.Contains('/')) return true;
        if (OperatingSystem.IsWindows() && (name.Contains('\\') || (name.Length >= 2 && name[1] == ':')))
            return true;
        return false;
    }

    // Cache of name → "is on PATH?" results. Bounded only by the
    // distinct command names seen across a process lifetime, which
    // is small in practice. The lookup is read-mostly so a plain
    // dictionary with a lock is more than fast enough.
    private static readonly Dictionary<string, bool> s_pathProbeCache = new(StringComparer.Ordinal);
    private static readonly object s_pathProbeLock = new();

    /// <summary>
    /// Returns true if an executable with the given name exists on
    /// <c>$PATH</c>. The result is cached per process; we do not
    /// invalidate the cache when <c>$PATH</c> changes — startup
    /// invocations dominate, and a tosh restart is cheap.
    /// </summary>
    private static bool IsExecutableOnPath(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        lock (s_pathProbeLock)
        {
            if (s_pathProbeCache.TryGetValue(name, out var cached)) return cached;
        }

        var found = ProbePath(name);

        lock (s_pathProbeLock)
        {
            s_pathProbeCache[name] = found;
        }
        return found;
    }

    private static bool ProbePath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { string.Empty };

        foreach (var dir in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir, name + ext);
                }
                catch
                {
                    continue;
                }
                if (File.Exists(candidate)) return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<string> FindSuggestions(string name, ShellCommandRegistry registry)
    {
        // `TS-P2-60`. Edit distance answers any question it is asked, including nonsensical
        // ones: a bare `~` scored within one character of the command `f` and was offered as a
        // correction. The rule lives on the registry so the engine's own suggestion helper
        // reads the same one.
        if (!ShellCommandRegistry.IsNameShaped(name))
        {
            return [];
        }

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

        foreach (var keyword in KeywordSuggestionPool)
        {
            if (Math.Abs(keyword.Length - name.Length) > threshold) continue;
            if (string.Equals(keyword, name, StringComparison.Ordinal)) continue;
            var distance = Levenshtein(name, keyword);
            if (distance <= threshold) scored.Add((keyword, distance));
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
        Func<string, bool> IsExecutableOnPath,
        List<ToshDiagnostic> Diagnostics,
        bool ImportsUnseenNames = false)
    {
        // Tracks how many deferred-body scopes (func/method/rune/ctor)
        // we're nested inside. When > 0, ShellOnly is not enforced at
        // bind time because the body only runs when the caller invokes
        // it — which may well be from an interactive REPL session,
        // even if the file containing the declaration is loaded
        // non-interactively (profile.tosh, autoload/*.tosh, etc.).
        public int DeferredDepth { get; set; }

        /// <summary>
        /// The class whose body is being walked, or <see langword="null"/> outside one —
        /// <c>TS-P2-41</c>, so an unresolved bare name can be checked against its siblings.
        /// </summary>
        /// <remarks>
        /// Saved and restored rather than pushed onto a stack: a nested class definition replaces
        /// the enclosing one for the duration of its body, which is the scope a bare name in that
        /// body would be reaching for.
        /// </remarks>
        public EnclosingTypeBody? EnclosingClass { get; set; }

        /// <summary>
        /// True while walking a property initializer, the one place a primary-constructor
        /// parameter is in scope — <c>TS-P2-41</c>.
        /// </summary>
        public bool InPropertyInitializer { get; set; }
    }
}
