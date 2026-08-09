using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Stdlib;
using Tosh.Language.Binding;
using Tosh.Language.Bridge;
using Tosh.Language.Bridge.Shell;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine : IShellEvaluator, IShellNamedTypeView
{
    private readonly record struct EvaluatedCommandArgument(ArgumentSyntax Syntax, object? Value);
    private readonly record struct ScriptArgumentValue(object? Value, int Index);
    private readonly record struct CapturedEnumeratorMove(
        bool HasValue,
        object? Value,
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure);

    private readonly Stack<LexicalScope> _scopes = new();
    private readonly Stack<string> _functionCallStack = new();
    private readonly Stack<string> _scriptNameStack = new();
    private readonly Stack<IReadOnlyList<object?>> _scriptArgumentsStack = new();
    // When this engine is a fork (created via Fork()), this holds its own executor for
    // propagation through CommandContext.  Null for the primary (root) engine.
    private readonly IShellBlockExecutor? _ownBlockExecutor;
    private readonly Stack<IReadOnlyList<object?>> _functionArgumentsStack = new();
    private readonly Stack<object?> _functionInputStack = new();
    private readonly Dictionary<string, ToshRequiredScriptArtifact> _requiredScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentlyRequiring = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NativeLibraryBinding> _requiredNativeLibraries = new(StringComparer.OrdinalIgnoreCase);
    private int _commandEventDepth;
    private readonly ToshRuntimeNamespace _toshNamespace;
    private readonly ShellEnvironmentNamespace _environmentNamespace;

    /// <summary>
    /// Phase 3.2 — When evaluating the initializer pipeline of a typed
    /// `var x: T = …` declaration (or any other call site that knows
    /// its expected target type), this carries the LHS annotation so
    /// the inner CommandInvocation can be stamped with it. Generic
    /// function calls then seed type-parameter bindings from the
    /// target before parameter conversion runs.
    /// </summary>
    private readonly System.Threading.AsyncLocal<string?> _targetTypeAnnotation = new();

    /// <summary>
    /// Active binder strictness for evaluation calls that don't pass an explicit override.
    /// Defaults to <see cref="BinderStrictness.Warn"/>; the CLI raises this to
    /// <see cref="BinderStrictness.Strict"/> for <c>-c</c>, script files, and the
    /// <c>source</c> command via <see cref="PushBinderStrictness"/>.
    /// </summary>
    public BinderStrictness BinderStrictness { get; set; } = BinderStrictness.Warn;

    /// <summary>
    /// Temporarily overrides <see cref="BinderStrictness"/> for the lifetime of the returned
    /// disposable. Use within a <c>using</c> block around an evaluation that needs different
    /// semantics from the engine default (e.g. running a script file under Strict).
    /// </summary>
    public IDisposable PushBinderStrictness(BinderStrictness strictness)
    {
        var previous = BinderStrictness;
        BinderStrictness = strictness;
        return new BinderStrictnessScope(this, previous);
    }

    private sealed class BinderStrictnessScope : IDisposable
    {
        private readonly ToshEngine _engine;
        private readonly BinderStrictness _previous;
        private bool _disposed;
        public BinderStrictnessScope(ToshEngine engine, BinderStrictness previous)
        {
            _engine = engine;
            _previous = previous;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _engine.BinderStrictness = _previous;
        }
    }

    public ToshEngine(ToshRuntime? runtime = null)
    {
        Runtime = runtime ?? ToshRuntime.CreateDefault();
        Runtime.BlockExecutor = new EngineBlockExecutor(this);
        Runtime.Evaluator = this;
        Runtime.EventSenderFactory = CreateEventSender;
        _toshNamespace = new ToshRuntimeNamespace(this);
        Runtime.RuntimeNamespace ??= _toshNamespace;
        _environmentNamespace = new ShellEnvironmentNamespace(Runtime);
        if (!Runtime.Commands.TryGet("source", out _))
        {
            Runtime.Commands.Register(new SourceCommand(this));
        }

        if (!Runtime.Commands.TryGet("eval", out _))
        {
            Runtime.Commands.Register(new EvalCommand(this));
        }

        if (!Runtime.Commands.TryGet("debug", out _))
        {
            Runtime.Commands.Register(new DebugCommand(this));
        }

        if (!Runtime.Commands.TryGet("format", out _))
        {
            Runtime.Commands.Register(new Bridge.Scripting.FormatCommand());
        }

        // Load built-in rune definitions
        LoadBuiltinRunesAsync().GetAwaiter().GetResult();

        // Register the trait-constraint resolver for the `is` / `is-not` operators so
        // expressions like `$x is Numeric` consult the same registry used by generic
        // type-parameter constraint validation.
        Tosh.Runtime.OperatorEvaluator.ResolveTraitConstraint ??= static (name, type) =>
            ToshTypeParameterConstraintRegistry.TryGet(name, out var predicate) && predicate(type);
    }

    /// <summary>
    /// Creates a forked child engine that shares the same <see cref="ToshRuntime"/> but has
    /// its own isolated scope stack pre-seeded with cloned copies of <paramref name="capturedScopes"/>.
    /// The fork does NOT write back to <c>Runtime.BlockExecutor</c> / <c>Runtime.Evaluator</c> /
    /// <c>Runtime.EventSenderFactory</c>; instead it propagates its executor via
    /// <see cref="CommandContext.BlockExecutor"/>.
    /// </summary>
    private ToshEngine(ToshRuntime runtime, IReadOnlyList<LexicalScope>? capturedScopes)
    {
        Runtime = runtime;
        _toshNamespace = new ToshRuntimeNamespace(this);
        _environmentNamespace = new ShellEnvironmentNamespace(Runtime);
        // Pre-seed the scope stack with clones of the parent's visible scopes.
        // capturedScopes is ordered outer-to-inner; pushing outer-first means
        // the inner-most scope ends up on top of the stack — correct lookup order.
        if (capturedScopes is not null)
        {
            foreach (var scope in capturedScopes)
            {
                _scopes.Push(scope.Clone());
            }
        }
        // Own executor — propagated through CommandContext so nested commands
        // (including race/settle/parallel) continue using this fork's engine.
        _ownBlockExecutor = new EngineBlockExecutor(this);
    }

    /// <summary>
    /// Creates an isolated child engine that shares the same runtime but has its own
    /// execution state. Pass <see cref="CaptureVisibleScopes"/> as the snapshot.
    /// </summary>
    internal ToshEngine Fork(IReadOnlyList<LexicalScope>? capturedScopes)
        => new(Runtime, capturedScopes);

    private static readonly ParseResult _builtinRunesParseResult =
        ToshParser.Parse(BuiltinRunes.Source, "<builtin-runes>");

    private async Task LoadBuiltinRunesAsync()
    {
        await foreach (var _ in EvaluateAsync(_builtinRunesParseResult, CancellationToken.None)) { }
    }

    public ToshRuntime Runtime { get; }

    /// <summary>
    /// True when this engine is hosting an interactive REPL session.
    /// Set to <c>true</c> by <c>ToshRepl</c> on construction; remains <c>false</c>
    /// for one-shot script execution (<c>tosh script.tosh</c>, <c>tosh -c …</c>,
    /// embedded test-host engines).
    ///
    /// When <c>false</c>, invoking a command marked
    /// <see cref="Tosh.Runtime.ShellOnlyAttribute"/> emits a hushable warning
    /// (<c>tosh.shell_only</c>) — those commands depend on REPL state
    /// (history, directory stack, prompt rendering, TUI) and don't make
    /// sense in non-interactive contexts.
    /// </summary>
    public bool IsInteractiveSession { get; set; }

    /// <summary>
    /// Optional hook invoked before each statement in a block is evaluated.
    /// Used for step-through debugging, breakpoints, and script tracing.
    /// </summary>
    public DebugHookDelegate? DebugHook { get; set; }

    internal ShellEventSender CreateEventSender()
    {
        var function = _functionCallStack.Count > 0 ? _functionCallStack.Peek() : null;
        var script = _scriptNameStack.Count > 0 ? _scriptNameStack.Peek() : null;
        return new ShellEventSender(function, script, Line: null);
    }

    internal string GetCurrentScriptPath() => _scriptNameStack.Count > 0 ? _scriptNameStack.Peek() : string.Empty;

    internal IReadOnlyList<object?> GetCurrentScriptArguments() => _scriptArgumentsStack.Count > 0 ? _scriptArgumentsStack.Peek() : Runtime.InvocationArguments;

    internal string GetCurrentFunctionName() => _functionCallStack.Count > 0 ? _functionCallStack.Peek() : string.Empty;

    internal IReadOnlyList<object?> GetCurrentFunctionArguments() => _functionArgumentsStack.Count > 0 ? _functionArgumentsStack.Peek() : Array.Empty<object?>();

    internal object? GetCurrentFunctionInput() => _functionInputStack.Count > 0 ? _functionInputStack.Peek() : null;

    /// <summary>
    /// Current function call depth. 0 = top-level script, 1 = inside first function call, etc.
    /// Used by the debug session for step-over and step-out tracking.
    /// </summary>
    public int CallStackDepth => _functionCallStack.Count;

    /// <summary>
    /// Returns the function call stack from innermost (index 0) to outermost, plus the active script names.
    /// </summary>
    public IReadOnlyList<string> GetCallStackNames()
    {
        var frames = new List<string>();
        foreach (var name in _functionCallStack)
            frames.Add(string.IsNullOrEmpty(name) ? "<anonymous>" : name);
        // Append script context if not already represented
        if (_scriptNameStack.Count > 0)
            frames.Add(Path.GetFileName(_scriptNameStack.Peek()) ?? _scriptNameStack.Peek());
        return frames;
    }

    internal ITypeResolver CreateScopedTypeResolver()
    {
        // Runtime.NativeTypes holds globally-declared `raw struct` types. It sits
        // under the lexical scopes but above the CLR resolver, so a global raw
        // struct is nameable even with no scope on the stack.
        var baseResolver = Runtime.NativeTypes.Count == 0 && Runtime.Modules.Count == 0
            ? Runtime.TypeResolver
            : new NativeTypeRegistryResolver(Runtime.TypeResolver, Runtime.NativeTypes, Runtime.Modules);

        if (_scopes.Count == 0)
        {
            return baseResolver;
        }

        return new ScopedTypeResolver(baseResolver, _scopes.ToArray());
    }

    /// <summary>
    /// Snapshots the commands visible from here, so an introspecting command can see what the
    /// caller can rather than only what is globally registered (<c>TS-P2-54</c>).
    /// </summary>
    public IScopedCommandView CreateScopedCommandView()
    {
        // No scope means nothing shadows the registry, and the registry is already a view — the
        // `-c` prompt takes this path, which is why introspection appeared to work there.
        var modules = EnumerateVisibleModules();

        return _scopes.Count == 0 && modules.Count == 0
            ? Runtime.Commands
            : new ScopedCommandView(_scopes.ToArray(), Runtime.Commands, modules, this);
    }

    public ParseResult Parse(string source, string sourceName = "<input>")
    {
        var result = ToshParser.Parse(source, sourceName, CreateParseContext());
        RegisterLineHushDirectives(sourceName, result.LineHushDirectives);
        return result;
    }

    /// <summary>
    /// Hands the parser what this engine already knows (TS-P2-23), so
    /// identity decisions consult a table rather than inferring from
    /// capitalization. Modules come from the live scope chain, which is
    /// how an *imported* module is recognised — the source being parsed
    /// never declares it.
    /// </summary>
    private ParseContext CreateParseContext()
    {
        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in _scopes)
        {
            foreach (var name in scope.Modules.Keys)
            {
                moduleNames.Add(name);
            }

            // Classes, records, structs, enums and traits the session has
            // declared, plus the shell types registered as defaults.
            foreach (var name in scope.Classes.Keys)
            {
                typeNames.Add(name);
            }
        }

        foreach (var name in Runtime.Classes.Keys)
        {
            typeNames.Add(name);
        }

        // The built-in aliases are the ones the casing rule could never get
        // right: `string`, `int`, `record` and friends are types spelled in
        // lower case. `using X = Y` aliases land here for the same reason.
        foreach (var alias in DotNetTypeResolver.BuiltInAliases.Keys)
        {
            typeNames.Add(alias);
        }

        if (Runtime.TypeResolver is DotNetTypeResolver resolver)
        {
            foreach (var alias in resolver.GetAliases().Keys)
            {
                typeNames.Add(alias);
            }
        }

        return ParseContext.Create(
            commandNames: Runtime.Commands.AllNames,
            moduleNames: moduleNames,
            typeNames: typeNames);
    }

    /// <summary>
    /// Per-source-name line-hush index built from inline `# hush &lt;code&gt;` comment
    /// directives. Outer key is <c>SourceName</c>, inner key is the 1-based line
    /// number, and the value is the set of codes silenced at that line. Looked up
    /// during warning emission so the suppression is line-local without touching
    /// scope or global config.
    /// </summary>
    private readonly Dictionary<string, Dictionary<int, HashSet<string>>> _lineHushBySource =
        new(StringComparer.Ordinal);

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

    /// <summary>
    /// Computes the 1-based line number containing <paramref name="offset"/> within
    /// <paramref name="sourceText"/>. Returns <c>0</c> when the offset is out of range,
    /// signaling "no location available" to <see cref="WriteWarning(string?, string, string?, string?, ToshDiagnosticCategory, string?, int)"/>.
    /// </summary>
    private static int LineFromOffset(string sourceText, int offset)
    {
        if (offset < 0 || offset > sourceText.Length)
        {
            return 0;
        }
        var line = 1;
        for (var i = 0; i < offset; i++)
        {
            if (sourceText[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    public IAsyncEnumerable<object?> EvaluateAsync(string source, CancellationToken cancellationToken = default)
    {
        return EvaluateAsync(source, "<input>", cancellationToken);
    }

    /// <summary>
    /// The <see cref="IShellEvaluator"/> seam. Kept at exactly three parameters because the
    /// interface declares it so; capture-aware callers use the overload below.
    /// </summary>
    public IAsyncEnumerable<object?> EvaluateAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(source, sourceName, cancellationToken, outputIsCaptured: false);

    /// <param name="outputIsCaptured">
    /// Whether the caller is consuming the value rather than displaying it, so an external
    /// command's stdout must be piped (<c>TS-P1-30</c>). An interpolation hole re-parses its text
    /// and runs it through here as a whole statement, which is why capture has to be expressible
    /// on this seam rather than only at the consuming sites (<c>TS-P1-32</c>).
    /// </param>
    public IAsyncEnumerable<object?> EvaluateAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken,
        bool outputIsCaptured)
    {
        var parseResult = Parse(source, sourceName);

        if (parseResult.Diagnostics.Count > 0)
        {
            throw new ToshDiagnosticException(parseResult.Diagnostics
                .Select(diagnostic => new ToshDiagnostic(
                    Code: diagnostic.Code,
                    Title: diagnostic.Title,
                    SourceName: parseResult.SourceName,
                    SourceText: parseResult.SourceText,
                    Span: diagnostic.Span,
                    Label: diagnostic.Label,
                    Help: diagnostic.Help))
                .ToArray());
        }

        ApplyBinder(parseResult);
        ApplyLowering(parseResult);

        return EvaluateParseResultAsync(parseResult, cancellationToken, outputIsCaptured);
    }

    /// <summary>
    /// Run the lowering pass for its side effects on the parse tree
    /// (constant folding stamps <c>FoldedConstant</c> annotations on
    /// operator nodes the evaluator then short-circuits on). The
    /// resulting <see cref="Tosh.Language.Binding.BoundUnit"/> is
    /// discarded for now; future commits will route evaluation through
    /// it directly. Disabled by <c>TOSH_DISABLE_LOWERER=1</c>.
    /// </summary>
    private void ApplyLowering(ParseResult parseResult)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("TOSH_DISABLE_LOWERER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var unit = Tosh.Language.Binding.Lowerer.Lower(parseResult, Runtime.Commands);

            // Type-check pass: piggy-backs on the lowered unit. Same
            // disable env var (TOSH_DISABLE_LOWERER) suppresses both
            // — they're implemented as one pipeline. Diagnostics flow
            // through the same renderer the binder uses, at Warning
            // severity for now (T3 will promote under --compile).
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TOSH_DISABLE_TYPECHECK"),
                    "1",
                    StringComparison.Ordinal))
            {
                var typeDiagnostics = Tosh.Language.Binding.TypeChecker.Check(unit);
                if (typeDiagnostics.Count > 0 && !IsInteractiveSession)
                {
                    var renderer = new DiagnosticRenderer(
                        Runtime.Config.Theme.Diagnostics, Runtime.Config.Diagnostics);
                    foreach (var d in typeDiagnostics)
                    {
                        Runtime.Error.WriteLine(renderer.RenderWarning(d));
                    }
                }
            }
        }
        catch
        {
            // Lowering must never break evaluation. If anything goes
            // wrong we fall back to the parse-tree path with no
            // annotations — exactly the pre-Phase-A behavior.
        }
    }

    private void ApplyBinder(ParseResult parseResult)
    {
        // Bailout: an undocumented escape hatch in case the binder misbehaves on some
        // unforeseen AST shape. Documented in AGENTS.md as a recovery mechanism only.
        if (string.Equals(
                Environment.GetEnvironmentVariable("TOSH_DISABLE_BINDER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var diagnostics = Tosh.Language.Binding.Binder.Bind(parseResult, Runtime.Commands, IsInteractiveSession);
        if (diagnostics.Count == 0) return;

        switch (BinderStrictness)
        {
            case BinderStrictness.Lenient:
                return;
            case BinderStrictness.Warn:
                var renderer = new DiagnosticRenderer(Runtime.Config.Theme.Diagnostics, Runtime.Config.Diagnostics);
                foreach (var diagnostic in diagnostics)
                {
                    Runtime.Error.WriteLine(renderer.RenderWarning(diagnostic));
                }
                return;
            case BinderStrictness.Strict:
                throw new ToshDiagnosticException(diagnostics.ToArray());
        }
    }

    public async Task<IReadOnlyList<object?>> ExecuteToListAsync(string source, CancellationToken cancellationToken = default)
    {
        return await ExecuteToListAsync(source, "<input>", cancellationToken);
    }

    public async Task<IReadOnlyList<object?>> ExecuteToListAsync(string source, string sourceName, CancellationToken cancellationToken = default)
    {
        return await AsyncEnumerableExtensions.ToListAsync(EvaluateAsync(source, sourceName, cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<object?> ExecuteScriptFileAsync(
        string path,
        IReadOnlyList<object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteScriptFileAsync(path, arguments, isolateScope: true, cancellationToken);
    }

    internal IAsyncEnumerable<object?> ExecuteScriptFileAsync(
        string path,
        IReadOnlyList<object?>? arguments,
        bool isolateScope,
        CancellationToken cancellationToken)
    {
        return ExecuteScriptFileCoreAsync(path, arguments ?? Array.Empty<object?>(), isolateScope, cancellationToken);
    }

    private async IAsyncEnumerable<object?> ExecuteScriptFileCoreAsync(
        string path,
        IReadOnlyList<object?> arguments,
        bool isolateScope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolvedPath = PathUtilities.ResolvePath(Runtime.CurrentDirectory, path);

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Script file '{resolvedPath}' was not found.", resolvedPath);
        }

        var source = await File.ReadAllTextAsync(resolvedPath, cancellationToken);

        await foreach (var value in ExecuteScriptAsync(source, resolvedPath, arguments, isolateScope, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteScriptAsync(
        string source,
        string sourceName,
        IReadOnlyList<object?> arguments,
        bool isolateScope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scriptArgs = arguments.ToArray();
        IDisposable scopeFrame = ScopeFrames.Empty;

        if (isolateScope)
        {
            scopeFrame = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal));
        }

        // Script files (and `source` invocations, which route through here)
        // run under Strict binder semantics: an unrecognized command with
        // a close suggestion aborts the script before evaluation begins.
        var binderScope = PushBinderStrictness(BinderStrictness.Strict);

        try
        {
            _scriptArgumentsStack.Push(scriptArgs);
            await foreach (var value in EvaluateAsync(source, sourceName, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
        finally
        {
            _scriptArgumentsStack.Pop();
            binderScope.Dispose();
            scopeFrame.Dispose();
        }
    }

    private IAsyncEnumerable<object?> EvaluateAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return EvaluateParseResultAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Evaluate a previously-lowered <see cref="Tosh.Compiler.IR.BoundUnit"/>.
    /// v1 delegates to the parse-tree evaluator using the unit's
    /// <see cref="Tosh.Compiler.IR.BoundUnit.ParseResult"/>;
    /// future commits will fast-path individual carved-out bound
    /// shapes without changing this public seam.
    /// </summary>
    public IAsyncEnumerable<object?> EvaluateAsync(
        Tosh.Compiler.IR.BoundUnit unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return EvaluateParseResultAsync((ParseResult)unit.ParseResult, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateParseResultAsync(
        ParseResult parseResult,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        bool outputIsCaptured = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            Runtime.Config.Shell.MaxRecursionDepth,
            $"script {parseResult.SourceName}",
            parseResult.SourceName,
            parseResult.SourceText,
            parseResult.Statement.Span);

        var isTopLevel = _commandEventDepth == 0;
        var values = new List<object?>();
        var stopwatch = isTopLevel ? System.Diagnostics.Stopwatch.StartNew() : null;

        // Raise CommandStarting for top-level user input only
        if (isTopLevel && Runtime.Events.GetHandlers(BuiltInEventNames.CommandStarting).Count > 0)
        {
            _commandEventDepth++;
            try
            {
                var sender = Runtime.EventSenderFactory?.Invoke()
                    ?? new ShellEventSender(Function: null, Script: null, Line: null);
                var inputText = parseResult.SourceText.Trim();
                var startingEvent = new CommandStartingEvent(
                    inputText, [], inputText, sender);
                await Runtime.Events.RaiseAsync(startingEvent, cancellationToken);

                if (startingEvent.Cancelled)
                {
                    yield break;
                }
            }
            finally
            {
                _commandEventDepth--;
            }
        }

        _commandEventDepth++;
        _scriptNameStack.Push(parseResult.SourceName);
        var exitCode = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingException = null;
        // Values produced by a top-level `return ...` statement: the catch
        // arm captures them but cannot `yield return` from inside the
        // try (C# forbids yield in try/catch). They're flushed below
        // after the outer try/finally has run.
        IReadOnlyList<object?>? pendingReturnValues = null;
        Exception? pendingThrownValue = null;

        // Drive the inner enumerator manually so we can yield values as they arrive.
        // C# allows yield return inside try-finally but not try-catch; the inner
        // try-catch only wraps MoveNextAsync, keeping the yield return outside it.
        //
        // Some statements (break, continue, return) throw signal exceptions
        // synchronously inside EvaluateStatementAsync before returning an
        // IAsyncEnumerable. Wrap enumerator creation in its own catch so those
        // signals are handled identically to the in-loop case below.
        IAsyncEnumerator<object?> enumerator;
        try
        {
            enumerator = EvaluateStatementAsync(
                parseResult.SourceName,
                parseResult.SourceText,
                parseResult.Statement,
                cancellationToken,
                outputIsCaptured)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (ReturnSignalException signal)
        {
            values.AddRange(signal.Values);
            UpdateLastResultIfAny(signal.Values);
            pendingReturnValues = signal.Values;
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }
        catch (BreakSignalException signal)
        {
            pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                CreateLoopControlDiagnostic(
                    parseResult.SourceName, parseResult.SourceText, signal.Span,
                    keyword: "break", code: "tosh.runtime.break_outside_loop",
                    title: "'break' can only be used inside 'for', 'while', or 'each' blocks."));
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }
        catch (ContinueSignalException signal)
        {
            pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                CreateLoopControlDiagnostic(
                    parseResult.SourceName, parseResult.SourceText, signal.Span,
                    keyword: "continue", code: "tosh.runtime.continue_outside_loop",
                    title: "'continue' can only be used inside 'for', 'while', or 'each' blocks."));
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception failure) when (
            failure is not OperationCanceledException &&
            ToshDeferFailures.IsDeferFailure(failure))
        {
            exitCode = 1;
            pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                ToshDeferFailures.ToDiagnosticException(
                    failure,
                    parseResult.SourceName,
                    parseResult.SourceText));
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }
        catch (ThrowSignalException signal)
        {
            exitCode = 1;
            pendingThrownValue = signal;
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception thrown) when (IsToshThrown(thrown))
        {
            exitCode = 1;
            pendingThrownValue = thrown;
            enumerator = EmptyAsyncEnumerable().GetAsyncEnumerator(cancellationToken);
        }

        try // outer: ensures cleanup + CommandCompleted event
        {
            if (pendingThrownValue is not null)
            {
                pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                    await CreateThrownValueDiagnosticAsync(
                        parseResult.SourceName,
                        parseResult.SourceText,
                        pendingThrownValue,
                        cancellationToken));
            }

            try // inner: ensures enumerator disposal
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (ReturnSignalException signal)
                    {
                        values.AddRange(signal.Values);
                        UpdateLastResultIfAny(signal.Values);
                        pendingReturnValues = signal.Values;
                        break;
                    }
                    catch (BreakSignalException signal)
                    {
                        pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                            CreateLoopControlDiagnostic(
                                parseResult.SourceName,
                                parseResult.SourceText,
                                signal.Span,
                                keyword: "break",
                                code: "tosh.runtime.break_outside_loop",
                                title: "'break' can only be used inside 'for', 'while', or 'each' blocks."));
                        break;
                    }
                    catch (ContinueSignalException signal)
                    {
                        pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                            CreateLoopControlDiagnostic(
                                parseResult.SourceName,
                                parseResult.SourceText,
                                signal.Span,
                                keyword: "continue",
                                code: "tosh.runtime.continue_outside_loop",
                                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks."));
                        break;
                    }
                    catch (Exception failure) when (
                        failure is not OperationCanceledException &&
                        ToshDeferFailures.IsDeferFailure(failure))
                    {
                        exitCode = 1;
                        pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                            ToshDeferFailures.ToDiagnosticException(
                                failure,
                                parseResult.SourceName,
                                parseResult.SourceText));
                        break;
                    }
                    catch (ThrowSignalException signal)
                    {
                        exitCode = 1;
                        pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                            await CreateThrownValueDiagnosticAsync(
                                parseResult.SourceName,
                                parseResult.SourceText,
                                signal,
                                cancellationToken));
                        break;
                    }
                    catch (Exception thrown) when (IsToshThrown(thrown))
                    {
                        // A user-thrown CLR exception (e.g. `throw (new MyError())`)
                        // bubbled past every tosh-level catch. Surface it via
                        // the same pretty diagnostic path used for
                        // ThrowSignalException so the REPL frame still gets
                        // a span, label, and source snippet.
                        exitCode = 1;
                        pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                            await CreateThrownValueDiagnosticAsync(
                                parseResult.SourceName,
                                parseResult.SourceText,
                                thrown,
                                cancellationToken));
                        break;
                    }
                    catch (Exception) when (exitCode == 0)
                    {
                        exitCode = 1;
                        throw; // re-throw immediately; outer finally still runs
                    }

                    if (!hasNext) break;

                    values.Add(enumerator.Current);
                    yield return enumerator.Current; // allowed: inside try-finally only, no catch
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            _scriptNameStack.Pop();
            _commandEventDepth--;

            // Raise CommandCompleted for top-level user input only
            if (isTopLevel && Runtime.Events.GetHandlers(BuiltInEventNames.CommandCompleted).Count > 0)
            {
                stopwatch?.Stop();
                _commandEventDepth++;
                try
                {
                    var sender = Runtime.EventSenderFactory?.Invoke()
                        ?? new ShellEventSender(Function: null, Script: null, Line: null);
                    var inputText = parseResult.SourceText.Trim();
                    var completedEvent = new CommandCompletedEvent(
                        inputText, exitCode, stopwatch?.Elapsed ?? TimeSpan.Zero,
                        values.Count > 0 ? values[^1] : null, sender);
                    await Runtime.Events.RaiseAsync(completedEvent, cancellationToken);
                }
                finally
                {
                    _commandEventDepth--;
                }
            }
        }

        if (parseResult.Statement is not ScriptStatementSyntax)
        {
            UpdateLastResultIfAny(values);
        }

        // Flush any values captured by a top-level `return` statement
        // (added to `values` inside the catch but not yet yielded —
        // `yield return` is illegal inside a try/catch in C#).
        if (pendingReturnValues is not null)
        {
            foreach (var value in pendingReturnValues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }

        pendingException?.Throw();
    }

    /// <param name="outputIsCaptured">
    /// Whether a consumer is waiting for this statement's value, so an external command must have
    /// its stdout piped rather than inherited (<c>TS-P1-30</c>). Only the pipeline arm can act on
    /// it; every other arm ignores it, which is why it rides here rather than in the pipeline
    /// syntax. Defaulted so the engine's hottest dispatch keeps its existing call shape — the
    /// single caller that sets it is the interpolation hole (<c>TS-P1-32</c>).
    /// </param>
    private IAsyncEnumerable<object?> EvaluateStatementAsync(
        string sourceName,
        string sourceText,
        StatementSyntax statement,
        CancellationToken cancellationToken,
        bool outputIsCaptured = false)
    {
        return statement switch
        {
            ScriptStatementSyntax script => EvaluateScriptStatementAsync(sourceName, sourceText, script, cancellationToken),
            PipelineStatementSyntax pipeline => pipeline.Pipeline.IsBackground
                ? EvaluateBackgroundPipelineAsync(sourceName, sourceText, pipeline, cancellationToken)
                : EvaluatePipelineWithRedirectionAsync(
                    sourceName,
                    sourceText,
                    pipeline.Pipeline,
                    cancellationToken,
                    outputIsCaptured: outputIsCaptured),
            VariableDeclarationStatementSyntax declaration => EvaluateVariableDeclarationAsync(sourceName, sourceText, declaration, cancellationToken),
            ScriptInputStatementSyntax input => EvaluateScriptInputStatementAsync(sourceName, sourceText, input),
            SubcommandStatementSyntax subcommand => EvaluateOrphanSubcommandStatementAsync(sourceName, sourceText, subcommand),
            DestructuringDeclarationStatementSyntax destructuring => EvaluateDestructuringDeclarationAsync(sourceName, sourceText, destructuring, cancellationToken),
            AllocStatementSyntax alloc => EvaluateAllocStatementAsync(sourceName, sourceText, alloc, cancellationToken),
            UsingStatementSyntax @using => EvaluateUsingStatementAsync(sourceName, sourceText, @using, cancellationToken),
            TypeAliasStatementSyntax typeAlias => EvaluateTypeAliasStatementAsync(sourceName, sourceText, typeAlias),
            RequireStatementSyntax require => EvaluateRequireStatementAsync(sourceName, sourceText, require, cancellationToken),
            BindStatementSyntax bind => EvaluateBindStatementAsync(sourceName, sourceText, bind, cancellationToken),
            ReturnStatementSyntax @return => EvaluateReturnStatementAsync(sourceName, sourceText, @return, cancellationToken),
            ThrowStatementSyntax @throw => EvaluateThrowStatementAsync(sourceName, sourceText, @throw, cancellationToken),
            BreakStatementSyntax @break => EvaluateBreakStatementAsync(@break),
            ContinueStatementSyntax @continue => EvaluateContinueStatementAsync(@continue),
            VariableAssignmentStatementSyntax assignment => EvaluateVariableAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            MemberAssignmentStatementSyntax assignment => EvaluateMemberAssignmentAsync(sourceName, sourceText, assignment, cancellationToken),
            TupleAssignmentStatementSyntax tupleAssign => EvaluateTupleAssignmentAsync(sourceName, sourceText, tupleAssign, cancellationToken),
            FunctionDefinitionStatementSyntax function => EvaluateFunctionDefinitionAsync(sourceName, sourceText, function, cancellationToken),
            RuneDefinitionStatementSyntax rune => EvaluateRuneDefinitionAsync(sourceName, sourceText, rune, cancellationToken),
            ClassDefinitionStatementSyntax @class => EvaluateClassDefinitionAsync(sourceName, sourceText, @class, cancellationToken),
            InterfaceDefinitionStatementSyntax @interface => EvaluateInterfaceDefinitionAsync(sourceName, sourceText, @interface, cancellationToken),
            UnionDefinitionStatementSyntax union => EvaluateUnionDefinitionAsync(sourceName, sourceText, union, cancellationToken),
            ModuleDefinitionStatementSyntax module => EvaluateModuleDefinitionAsync(sourceName, sourceText, module, cancellationToken),
            EnumDefinitionStatementSyntax @enum => EvaluateEnumDefinitionAsync(sourceName, sourceText, @enum, cancellationToken),
            RecordDefinitionStatementSyntax record => EvaluateRecordDefinitionAsync(sourceName, sourceText, record, cancellationToken),
            StructDefinitionStatementSyntax @struct => EvaluateStructDefinitionAsync(sourceName, sourceText, @struct, cancellationToken),
            RawStructDefinitionStatementSyntax rawStruct => EvaluateRawStructDefinitionAsync(sourceName, sourceText, rawStruct, cancellationToken),
            RawFunctionStatementSyntax rawFunction => EvaluateRawFunctionStatementAsync(sourceName, sourceText, rawFunction, cancellationToken),
            TraitDefinitionStatementSyntax trait => EvaluateTraitDefinitionAsync(sourceName, sourceText, trait, cancellationToken),
            EventDefinitionStatementSyntax @event => EvaluateEventDefinitionAsync(sourceName, sourceText, @event, cancellationToken),
            IfStatementSyntax @if => EvaluateIfStatementAsync(sourceName, sourceText, @if, cancellationToken),
            ForStatementSyntax @for => EvaluateForStatementAsync(sourceName, sourceText, @for, cancellationToken),
            WhileStatementSyntax @while => EvaluateWhileStatementAsync(sourceName, sourceText, @while, cancellationToken),
            UntilStatementSyntax until => EvaluateUntilStatementAsync(sourceName, sourceText, until, cancellationToken),
            TryStatementSyntax @try => EvaluateTryStatementAsync(sourceName, sourceText, @try, cancellationToken),
            SwitchStatementSyntax @switch => EvaluateSwitchStatementAsync(sourceName, sourceText, @switch, cancellationToken),
            DeferStatementSyntax => AsyncEnumerableExtensions.Empty<object?>(),
            _ => throw new InvalidOperationException($"Unsupported statement syntax: {statement.GetType().Name}."),
        };
    }

    /// <summary>
    /// Refuses a destructuring whose target count does not match a <em>tuple</em> source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TS-P2-59` asked for an arity mismatch to be named rather than absorbed. Applying that to
    /// every source would have contradicted the specification, which documents taking a prefix
    /// of an array on purpose: <c>var [first, second] = $fiveItems</c> is a worked example.
    /// </para>
    /// <para>
    /// The distinction that resolves it is a real one rather than a compromise. An array is a
    /// variable-length collection and reading the first two of it is a meaningful thing to ask
    /// for. A tuple has a fixed, declared shape, so naming two targets for three elements is a
    /// miscount every time — and absorbing it silently is what turns that miscount into a
    /// <c>null</c> reported three lines later.
    /// </para>
    /// </remarks>
    private static void EnsureTupleArityMatches(
        string sourceName,
        string sourceText,
        object? source,
        int valueCount,
        int targetCount,
        TextSpan span)
    {
        if (source is not ToshTuple || valueCount == targetCount)
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.tuple_assignment_arity_mismatch",
            Title: $"This destructuring has {targetCount} targets but the tuple has {valueCount} elements.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: valueCount < targetCount
                ? "there are not enough elements to fill every target"
                : "there are more elements than targets to hold them",
            Help: "give one target per element, or use '_' to discard one you do not want. "
                + "An array may be destructured into fewer targets; a tuple's shape is fixed."));
    }

    /// <summary>
    /// Spreads one value into positional elements, or answers <see langword="null"/> when it is
    /// not a positional shape at all.
    /// </summary>
    /// <remarks>
    /// `TS-P2-59`. This rule existed twice with two different answers. The declaring form,
    /// <c>var [a, b] = …</c>, accepted arrays, lists, tuples and any other enumerable; the
    /// assigning form, <c>(a, b) = …</c>, accepted arrays alone. So <c>var [a, b] = (1, 2)</c>
    /// bound 1 and 2, while <c>(a, b) = (1, 2)</c> bound the whole tuple to <c>a</c> and null to
    /// <c>b</c> — one language, two destructurings, different results, and no diagnostic to say
    /// so. A string is excluded because spreading text into characters is never what a
    /// destructuring meant.
    /// </remarks>
    private static object?[]? TryUnpackPositionalValue(object? value)
    {
        return value switch
        {
            object?[] array => array,
            Array typedArray => Enumerable.Range(0, typedArray.Length).Select(index => typedArray.GetValue(index)).ToArray(),
            IReadOnlyList<object?> list => list.ToArray(),
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
            _ => null,
        };
    }

    private async IAsyncEnumerable<object?> EvaluateTupleAssignmentAsync(
        string sourceName,
        string sourceText,
        TupleAssignmentStatementSyntax tupleAssign,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Evaluate the right-hand side
        var values = await AsyncEnumerableExtensions.ToListAsync(
            EvaluatePipelineAsync(sourceName, sourceText, tupleAssign.Value, cancellationToken, outputIsCaptured: true),
            cancellationToken);

        // One value that is itself positional spreads; anything else is taken as the values the
        // pipeline produced.
        IReadOnlyList<object?> unpacked = values.Count == 1 && TryUnpackPositionalValue(values[0]) is { } spread
            ? spread
            : values;

        EnsureTupleArityMatches(
            sourceName,
            sourceText,
            values.Count == 1 ? values[0] : null,
            unpacked.Count,
            tupleAssign.LeftNames.Count,
            tupleAssign.Span);

        // Prepare every target before mutating any of them. Tuple assignment
        // is simultaneous: an unknown/const target or failed annotation
        // conversion must not leave earlier targets partially updated.
        var preparedAssignments = new List<(string Name, VariableBinding Binding)>(tupleAssign.LeftNames.Count);
        var valueSpan = GetPipelineSpan(tupleAssign.Value) ?? tupleAssign.Span;

        for (int i = 0; i < tupleAssign.LeftNames.Count; i++)
        {
            var name = tupleAssign.LeftNames[i];
            var assignedValue = i < unpacked.Count ? unpacked[i] : null;

            EnsureBindingNameIsNotReserved(sourceName, sourceText, name, tupleAssign.Span, "reserved runtime namespace");
            var existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                name,
                tupleAssign.Span);
            preparedAssignments.Add((
                name,
                CreateAssignedVariableBinding(
                    sourceName,
                    sourceText,
                    name,
                    valueSpan,
                    existingBinding,
                    CloneStructAssignmentValue(assignedValue))));
        }

        foreach (var (name, binding) in preparedAssignments)
        {
            AssignExistingVariableBinding(sourceName, sourceText, name, tupleAssign.Span, binding);
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateScriptStatementAsync(
        string sourceName,
        string sourceText,
        ScriptStatementSyntax script,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PreRegisterTypeDefinitions(sourceName, sourceText, script.Statements);
        PreRegisterRefinementTypeAliases(sourceName, sourceText, script.Statements);

        if (script.Statements.Any(static s => s is SubcommandStatementSyntax))
        {
            await foreach (var value in EvaluateScriptWithSubcommandsAsync(sourceName, sourceText, script, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }
            yield break;
        }

        await BindScriptInputsAsync(
            sourceName,
            sourceText,
            script.Statements.OfType<ScriptInputStatementSyntax>().ToArray(),
            script.DocComment,
            cancellationToken);

        foreach (var statement in script.Statements)
        {
            // The top level runs its own statement loop rather than going through
            // ExecuteBlockAsync, so the check that stops a body on `exit` has to be made here
            // too — the first attempt at this fixed only the block executor and a plain script
            // carried on exactly as before.
            if (Runtime.ExitRequested)
            {
                break;
            }

            if (statement is ScriptInputStatementSyntax)
            {
                continue;
            }

            IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
                cancellationToken);

            if (ShouldSuppressStatementResults(statement, values))
            {
                values = Array.Empty<object?>();
            }

            UpdateLastResultIfAny(values);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
    }

    private async IAsyncEnumerable<object?> EvaluateScriptInputStatementAsync(
        string sourceName,
        string sourceText,
        ScriptInputStatementSyntax statement)
    {
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.script_inputs_must_be_top_level",
            Title: "Script input declarations must be top-level statements.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: statement.Span,
            Label: "move this input declaration to the top level of the script"));

#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<object?> EvaluateOrphanSubcommandStatementAsync(
        string sourceName,
        string sourceText,
        SubcommandStatementSyntax statement)
    {
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.subcommand_must_be_script_scoped",
            Title: $"Subcommand '{statement.Name}' must be declared at script or parent-subcommand scope.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: statement.Span,
            Label: "move this subcommand to the top level of the script or inside another subcommand"));

#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

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
            Runtime.RequestExit();
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
        IReadOnlyDictionary<string, string> documented)
    {
        var name = Path.GetFileName(sourceName);
        var output = Runtime.Output;

        // The file-level doc-block, which the parser separates from a declaration's own block by
        // requiring a blank line between them. Taking the first declaration's description instead
        // printed "Who to greet." as the summary of a script that greets people.
        if (scriptDoc?.Description is { Length: > 0 } summary && !string.IsNullOrWhiteSpace(summary))
        {
            await output.WriteLineAsync(summary.Trim());
            await output.WriteLineAsync();
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

        await output.WriteLineAsync(usage.ToString());

        // `TS-P2-67`. The script's own `@arg` / `@flag` tags describe its inputs, exactly as a
        // subcommand block's do. Without this a subcommand-free script showed its summary as the
        // description of every argument, so three documented arguments read the same sentence
        // three times — and the tags a reader had written were parsed and thrown away.
        await WriteScriptUsageSectionAsync(
            output,
            "Arguments",
            ApplyDocumentedDescriptions(argumentParameters, documented),
            isFlag: false);
        await WriteScriptUsageSectionAsync(
            output,
            "Options",
            ApplyDocumentedDescriptions(flagParameters, documented),
            isFlag: true);
    }

    private static async Task WriteScriptUsageSectionAsync(
        TextWriter output,
        string heading,
        IReadOnlyList<FunctionParameterSyntax> parameters,
        bool isFlag)
    {
        if (parameters.Count == 0)
        {
            return;
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync($"{heading}:");

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

            await output.WriteLineAsync(line.ToString());
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

    private static string FormatScriptArgumentForDiagnostic(object? value)
    {
        return value switch
        {
            null => "null",
            string text => text,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private async IAsyncEnumerable<object?> EvaluateBackgroundPipelineAsync(
        string sourceName,
        string sourceText,
        PipelineStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<object?>? initialInput = null;
        var processStages = new List<ShellJobProcessSpec>();
        var redirections = new List<ShellJobRedirectionSpec>();
        var stages = statement.Pipeline.Stages;
        var stageIndex = 0;

        // Resolve input redirection for background pipelines.
        if (statement.Pipeline.InputRedirection is { } bgInputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, bgInputRedirection.Source, cancellationToken);
            var inputPath = ResolveInputRedirectionPath(sourceName, sourceText, bgInputRedirection, inputTarget);
            initialInput = await AsyncEnumerableExtensions.ToListAsync(ReadLinesAsync(inputPath, cancellationToken), cancellationToken);
        }

        if (stages.Count > 0 && stages[0] is ExpressionPipelineStageSyntax initialExpression)
        {
            initialInput = await AsyncEnumerableExtensions.ToListAsync(
                ExecuteExpressionStageAsync(sourceName, sourceText, initialExpression, cancellationToken),
                cancellationToken);
            stageIndex = 1;
        }

        if (stageIndex >= stages.Count)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.background_pipeline_requires_command",
                Title: "Background pipelines require at least one external command stage.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "add an external command after the input expression"));
        }

        for (; stageIndex < stages.Count; stageIndex++)
        {
            var stage = stages[stageIndex];

            if (stage is not CommandSyntax commandSyntax)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.background_pipeline_not_supported",
                    Title: "Background jobs currently support an optional input expression followed by external command stages only.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: stage.Span,
                    Label: "this stage is not an external command"));
            }

            var command = ResolveCommand(sourceName, sourceText, commandSyntax);

            if (command is not ExternalProcessCommand externalCommand)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.background_command_must_be_external",
                    Title: "Background jobs currently require external command stages.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' is not being launched as a native process"));
            }

            IReadOnlyList<object?> arguments;

            try
            {
                var evaluatedArguments = await EvaluateCommandArgumentsAsync(sourceName, sourceText, command, commandSyntax, cancellationToken);
                arguments = ExpandCommandArguments(command, evaluatedArguments, sourceName, sourceText);
            }
            catch (ToshDiagnosticException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateCommandDiagnostic(sourceName, sourceText, commandSyntax, exception);
            }

            processStages.Add(new ShellJobProcessSpec(externalCommand.ResolvedPath, arguments));
        }

        if (statement.Pipeline.Redirections is { Count: > 0 })
        {
            foreach (var redirection in statement.Pipeline.Redirections)
            {
                var targetPath = await EvaluateArgumentAsync(sourceName, sourceText, redirection.Target, cancellationToken);
                var path = ResolveRedirectionTargetPath(sourceName, sourceText, redirection, targetPath);
                redirections.Add(new ShellJobRedirectionSpec(
                    path,
                    redirection.Stream switch
                    {
                        RedirectionStream.Output => ShellJobRedirectionStream.Output,
                        RedirectionStream.Error => ShellJobRedirectionStream.Error,
                        RedirectionStream.OutputThenError => ShellJobRedirectionStream.OutputThenError,
                        _ => ShellJobRedirectionStream.ErrorThenOutput,
                    },
                    redirection.Mode == RedirectionMode.Append
                        ? ShellJobRedirectionMode.Append
                        : ShellJobRedirectionMode.Truncate));
            }
        }

        var commandText = ExtractSourceSnippet(sourceText, statement.Span);
        var job = Runtime.RegisterJob(
            ShellJob.StartExternalPipeline(
                Runtime.AllocateJobId(),
                commandText,
                Runtime.CurrentDirectory,
                processStages,
                initialInput,
                redirections));

        Runtime.SetLastResult(job.ToInfo());
        Runtime.SetLastExitCode(0);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateVariableDeclarationAsync(
        string sourceName,
        string sourceText,
        VariableDeclarationStatementSyntax declaration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, declaration.Name, declaration.Span, "reserved runtime namespace");

        VariableBinding binding;
        if (declaration.Value is null)
        {
            binding = new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: true);
        }
        else
        {
            // Phase 3.2 — push the target type annotation so generic
            // calls in the initializer can seed bindings from it.
            var prevTarget = _targetTypeAnnotation.Value;
            _targetTypeAnnotation.Value = declaration.TypeName;
            try
            {
                binding = await EvaluateVariableBindingAsync(sourceName, sourceText, declaration.Value, cancellationToken);
            }
            finally
            {
                _targetTypeAnnotation.Value = prevTarget;
            }
        }

        // Struct copy-on-assign: clone struct instances to enforce value-type semantics
        if (binding.Value is ToshStructInstance structInstance)
        {
            binding = binding with { Value = structInstance.Clone() };
        }

        var declaredRefinement = declaration.TypeName is not null
            ? CreateRefinementAnnotation(sourceName, sourceText, declaration.Refinement)
            : null;

        if (declaration.TypeName is not null)
        {
            if (!binding.IsAllocatedOnly)
            {
                var valueSpan = GetPipelineSpan(declaration.Value) ?? declaration.Span;
                var converted = ConvertAnnotatedValue(
                    declaration.TypeName,
                    declaredRefinement,
                    binding.Value,
                    valueSpan,
                    sourceName,
                    sourceText,
                    declaration.Name);

                binding = binding with { Value = converted };
            }

            binding = binding with
            {
                DeclaredTypeName = declaration.TypeName,
                DeclaredRefinement = declaredRefinement,
            };
        }

        if (declaration.Name == "_" && TryGetVariableBinding("_", out _))
        {
            WriteWarning(
                code: "tosh.naming.shadowed_underscore",
                title: "Redeclaring '_' shadows an existing binding.",
                help: "Use a different name if this value matters, or hush this code: hush tosh.naming.shadowed_underscore",
                category: ToshDiagnosticCategory.Naming,
                sourceName: sourceName,
                line: LineFromOffset(sourceText, declaration.Span.Start));
        }

        if (declaration.IsConst)
        {
            binding = binding with { IsConst = true };
        }

        DeclareVariable(
            declaration.Name,
            binding,
            declaration.Modifier,
            sourceName,
            sourceText,
            declaration.Span);
        yield break;
    }

    /// <summary>
    /// True for a destructuring target that discards rather than binds. Only bare
    /// <c>_</c> — a name that merely *starts* with an underscore is an ordinary
    /// identifier, as it is in every language that has this convention.
    /// </summary>
    private static bool IsDiscardTarget(string name) =>
        string.Equals(name, "_", StringComparison.Ordinal);

    private async IAsyncEnumerable<object?> EvaluateDestructuringDeclarationAsync(
        string sourceName,
        string sourceText,
        DestructuringDeclarationStatementSyntax destructuring,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var binding = await EvaluateVariableBindingAsync(sourceName, sourceText, destructuring.Value, cancellationToken);
        var value = binding.Value;

        switch (destructuring.Pattern)
        {
            case ArrayDestructuringPatternSyntax arrayPattern:
                {
                    var array = TryUnpackPositionalValue(value);

                    if (array is null)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.destructuring_requires_array",
                            Title: "Array destructuring requires an array or list value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: destructuring.Span,
                            Label: $"got {(value?.GetType().Name ?? "null")} instead of an array"));
                    }

                    EnsureTupleArityMatches(
                        sourceName,
                        sourceText,
                        value,
                        array.Length,
                        arrayPattern.Names.Count,
                        destructuring.Span);

                    for (var i = 0; i < arrayPattern.Names.Count; i++)
                    {
                        var name = arrayPattern.Names[i];

                        // `_` discards its element — it must not create a binding and must
                        // not overwrite an existing one (TS-P1-11). Before this,
                        // `var [a, _, c] = [1, 2, 3]` left `$_` holding 2, clobbering
                        // whatever `_` meant beforehand — and `_` is the current pipeline
                        // item, so a destructuring inside a predicate silently changed it.
                        if (IsDiscardTarget(name))
                        {
                            continue;
                        }

                        var elementValue = i < array.Length ? array[i] : null;
                        DeclareVariable(
                            name,
                            new VariableBinding(
                                elementValue,
                                ReplayAsPipeline: false,
                                IsAllocatedOnly: false,
                                IsConst: destructuring.IsConst),
                            destructuring.Modifier);
                    }

                    break;
                }

            case RecordDestructuringPatternSyntax recordPattern:
                {
                    IDictionary<string, object?>? dict;
                    if (value is IDictionary<string, object?> dictionary)
                    {
                        dict = dictionary;
                    }
                    else if (value is IShellRecordObject record)
                    {
                        dict = (await record.GetMembersAsync(
                                includeHidden: false,
                                cancellationToken))
                            .ToDictionary(
                                member => member.Key,
                                member => member.Value,
                                StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        dict = null;
                    }

                    if (dict is null)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.destructuring_requires_record",
                            Title: "Record destructuring requires a record or dictionary value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: destructuring.Span,
                            Label: $"got {(value?.GetType().Name ?? "null")} instead of a record"));
                    }

                    foreach (var name in recordPattern.Names)
                    {
                        // Same discard rule as array destructuring above (TS-P1-11).
                        if (IsDiscardTarget(name))
                        {
                            continue;
                        }

                        dict.TryGetValue(name, out var memberValue);
                        DeclareVariable(
                            name,
                            new VariableBinding(
                                memberValue,
                                ReplayAsPipeline: false,
                                IsAllocatedOnly: false,
                                IsConst: destructuring.IsConst),
                            destructuring.Modifier);
                    }

                    break;
                }
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateAllocStatementAsync(
        string sourceName,
        string sourceText,
        AllocStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Name, statement.Span, "reserved runtime namespace");

        object? allocationSpecification;

        if (TryGetSimpleAllocationTypeName(statement.Value, out var typeName))
        {
            allocationSpecification = typeName;
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            allocationSpecification = raw.Value;
        }
        else
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);

            if (values.Count != 1)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.alloc_requires_single_value",
                    Title: "Allocated buffer declarations require exactly one size or type value.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: values.Count == 0
                        ? "this allocation expression produced no values"
                        : $"this allocation expression produced {values.Count} values",
                    Help: "use a byte count or a single interop type name here."));
            }

            allocationSpecification = values[0];
        }

        var context = new CommandContext(Runtime, AsyncEnumerableExtensions.Empty<object?>(), [allocationSpecification], cancellationToken, ScopedTypeResolver: CreateScopedTypeResolver(), BlockExecutor: _ownBlockExecutor, ScopedCommands: CreateScopedCommandView(), ShellTypes: this);
        var size = NativeCommandUtilities.ResolveAllocationSize(context, allocationSpecification, 0);

        if (size < 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.alloc_negative_size",
                Title: "Allocated buffers cannot have a negative size.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "use zero or a positive size"));
        }

        DeclareVariable(statement.Name, ToVariableBinding(new NativeBuffer(size)), statement.Modifier);
        yield break;
    }

    private static bool TryGetSimpleAllocationTypeName(PipelineSyntax pipeline, out string typeName)
    {
        typeName = string.Empty;
        var redirections = pipeline.Redirections ?? Array.Empty<RedirectionSyntax>();

        if (redirections.Count != 0 || pipeline.IsBackground || pipeline.Stages.Count != 1)
        {
            return false;
        }

        if (pipeline.Stages[0] is CommandSyntax command && command.Arguments.Count == 0)
        {
            typeName = command.Name;
            return true;
        }

        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateUsingStatementAsync(
        string sourceName,
        string sourceText,
        UsingStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statement.Modifier == DeclarationModifier.Export)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.using_export_not_supported",
                Title: "'using' cannot be exported.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "use 'global using ...' if you want the import to persist outside the current scope"));
        }

        if (statement.Alias is not null)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Alias, statement.Span, "reserved runtime namespace");
        }

        if (statement.Modifier == DeclarationModifier.Global || (statement.Modifier == DeclarationModifier.Default && _scopes.Count == 0))
        {
            if (Runtime.TypeResolver is not IImportingTypeResolver importingResolver)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.using_not_supported",
                    Title: "This runtime does not support 'using' statements.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: "the active type resolver cannot record imports or aliases"));
            }

            if (statement.Alias is null)
            {
                importingResolver.AddUsing(statement.Target);
            }
            else
            {
                importingResolver.AddAlias(statement.Alias, statement.Target);
            }

            yield break;
        }

        if (_scopes.Count == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.shy_using_requires_scope",
                Title: "Shy using statements require a function, block, or module scope.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "remove 'shy' or place this using inside a scoped block"));
        }

        var scope = _scopes.Peek();

        if (statement.Alias is null)
        {
            scope.TypeImports.Add(statement.Target);
        }
        else
        {
            scope.TypeAliases[statement.Alias] = statement.Target;
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateTypeAliasStatementAsync(
        string sourceName,
        string sourceText,
        TypeAliasStatementSyntax statement)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.Name, statement.Span, "reserved runtime namespace");
        DeclareRefinementType(
            CreateRefinementTypeDefinition(sourceName, sourceText, statement),
            statement.Modifier,
            sourceName,
            sourceText,
            statement.Span,
            allowTypeNameConflict: false);

        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Registers a type-alias declaration from source text without executing
    /// it as a script statement. Used by compiled CLR emission to preserve
    /// refinement/generic alias semantics while avoiding source replay.
    /// </summary>
    public void RegisterCompiledTypeAliasFromSource(string sourceName, string aliasSourceText)
    {
        var parse = ToshParser.Parse(aliasSourceText, sourceName);
        if (parse.Diagnostics.Count > 0 || parse.Statement is not TypeAliasStatementSyntax alias)
        {
            var diagnostic = parse.Diagnostics.FirstOrDefault();
            throw new InvalidOperationException(
                $"Compiled alias registration failed: {diagnostic?.Title ?? "not a valid type alias declaration."}");
        }

        EnsureBindingNameIsNotReserved(sourceName, aliasSourceText, alias.Name, alias.Span, "reserved runtime namespace");
        DeclareRefinementType(
            CreateRefinementTypeDefinition(sourceName, aliasSourceText, alias),
            alias.Modifier,
            sourceName,
            aliasSourceText,
            alias.Span,
            allowTypeNameConflict: true);
    }

    /// <summary>
    /// Registers a rune (macro) declaration from a source slice without executing it
    /// through the interpreter pipeline. Used by compiled CLR emission to preserve rune
    /// semantics while avoiding Tier-3 source replay.
    /// </summary>
    public void RegisterRuneFromSource(string sourceName, string runeDeclarationSlice)
    {
        var parse = ToshParser.Parse(runeDeclarationSlice, sourceName);
        if (parse.Diagnostics.Count > 0 || parse.Statement is not RuneDefinitionStatementSyntax rune)
        {
            var diagnostic = parse.Diagnostics.FirstOrDefault();
            throw new InvalidOperationException(
                $"Compiled rune registration failed: {diagnostic?.Title ?? "not a valid rune declaration."}");
        }

        EnsureBindingNameIsNotReserved(sourceName, runeDeclarationSlice, rune.Name, rune.Span, "reserved runtime namespace");

        var duplicateParameters = rune.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_rune_parameter",
                Title: $"Rune '{rune.Name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: runeDeclarationSlice,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        var definition = new RuneDefinition(
            rune.Name,
            rune.Parameters.Select(p => new RuneParameterDefinition(p.Name, p.Span)).ToArray(),
            rune.Body,
            rune.IsSealed,
            rune.IsFixed,
            sourceName,
            runeDeclarationSlice,
            rune.Span,
            CapturedScopes: null,
            rune.DocComment is not null
                ? DocComment.Parse(new[] { new SyntaxToken(SyntaxTokenKind.DocComment, 0, rune.DocComment.ToString() ?? "") })
                : null);

        DeclareCommand(new RuneCommand(definition), rune.Modifier);
    }

    /// <summary>
    /// Loads and imports a required module from a compiled assembly context, bypassing
    /// Tier-3 source replay. Called by compiled assemblies at runtime to satisfy
    /// <c>require</c> statements targeting external .tosh scripts or assemblies.
    /// </summary>
    public void RequireModuleFromCompiled(
        string target,
        string[] importedNames,
        string[] importedAliases,
        string resolveFrom)
    {
        var requirement = ResolveRequirement(target, resolveFrom);

        switch (requirement.Kind)
        {
            case RequireTargetKind.Script:
                {
                    if (!_requiredScripts.TryGetValue(requirement.CacheKey, out var artifact))
                    {
                        if (!_currentlyRequiring.Add(requirement.CacheKey))
                        {
                            throw new InvalidOperationException(
                                $"Circular require detected: '{requirement.CacheKey}' is already being loaded.");
                        }

                        try
                        {
                            var moduleSource = File.ReadAllText(requirement.ResolvedPath);
                            artifact = ExecuteRequiredScriptAsync(moduleSource, requirement.ResolvedPath, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            _requiredScripts[requirement.CacheKey] = artifact;
                        }
                        finally
                        {
                            _currentlyRequiring.Remove(requirement.CacheKey);
                        }
                    }

                    ImportRequiredArtifact(artifact, importedNames, importedAliases);
                    break;
                }

            case RequireTargetKind.Assembly:
                {
                    if (importedNames.Length > 0)
                    {
                        throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                    }

                    if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                    {
                        break;
                    }

                    AssemblyLoadContext.Default.LoadFromAssemblyPath(requirement.ResolvedPath);
                    break;
                }

            case RequireTargetKind.Project:
                {
                    if (importedNames.Length > 0)
                    {
                        throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                    }

                    if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                    {
                        break;
                    }

                    var assemblyPath = BuildProjectAndResolveAssemblyPathAsync(requirement.ResolvedPath, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported require target kind '{requirement.Kind}'.");
        }
    }

    private void ImportRequiredArtifact(
        ToshRequiredScriptArtifact artifact,
        string[] importedNames,
        string[] importedAliases)
    {
        if (importedNames.Length == 0)
        {
            foreach (var (name, value) in artifact.Exports.Variables)
                DeclareVariable(name, ToVariableBinding(value), DeclarationModifier.Default);

            foreach (var (_, command) in artifact.Exports.Commands)
                DeclareCommand(command, DeclarationModifier.Default);

            foreach (var (name, type) in artifact.Exports.Types)
                DeclareType(name, type, DeclarationModifier.Default, artifact.Path);

            foreach (var (_, refinementType) in artifact.Exports.RefinementTypes)
                DeclareRefinementType(refinementType, DeclarationModifier.Default, artifact.Path);

            foreach (var (name, module) in artifact.Exports.Modules)
            {
                if (module is not null)
                    DeclareModule(name, module, DeclarationModifier.Default);
            }

            return;
        }

        for (var i = 0; i < importedNames.Length; i++)
        {
            var name = importedNames[i];
            var bindingName = (i < importedAliases.Length && !string.IsNullOrEmpty(importedAliases[i]))
                ? importedAliases[i]
                : name;

            // A dotted import name walks into nested modules, so a library organised
            // as `module Outer { module Inner { ... } }` can be imported as
            // `require Outer.Inner from "..." as Alias`. Without this only the
            // outermost name resolved, and the nested form reported the whole dotted
            // string as a missing export — accurate but unhelpful, since `Outer` was
            // there and `Inner` was inside it.
            if (name.Contains('.', StringComparison.Ordinal) &&
                TryResolveNestedExport(artifact, name, bindingName, DeclarationModifier.Default))
            {
                continue;
            }

            if (artifact.Exports.Modules.TryGetValue(name, out var module))
            {
                if (module is null)
                    throw new InvalidOperationException($"Export '{name}' in '{artifact.Path}' was null.");
                DeclareModule(bindingName, module, DeclarationModifier.Default);
                continue;
            }

            if (artifact.Exports.Types.TryGetValue(name, out var type))
            {
                DeclareType(bindingName, type, DeclarationModifier.Default, artifact.Path);
                continue;
            }

            if (artifact.Exports.RefinementTypes.TryGetValue(name, out var refinementType))
            {
                DeclareRefinementType(refinementType with { Name = bindingName }, DeclarationModifier.Default, artifact.Path);
                continue;
            }

            if (artifact.Exports.Commands.TryGetValue(name, out var command))
            {
                DeclareCommand(
                    string.Equals(bindingName, command.Name, StringComparison.Ordinal)
                        ? command
                        : new RenamedCommand(bindingName, command),
                    DeclarationModifier.Default);
                continue;
            }

            if (artifact.Exports.Variables.TryGetValue(name, out var value))
            {
                DeclareVariable(bindingName, ToVariableBinding(value), DeclarationModifier.Default);
                continue;
            }

            throw new InvalidOperationException($"Export '{name}' was not found in '{artifact.Path}'.");
        }
    }

    /// <summary>
    /// Resolves a dotted import name such as <c>Outer.Inner</c> by walking module
    /// exports, and declares whatever the final segment names.
    /// </summary>
    /// <remarks>
    /// Every segment but the last must be a module; the last may be anything a flat
    /// import can bring in. When the caller supplied no <c>as</c> alias the binding
    /// takes the *final* segment, because the dotted path is not itself a usable
    /// identifier.
    /// </remarks>
    private bool TryResolveNestedExport(
        ToshRequiredScriptArtifact artifact,
        string name,
        string bindingName,
        DeclarationModifier modifier)
    {
        var segments = name.Split('.', StringSplitOptions.None);

        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        if (!artifact.Exports.Modules.TryGetValue(segments[0], out var root) ||
            root is not ToshModuleObject current)
        {
            return false;
        }

        // Walk to the module holding the final segment.
        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                next is not ToshModuleObject nested)
            {
                return false;
            }

            current = nested;
        }

        var leaf = segments[^1];
        var binding = string.Equals(bindingName, name, StringComparison.Ordinal) ? leaf : bindingName;
        var exports = current.ExportTable;

        if (exports.Modules.TryGetValue(leaf, out var leafModule) && leafModule is not null)
        {
            DeclareModule(binding, leafModule, modifier);
            return true;
        }

        if (exports.Types.TryGetValue(leaf, out var leafType))
        {
            DeclareType(binding, leafType, modifier, artifact.Path);
            return true;
        }

        if (exports.RefinementTypes.TryGetValue(leaf, out var leafRefinement))
        {
            DeclareRefinementType(
                leafRefinement with { Name = binding },
                modifier,
                artifact.Path);
            return true;
        }

        if (exports.Commands.TryGetValue(leaf, out var leafCommand))
        {
            DeclareCommand(
                string.Equals(binding, leafCommand.Name, StringComparison.Ordinal)
                    ? leafCommand
                    : new RenamedCommand(binding, leafCommand),
                modifier);
            return true;
        }

        if (exports.Variables.TryGetValue(leaf, out var leafValue))
        {
            DeclareVariable(binding, ToVariableBinding(leafValue), modifier);
            return true;
        }

        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateRequireStatementAsync(
        string sourceName,
        string sourceText,
        RequireStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (statement.IsNative)
            {
                if (statement.Imports.Count > 0)
                {
                    throw new InvalidOperationException("Selective require imports are not supported for native libraries.");
                }

                var moduleName = statement.Alias ?? GetDefaultNativeModuleName(statement.Target);
                EnsureNativeModuleAvailable(sourceName, statement.Target, moduleName, statement.Modifier);
            }
            else
            {
                var requirement = ResolveRequirement(statement.Target, GetExecutionDirectory(sourceName));

                switch (requirement.Kind)
                {
                    case RequireTargetKind.Script:
                        {
                            if (!_requiredScripts.TryGetValue(requirement.CacheKey, out var artifact))
                            {
                                if (!_currentlyRequiring.Add(requirement.CacheKey))
                                {
                                    throw new InvalidOperationException(
                                        $"Circular require detected: '{requirement.CacheKey}' is already being loaded.");
                                }

                                try
                                {
                                    var moduleSource = await File.ReadAllTextAsync(requirement.ResolvedPath, cancellationToken);
                                    artifact = await ExecuteRequiredScriptAsync(moduleSource, requirement.ResolvedPath, cancellationToken);
                                    _requiredScripts[requirement.CacheKey] = artifact;
                                }
                                finally
                                {
                                    _currentlyRequiring.Remove(requirement.CacheKey);
                                }
                            }

                            ImportRequiredArtifact(sourceName, sourceText, artifact, statement);
                            break;
                        }

                    case RequireTargetKind.Assembly:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            AssemblyLoadContext.Default.LoadFromAssemblyPath(requirement.ResolvedPath);
                            break;
                        }

                    case RequireTargetKind.Project:
                        {
                            if (statement.Imports.Count > 0)
                            {
                                throw new InvalidOperationException("Selective require imports are only supported for .tosh files.");
                            }

                            if (!Runtime.LoadedModules.Add(requirement.CacheKey))
                            {
                                break;
                            }

                            var assemblyPath = await BuildProjectAndResolveAssemblyPathAsync(requirement.ResolvedPath, cancellationToken);
                            AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unsupported require target kind '{requirement.Kind}'.");
                }
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
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.require_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"while requiring '{statement.Target}'"));
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateBindStatementAsync(
        string sourceName,
        string sourceText,
        BindStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statement.NativeTarget is not null)
        {
            EnsureNativeModuleAvailable(sourceName, statement.NativeTarget, statement.ModuleName, DeclarationModifier.Default);
        }

        if (!TryGetModule(statement.ModuleName, out var module) ||
            module.NativeLibraryBinding is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.bind_target_not_native_module",
                Title: $"'{statement.ModuleName}' is not a native library module.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"load a native library with 'require native ... as {statement.ModuleName}' first"));
        }

        foreach (var function in statement.Functions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            module.SetCommand(BuildNativeFunctionCommand(
                sourceName, sourceText, statement.ModuleName, module.NativeLibraryBinding, function));
        }

        yield break;
    }

    /// <summary>
    /// Declares a top-level <c>raw func … from "lib"</c> as a command in the
    /// enclosing scope, so it is callable by the name it was given rather than
    /// through a module the caller never asked for.
    /// </summary>
    private async IAsyncEnumerable<object?> EvaluateRawFunctionStatementAsync(
        string sourceName,
        string sourceText,
        RawFunctionStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = statement.Binding.NativeTarget;

        if (string.IsNullOrWhiteSpace(target))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_func_requires_library",
                Title: $"Raw function '{statement.Binding.Name}' needs a library.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: "write 'from \"libc.so.6\"' after the signature"));
        }

        // The backing module only exists to own the loaded handle; the command
        // itself is what gets declared.
        var moduleName = $"__raw_{statement.Binding.Name}";
        EnsureNativeModuleAvailable(sourceName, target!, moduleName, DeclarationModifier.Default);

        if (!TryGetModule(moduleName, out var module) || module.NativeLibraryBinding is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.bind_target_not_native_module",
                Title: $"'{target}' could not be loaded as a native library.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: statement.Span,
                Label: $"while binding '{statement.Binding.Name}'"));
        }

        DeclareCommand(
            BuildNativeFunctionCommand(
                sourceName, sourceText, statement.Binding.Name, module.NativeLibraryBinding, statement.Binding),
            statement.Modifier);

        await ValueTask.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Declares the <c>bind native</c> blocks written inside a class body,
    /// attaching each bound function as a static member of the class.
    ///
    /// The library is loaded under a module name derived from the class so two
    /// classes binding the same library do not collide, but the module itself is
    /// incidental — what the user sees is <c>SystemInfo.sysinfo()</c>.
    /// </summary>
    private async ValueTask BindNativeClassMembersAsync(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        ToshClassDefinition definition,
        CancellationToken cancellationToken)
    {
        foreach (var member in @class.Members.OfType<ClassBindMemberSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var statement = member.Bind;

            // Default, not Shy: the backing module is an implementation detail of
            // loading the library, and a class body is not a scope a shy module
            // could live in. Hiding is expressed on the members instead.
            if (statement.NativeTarget is not null)
            {
                EnsureNativeModuleAvailable(sourceName, statement.NativeTarget, statement.ModuleName, DeclarationModifier.Default);
            }

            if (!TryGetModule(statement.ModuleName, out var module) || module.NativeLibraryBinding is null)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.bind_target_not_native_module",
                    Title: $"'{statement.ModuleName}' is not a native library module.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: $"write 'bind native \"<library>\" {{ ... }}' inside class '{@class.Name}'"));
            }

            foreach (var function in statement.Functions)
            {
                definition.SetNativeMember(
                    BuildNativeFunctionCommand(
                        sourceName, sourceText, @class.Name, module.NativeLibraryBinding, function),
                    member.IsShy);
            }
        }

        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves one native function signature into an invocable command. Shared
    /// by the module and class paths so a binding behaves identically wherever
    /// it is written.
    /// </summary>
    private NativeFunctionCommand BuildNativeFunctionCommand(
        string sourceName,
        string sourceText,
        string ownerName,
        NativeLibraryBinding binding,
        NativeFunctionBindingSyntax function)
    {
        {
            var parameters = new List<NativeFunctionParameterDefinition>(function.Parameters.Count);

            foreach (var parameter in function.Parameters)
            {
                // Out-array parameters: `buffer[n]` for C's output-string idiom,
                // or a typed `T[n]` such as getloadavg's `double[3]`.
                if (TryParseOutArrayParameter(parameter.TypeName, out var elementName, out var arrayLength))
                {
                    if (parameter.PassingMode != NativeParameterPassingMode.Out)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.native_buffer_requires_out",
                            Title: $"Parameter '{parameter.Name}' must be declared 'out' to use a buffer.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: parameter.Span,
                            Label: $"write 'out {parameter.Name}: {parameter.TypeName}'",
                            Help: "A buffer is memory the callee writes into; it carries no value in."));
                    }

                    var isCString = elementName is null;

                    var elementType = isCString
                        ? typeof(byte)
                        : ResolveNativeInteropParameterType(
                            elementName, NativeParameterPassingMode.In, sourceName, sourceText, parameter.Span,
                            $"parameter '{parameter.Name}'");

                    parameters.Add(new NativeFunctionParameterDefinition(
                        parameter.Name,
                        parameter.TypeName ?? string.Empty,
                        elementType.MakeArrayType(),
                        NativeParameterPassingMode.Out,
                        InlineBufferLength: arrayLength,
                        DecodeAsCString: isCString));

                    // Only `buffer[n]` carries the implicit length argument; a
                    // typed T[n] leaves its count to whatever the signature says.
                    if (isCString)
                    {
                        parameters.Add(new NativeFunctionParameterDefinition(
                            parameter.Name + "__length",
                            "nuint",
                            typeof(UIntPtr),
                            NativeParameterPassingMode.In,
                            IsSynthesizedLength: true));
                    }

                    continue;
                }

                parameters.Add(new NativeFunctionParameterDefinition(
                    parameter.Name,
                    parameter.TypeName ?? string.Empty,
                    ResolveNativeInteropParameterType(parameter.TypeName, parameter.PassingMode, sourceName, sourceText, parameter.Span, $"parameter '{parameter.Name}'"),
                    parameter.PassingMode));
            }
            var returnType = ResolveNativeInteropReturnType(function.ReturnTypeName, sourceName, sourceText, function.Span);
            var callingConvention = ResolveNativeCallingConvention(function.CallingConventionName, sourceName, sourceText, function.Span);

            // An explicit `where (…)` overrides any named convention on the
            // return type — you cannot mean both at once.
            var successPredicate = CreateNativeSuccessPredicate(sourceName, sourceText, function);

            if (successPredicate is not null)
            {
                returnType = returnType with { Convention = NativeErrorConvention.Predicate };
            }

            return new NativeFunctionCommand(
                ownerName,
                function.Name,
                function.SymbolName,
                binding,
                parameters,
                returnType,
                callingConvention,
                successPredicate);
        }
    }

    private void EnsureNativeModuleAvailable(
        string sourceName,
        string nativeTarget,
        string moduleName,
        DeclarationModifier modifier)
    {
        var requirement = ResolveNativeRequirement(nativeTarget, GetExecutionDirectory(sourceName));

        if (!_requiredNativeLibraries.TryGetValue(requirement.CacheKey, out var binding))
        {
            var handle = NativeLibrary.Load(requirement.ResolvedPath);
            binding = new NativeLibraryBinding(
                requirement.ResolvedPath,
                requirement.CacheKey,
                handle,
                new ModuleExportTable());
            _requiredNativeLibraries[requirement.CacheKey] = binding;
        }

        var module = new ToshModuleObject(this, moduleName, binding.Exports)
        {
            NativeLibraryBinding = binding,
        };
        DeclareModule(moduleName, module, modifier);
    }

    private IAsyncEnumerable<object?> EvaluateReturnStatementAsync(
        string sourceName,
        string sourceText,
        ReturnStatementSyntax statement,
        CancellationToken cancellationToken)
    {
        return EvaluateReturnStatementCoreAsync(sourceName, sourceText, statement, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateReturnStatementCoreAsync(
        string sourceName,
        string sourceText,
        ReturnStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<object?> values;

        if (statement.Value is null)
        {
            values = Array.Empty<object?>();
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            values = [raw.Value];
        }
        else
        {
            values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);
        }

        throw new ReturnSignalException(statement.Span, values);
#pragma warning disable CS0162 // required by async-iterator contract
        yield break;
#pragma warning restore CS0162
    }

    private IAsyncEnumerable<object?> EvaluateBreakStatementAsync(BreakStatementSyntax statement)
    {
        throw new BreakSignalException(statement.Span);
    }

    private IAsyncEnumerable<object?> EvaluateContinueStatementAsync(ContinueStatementSyntax statement)
    {
        throw new ContinueSignalException(statement.Span);
    }

    private IAsyncEnumerable<object?> EvaluateThrowStatementAsync(
        string sourceName,
        string sourceText,
        ThrowStatementSyntax statement,
        CancellationToken cancellationToken)
    {
        return EvaluateThrowStatementCoreAsync(sourceName, sourceText, statement, cancellationToken);
    }

    private async IAsyncEnumerable<object?> EvaluateThrowStatementCoreAsync(
        string sourceName,
        string sourceText,
        ThrowStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        object? value;

        if (statement.Value is null)
        {
            value = new CommandFailure("An error was thrown.");
        }
        else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, statement.Value, cancellationToken) is { Matched: true } raw)
        {
            value = raw.Value;
        }
        else
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, statement.Value, cancellationToken, outputIsCaptured: true),
                cancellationToken);
            value = values.Count switch
            {
                0 => new CommandFailure("An error was thrown."),
                1 => values[0],
                _ => values.ToArray(),
            };
        }

        await RaiseThrownValueAsync(statement.Span, value, cancellationToken);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private async IAsyncEnumerable<object?> EvaluateVariableAssignmentAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EvaluateVariableAssignmentCoreAsync(sourceName, sourceText, assignment, cancellationToken);
        yield break;
    }

    private async Task EvaluateVariableAssignmentCoreAsync(
        string sourceName,
        string sourceText,
        VariableAssignmentStatementSyntax assignment,
        CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, assignment.Name, assignment.Span, "reserved runtime namespace");

        VariableBinding existingBinding;
        VariableBinding value;
        if (assignment.Operator == "??=")
        {
            existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                assignment.Name,
                assignment.Span);

            if (!existingBinding.IsAllocatedOnly && existingBinding.Value is not null)
            {
                return;
            }

            value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
        }
        else
        {
            // Preserve the established order for every other assignment:
            // evaluate the RHS before resolving the mutable target.
            value = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
            existingBinding = RequireMutableVariableBinding(
                sourceName,
                sourceText,
                assignment.Name,
                assignment.Span);
        }

        var incomingValue = CloneStructAssignmentValue(value.Value);
        object? assignedValue = incomingValue;

        if (assignment.Operator is not "=" and not "??=")
        {
            if (existingBinding.IsAllocatedOnly)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.compound_assignment_requires_value",
                    Title: $"Variable '{assignment.Name}' does not have a value yet.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Span,
                    Label: $"assign '{assignment.Name}' before using '{assignment.Operator}'"));
            }

            assignedValue = await ApplyCompoundAssignmentAsync(
                sourceName,
                sourceText,
                assignment.Span,
                existingBinding.Value,
                assignment.Operator,
                incomingValue,
                cancellationToken);
        }

        var assignedBinding = CreateAssignedVariableBinding(
            sourceName,
            sourceText,
            assignment.Name,
            GetPipelineSpan(assignment.Value) ?? assignment.Span,
            existingBinding,
            assignedValue);
        AssignExistingVariableBinding(
            sourceName,
            sourceText,
            assignment.Name,
            assignment.Span,
            assignedBinding);
    }

    private async IAsyncEnumerable<object?> EvaluateMemberAssignmentAsync(
        string sourceName,
        string sourceText,
        MemberAssignmentStatementSyntax assignment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Preserve the established RHS-first order for ordinary assignment,
        // but defer a null-coalescing RHS until the existing target value has
        // actually been observed as null.
        VariableBinding? binding = null;
        if (assignment.Operator != "??=")
        {
            binding = await EvaluateVariableBindingAsync(sourceName, sourceText, assignment.Value, cancellationToken);
        }

        // Path 1 — terminal index access: `$root[..].chain[..]["key"] = value`.
        // We evaluate the indexer's own target (everything but the final
        // `[index]`) into an object, then route through SetIndexedValue.
        if (assignment.Target is IndexAccessArgumentSyntax idx)
        {
            var indexedTarget = await EvaluateArgumentAsync(sourceName, sourceText, idx.Target, cancellationToken);
            var indexValue = await EvaluateArgumentAsync(sourceName, sourceText, idx.Index, cancellationToken);

            if (assignment.Operator == "??=")
            {
                try
                {
                    var currentValue = await ShellIndexingUtilities.GetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        idx.LookupKind,
                        cancellationToken);
                    if (currentValue is not null)
                    {
                        yield break;
                    }
                }
                catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.index_assignment_failed",
                        Title: exception.Message,
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: assignment.Target.Span,
                        Label: "while reading this index for null-coalescing assignment"));
                }

                var coalescedBinding = await EvaluateVariableBindingAsync(
                    sourceName,
                    sourceText,
                    assignment.Value,
                    cancellationToken);

                try
                {
                    await ShellIndexingUtilities.SetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        coalescedBinding.Value,
                        idx.LookupKind,
                        cancellationToken);
                }
                catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.index_assignment_failed",
                        Title: exception.Message,
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: assignment.Target.Span,
                        Label: "while assigning to this index"));
                }

                yield break;
            }

            var newValue = binding!.Value;
            try
            {
                if (assignment.Operator != "=")
                {
                    var currentValue = await ShellIndexingUtilities.GetIndexedValueAsync(
                        indexedTarget,
                        indexValue,
                        idx.LookupKind,
                        cancellationToken);
                    newValue = await ApplyCompoundAssignmentAsync(
                        sourceName,
                        sourceText,
                        assignment.Span,
                        currentValue,
                        assignment.Operator,
                        binding.Value,
                        cancellationToken);
                }

                await ShellIndexingUtilities.SetIndexedValueAsync(
                    indexedTarget,
                    indexValue,
                    newValue,
                    idx.LookupKind,
                    cancellationToken);
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.index_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while assigning to this index"));
            }

            yield break;
        }

        object? target;
        string memberPath;

        // `TS-P2-51`. A `$`-less dotted target — `B.S`, `Outer.Inner.V`, `System.Console.Title`
        // — is a static member path. The parser hands the whole path over unresolved because
        // only the engine can say where the type ends and the members begin.
        if (TryDecomposeStaticAssignmentTarget(assignment.Target, out var staticPath))
        {
            (target, memberPath) = ResolveStaticAssignmentTarget(
                sourceName,
                sourceText,
                staticPath,
                assignment.Target.Span);
        }
        else if (TryDecomposeMemberAssignmentTarget(assignment.Target, out var rootExpression, out memberPath))
        {
            target = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);
        }
        else
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.invalid_member_assignment_target",
                Title: "Assignments to members require a member path target.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "use a target like '$person.Name'"));
        }

        if (assignment.Operator == "??=")
        {
            try
            {
                var currentValue = await Runtime.ObjectAccessor.GetValueAsync(
                    target,
                    memberPath,
                    cancellationToken);
                if (currentValue is not null)
                {
                    yield break;
                }
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.member_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while reading this member for null-coalescing assignment"));
            }

            var coalescedBinding = await EvaluateVariableBindingAsync(
                sourceName,
                sourceText,
                assignment.Value,
                cancellationToken);

            try
            {
                await Runtime.ObjectAccessor.SetValueAsync(
                    target,
                    memberPath,
                    coalescedBinding.Value,
                    cancellationToken);
            }
            catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.member_assignment_failed",
                    Title: exception.Message,
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: assignment.Target.Span,
                    Label: "while assigning to this member"));
            }

            yield break;
        }

        var valueToAssign = binding!.Value;
        try
        {
            if (assignment.Operator != "=")
            {
                var currentValue = await Runtime.ObjectAccessor.GetValueAsync(
                    target,
                    memberPath,
                    cancellationToken);
                valueToAssign = await ApplyCompoundAssignmentAsync(
                    sourceName,
                    sourceText,
                    assignment.Span,
                    currentValue,
                    assignment.Operator,
                    binding.Value,
                    cancellationToken);
            }

            await Runtime.ObjectAccessor.SetValueAsync(
                target,
                memberPath,
                valueToAssign,
                cancellationToken);
        }
        catch (Exception exception) when (ShouldWrapAssignmentFailure(exception))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.member_assignment_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: assignment.Target.Span,
                Label: "while assigning to this member"));
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateFunctionDefinitionAsync(
        string sourceName,
        string sourceText,
        FunctionDefinitionStatementSyntax function,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, function.Name, function.Span, "reserved runtime namespace");
        var definition = CreateFunctionDefinition(
            function.Name,
            function.Parameters,
            function.ReturnTypeName,
            function.Body,
            function.IsCommandWrapper,
            sourceName,
            sourceText,
            function.Span,
            function.DocComment,
            typeParameters: function.TypeParameters,
            typeParameterConstraints: function.TypeParameterConstraints);

        var functionCommand = new FunctionCommand(this, definition);
        DeclareCommand(functionCommand, function.Modifier);

        if (function.HandlesEvent is not null)
        {
            RegisterEventHandler(functionCommand, function.HandlesEvent, function.HandlerPriority, function.IsOnceHandler, function.WhenGuard, sourceName, sourceText);
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateRuneDefinitionAsync(
        string sourceName,
        string sourceText,
        RuneDefinitionStatementSyntax rune,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rune.Name, rune.Span, "reserved runtime namespace");

        var duplicateParameters = rune.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_rune_parameter",
                Title: $"Rune '{rune.Name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        var definition = new RuneDefinition(
            rune.Name,
            rune.Parameters.Select(p => new RuneParameterDefinition(p.Name, p.Span)).ToArray(),
            rune.Body,
            rune.IsSealed,
            rune.IsFixed,
            sourceName,
            sourceText,
            rune.Span,
            CaptureVisibleScopes(),
            rune.DocComment is not null ? DocComment.Parse(new[] { new SyntaxToken(SyntaxTokenKind.DocComment, 0, rune.DocComment.ToString() ?? "") }) : null);

        var runeCommand = new RuneCommand(definition);
        DeclareCommand(runeCommand, rune.Modifier);

        yield break;
    }

    private FunctionDefinition CreateFunctionDefinition(
        string name,
        IReadOnlyList<FunctionParameterSyntax> parameters,
        string? returnTypeName,
        BlockSyntax body,
        bool isCommandWrapper,
        string sourceName,
        string sourceText,
        TextSpan span,
        DocComment? docComment = null,
        IReadOnlyList<string>? typeParameters = null,
        IReadOnlyList<TypeParameterConstraintSyntax>? typeParameterConstraints = null)
    {
        var duplicateParameters = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateParameters is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_function_parameter",
                Title: $"Function '{name}' defines parameter '{duplicateParameters.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateParameters.First().Span,
                Label: $"'{duplicateParameters.Key}' is declared multiple times"));
        }

        foreach (var parameter in parameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");
        }

        return new FunctionDefinition(
            name,
            parameters
                .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, typeParameters))
                .ToArray(),
            EraseTypeParameter(returnTypeName, typeParameters),
            body,
            isCommandWrapper,
            sourceName,
            sourceText,
            span,
            CaptureVisibleScopes(),
            docComment,
            IsGenerator: ContainsYieldStatement(body),
            TypeParameters: typeParameters,
            RawReturnTypeName: returnTypeName,
            TypeParameterConstraints: typeParameterConstraints?
                .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
                .ToArray());
    }

    private static string? EraseTypeParameter(string? typeName, IReadOnlyList<string>? typeParameters)
    {
        if (typeName is null || typeParameters is not { Count: > 0 }) return typeName;
        if (typeParameters.Contains(typeName, StringComparer.Ordinal)) return null;

        // Generic type whose arg list mentions a type parameter — strip
        // arguments that are themselves type-parameter names so the
        // outer constructor can still be resolved (e.g. `list<T>`
        // becomes `list`). Recursively erase nested generic args.
        var lt = typeName.IndexOf('<');
        var gt = typeName.LastIndexOf('>');
        if (lt > 0 && gt == typeName.Length - 1)
        {
            var head = typeName.Substring(0, lt);
            var inner = typeName.Substring(lt + 1, gt - lt - 1);
            var args = SplitTopLevelCommas(inner);
            var anyParam = false;
            var rebuilt = new List<string>(args.Count);
            foreach (var arg in args)
            {
                var trimmed = arg.Trim();
                if (typeParameters.Contains(trimmed, StringComparer.Ordinal))
                {
                    anyParam = true;
                    continue;
                }
                var erased = EraseTypeParameter(trimmed, typeParameters);
                if (erased is null)
                {
                    anyParam = true;
                    continue;
                }
                if (!ReferenceEquals(erased, trimmed)) anyParam = true;
                rebuilt.Add(erased);
            }
            if (anyParam)
            {
                return rebuilt.Count == 0 ? head : $"{head}<{string.Join(", ", rebuilt)}>";
            }
        }
        return typeName;
    }

    private static IReadOnlyList<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start <= s.Length) result.Add(s.Substring(start));
        return result;
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

    private RefinementTypeDefinition CreateRefinementTypeDefinition(
        string sourceName,
        string sourceText,
        TypeAliasStatementSyntax statement)
    {
        return new RefinementTypeDefinition(
            statement.Name,
            statement.TypeParameters,
            statement.BaseTypeName,
            CreateRefinementAnnotation(sourceName, sourceText, statement.Refinement),
            sourceName,
            sourceText,
            statement.Modifier,
            statement.Span,
            statement.DocComment?.Description?.Trim() is { Length: > 0 } desc ? desc : null);
    }

    private RefinementAnnotation? CreateRefinementAnnotation(
        string sourceName,
        string sourceText,
        ArgumentSyntax? predicate)
    {
        if (predicate is null)
        {
            return null;
        }

        if (predicate is RefinementClauseArgumentSyntax clause)
        {
            return new RefinementAnnotation(
                clause.Clauses.Select(static clause => clause switch
                {
                    RefinementWhereClauseSyntax whereClause => (RefinementClause)new RefinementWhereClause(whereClause.Predicate, whereClause.Span),
                    RefinementCoerceClauseSyntax coerceClause => new RefinementCoerceClause(coerceClause.Guard, coerceClause.Coercer, coerceClause.Span),
                    _ => throw new InvalidOperationException($"Unsupported refinement clause '{clause.GetType().Name}'."),
                }).ToArray(),
                sourceName,
                sourceText,
                clause.Span,
                CaptureVisibleScopes());
        }

        return new RefinementAnnotation(
            [new RefinementWhereClause(predicate, predicate.Span)],
            sourceName,
            sourceText,
            predicate.Span,
            CaptureVisibleScopes());
    }

    /// <summary>
    /// Whether executing <paramref name="statement"/> can produce a <c>yield</c>, and so must be
    /// streamed rather than collected. Answered per syntax node and cached, because the block
    /// executor asks on every statement of every iteration while the answer is fixed at parse
    /// time; the table holds weak references, so a discarded parse tree still collects.
    /// </summary>
    private static bool StatementYields(StatementSyntax statement)
    {
        return YieldingStatements
            .GetValue(statement, static node => new System.Runtime.CompilerServices.StrongBox<bool>(ContainsYieldInStatement(node)))
            .Value;
    }

    private static bool StatementStreamsOutput(StatementSyntax statement)
    {
        // These statements forward a nested block's values and can terminate through a jump or
        // failure after producing some of them. Draining the whole statement to a list first
        // loses those already-produced values when the jump escapes. Their nested blocks maintain
        // the last-result state, and result suppression applies only to pipeline statements, so
        // streaming them changes neither of those contracts.
        return StatementYields(statement) || statement is
            IfStatementSyntax or
            ForStatementSyntax or
            WhileStatementSyntax or
            UntilStatementSyntax or
            TryStatementSyntax or
            SwitchStatementSyntax;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StatementSyntax, System.Runtime.CompilerServices.StrongBox<bool>> YieldingStatements = new();

    private static bool ContainsYieldStatement(BlockSyntax block)
    {
        foreach (var statement in block.Statements)
        {
            if (statement is YieldStatementSyntax)
                return true;

            // Check nested blocks (if, for, while, try, etc.)
            if (ContainsYieldInStatement(statement))
                return true;
        }

        return false;
    }

    private static bool ContainsYieldInStatement(StatementSyntax statement)
    {
        return statement switch
        {
            IfStatementSyntax ifStmt =>
                ContainsYieldStatement(ifStmt.ThenBlock) ||
                (ifStmt.ElseBlock is not null && ContainsYieldStatement(ifStmt.ElseBlock)),
            ForStatementSyntax forStmt => ContainsYieldStatement(forStmt.Body),
            WhileStatementSyntax whileStmt => ContainsYieldStatement(whileStmt.Body),
            // `until` is `while` with the condition negated, and it was the one loop missing
            // here. The omission cost twice over: a function whose only yield sat in an `until`
            // was not recognised as a generator, and the loop was collected rather than
            // streamed, so an endless one never returned.
            UntilStatementSyntax untilStmt => ContainsYieldStatement(untilStmt.Body),
            TryStatementSyntax tryStmt =>
                ContainsYieldStatement(tryStmt.TryBlock) ||
                (tryStmt.CatchClause is not null && ContainsYieldStatement(tryStmt.CatchClause.Body)) ||
                (tryStmt.FinallyBlock is not null && ContainsYieldStatement(tryStmt.FinallyBlock)),
            SwitchStatementSyntax switchStmt =>
                switchStmt.Cases.Any(c => ContainsYieldStatement(c.Body)) ||
                (switchStmt.DefaultBlock is not null && ContainsYieldStatement(switchStmt.DefaultBlock)),
            _ => false,
        };
    }

    private void RegisterEventHandler(
        FunctionCommand functionCommand,
        string eventName,
        int? priority,
        bool once,
        BlockSyntax? whenGuard,
        string sourceName,
        string sourceText)
    {
        var capturedScopes = CaptureVisibleScopes();

        var handler = new ShellEventHandler(
            eventName,
            functionCommand.Name,
            async (shellEvent, cancellationToken) =>
            {
                try
                {
                    if (whenGuard is not null)
                    {
                        var guardResult = await EvaluateWhenGuardAsync(
                            sourceName, sourceText, whenGuard, shellEvent, capturedScopes, cancellationToken);

                        if (!guardResult)
                        {
                            return null;
                        }
                    }

                    object? result = null;
                    var context = new CommandContext(
                        Runtime,
                        EmptyAsyncEnumerable(),
                        new object?[] { shellEvent },
                        cancellationToken,
                        BlockExecutor: _ownBlockExecutor,
                        ScopedCommands: CreateScopedCommandView(), ShellTypes: this);

                    await foreach (var value in functionCommand.ExecuteAsync(context))
                    {
                        result = value;
                    }

                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Runtime.Error.WriteLineAsync(
                        $"Event handler '{functionCommand.Name}' for '{eventName}' failed: {ex.Message}");
                    return null;
                }
            },
            priority,
            once,
            capturedScopes?.Cast<object>().ToArray());

        Runtime.Events.Register(handler);
    }

    private async Task<bool> EvaluateWhenGuardAsync(
        string sourceName,
        string sourceText,
        BlockSyntax guard,
        ShellEvent shellEvent,
        IReadOnlyList<LexicalScope>? capturedScopes,
        CancellationToken cancellationToken)
    {
        if (capturedScopes is not null)
        {
            foreach (var scope in capturedScopes)
            {
                _scopes.Push(scope);
            }
        }

        _scopes.Push(new LexicalScope());
        _scopes.Peek().Variables["_"] = new VariableBinding(shellEvent, ReplayAsPipeline: false, IsAllocatedOnly: false);

        try
        {
            object? lastValue = null;

            await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, guard, cancellationToken, pushNewScope: false))
            {
                lastValue = value;
            }

            return ToshTruthiness.IsTruthy(lastValue);
        }
        finally
        {
            _scopes.Pop();

            if (capturedScopes is not null)
            {
                for (var index = 0; index < capturedScopes.Count; index++)
                {
                    _scopes.Pop();
                }
            }
        }
    }

    private static async IAsyncEnumerable<object?> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateClassDefinitionAsync(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @class.Name, @class.Span, "reserved runtime namespace");

        var duplicateProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateProperties is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_class_property",
                Title: $"Class '{@class.Name}' defines property '{duplicateProperties.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateProperties.First().Span,
                Label: $"'{duplicateProperties.Key}' is declared multiple times"));
        }

        foreach (var parameter in @class.PrimaryConstructorParameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");
        }

        foreach (var constructorParameter in @class.Members
                     .OfType<ClassConstructorMemberSyntax>()
                     .SelectMany(member => member.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, constructorParameter.Name, constructorParameter.Span, "reserved runtime namespace");
        }

        foreach (var methodParameter in @class.Members
                     .OfType<ClassMethodMemberSyntax>()
                     .SelectMany(member => member.Method.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, methodParameter.Name, methodParameter.Span, "reserved runtime namespace");
        }

        var classTypeParams = @class.TypeParameters;

        var runtimeProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .Select(property => new ToshClassPropertyDefinition(
                property.Name,
                // Keep the original type-parameter name (e.g. 'T1') so
                // the runtime can substitute it against the instance's
                // generic bindings on each access. Concrete CLR-style
                // types are passed through unchanged.
                property.TypeName,
                property.Initializer,
                property.GetterBody,
                property.SetterBody,
                property.IsShy,
                property.IsStatic || @class.IsHermit,  // hermit classes make all members implicitly shared
                property.IsFixed || @class.IsStrict,  // strict classes make all properties fixed
                property.IsVital,
                property.IsGuarded,
                property.IsLazy,
                property.IsFading,
                property.IsLocal,
                property.IsAbstract,
                property.Span,
                CreateRefinementAnnotation(sourceName, sourceText, property.Refinement)))
            .ToArray();

        var runtimeMethods = @class.Members
            .OfType<ClassMethodMemberSyntax>()
            .Select(method =>
            {
                // Phase 3.4 — method-level type parameters. Combine
                // the class's type-parameter names with the method's
                // own so erasure removes both before annotation
                // resolution.
                var methodTypeParams = method.Method.TypeParameters;
                IReadOnlyList<string>? combinedTypeParams = classTypeParams;
                if (methodTypeParams is { Count: > 0 })
                {
                    combinedTypeParams = (classTypeParams is { Count: > 0 })
                        ? classTypeParams.Concat(methodTypeParams).ToArray()
                        : methodTypeParams;
                }
                var methodConstraints = method.Method.TypeParameterConstraints?
                    .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
                    .ToArray();

                return new ToshClassMethodDefinition(
                method.Method.Name,
                method.Method.Parameters
                    .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, combinedTypeParams))
                    .ToArray(),
                EraseTypeParameter(method.Method.ReturnTypeName, combinedTypeParams),
                method.Method.Body,
                method.IsStatic || @class.IsHermit,  // hermit classes make all members implicitly shared
                method.IsShy,
                method.IsAbstract,
                method.IsOverride,
                method.IsGuarded,
                method.IsFading,
                method.IsLocal,
                method.IsRaw,
                sourceName,
                sourceText,
                method.Span,
                CaptureVisibleScopes(),
                // Preserve the un-erased return-type annotation so a generic
                // class can substitute T against the instance's bindings at
                // call time (see ToshClassDefinition.ExecuteMethodBlock).
                RawReturnTypeName: method.Method.ReturnTypeName,
                TypeParameters: methodTypeParams,
                TypeParameterConstraints: methodConstraints);
            })
            .ToArray();

        var runtimeConstructors = @class.Members
            .OfType<ClassConstructorMemberSyntax>()
            .Select(constructor => new ToshClassConstructorDefinition(
                constructor.Parameters
                    .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, classTypeParams))
                    .ToArray(),
                constructor.Body,
                sourceName,
                sourceText,
                constructor.Span,
                CaptureVisibleScopes()))
            .ToArray();

        var typeParameterConstraints = @class.TypeParameterConstraints?
            .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
            .ToArray();

        var definition = new ToshClassDefinition(
            this,
            @class.Name,
            @class.PrimaryConstructorParameters
                .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, classTypeParams))
                .ToArray(),
            runtimeProperties,
            runtimeMethods,
            runtimeConstructors,
            sourceName,
            sourceText,
            @class.Span,
            CaptureVisibleScopes(),
            typeParameters: classTypeParams,
            typeParameterConstraints: typeParameterConstraints);

        // Handle partial class merging: if this is a partial class and one already exists, merge members
        if (@class.IsPartial && TryGetNamedType(@class.Name, out var existingType) && existingType is ToshClassDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_mismatch",
                    Title: $"Cannot extend class '{@class.Name}' as partial: the original class was not declared as partial.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: "both declarations must be partial"));
            }

            existingDef.MergePartial(runtimeProperties, runtimeMethods, runtimeConstructors);

            // After the merge, not before: a later part can contribute the constructor that
            // collides with an earlier part's, and neither part is wrong on its own.
            existingDef.ValidateConstructorSignatures();

            await BindNativeClassMembersAsync(sourceName, sourceText, @class, existingDef, cancellationToken);

            // Declared again rather than returning early, so a file contributing
            // a partial exports the name it contributed to. Same object, so the
            // two declarations cannot diverge.
            DeclareType(@class.Name, existingDef, @class.Modifier, sourceName, sourceText, @class.Span);
            yield break;
        }

        // Before DeclareType, so a class the resolver could never construct never enters scope.
        definition.ValidateConstructorSignatures();

        await BindNativeClassMembersAsync(sourceName, sourceText, @class, definition, cancellationToken);

        DeclareType(@class.Name, definition, @class.Modifier, sourceName, sourceText, @class.Span);

        // Nested types are evaluated after the class is in scope, so one may refer to the class
        // that declares it.
        await EvaluateNestedTypeMembersAsync(sourceName, sourceText, @class, definition, cancellationToken);

        definition.IsSealed = @class.IsSealed;
        definition.IsAbstract = @class.IsAbstract;
        definition.IsHermit = @class.IsHermit;
        definition.IsStrict = @class.IsStrict;
        definition.IsPartial = @class.IsPartial;

        // Validate hermit (static) classes: constructors not allowed (members are auto-shared)
        if (definition.IsHermit)
        {
            if (runtimeConstructors.Length > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.hermit_has_constructor",
                    Title: $"Hermit class '{@class.Name}' cannot have constructors.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: "hermit classes cannot be instantiated"));
            }
        }

        // Resolve base class
        if (@class.BaseClassName is not null)
        {
            // Preserve the distinction between no constructor initializer and
            // an explicitly empty `extends Base()` initializer for both TōSh
            // and CLR base classes.
            if (@class.BaseConstructorArgs is not null)
            {
                definition.BaseConstructorArgs = @class.BaseConstructorArgs;
            }

            if (TryGetNamedType(@class.BaseClassName, out var baseType) && baseType is ToshClassDefinition baseClassDef)
            {
                if (baseClassDef.IsSealed)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.extend_sealed_class",
                        Title: $"Class '{@class.Name}' cannot extend sealed class '{@class.BaseClassName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{@class.BaseClassName}' is marked sealed and cannot be extended"));
                }

                definition.BaseClass = baseClassDef;

                // Store extends clause type-arguments and validate arity
                if (@class.BaseTypeArguments is not null)
                {
                    if (@class.BaseTypeArguments.Count != baseClassDef.TypeParameterNames.Count)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.base_type_argument_arity",
                            Title: $"Class '{@class.Name}' supplies {@class.BaseTypeArguments.Count} type argument(s) to base class '{baseClassDef.Name}', which expects {baseClassDef.TypeParameterNames.Count}.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: @class.Span,
                            Label: $"'{baseClassDef.Name}' has {baseClassDef.TypeParameterNames.Count} type parameter(s): <{string.Join(", ", baseClassDef.TypeParameterNames)}>"));
                    }

                    definition.BaseTypeArguments = @class.BaseTypeArguments;

                    // Eagerly resolve concrete type-argument strings; entries
                    // that are themselves child type-parameters are left null
                    // (they get forwarded at instance construction time).
                    var childTypeParams = definition.TypeParameterNames;
                    var resolved = new Type?[@class.BaseTypeArguments.Count];
                    for (int i = 0; i < @class.BaseTypeArguments.Count; i++)
                    {
                        var argString = @class.BaseTypeArguments[i];
                        if (childTypeParams.Contains(argString, StringComparer.OrdinalIgnoreCase))
                        {
                            resolved[i] = null;
                        }
                        else
                        {
                            resolved[i] = ResolveTypeName(argString);
                        }
                    }
                    definition.BaseTypeArgumentsResolved = resolved;
                }
                else if (baseClassDef.TypeParameterNames.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.base_type_argument_missing",
                        Title: $"Class '{@class.Name}' extends generic class '{baseClassDef.Name}' without supplying type arguments.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"write 'extends {baseClassDef.Name}<{string.Join(", ", baseClassDef.TypeParameterNames)}>'"));
                }
            }
            else
            {
                // Try resolving as a CLR type
                var clrType = ResolveTypeName(@class.BaseClassName);
                if (clrType is not null)
                {
                    definition.ClrBaseType = clrType;
                }
                else
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_base_class",
                        Title: $"Class '{@class.Name}' extends unknown class '{@class.BaseClassName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{@class.BaseClassName}' is not a known class"));
                }
            }
        }

        // Validate implemented interfaces
        if (@class.ImplementedInterfaces is { Count: > 0 })
        {
            foreach (var ifaceName in @class.ImplementedInterfaces)
            {
                // The fulfills clause may carry generic type arguments
                // (e.g. 'fulfills IPoint<int>'). Type definitions are
                // registered by their bare name, so look up using the
                // unparameterised head while keeping the full reference
                // string for diagnostics.
                var lookupName = StripGenericTypeArguments(ifaceName);
                if (!TryGetNamedType(lookupName, out var namedType) || namedType is not ToshInterfaceDefinition ifaceDefinition)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_interface",
                        Title: $"Class '{@class.Name}' fulfills unknown interface '{ifaceName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{ifaceName}' is not a known interface"));
                }

                // Validate type-argument arity / constraints when the
                // interface is generic and the fulfills clause carries
                // type arguments.
                ValidateInterfaceTypeArguments(
                    sourceName,
                    sourceText,
                    @class,
                    ifaceDefinition,
                    ifaceName);

                var missing = ifaceDefinition.GetMissingMethods(definition);
                if (missing.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_interface_methods",
                        Title: $"Class '{@class.Name}' does not implement all methods of interface '{ifaceName}'. Missing: {string.Join(", ", missing)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missing)}"));
                }
            }

            definition.ImplementedInterfaces = @class.ImplementedInterfaces
                .Select(name => TryGetNamedType(StripGenericTypeArguments(name), out var t) && t is ToshInterfaceDefinition iface ? iface : null)
                .Where(i => i is not null)
                .ToArray()!;
        }

        // Validate used traits and inject default methods/properties
        if (@class.UsedTraits is { Count: > 0 })
        {
            foreach (var traitName in @class.UsedTraits)
            {
                if (!TryGetNamedType(traitName, out var namedType) || namedType is not ToshTraitDefinition traitDefinition)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_trait",
                        Title: $"Class '{@class.Name}' uses unknown trait '{traitName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{traitName}' is not a known trait"));
                }

                // Check required methods (those without default bodies)
                var missingMethods = traitDefinition.GetMissingMethods(definition);
                if (missingMethods.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_trait_methods",
                        Title: $"Class '{@class.Name}' does not implement required methods from trait '{traitName}'. Missing: {string.Join(", ", missingMethods)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missingMethods)}"));
                }

                // Check required properties (those without default values)
                var missingProps = traitDefinition.GetMissingProperties(definition);
                if (missingProps.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_trait_properties",
                        Title: $"Class '{@class.Name}' does not implement required properties from trait '{traitName}'. Missing: {string.Join(", ", missingProps)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missingProps)}"));
                }

                // Inject default methods that the class doesn't already define
                foreach (var traitMethod in traitDefinition.Methods.Where(m => m.HasDefaultBody))
                {
                    if (!definition.Methods.Any(m => string.Equals(m.Name, traitMethod.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        definition.AddMethod(new ToshClassMethodDefinition(
                            traitMethod.Name,
                            traitMethod.Parameters,
                            traitMethod.ReturnTypeName,
                            traitMethod.DefaultBody!,
                            IsStatic: false,
                            IsShy: false,
                            IsAbstract: false,
                            IsOverride: false,
                            IsGuarded: false,
                            IsFading: false,
                            IsLocal: false,
                            IsRaw: false,
                            sourceName,
                            sourceText,
                            @class.Span,
                            CapturedScopes: CaptureVisibleScopes()));
                    }
                }

                // Inject default property values for properties the class doesn't define
                foreach (var traitProp in traitDefinition.Properties.Where(p => p.DefaultValue is not null))
                {
                    if (!definition.Properties.Any(p => string.Equals(p.Name, traitProp.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        definition.AddProperty(new ToshClassPropertyDefinition(
                            traitProp.Name,
                            traitProp.TypeName,
                            traitProp.DefaultValue,
                            GetterBody: null,
                            SetterBody: null,
                            IsShy: false,
                            IsStatic: false,
                            IsFixed: false,
                            IsVital: false,
                            IsGuarded: false,
                            IsLazy: false,
                            IsFading: false,
                            IsLocal: false,
                            IsAbstract: false,
                            @class.Span));
                    }
                }
            }

            definition.UsedTraits = @class.UsedTraits
                .Select(name => TryGetNamedType(name, out var t) && t is ToshTraitDefinition trait ? trait : null)
                .Where(t => t is not null)
                .ToArray()!;
        }

        // Validate that non-abstract classes implement all hollow (abstract) methods from parent
        if (!definition.IsAbstract && definition.BaseClass is { } parentClass)
        {
            var unimplemented = GetUnimplementedAbstractMethods(parentClass, definition);
            if (unimplemented.Count > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_hollow_methods",
                    Title: $"Class '{@class.Name}' must implement hollow methods from '{parentClass.Name}': {string.Join(", ", unimplemented)}.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"missing hollow methods: {string.Join(", ", unimplemented)}"));
            }

            var unimplementedProps = GetUnimplementedAbstractProperties(parentClass, definition);
            if (unimplementedProps.Count > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_hollow_properties",
                    Title: $"Class '{@class.Name}' must implement hollow properties from '{parentClass.Name}': {string.Join(", ", unimplementedProps)}.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"missing hollow properties: {string.Join(", ", unimplementedProps)}"));
            }
        }

        // Validate overrule methods have a matching parent method
        foreach (var method in runtimeMethods.Where(m => m.IsOverride))
        {
            if (definition.BaseClass is null || !HasMethodInHierarchy(definition.BaseClass, method.Name))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.overrule_no_base_method",
                    Title: $"Method '{method.Name}' in class '{@class.Name}' is marked 'overrule' but no parent class defines '{method.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: method.Span,
                    Label: $"no '{method.Name}' found in parent hierarchy to overrule"));
            }
        }

        // Validate that methods shadowing a parent method are marked 'overrule'
        if (definition.BaseClass is not null)
        {
            foreach (var method in runtimeMethods.Where(m => !m.IsOverride && !m.IsAbstract && !m.IsStatic))
            {
                // Matched on the whole signature rather than the name alone. A same-named method
                // with different parameters is an *overload*, not an override, and demanding
                // `overrule` for it made an inherited overload set impossible to extend: a class
                // whose base declared `func f(a: int)` could not declare `func f(a: string)`,
                // nor even `func f(a: int, b: int)`, without claiming to override something it
                // does not.
                if (OverridesMethodInHierarchy(definition.BaseClass, method))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_overrule",
                        Title: $"Method '{method.Name}' in class '{@class.Name}' shadows a parent method but is not marked 'overrule'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: method.Span,
                        Label: $"add 'overrule' to override '{method.Name}'"));
                }
            }
        }

        // Initialize static property values
        foreach (var prop in runtimeProperties.Where(p => p.IsStatic && p.Initializer is not null))
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, prop.Initializer!, cancellationToken, outputIsCaptured: true),
                cancellationToken);
            definition.InitializeStaticMember(prop.Name, values.Count == 1 ? values[0] : values);
        }

        yield break;
    }

    private static IReadOnlyList<string> GetUnimplementedAbstractMethods(ToshClassDefinition parent, ToshClassDefinition child)
    {
        var missing = new List<string>();
        var current = parent;

        while (current is not null)
        {
            foreach (var method in current.Methods.Where(m => m.IsAbstract))
            {
                if (!child.Methods.Any(m => string.Equals(m.Name, method.Name, StringComparison.OrdinalIgnoreCase) && !m.IsAbstract))
                {
                    missing.Add(method.Name);
                }
            }
            current = current.BaseClass;
        }

        return missing;
    }

    private static IReadOnlyList<string> GetUnimplementedAbstractProperties(ToshClassDefinition parent, ToshClassDefinition child)
    {
        var missing = new List<string>();
        var current = parent;

        while (current is not null)
        {
            foreach (var prop in current.Properties.Where(p => p.IsAbstract))
            {
                if (!child.Properties.Any(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase) && !p.IsAbstract))
                {
                    missing.Add(prop.Name);
                }
            }
            current = current.BaseClass;
        }

        return missing;
    }

    /// <summary>
    /// Whether any class in <paramref name="classDefinition"/>'s chain declares a method that
    /// <paramref name="method"/> would genuinely override: the same name *and* the same parameter
    /// list. Name alone is what <see cref="HasMethodInHierarchy"/> answers, which is right for
    /// asking whether `overrule` has anything at all to point at, and wrong for deciding whether
    /// a method shadows one — an overload shares the name by definition.
    /// </summary>
    private static bool OverridesMethodInHierarchy(
        ToshClassDefinition classDefinition,
        ToshClassMethodDefinition method)
    {
        for (var current = classDefinition; current is not null; current = current.BaseClass)
        {
            foreach (var candidate in current.Methods)
            {
                if (string.Equals(candidate.Name, method.Name, StringComparison.OrdinalIgnoreCase) &&
                    ParameterListsMatch(candidate.Parameters, method.Parameters))
                {
                    return true;
                }
            }
        }

        return false;
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

    private static bool HasMethodInHierarchy(ToshClassDefinition classDefinition, string methodName)
    {
        var current = classDefinition;
        while (current is not null)
        {
            if (current.Methods.Any(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            current = current.BaseClass;
        }
        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateInterfaceDefinitionAsync(
        string sourceName,
        string sourceText,
        InterfaceDefinitionStatementSyntax @interface,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @interface.Name, @interface.Span, "reserved runtime namespace");

        var methods = @interface.Methods
            .Select(m => new InterfaceMethodSignature(
                m.Name,
                m.Parameters
                    .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                    .ToArray(),
                m.ReturnTypeName))
            .ToArray();

        var definition = new ToshInterfaceDefinition(
            @interface.Name,
            methods,
            sourceName,
            sourceText,
            @interface.Span,
            typeParameterNames: @interface.TypeParameters,
            typeParameterConstraints: @interface.TypeParameterConstraints,
            typeParameterVariances: @interface.TypeParameterVariances);

        DeclareType(@interface.Name, definition, @interface.Modifier, sourceName, sourceText, @interface.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateUnionDefinitionAsync(
        string sourceName,
        string sourceText,
        UnionDefinitionStatementSyntax union,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, union.Name, union.Span, "reserved runtime namespace");

        var variants = union.Variants
            .Select(v => new UnionVariantDefinition(
                v.Name,
                v.Fields.Select(f => f.Name).ToArray()))
            .ToArray();

        var definition = new ToshUnionDefinition(
            union.Name,
            variants,
            sourceName,
            sourceText,
            union.Span);

        DeclareType(union.Name, definition, union.Modifier, sourceName, sourceText, union.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateModuleDefinitionAsync(
        string sourceName,
        string sourceText,
        ModuleDefinitionStatementSyntax module,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, module.Name, module.Span, "reserved runtime namespace");

        // Partial modules merge their members into an existing module of the
        // same name. We pre-seed the new module scope with the existing
        // exports so that name resolution inside the partial body sees prior
        // contributions, and we re-use the same ModuleExportTable so that all
        // ToshModuleObject views observe the merged state automatically.
        ToshModuleObject? existingModule = null;
        ModuleExportTable? sharedExports = null;
        if (module.IsPartial && TryFindExistingModule(module.Name, out existingModule))
        {
            // Classes, records and structs all refuse this; modules merged
            // silently, which is the one place the four kinds disagreed.
            if (!existingModule.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_mismatch",
                    Title: $"Cannot extend module '{module.Name}' as partial: the original module was not declared as partial.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: module.Span,
                    Label: "both declarations must be partial"));
            }

            sharedExports = existingModule.ExportTable;
        }

        var moduleScope = new LexicalScope(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            isModuleScope: true,
            exportDeclarationsByDefault: true,
            exports: sharedExports);

        if (sharedExports is not null)
        {
            // Make prior exports visible to body-local resolution. Variables /
            // types / commands / refinements / nested modules are all copied
            // by reference so updates from the new body still flow through to
            // the shared export table.
            foreach (var (key, value) in sharedExports.Variables) moduleScope.Variables[key] = value;
            foreach (var (key, value) in sharedExports.Commands) moduleScope.Commands[key] = value;
            foreach (var (key, value) in sharedExports.Types) moduleScope.Classes[key] = value;
            foreach (var (key, value) in sharedExports.RefinementTypes) moduleScope.RefinementTypes[key] = value;
            foreach (var (key, value) in sharedExports.Modules) moduleScope.Modules[key] = value;
        }

        using (PushScope(moduleScope))
        {
            await foreach (var _ in ExecuteBlockAsync(sourceName, sourceText, module.Body, cancellationToken, pushNewScope: false)
                               .WithCancellation(cancellationToken))
            {
            }
        }

        // A partial declaration that merged into an existing module still
        // *declares* that module here, rather than returning early. The shared
        // ModuleExportTable means this is the same object and not a copy, so
        // there is nothing to keep in step — but without the declaration, a file
        // contributing a partial exported nothing under the name, and
        // `require Sys from "./b.tosh"` failed with "Export 'Sys' was not found"
        // while the merge had in fact succeeded. The bare `require "./b.tosh"`
        // form worked only because it never looks a name up.
        var moduleObject = existingModule
            ?? new ToshModuleObject(this, module.Name, moduleScope.Exports ?? new ModuleExportTable());

        var effectiveModifier = module.Modifier;

        if (effectiveModifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek().IsModuleScope)
        {
            effectiveModifier = DeclarationModifier.Export;
        }

        moduleObject.IsPartial = module.IsPartial;
        DeclareModule(module.Name, moduleObject, effectiveModifier);
        yield break;
    }

    private bool TryFindExistingModule(string name, out ToshModuleObject module)
    {
        // Walk inner scopes outward (most-nested first), then runtime, looking
        // for a previously declared module with this name.
        foreach (var scope in _scopes)
        {
            if (scope.Modules.TryGetValue(name, out var scoped) && scoped is ToshModuleObject scopedModule)
            {
                module = scopedModule;
                return true;
            }

            if (scope.IsModuleScope &&
                scope.Exports is { } exports &&
                exports.Modules.TryGetValue(name, out var exported) &&
                exported is ToshModuleObject exportedModule)
            {
                module = exportedModule;
                return true;
            }
        }

        if (Runtime.Modules.TryGetValue(name, out var runtimeModule) && runtimeModule is ToshModuleObject runtime)
        {
            module = runtime;
            return true;
        }

        module = null!;
        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateEnumDefinitionAsync(
        string sourceName,
        string sourceText,
        EnumDefinitionStatementSyntax @enum,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @enum.Name, @enum.Span, "reserved runtime namespace");

        var underlyingType = string.IsNullOrWhiteSpace(@enum.UnderlyingTypeName)
            ? typeof(int)
            : ResolveTypeName(@enum.UnderlyingTypeName!)
                ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_enum_underlying_type",
                    Title: $"Enum '{@enum.Name}' uses unknown underlying type '{@enum.UnderlyingTypeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @enum.Span,
                    Label: $"the type '{@enum.UnderlyingTypeName}' could not be resolved"));

        var members = new List<ToshEnumValue>();
        long nextNumericValue = 0;
        var canAutoIncrement = IsNumericEnumUnderlyingType(underlyingType);

        foreach (var member in @enum.Members)
        {
            object? rawValue;

            if (member.Value is null)
            {
                if (!canAutoIncrement)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.enum_member_value_required",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' requires an explicit value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: $"'{underlyingType.Name}' values cannot be auto-incremented"));
                }

                rawValue = Convert.ChangeType(nextNumericValue, underlyingType);
            }
            else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, member.Value, cancellationToken) is { Matched: true } raw)
            {
                rawValue = raw.Value;
            }
            else
            {
                var values = await AsyncEnumerableExtensions.ToListAsync(
                    EvaluatePipelineAsync(sourceName, sourceText, member.Value, cancellationToken, outputIsCaptured: true),
                    cancellationToken);
                rawValue = values.Count switch
                {
                    0 => null,
                    1 => values[0],
                    _ => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.enum_member_requires_single_value",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' must resolve to exactly one value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: "this enum member initializer produced multiple values")),
                };
            }

            if (!TypeConversion.TryConvert(rawValue, underlyingType, out var converted))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.enum_member_conversion_failed",
                    Title: $"Enum member '{@enum.Name}.{member.Name}' could not be converted to '{underlyingType.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: member.Span,
                    Label: $"the value does not match '{underlyingType.Name}'"));
            }

            members.Add(new ToshEnumValue(default!, member.Name, converted));

            if (canAutoIncrement)
            {
                nextNumericValue = Convert.ToInt64(converted, System.Globalization.CultureInfo.InvariantCulture) + 1;
            }
        }

        var definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            members,
            sourceName,
            sourceText,
            @enum.Span);

        var fixedMembers = definition.Members
            .Select(member => new ToshEnumValue(definition, member.Name, member.UnderlyingValue))
            .ToArray();
        definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            fixedMembers,
            sourceName,
            sourceText,
            @enum.Span);

        DeclareType(@enum.Name, definition, @enum.Modifier, sourceName, sourceText, @enum.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateRecordDefinitionAsync(
        string sourceName,
        string sourceText,
        RecordDefinitionStatementSyntax record,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, record.Name, record.Span, "reserved runtime namespace");

        var runtimeFields = record.Fields
            .Select(field => new ToshRecordFieldDefinition(
                field.Name,
                field.TypeName,
                field.DefaultValue,
                field.IsOptional,
                field.Span,
                CreateRefinementAnnotation(sourceName, sourceText, field.Refinement)))
            .ToArray();

        // Handle partial record merging
        if (record.IsPartial && TryGetNamedType(record.Name, out var existingType) && existingType is ToshRecordDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_merge_non_partial_record",
                    Title: $"Cannot merge partial record '{record.Name}' with existing non-partial record.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: record.Span,
                    Label: "original record is not declared 'partial'"));
            }

            existingDef.MergePartial(runtimeFields);
            DeclareType(record.Name, existingDef, record.Modifier, sourceName, sourceText, record.Span);
            yield break;
        }

        var duplicateFields = record.Fields
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateFields is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_record_field",
                Title: $"Record '{record.Name}' defines field '{duplicateFields.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateFields.First().Span,
                Label: $"'{duplicateFields.Key}' is declared multiple times"));
        }

        var definition = new ToshRecordDefinition(
            this,
            record.Name,
            runtimeFields,
            sourceName,
            sourceText,
            record.Span,
            CaptureVisibleScopes(),
            typeParameterNames: record.TypeParameters,
            typeParameterConstraints: record.TypeParameterConstraints);

        definition.IsSealed = record.IsSealed;
        definition.IsStrict = record.IsStrict;
        definition.IsPartial = record.IsPartial;

        DeclareType(record.Name, definition, record.Modifier, sourceName, sourceText, record.Span);
        yield break;
    }

    /// <summary>
    /// Declares a <c>raw struct</c>: builds the shared layout plan, emits a real
    /// sequential-layout CLR type, and registers both the emitted type (for the
    /// interop type resolver) and an <see cref="IShellNamedType"/> façade (for
    /// `new`, `describe-type`, and `members`) from the one declaration.
    /// </summary>
    private async IAsyncEnumerable<object?> EvaluateRawStructDefinitionAsync(
        string sourceName,
        string sourceText,
        RawStructDefinitionStatementSyntax rawStruct,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rawStruct.Name, rawStruct.Span, "reserved runtime namespace");

        if (rawStruct.Fields.Count == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_requires_fields",
                Title: $"Raw struct '{rawStruct.Name}' has no fields.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: rawStruct.Span,
                Label: "a native layout needs at least one field"));
        }

        var typeResolver = CreateScopedTypeResolver();
        var plan = Bridge.RawStructPlanBuilder.Build(
            rawStruct,
            name => typeResolver.Resolve(name),
            sourceName,
            sourceText);

        var clrType = Bridge.NativeStructTypeFactory.GetOrCreate(plan);

        // Field defaults are evaluated now, in declaration scope, and applied
        // when TōSh constructs a value — never by the marshaller.
        var defaults = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in rawStruct.Fields)
        {
            if (field.DefaultValue is null) continue;

            defaults[field.Name] = await EvaluatePipelineAsync(
                sourceName,
                sourceText,
                field.DefaultValue,
                cancellationToken).FirstOrDefaultAsync(cancellationToken);
        }

        var definition = new ToshRawStructDefinition(plan, clrType, defaults);

        DeclareType(rawStruct.Name, definition, rawStruct.Modifier, sourceName, sourceText, rawStruct.Span, clrType);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateStructDefinitionAsync(
        string sourceName,
        string sourceText,
        StructDefinitionStatementSyntax @struct,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @struct.Name, @struct.Span, "reserved runtime namespace");

        var runtimeFields = @struct.Fields
            .Select(field => new ToshRecordFieldDefinition(
                field.Name,
                field.TypeName,
                field.DefaultValue,
                field.IsOptional,
                field.Span,
                CreateRefinementAnnotation(sourceName, sourceText, field.Refinement)))
            .ToArray();

        // Handle partial struct merging
        if (@struct.IsPartial && TryGetNamedType(@struct.Name, out var existingType) && existingType is ToshStructDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_merge_non_partial_struct",
                    Title: $"Cannot merge partial struct '{@struct.Name}' with existing non-partial struct.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @struct.Span,
                    Label: "original struct is not declared 'partial'"));
            }

            existingDef.MergePartial(runtimeFields);
            DeclareType(@struct.Name, existingDef, @struct.Modifier, sourceName, sourceText, @struct.Span);
            yield break;
        }

        // Build properties and methods from body members
        var properties = new List<ToshClassPropertyDefinition>();
        var methods = new List<ToshClassMethodDefinition>();

        foreach (var member in @struct.Members)
        {
            switch (member)
            {
                case ClassPropertyMemberSyntax property:
                    properties.Add(new ToshClassPropertyDefinition(
                        property.Name,
                        property.TypeName,
                        property.Initializer,
                        property.GetterBody,
                        property.SetterBody,
                        property.IsShy,
                        property.IsStatic,
                        property.IsFixed || !@struct.IsFluid,
                        property.IsVital,
                        property.IsGuarded,
                        property.IsLazy,
                        property.IsFading,
                        property.IsLocal,
                        property.IsAbstract,
                        property.Span,
                        CreateRefinementAnnotation(sourceName, sourceText, property.Refinement)));
                    break;
                case ClassMethodMemberSyntax method:
                    methods.Add(new ToshClassMethodDefinition(
                        method.Method.Name,
                        method.Method.Parameters
                            .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                            .ToArray(),
                        method.Method.ReturnTypeName,
                        method.Method.Body,
                        method.IsStatic,
                        method.IsShy,
                        method.IsAbstract,
                        method.IsOverride,
                        method.IsGuarded,
                        method.IsFading,
                        method.IsLocal,
                        method.IsRaw,
                        sourceName,
                        sourceText,
                        method.Span,
                        CapturedScopes: CaptureVisibleScopes()));
                    break;
            }
        }

        var definition = new ToshStructDefinition(
            this,
            @struct.Name,
            runtimeFields,
            properties,
            methods,
            sourceName,
            sourceText,
            @struct.Span,
            CaptureVisibleScopes());

        definition.IsSealed = @struct.IsSealed;
        definition.IsFluid = @struct.IsFluid;
        definition.IsPartial = @struct.IsPartial;

        DeclareType(@struct.Name, definition, @struct.Modifier, sourceName, sourceText, @struct.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateTraitDefinitionAsync(
        string sourceName,
        string sourceText,
        TraitDefinitionStatementSyntax trait,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, trait.Name, trait.Span, "reserved runtime namespace");

        var methods = trait.Methods
            .Select(m => new TraitMethodDefinition(
                m.Name,
                m.Parameters
                    .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                    .ToArray(),
                m.ReturnTypeName,
                m.DefaultBody,
                HasDefaultBody: m.DefaultBody is not null))
            .ToArray();

        var properties = trait.Properties
            .Select(p => new TraitPropertyDefinition(p.Name, p.TypeName, p.DefaultValue))
            .ToArray();

        var definition = new ToshTraitDefinition(
            trait.Name,
            methods,
            properties,
            sourceName,
            sourceText,
            trait.Span);

        DeclareType(trait.Name, definition, trait.Modifier, sourceName, sourceText, trait.Span);
        yield break;
    }

    internal IAsyncEnumerable<object?> InvokeStructStaticMethodAsync(
        ToshStructDefinition structDef,
        ToshClassMethodDefinition method,
        IReadOnlyList<object?> arguments)
    {
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < method.Parameters.Count && i < arguments.Count; i++)
        {
            locals[method.Parameters[i].Name] = arguments[i];
        }
        locals["args"] = arguments.ToArray();

        var values = ExecuteClassBlockSync(
            null,
            method.SourceName,
            method.SourceText,
            method.Body,
            locals,
            method.CapturedScopes,
            $"{structDef.Name}.{method.Name}");

        return values.ToAsyncEnumerable();
    }

    private async IAsyncEnumerable<object?> EvaluateEventDefinitionAsync(
        string sourceName,
        string sourceText,
        EventDefinitionStatementSyntax @event,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @event.Name, @event.Span, "reserved runtime namespace");

        var definition = new ToshEventDefinition(
            this,
            @event.Name,
            @event.Fields
                .Select(field => new ToshEventFieldDefinition(field.Name, field.TypeName, field.DefaultValue, field.Span))
                .ToArray(),
            @event.IsRequired,
            @event.IsLocal,
            sourceName,
            sourceText,
            @event.Span,
            CaptureVisibleScopes());

        if (definition.IsRequired)
        {
            Runtime.Events.MarkRequired(definition.Name);
        }

        if (definition.IsLocal && _scopes.Count > 0)
        {
            _scopes.Peek().LocalEventNames.Add(definition.Name);
        }

        DeclareVariable(definition.Name, new VariableBinding(definition, ReplayAsPipeline: false, IsAllocatedOnly: false), @event.Modifier);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateIfStatementAsync(
        string sourceName,
        string sourceText,
        IfStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var condition = await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken);
        var block = condition ? statement.ThenBlock : statement.ElseBlock;

        if (block is null)
        {
            yield break;
        }

        await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, block, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    /// <summary>
    /// Pulls one item without letting an exception cross an async-iterator catch boundary.
    /// Callers can therefore yield <see cref="CapturedEnumeratorMove.Value"/> immediately and
    /// rethrow a captured control-flow signal only after that value has left the iterator.
    /// </summary>
    private static async ValueTask<CapturedEnumeratorMove> MoveNextCapturingFailureAsync(
        IAsyncEnumerator<object?> enumerator)
    {
        try
        {
            if (!await enumerator.MoveNextAsync())
                return default;

            return new CapturedEnumeratorMove(
                HasValue: true,
                enumerator.Current,
                Failure: null);
        }
        catch (Exception failure)
        {
            return new CapturedEnumeratorMove(
                HasValue: false,
                Value: null,
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure));
        }
    }

    private async IAsyncEnumerable<object?> EvaluateForStatementAsync(
        string sourceName,
        string sourceText,
        ForStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, statement.VariableName, statement.Span, "reserved runtime namespace");

        // `for line in curl -s … { … }` consumes the values, so the child's stdout must be
        // captured even at a terminal. The loop still streams — the flag changes how the
        // process is spawned, not how values flow (TS-P1-30).
        await foreach (var item in EvaluatePipelineAsync(
                               sourceName, sourceText, statement.Source, cancellationToken,
                               outputIsCaptured: true)
                           .WithCancellation(cancellationToken))
        {
            await foreach (var current in ShellIterationUtilities
                               .ExpandIterationItemsAsync(item, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
                await using (var bodyEnumerator = ExecuteBlockAsync(
                                 sourceName,
                                 sourceText,
                                 statement.Body,
                                 cancellationToken,
                                 new Dictionary<string, object?>(StringComparer.Ordinal)
                                 {
                                     [statement.VariableName] = current,
                                     ["_"] = current,
                                 })
                                 .GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                        if (move.Failure is not null)
                        {
                            if (move.Failure.SourceException is ShellControlFlowException)
                            {
                                pendingControlFlow = move.Failure;
                                break;
                            }

                            move.Failure.Throw();
                        }

                        if (!move.HasValue)
                            break;

                        yield return move.Value;
                    }
                }

                if (pendingControlFlow?.SourceException is ReturnSignalException)
                {
                    pendingControlFlow.Throw();
                    yield break;
                }

                if (pendingControlFlow?.SourceException is BreakSignalException)
                {
                    yield break;
                }

                if (pendingControlFlow?.SourceException is ContinueSignalException)
                {
                    continue;
                }

                pendingControlFlow?.Throw();
            }
        }
    }

    private async IAsyncEnumerable<object?> EvaluateWhileStatementAsync(
        string sourceName,
        string sourceText,
        WhileStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
            await using (var bodyEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.Body,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException)
                        {
                            pendingControlFlow = move.Failure;
                            break;
                        }

                        move.Failure.Throw();
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (pendingControlFlow?.SourceException is ReturnSignalException)
            {
                pendingControlFlow.Throw();
                yield break;
            }

            if (pendingControlFlow?.SourceException is BreakSignalException)
            {
                yield break;
            }

            if (pendingControlFlow?.SourceException is ContinueSignalException)
            {
                continue;
            }

            pendingControlFlow?.Throw();
        }
    }

    private async IAsyncEnumerable<object?> EvaluateUntilStatementAsync(
        string sourceName,
        string sourceText,
        UntilStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!await EvaluateConditionAsync(sourceName, sourceText, statement.Condition, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingControlFlow = null;
            await using (var bodyEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.Body,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(bodyEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException)
                        {
                            pendingControlFlow = move.Failure;
                            break;
                        }

                        move.Failure.Throw();
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (pendingControlFlow?.SourceException is ReturnSignalException)
            {
                pendingControlFlow.Throw();
                yield break;
            }

            if (pendingControlFlow?.SourceException is BreakSignalException)
            {
                yield break;
            }

            if (pendingControlFlow?.SourceException is ContinueSignalException)
            {
                continue;
            }

            pendingControlFlow?.Throw();
        }
    }

    private async IAsyncEnumerable<object?> EvaluateTryStatementAsync(
        string sourceName,
        string sourceText,
        TryStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingFailure = null;
        Exception? caughtException = null;
        var bodyCompleted = false;
        try
        {
            await using (var tryEnumerator = ExecuteBlockAsync(
                             sourceName,
                             sourceText,
                             statement.TryBlock,
                             cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(tryEnumerator);
                    if (move.Failure is not null)
                    {
                        if (move.Failure.SourceException is ShellControlFlowException ||
                            statement.CatchClause is null)
                        {
                            pendingFailure = move.Failure;
                        }
                        else
                        {
                            caughtException = move.Failure.SourceException;
                        }

                        break;
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            if (caughtException is not null)
            {
                var catchClause = statement.CatchClause!;
                var catchLocals = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (!string.IsNullOrWhiteSpace(catchClause.VariableName))
                {
                    EnsureBindingNameIsNotReserved(
                        sourceName,
                        sourceText,
                        catchClause.VariableName!,
                        catchClause.Span,
                        "reserved runtime namespace");
                    catchLocals[catchClause.VariableName!] = CreateCaughtErrorValue(caughtException);
                }

                await using var catchEnumerator = ExecuteBlockAsync(
                        sourceName,
                        sourceText,
                        catchClause.Body,
                        cancellationToken,
                        catchLocals)
                    .GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    var move = await MoveNextCapturingFailureAsync(catchEnumerator);
                    if (move.Failure is not null)
                    {
                        pendingFailure = move.Failure;
                        break;
                    }

                    if (!move.HasValue)
                        break;

                    yield return move.Value;
                }
            }

            bodyCompleted = true;
        }
        finally
        {
            if (!bodyCompleted && statement.FinallyBlock is not null)
            {
                // A downstream short-circuit disposes this iterator at the active yield. The
                // source-level finally must still run to completion, although its values have no
                // remaining consumer and are therefore discarded on this disposal path.
                await foreach (var _ in ExecuteBlockAsync(
                                   sourceName,
                                   sourceText,
                                   statement.FinallyBlock,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                }
            }
        }

        if (statement.FinallyBlock is not null)
        {
            // Finish cleanup before exposing its values. Besides matching the established
            // finally ordering, this ensures a downstream short-circuit cannot abandon the
            // remaining cleanup statements after taking the first finally value.
            var finallyValues = await AsyncEnumerableExtensions.ToListAsync(
                ExecuteBlockAsync(
                    sourceName,
                    sourceText,
                    statement.FinallyBlock,
                    cancellationToken),
                cancellationToken);
            foreach (var value in finallyValues)
            {
                yield return value;
            }
        }

        pendingFailure?.Throw();
    }

    private async IAsyncEnumerable<object?> EvaluateSwitchStatementAsync(
        string sourceName,
        string sourceText,
        SwitchStatementSyntax statement,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var switchValue = await EvaluateArgumentAsync(sourceName, sourceText, statement.Value, cancellationToken);
        BlockSyntax? blockToExecute = null;

        foreach (var @case in statement.Cases)
        {
            if (!await MatchesPatternAsync(switchValue, sourceName, sourceText, @case.MatchExpression, cancellationToken))
            {
                continue;
            }

            if (@case.Guard is not null)
            {
                if (!await EvaluateGuardWithCurrentItemAsync(sourceName, sourceText, @case.Guard, switchValue, cancellationToken))
                {
                    continue;
                }
            }

            blockToExecute = @case.Body;
            break;
        }

        blockToExecute ??= statement.DefaultBlock;

        if (blockToExecute is null)
        {
            yield break;
        }

        await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, blockToExecute, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return value;
        }
    }

    /// <summary>
    /// Tries to invoke a user-defined binary operator method on <paramref name="instance"/>.
    /// The left operand's class is tried first; the operator method receives <paramref name="other"/>
    /// as its single argument.
    /// </summary>
    private static ValueTask<(bool Matched, object? Value)> TryInvokeClassBinaryOperatorAsync(
        ToshClassInstance instance,
        string @operator,
        object? other,
        CancellationToken cancellationToken)
    {
        return instance.Definition.TryInvokeSpecialInstanceMethodAsync(
            instance,
            @operator,
            new object?[] { other },
            cancellationToken);
    }

    /// <summary>
    /// Evaluates an eager binary operator through ToastScript's asynchronous
    /// class protocol before falling back to the shared CLR/runtime semantics.
    /// Ordinary expressions and compound assignments both use this boundary so
    /// overload order, cancellation, user throws, and structured diagnostics do
    /// not depend on the syntax that reached the operator.
    /// </summary>
    private async ValueTask<object?> EvaluateBinaryOperatorAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Symmetric operator overloading is intentionally left-biased:
            // consult the right operand only when the left has no matching
            // overload, preserving the established ToastScript protocol.
            if (left is ToshClassInstance leftInstance)
            {
                var leftOverload = await TryInvokeClassBinaryOperatorAsync(
                    leftInstance,
                    @operator,
                    right,
                    cancellationToken);
                if (leftOverload.Matched)
                {
                    return leftOverload.Value;
                }
            }

            if (right is ToshClassInstance rightInstance)
            {
                var rightOverload = await TryInvokeClassBinaryOperatorAsync(
                    rightInstance,
                    @operator,
                    left,
                    cancellationToken);
                if (rightOverload.Matched)
                {
                    return rightOverload.Value;
                }
            }

            return await EvaluateFallbackBinaryOperatorAsync(
                left,
                @operator,
                right,
                cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ShellControlFlowException)
        {
            throw;
        }
        catch (Exception exception) when (IsToshThrown(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(
                sourceName,
                sourceText,
                span,
                exception);
        }
    }

    private async ValueTask<object?> EvaluateFallbackBinaryOperatorAsync(
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (left is ShellTextLine leftLine)
        {
            left = leftLine.Text;
        }
        if (right is ShellTextLine rightLine)
        {
            right = rightLine.Text;
        }

        return @operator switch
        {
            "==" => await AreEqualAsync(left, right, cancellationToken),
            "!=" => !await AreEqualAsync(left, right, cancellationToken),
            "in" or "is-in" => await IsInAsync(left, right, cancellationToken),
            "not-in" or "is-not-in" => !await IsInAsync(left, right, cancellationToken),
            "=~" => await RegexMatchAsync(left, right, cancellationToken),
            "!~" => !await RegexMatchAsync(left, right, cancellationToken),
            "contains" => await ContainsAsync(left, right, cancellationToken),
            "starts-with" => await StartsWithAsync(left, right, cancellationToken),
            "ends-with" => await EndsWithAsync(left, right, cancellationToken),
            "+" when left is string => (string)left
                + await ToOperatorStringAsync(right, cancellationToken),
            "+" when right is string => await ToOperatorStringAsync(left, cancellationToken)
                + (string)right,
            _ => await EvaluateClrFallbackOperatorAsync(
                left,
                @operator,
                right,
                cancellationToken),
        };
    }

    private async ValueTask<object?> EvaluateClrFallbackOperatorAsync(
        object? left,
        string @operator,
        object? right,
        CancellationToken cancellationToken)
    {
        // OperatorEvaluator performs string conversions synchronously. Keep
        // explicit CLR operators as the compatibility boundary, but never let
        // that fallback synchronously re-enter a ToastScript ToString body.
        if (@operator is not ("is" or "is-not" or "as") &&
            left is string && right is ToshClassInstance)
        {
            right = await ToOperatorStringAsync(right, cancellationToken);
        }
        else if (@operator is not ("is" or "is-not" or "as") &&
                 right is string && left is ToshClassInstance)
        {
            left = await ToOperatorStringAsync(left, cancellationToken);
        }
        else if (@operator is "is" or "is-not" or "as" &&
                 right is ToshClassInstance)
        {
            right = await ToOperatorStringAsync(right, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return OperatorEvaluator.EvaluateBinary(left, @operator, right);
    }

    private async ValueTask<bool> MatchesOperatorAsync(
        object? actual,
        string @operator,
        object? expected,
        bool nullable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (actual is ShellTextLine actualLine)
        {
            actual = actualLine.Text;
        }
        if (expected is ShellTextLine expectedLine)
        {
            expected = expectedLine.Text;
        }

        switch (@operator)
        {
            case "==":
                return await AreEqualAsync(actual, expected, cancellationToken);
            case "!=":
                return !await AreEqualAsync(actual, expected, cancellationToken);
            case "in":
                return await IsInAsync(actual, expected, cancellationToken);
            case "not-in":
                return !await IsInAsync(actual, expected, cancellationToken);
            case "=~":
                return await RegexMatchAsync(actual, expected, cancellationToken);
            case "!~":
                return !await RegexMatchAsync(actual, expected, cancellationToken);
            case "contains":
                return await ContainsAsync(actual, expected, cancellationToken);
            case "starts-with":
                return await StartsWithAsync(actual, expected, cancellationToken);
            case "ends-with":
                return await EndsWithAsync(actual, expected, cancellationToken);
        }

        if (@operator is not ("is" or "is-not") &&
            actual is string && expected is ToshClassInstance)
        {
            expected = await ToOperatorStringAsync(expected, cancellationToken);
        }
        else if (@operator is not ("is" or "is-not") &&
                 expected is string && actual is ToshClassInstance)
        {
            actual = await ToOperatorStringAsync(actual, cancellationToken);
        }
        else if (@operator is "is" or "is-not" &&
                 expected is ToshClassInstance)
        {
            expected = await ToOperatorStringAsync(expected, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return OperatorEvaluator.Matches(actual, @operator, expected, nullable);
    }

    /// <summary>
    /// Asynchronous equality, kept alongside <see cref="OperatorEvaluator.AreEqual"/>
    /// because a user-defined <c>Equals</c> may be asynchronous.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so <c>EqualityParityTests</c> can
    /// compare the two implementations directly instead of inferring which path a
    /// script reached — the same reason <c>TS-P1-24</c> opened
    /// <c>TryConvertAnnotatedValue</c> to its drift guard. These two already
    /// diverged once: the <c>TS-P1-14</c> equality change landed here only after
    /// <c>TS-P1-15</c> discovered it had been applied to the evaluator alone.
    /// </remarks>
    internal async ValueTask<bool> AreEqualAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Records and dictionaries compare by name, not by enumeration order
        // (TS-P1-10). Delegated to OperatorEvaluator rather than reimplemented: this
        // method and OperatorEvaluator.AreEqual are the parallel pair TS-P1-24 was filed
        // for, and the first attempt at this fix landed only on the synchronous side —
        // `==` goes through here, so the defect survived a change that looked complete.
        if (OperatorEvaluator.TryCompareByName(actual, expected, out var byName))
        {
            return byName;
        }

        // Preserve OperatorEvaluator's collection-first semantics, including
        // recursive element comparison and ordered enumeration.
        if (actual is IEnumerable actualCollection && actual is not string &&
            expected is IEnumerable expectedCollection && expected is not string)
        {
            var actualEnumerator = actualCollection.GetEnumerator();
            var expectedEnumerator = expectedCollection.GetEnumerator();

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var actualHasNext = actualEnumerator.MoveNext();
                    var expectedHasNext = expectedEnumerator.MoveNext();

                    if (!actualHasNext && !expectedHasNext)
                    {
                        return true;
                    }

                    if (actualHasNext != expectedHasNext)
                    {
                        return false;
                    }

                    if (!await AreEqualAsync(
                            actualEnumerator.Current,
                            expectedEnumerator.Current,
                            cancellationToken))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                (actualEnumerator as IDisposable)?.Dispose();
                (expectedEnumerator as IDisposable)?.Dispose();
            }
        }

        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        // TS-P1-15: an enum member equals another member of the same
        // enum and equals its own backing value. Kept in step with
        // OperatorEvaluator.AreEqual so both surfaces agree.
        if ((actual is IShellEnumValue || expected is IShellEnumValue) &&
            actual is not string && expected is not string &&
            ShellEnumComparison.AreEquivalent(actual, expected))
        {
            return true;
        }

        // TS-P1-26: both directions are attempted, and equality holds if either
        // matches. See OperatorEvaluator.AreEqual for why returning on the first
        // successful *conversion* rather than the first successful *equality*
        // made this asymmetric.
        var convertedExpected = await TryConvertForEqualityAsync(
            expected,
            actual.GetType(),
            cancellationToken);
        if (convertedExpected.Converted &&
            await ObjectEqualsAsync(actual, convertedExpected.Value, cancellationToken))
        {
            return true;
        }

        var convertedActual = await TryConvertForEqualityAsync(
            actual,
            expected.GetType(),
            cancellationToken);
        if (convertedActual.Converted &&
            await ObjectEqualsAsync(convertedActual.Value, expected, cancellationToken))
        {
            return true;
        }

        // A successful conversion decides the answer even when it produced
        // unequal values; see OperatorEvaluator.AreEqual. Falling through here
        // would dispatch a user-defined `Equals` with an operand of the wrong
        // type — `ValueProbe.Equals("PROBE")` reading `$other.Value` off a
        // string — which the original early return shielded against.
        if (convertedExpected.Converted || convertedActual.Converted)
        {
            return false;
        }

        // TS-P1-14: no case-insensitive ToString fallback here either.
        // This mirrors OperatorEvaluator.AreEqual so the interpreter and
        // the shared runtime dispatcher agree on what equality means.
        return await ObjectEqualsAsync(actual, expected, cancellationToken);
    }

    private async ValueTask<(bool Converted, object? Value)> TryConvertForEqualityAsync(
        object? value,
        Type targetType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType == typeof(string) && value is ToshClassInstance)
        {
            return (
                true,
                await ToOperatorStringAsync(value, cancellationToken));
        }

        return TypeConversion.TryConvert(value, targetType, out var converted)
            ? (true, converted)
            : (false, null);
    }

    private async ValueTask<bool> ObjectEqualsAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(actual, expected))
        {
            return true;
        }
        if (actual is null || expected is null)
        {
            return false;
        }

        if (actual is ToshClassInstance classInstance)
        {
            var invocation = await classInstance.Definition.TryInvokeSpecialInstanceMethodAsync(
                classInstance,
                nameof(Equals),
                new object?[] { expected },
                cancellationToken);
            return invocation.Matched && OperatorEvaluator.ToBoolean(invocation.Value);
        }

        if (actual is ToshRecordInstance record)
        {
            if (expected is not ToshRecordInstance otherRecord ||
                !ReferenceEquals(record.Definition, otherRecord.Definition))
            {
                return false;
            }

            foreach (var field in record.Definition.Fields)
            {
                record.TryGetMember(field.Name, out var leftValue);
                otherRecord.TryGetMember(field.Name, out var rightValue);
                if (!await AreEqualAsync(leftValue, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is ToshStructInstance @struct)
        {
            if (expected is not ToshStructInstance otherStruct ||
                !ReferenceEquals(@struct.Definition, otherStruct.Definition))
            {
                return false;
            }

            foreach (var field in @struct.Definition.Fields)
            {
                @struct.TryGetMember(field.Name, out var leftValue);
                otherStruct.TryGetMember(field.Name, out var rightValue);
                if (!await AreEqualAsync(leftValue, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is ToshUnionVariantInstance union)
        {
            if (expected is not ToshUnionVariantInstance otherUnion ||
                !string.Equals(
                    union.UnionDefinition.Name,
                    otherUnion.UnionDefinition.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    union.VariantName,
                    otherUnion.VariantName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fields = union.GetMembers()
                .Where(member => !string.Equals(
                    member.Key,
                    "Variant",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var otherFields = otherUnion.GetMembers()
                .Where(member => !string.Equals(
                    member.Key,
                    "Variant",
                    StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    member => member.Key,
                    member => member.Value,
                    StringComparer.OrdinalIgnoreCase);
            if (fields.Length != otherFields.Count)
            {
                return false;
            }

            foreach (var field in fields)
            {
                if (!otherFields.TryGetValue(field.Key, out var rightValue) ||
                    !await ObjectEqualsAsync(field.Value, rightValue, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        }

        if (actual is DictionaryEntry entry)
        {
            return expected is DictionaryEntry otherEntry
                && await ObjectEqualsAsync(entry.Key, otherEntry.Key, cancellationToken)
                && await ObjectEqualsAsync(entry.Value, otherEntry.Value, cancellationToken);
        }

        var actualType = actual.GetType();
        if (actualType.IsGenericType &&
            actualType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            if (expected.GetType() != actualType)
            {
                return false;
            }

            var keyProperty = actualType.GetProperty(nameof(KeyValuePair<object, object>.Key))!;
            var valueProperty = actualType.GetProperty(nameof(KeyValuePair<object, object>.Value))!;
            return await ObjectEqualsAsync(
                    keyProperty.GetValue(actual),
                    keyProperty.GetValue(expected),
                    cancellationToken)
                && await ObjectEqualsAsync(
                    valueProperty.GetValue(actual),
                    valueProperty.GetValue(expected),
                    cancellationToken);
        }

        // Arbitrary CLR Equals implementations are intentionally synchronous;
        // they are a host protocol boundary rather than ToastScript execution.
        return actual.Equals(expected);
    }

    private async ValueTask<bool> IsInAsync(
        object? value,
        object? candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (candidates is null)
        {
            return false;
        }

        if (candidates is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await AreEqualAsync(value, entry.Key, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IShellEnumerableObject { HasShellItems: true } shellEnumerable)
        {
            await foreach (var candidate in shellEnumerable
                               .EnumerateShellItemsAsync(cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (await AreEqualAsync(value, candidate, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is IEnumerable enumerable && candidates is not string)
        {
            foreach (var candidate in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await AreEqualAsync(value, candidate, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        if (candidates is string text)
        {
            return text.Contains(
                await ToOperatorStringAsync(value, cancellationToken),
                StringComparison.Ordinal);
        }

        return await AreEqualAsync(value, candidates, cancellationToken);
    }

    private async ValueTask<bool> RegexMatchAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        var input = await ToOperatorStringAsync(actual, cancellationToken);
        if (expected is Regex regex)
        {
            return regex.IsMatch(input);
        }

        var pattern = await ToOperatorStringAsync(expected, cancellationToken);
        return Regex.IsMatch(input, pattern);
    }

    private async ValueTask<bool> ContainsAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        if (actual is string text)
        {
            return text.Contains(
                await ToOperatorStringAsync(expected, cancellationToken),
                StringComparison.Ordinal);
        }

        if (actual is IDictionary ||
            actual is IShellEnumerableObject { HasShellItems: true } ||
            actual is IEnumerable)
        {
            return await IsInAsync(expected, actual, cancellationToken);
        }

        return false;
    }

    private async ValueTask<bool> StartsWithAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        return (await ToOperatorStringAsync(actual, cancellationToken)).StartsWith(
            await ToOperatorStringAsync(expected, cancellationToken),
            StringComparison.Ordinal);
    }

    private async ValueTask<bool> EndsWithAsync(
        object? actual,
        object? expected,
        CancellationToken cancellationToken)
    {
        if (actual is null)
        {
            return false;
        }

        return (await ToOperatorStringAsync(actual, cancellationToken)).EndsWith(
            await ToOperatorStringAsync(expected, cancellationToken),
            StringComparison.Ordinal);
    }

    private async ValueTask<string> ToOperatorStringAsync(
        object? value,
        CancellationToken cancellationToken) =>
        await ToOperatorStringAsync(
            value,
            cancellationToken,
            new HashSet<ToshClassInstance>(ReferenceEqualityComparer.Instance));

    private async ValueTask<string> ToOperatorStringAsync(
        object? value,
        CancellationToken cancellationToken,
        HashSet<ToshClassInstance> activeInstances)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value is not ToshClassInstance instance)
        {
            return value?.ToString() ?? string.Empty;
        }

        if (!activeInstances.Add(instance))
        {
            return instance.Definition.Name;
        }

        try
        {
            var invocation = await instance.Definition.TryInvokeSpecialInstanceMethodAsync(
                instance,
                nameof(ToString),
                Array.Empty<object?>(),
                cancellationToken);
            if (!invocation.Matched)
            {
                return instance.Definition.Name;
            }

            return invocation.Value is ToshClassInstance nested
                ? await ToOperatorStringAsync(
                    nested,
                    cancellationToken,
                    activeInstances)
                : invocation.Value?.ToString() ?? string.Empty;
        }
        finally
        {
            activeInstances.Remove(instance);
        }
    }

    /// <summary>
    /// Tries to invoke a zero-argument user-defined unary operator method on <paramref name="instance"/>.
    /// </summary>
    private static ValueTask<(bool Matched, object? Value)> TryInvokeClassUnaryOperatorAsync(
        ToshClassInstance instance,
        string @operator,
        CancellationToken cancellationToken)
    {
        return instance.Definition.TryInvokeSpecialInstanceMethodAsync(
            instance,
            @operator,
            Array.Empty<object?>(),
            cancellationToken);
    }

    private async Task<bool> MatchesPatternAsync(
        object? switchValue,
        string sourceName,
        string sourceText,
        ArgumentSyntax pattern,
        CancellationToken cancellationToken)
    {
        switch (pattern)
        {
            case ComparisonPatternSyntax cp:
                {
                    var operand = await EvaluateArgumentAsync(sourceName, sourceText, cp.Operand, cancellationToken);
                    return await MatchesOperatorAsync(
                        switchValue,
                        cp.Operator,
                        operand,
                        nullable: false,
                        cancellationToken);
                }

            case RangeArgumentSyntax range:
                {
                    var startValue = await EvaluateArgumentAsync(sourceName, sourceText, range.Start, cancellationToken);
                    if (range.End is null)
                    {
                        // Infinite range in match: only check lower bound
                        return await MatchesOperatorAsync(
                            switchValue,
                            ">=",
                            startValue,
                            nullable: false,
                            cancellationToken);
                    }
                    var endValue = await EvaluateArgumentAsync(sourceName, sourceText, range.End, cancellationToken);
                    return await MatchesOperatorAsync(
                            switchValue,
                            ">=",
                            startValue,
                            nullable: false,
                            cancellationToken)
                        && await MatchesOperatorAsync(
                            switchValue,
                            "<=",
                            endValue,
                            nullable: false,
                            cancellationToken);
                }

            default:
                {
                    var patternValue = await EvaluateArgumentAsync(sourceName, sourceText, pattern, cancellationToken);
                    return await AreEqualAsync(switchValue, patternValue, cancellationToken);
                }
        }
    }

    private async Task<VariableBinding> EvaluateVariableBindingAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken)
    {
        if (pipeline.Stages.Count == 1 &&
            pipeline.Stages[0] is ExpressionPipelineStageSyntax
            {
                Expression: VariableReferenceArgumentSyntax variableReference,
            } &&
            TryGetVariableBinding(variableReference.Name, out var existingBinding))
        {
            return existingBinding;
        }

        if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, pipeline, cancellationToken) is { Matched: true } raw)
        {
            return new VariableBinding(raw.Value,
                ReplayAsPipeline: ShouldReplayAsPipeline(raw.Value),
                IsAllocatedOnly: false);
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(
            EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, outputIsCaptured: true),
            cancellationToken);

        return values.Count switch
        {
            0 => new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: false),
            1 => new VariableBinding(values[0],
                ReplayAsPipeline: ShouldReplayAsPipeline(values[0]),
                IsAllocatedOnly: false),
            _ => new VariableBinding(values.ToArray(), ReplayAsPipeline: true, IsAllocatedOnly: false),
        };
    }

    private async IAsyncEnumerable<object?> EvaluatePipelineWithRedirectionAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        bool outputIsCaptured = false)
    {
        // Resolve input redirection (in< / i<) before executing the pipeline.
        if (pipeline.InputRedirection is { } inputRedirection)
        {
            var inputTarget = await EvaluateArgumentAsync(sourceName, sourceText, inputRedirection.Source, cancellationToken);
            var inputPath = ResolveInputRedirectionPath(sourceName, sourceText, inputRedirection, inputTarget);
            initialInput = ReadLinesAsync(inputPath, cancellationToken);
        }

        if (pipeline.Redirections is null or { Count: 0 })
        {
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured)
                               .WithCancellation(cancellationToken))
            {
                yield return value;
            }

            yield break;
        }

        var resolvedRedirections = new List<ResolvedPipelineRedirection>();

        foreach (var redirection in pipeline.Redirections)
        {
            var targetPath = await EvaluateArgumentAsync(sourceName, sourceText, redirection.Target, cancellationToken);
            var path = ResolveRedirectionTargetPath(sourceName, sourceText, redirection, targetPath);
            resolvedRedirections.Add(new ResolvedPipelineRedirection(path, redirection.Stream, redirection.Mode));
        }

        var bufferedPlans = CreateBufferedPipelineRedirectionPlans(resolvedRedirections);
        var disposableWriters = new List<TextWriter>();
        var outputTargets = new List<TextWriter>();
        var errorTargets = new List<TextWriter>();
        TextWriter? originalOutput = null;
        TextWriter? originalError = null;

        try
        {
            foreach (var plan in bufferedPlans.Values)
            {
                if (plan.HasOutput)
                {
                    outputTargets.Add(plan.OutputWriter);
                }

                if (plan.HasError)
                {
                    errorTargets.Add(plan.ErrorWriter);
                }
            }

            foreach (var redirection in resolvedRedirections)
            {
                if (bufferedPlans.ContainsKey(redirection.Path))
                {
                    continue;
                }

                var mode = redirection.Mode == RedirectionMode.Append ? FileMode.Append : FileMode.Create;

                FileStream stream;
                try
                {
                    stream = File.Open(redirection.Path, mode, FileAccess.Write, FileShare.Read);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.redirection_target_unavailable",
                        Title: $"Cannot open '{redirection.Path}' for redirection: {exception.Message}",
                        SourceName: null,
                        SourceText: null,
                        Span: null,
                        Label: "this redirection target could not be opened for writing",
                        Help: "check that the directory exists and is writable. Redirection creates the "
                            + "file but not the directories above it."));
                }

                var writer = TextWriter.Synchronized(new StreamWriter(stream, RedirectionEncoding));
                disposableWriters.Add(writer);

                if (RedirectionIncludesOutput(redirection.Stream))
                {
                    outputTargets.Add(writer);
                }

                if (RedirectionIncludesError(redirection.Stream))
                {
                    errorTargets.Add(writer);
                }
            }

            if (outputTargets.Count > 0)
            {
                originalOutput = Runtime.Output;
                Runtime.Output = CreateCompositeWriter(outputTargets);
            }

            if (errorTargets.Count > 0)
            {
                originalError = Runtime.Error;
                Runtime.Error = CreateCompositeWriter(errorTargets);
            }

            var hasOutputRedirection = outputTargets.Count > 0;

            // Deliberately NOT captured: this is the top-level display path, and terminal
            // passthrough is exactly what it is for. Redirection is handled below from the
            // values the pipeline yields (TS-P1-30).
            await foreach (var value in EvaluatePipelineAsync(sourceName, sourceText, pipeline, cancellationToken, initialInput, firstCommandArguments, outputIsCaptured: outputIsCaptured)
                               .WithCancellation(cancellationToken))
            {
                if (hasOutputRedirection)
                {
                    var text = value switch
                    {
                        ShellTextLine line => line.Text,
                        _ => Runtime.Formatter.Format(value),
                    };

                    await Runtime.Output.WriteLineAsync(text);
                    await Runtime.Output.FlushAsync(cancellationToken);
                }
                else
                {
                    // No stdout redirection — pass values through (e.g., only stderr was redirected)
                    yield return value;
                }
            }
        }
        finally
        {
            if (originalOutput is not null)
            {
                Runtime.Output = originalOutput;
            }

            if (originalError is not null)
            {
                Runtime.Error = originalError;
            }

            await FlushBufferedPipelineRedirectionsAsync(bufferedPlans.Values, cancellationToken);

            foreach (var writer in disposableWriters)
            {
                await writer.DisposeAsync();
            }
        }
    }

    private static TextWriter CreateCompositeWriter(IReadOnlyList<TextWriter> writers)
        => writers.Count == 1 ? writers[0] : new CompositeTextWriter(writers);

    private static bool RedirectionIncludesOutput(RedirectionStream stream)
        => stream is RedirectionStream.Output or RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput;

    private static bool RedirectionIncludesError(RedirectionStream stream)
        => stream is RedirectionStream.Error or RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput;

    private static Dictionary<string, BufferedPipelineRedirectionPlan> CreateBufferedPipelineRedirectionPlans(
        IReadOnlyList<ResolvedPipelineRedirection> redirections)
    {
        return redirections
            .GroupBy(static redirection => redirection.Path, StringComparer.OrdinalIgnoreCase)
            .Where(static group =>
                group.Count() > 1 ||
                group.Any(static redirection => redirection.Stream is RedirectionStream.OutputThenError or RedirectionStream.ErrorThenOutput))
            .ToDictionary(
                static group => group.Key,
                static group => new BufferedPipelineRedirectionPlan(group.Key, group.ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task FlushBufferedPipelineRedirectionsAsync(
        IEnumerable<BufferedPipelineRedirectionPlan> plans,
        CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
        {
            var outputText = plan.OutputBuffer.ToString();
            var errorText = plan.ErrorBuffer.ToString();

            foreach (var redirection in plan.Redirections)
            {
                var text = GetRedirectionContent(redirection.Stream, outputText, errorText);
                var fileMode = redirection.Mode == RedirectionMode.Append ? FileMode.Append : FileMode.Create;

                FileStream stream;
                try
                {
                    stream = File.Open(redirection.Path, fileMode, FileAccess.Write, FileShare.Read);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.redirection_target_unavailable",
                        Title: $"Cannot open '{redirection.Path}' for redirection: {exception.Message}",
                        SourceName: null,
                        SourceText: null,
                        Span: null,
                        Label: "this redirection target could not be opened for writing",
                        Help: "check that the directory exists and is writable. Redirection creates the "
                            + "file but not the directories above it."));
                }

                await using var writer = new StreamWriter(stream, RedirectionEncoding);

                if (text.Length > 0)
                {
                    await writer.WriteAsync(text.AsMemory(), cancellationToken);
                }

                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static string GetRedirectionContent(
        RedirectionStream stream,
        string outputText,
        string errorText)
        => stream switch
        {
            RedirectionStream.Output => outputText,
            RedirectionStream.Error => errorText,
            RedirectionStream.OutputThenError => outputText + errorText,
            RedirectionStream.ErrorThenOutput => outputText + errorText,
            _ => string.Empty,
        };

    private sealed record ResolvedPipelineRedirection(
        string Path,
        RedirectionStream Stream,
        RedirectionMode Mode);

    private sealed class BufferedPipelineRedirectionPlan
    {
        public BufferedPipelineRedirectionPlan(
            string path,
            IReadOnlyList<ResolvedPipelineRedirection> redirections)
        {
            Path = path;
            Redirections = redirections;
            OutputWriter = TextWriter.Synchronized(new StringWriter(OutputBuffer));
            ErrorWriter = TextWriter.Synchronized(new StringWriter(ErrorBuffer));
            HasOutput = redirections.Any(static redirection => RedirectionIncludesOutput(redirection.Stream));
            HasError = redirections.Any(static redirection => RedirectionIncludesError(redirection.Stream));
        }

        public string Path { get; }

        public IReadOnlyList<ResolvedPipelineRedirection> Redirections { get; }

        public StringBuilder OutputBuffer { get; } = new();

        public StringBuilder ErrorBuffer { get; } = new();

        public TextWriter OutputWriter { get; }

        public TextWriter ErrorWriter { get; }

        public bool HasOutput { get; }

        public bool HasError { get; }
    }

    private string ResolveRedirectionTargetPath(
        string sourceName,
        string sourceText,
        RedirectionSyntax redirection,
        object? targetPath)
    {
        if (targetPath is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.redirection_target_null",
                Title: "Redirection target cannot be null.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this redirection target evaluated to null"));
        }

        IReadOnlyList<string> resolvedPaths = targetPath switch
        {
            FileSystemInfo fileSystemInfo => [fileSystemInfo.FullName],
            FileSystemEntry entry => [entry.FullName],
            string text => ShellPathArguments.Expand(Runtime.CurrentDirectory, text),
            _ => [PathUtilities.ResolvePath(Runtime.CurrentDirectory, targetPath.ToString() ?? string.Empty)],
        };

        if (resolvedPaths.Count == 1)
        {
            return resolvedPaths[0];
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.redirection_target_not_single_path",
            Title: "Redirection targets must resolve to exactly one path.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: redirection.Span,
            Label: "this target resolved to multiple paths",
            Help: "use a single file path or quote the pattern if you meant a literal name."));
    }

    private string ResolveInputRedirectionPath(
        string sourceName,
        string sourceText,
        InputRedirectionSyntax redirection,
        object? sourcePath)
    {
        if (sourcePath is null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.input_redirection_source_null",
                Title: "Input redirection source cannot be null.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this input redirection source evaluated to null"));
        }

        var resolved = sourcePath switch
        {
            FileSystemInfo fileSystemInfo => fileSystemInfo.FullName,
            FileSystemEntry entry => entry.FullName,
            string text => PathUtilities.ResolvePath(Runtime.CurrentDirectory, text),
            _ => PathUtilities.ResolvePath(Runtime.CurrentDirectory, sourcePath.ToString() ?? string.Empty),
        };

        if (!File.Exists(resolved))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.input_redirection_source_not_found",
                Title: $"Input redirection source '{resolved}' does not exist.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: redirection.Span,
                Label: "this file does not exist"));
        }

        return resolved;
    }

    private static async IAsyncEnumerable<object?> ReadLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private IAsyncEnumerable<object?> EvaluatePipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        PipelineExitStatusTracker? pipelineExitStatusTracker = null,
        bool outputIsCaptured = false)
    {
        var ownsTracker = pipelineExitStatusTracker is null;
        pipelineExitStatusTracker ??= new PipelineExitStatusTracker(Runtime.Config.Shell.Pipefail);
        IAsyncEnumerable<object?> current = initialInput ?? AsyncEnumerableExtensions.Empty<object?>();
        IReadOnlyList<object?>? pendingFirstCommandArguments = firstCommandArguments;
        var isPipelined = pipeline.Stages.Count > 1 || initialInput is not null;

        // If lowering recognised a fusable trailing pattern (e.g.
        // `... | sort | first N`), execute the upstream stages normally
        // and replace the trailing stages with a specialised iterator.
        var fusion = pipeline.Fusion;
        var stageCount = pipeline.Stages.Count;
        var stagesToRun = fusion is null ? stageCount : stageCount - GetStagesConsumed(fusion);

        for (int i = 0; i < stagesToRun; i++)
        {
            var stage = pipeline.Stages[i];
            current = stage switch
            {
                ExpressionPipelineStageSyntax expressionStage => ExecuteExpressionStageAsync(
                    sourceName,
                    sourceText,
                    expressionStage,
                    cancellationToken),
                CommandSyntax commandSyntax => ExecuteCommandSyntaxAsync(
                    sourceName,
                    sourceText,
                    commandSyntax,
                    current,
                    pendingFirstCommandArguments,
                    isPipelined,
                    pipelineExitStatusTracker,
                    cancellationToken,
                    outputIsCaptured: outputIsCaptured),
                PipeForwardStageSyntax pipeForward => ExecutePipeForwardStageAsync(
                    sourceName,
                    sourceText,
                    pipeForward,
                    current,
                    pipelineExitStatusTracker,
                    cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported pipeline stage syntax: {stage.GetType().Name}."),
            };

            if (stage is CommandSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
        }

        if (fusion is SortFirstFusion sortFirst)
        {
            current = ExecuteSortFirstFusionAsync(current, sortFirst, cancellationToken);
        }

        return FinalizePipelineExitCodeAsync(current, pipelineExitStatusTracker, ownsTracker, cancellationToken);
    }

    private static int GetStagesConsumed(Tosh.Language.Binding.PipelineFusion fusion) => fusion switch
    {
        SortFirstFusion sortFirst => sortFirst.StagesConsumed,
        _ => 0,
    };

    /// <summary>
    /// Specialised executor for <c>... | sort [-r] | first N</c>. Uses a
    /// bounded <see cref="PriorityQueue{TElement, TPriority}"/> of size N
    /// to retain only the items we need, then emits them in sort order.
    /// Memory: O(N) instead of O(M); time: O(M log N).
    /// </summary>
    private static async IAsyncEnumerable<object?> ExecuteSortFirstFusionAsync(
        IAsyncEnumerable<object?> source,
        SortFirstFusion fusion,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (fusion.Count == 0)
        {
            yield break;
        }

        // Comparator mirrors SortCommand's default (no -n, no -h, no key).
        // Forward direction: ascending; reverse: descending.
        var ascending = SortFirstFusionComparer.Instance;
        var comparer = fusion.Reverse ? SortFirstFusionComparer.Reversed : ascending;

        // Heap orders by the OPPOSITE direction so its top is the
        // candidate to evict. For ascending top-N (N smallest), we keep
        // a max-heap; for reverse (N largest), a min-heap.
        var evictionComparer = fusion.Reverse ? ascending : SortFirstFusionComparer.Reversed;
        var heap = new PriorityQueue<object?, object?>(fusion.Count, evictionComparer);

        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            if (heap.Count < fusion.Count)
            {
                heap.Enqueue(item, item);
                continue;
            }

            // EnqueueDequeue replaces the top if the new item is "better"
            // (smaller for ascending top-N, larger for reverse).
            heap.EnqueueDequeue(item, item);
        }

        // Drain to a buffer, then sort in the requested direction.
        var buffer = new List<object?>(heap.Count);
        while (heap.Count > 0)
        {
            buffer.Add(heap.Dequeue());
        }

        buffer.Sort(comparer);

        foreach (var item in buffer)
        {
            yield return item;
        }
    }

    /// <summary>
    /// Default ascending comparer used by the fused sort+first path.
    /// Mirrors the no-flag, no-key behaviour of <c>SortCommand</c>'s
    /// internal comparer for the common case (uniformly-typed items).
    /// </summary>
    private sealed class SortFirstFusionComparer : IComparer<object?>
    {
        public static readonly SortFirstFusionComparer Instance = new(reverse: false);
        public static readonly SortFirstFusionComparer Reversed = new(reverse: true);

        private readonly int _direction;

        private SortFirstFusionComparer(bool reverse)
        {
            _direction = reverse ? -1 : 1;
        }

        public int Compare(object? x, object? y)
        {
            int cmp = CompareCore(x, y);
            return cmp * _direction;
        }

        private static int CompareCore(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x is string xs && y is string ys)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(xs, ys);
            }

            if (x is IComparable comparable && x.GetType() == y.GetType())
            {
                return comparable.CompareTo(y);
            }

            // Fallback: type-name then string comparison. Matches
            // SortCommand's ultimate fallback for incompatible types.
            var xTypeName = x.GetType().Name;
            var yTypeName = y.GetType().Name;
            var typeCmp = string.Compare(xTypeName, yTypeName, StringComparison.Ordinal);
            if (typeCmp != 0) return typeCmp;
            return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
        }
    }

    private async IAsyncEnumerable<object?> FinalizePipelineExitCodeAsync(
        IAsyncEnumerable<object?> current,
        PipelineExitStatusTracker tracker,
        bool ownsTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in current.WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            // Once `exit` has spoken, its code is the answer. The tracker records the status of
            // the pipeline that just ran — and `exit 3` is itself a command that succeeds — so
            // letting it write here overwrote the 3 with a 0 and every script exited cleanly no
            // matter what it asked for.
            if (ownsTracker && tracker.HasExitCodes && !Runtime.ExitRequested)
            {
                var exitCode = tracker.GetFinalExitCode();
                Runtime.SetLastExitCode(exitCode);

                if (exitCode != 0 && Runtime.Config.Shell.ExitOnError)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.nonzero_exit_code",
                        Title: $"Command exited with code {exitCode}.",
                        Help: "A command in the pipeline returned a non-zero exit code while Shell.ExitOnError is enabled. " +
                              "Set $tosh.Config.Shell.ExitOnError = false to disable this behavior."));
                }
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteExpressionStageAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (expressionStage.Expression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var binding) &&
            binding.ReplayAsPipeline &&
            binding.Value is IEnumerable enumerable &&
            binding.Value is not string)
        {
            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        object? value;

        try
        {
            value = await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Tosh.Runtime.ShellControlFlowException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsToshThrown(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, expressionStage.Expression, exception);
        }

        if (ShouldReplayRuntimeNamespaceCollectionAccess(expressionStage.Expression) &&
            ShouldReplayAsPipeline(value) &&
            value is IEnumerable replayable &&
            value is not string)
        {
            foreach (var item in replayable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        // Expand ranges into their individual values.
        if (value is ToshRange range)
        {
            foreach (var item in range.Enumerate())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        yield return value;
    }

    private async IAsyncEnumerable<object?> ExecutePipeForwardStageAsync(
        string sourceName,
        string sourceText,
        PipeForwardStageSyntax pipeForward,
        IAsyncEnumerable<object?> input,
        PipelineExitStatusTracker? pipelineExitStatusTracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Collect all items from the previous stage.
        var items = new List<object?>();
        await foreach (var item in input.WithCancellation(cancellationToken))
        {
            items.Add(item);
        }

        // Collapse: single item → unwrap, multiple → list, zero → null.
        object? collectedValue = items.Count switch
        {
            0 => null,
            1 => items[0],
            _ => items,
        };

        // Execute the command with the collected value prepended as first argument.
        var prependedArgs = new List<object?> { collectedValue };
        await foreach (var result in ExecuteCommandSyntaxAsync(
            sourceName,
            sourceText,
            pipeForward.Command,
            AsyncEnumerableExtensions.Empty<object?>(),
            additionalArguments: null,
            isPipelined: false,
            pipelineExitStatusTracker,
            cancellationToken,
            prependedArguments: prependedArgs))
        {
            yield return result;
        }
    }

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
        bool outputIsCaptured = false)
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
            Runtime,
            input,
            arguments,
            cancellationToken,
            invocation,
            isPipelined,
            CreateScopedTypeResolver(),
            pipelineExitStatusTracker,
            BlockExecutor: _ownBlockExecutor,
            OutputIsCaptured: outputIsCaptured,
            ScopedCommands: CreateScopedCommandView(), ShellTypes: this);

        if (Runtime.Config.Shell.Trace)
        {
            var traceArgs = string.Join(" ", arguments.Select(FormatTraceArgument));
            var traceLine = string.IsNullOrEmpty(traceArgs)
                ? $"+ {commandSyntax.Name}"
                : $"+ {commandSyntax.Name} {traceArgs}";
            await Runtime.Error.WriteLineAsync(traceLine);
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

            if (evaluatedArgument.Syntax is BarewordArgumentSyntax or SplatArgumentSyntax &&
                evaluatedArgument.Value is string text &&
                !string.IsNullOrWhiteSpace(text) &&
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

        if (Runtime.Commands.TryGet(commandSyntax.Name, out var command))
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
        var external = ExternalCommandResolver.Resolve(Runtime.CurrentDirectory, externalName);

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
            ExternalCommandLookupStatus.Found when external.ResolvedPath is not null =>
                new ExternalProcessCommand(commandSyntax.Name, external.ResolvedPath),
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
            ExternalCommandLookupStatus.IsDirectory when Runtime.Config.Shell.AutoCd =>
                new AutoCdCommand(external.ResolvedPath ?? commandSyntax.Name),
            ExternalCommandLookupStatus.IsDirectory =>
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.external_command_is_directory",
                    Title: $"'{external.ResolvedPath ?? commandSyntax.Name}' is a directory, not an executable file.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: commandSyntax.Span,
                    Label: $"'{commandSyntax.Name}' does not refer to a runnable program")),
            _ when Runtime.Config.Shell.AutoCd && TryResolveAutoCdDirectory(commandSyntax.Name, out var autoCdPath) =>
                new AutoCdCommand(autoCdPath),
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

        foreach (var command in Runtime.Commands.All)
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

    private async Task<IReadOnlyList<object?>> EvaluateArgumentsAsync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ArgumentSyntax> arguments,
        CancellationToken cancellationToken)
    {
        var values = new object?[arguments.Count];

        for (var index = 0; index < arguments.Count; index++)
        {
            values[index] = await EvaluateArgumentAsync(sourceName, sourceText, arguments[index], cancellationToken);
        }

        return values;
    }

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

    private async Task<object?> EvaluateArgumentAsync(
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
                    return bareword.Value;

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
                        return await InvokeQualifiedMethodAsync(
                            staticMethodCall.Path,
                            methodArguments,
                            cancellationToken);
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
                        var target = await EvaluateArgumentAsync(sourceName, sourceText, callableInvocation.Target, cancellationToken);

                        if (target is not IShellCallable callable)
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

                        var callArguments = await EvaluateCallableInvocationArgumentsAsync(sourceName, sourceText, callableInvocation.Arguments, cancellationToken);
                        var invocation = new CommandInvocation(
                            sourceName,
                            sourceText,
                            callable.CallableName,
                            callableInvocation.Span,
                            callableInvocation.Arguments.Select(argument => argument.Span).ToArray());
                        var context = new CommandContext(
                            Runtime,
                            AsyncEnumerableExtensions.Empty<object?>(),
                            callArguments,
                            cancellationToken,
                            invocation,
                            IsPipelined: false,
                            ScopedTypeResolver: CreateScopedTypeResolver(),
                            BlockExecutor: _ownBlockExecutor,
                            ScopedCommands: CreateScopedCommandView(), ShellTypes: this);
                        var results = await AsyncEnumerableExtensions.ToListAsync(
                            callable.InvokeAsync(context),
                            cancellationToken);

                        if (results.Count <= 1)
                        {
                            return results.Count == 1 ? results[0] : null;
                        }

                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.callable_invocation_requires_single_value",
                            Title: "Callable invocation in expression context must produce exactly one value.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: callableInvocation.Span,
                            Label: results.Count == 0
                                ? "this invocation produced no values"
                                : $"this invocation produced {results.Count} values",
                            Help: "ensure the callable returns exactly one value, or use 'invoke' in pipeline context for multi-value output."));
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
                                            EvaluateAsync(
                                                expression.Expression,
                                                sourceName,
                                                cancellationToken,
                                                outputIsCaptured: true),
                                            cancellationToken);

                                        if (results.Count == 1)
                                        {
                                            builder.Append(
                                                await FormatInterpolatedValueAsync(
                                                    results[0],
                                                    cancellationToken));
                                        }
                                        else if (results.Count > 1)
                                        {
                                            var formatted = new string[results.Count];
                                            for (var index = 0; index < results.Count; index++)
                                            {
                                                formatted[index] = await FormatInterpolatedValueAsync(
                                                    results[index],
                                                    cancellationToken);
                                            }

                                            builder.Append(string.Join(" ", formatted));
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
                        if (!nameOf.IsVariableReference && TryGetVariableBinding(nameOf.Identifier, out _))
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

    private string FormatCommandSubstitutionValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            ShellTextLine textLine => textLine.Text,
            string text => text,
            _ => Runtime.Formatter.Format(value),
        };
    }

    private async Task<MatchArmSyntax> ResolveMatchArmAsync(
        string sourceName,
        string sourceText,
        MatchArgumentSyntax match,
        CancellationToken cancellationToken)
    {
        var value = await EvaluateArgumentAsync(sourceName, sourceText, match.Value, cancellationToken);

        foreach (var arm in match.Arms)
        {
            var matched = arm.IsWildcard;

            if (!matched && arm.Pattern is not null)
            {
                matched = await MatchesPatternAsync(value, sourceName, sourceText, arm.Pattern, cancellationToken);
            }

            if (!matched)
            {
                continue;
            }

            if (arm.Guard is not null)
            {
                if (!await EvaluateGuardWithCurrentItemAsync(sourceName, sourceText, arm.Guard, value, cancellationToken))
                {
                    continue;
                }
            }

            return arm;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.non_exhaustive_match",
            Title: "This match expression did not match any arm.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: match.Span,
            Label: "add a matching arm or a fallback arm like `default => ...`",
            Help: "match expressions should usually end with a `default => ...` arm to cover unmatched values."));
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

    private async Task<object?> EvaluateMatchArmValueAsync(
        string sourceName,
        string sourceText,
        MatchArmSyntax arm,
        CancellationToken cancellationToken)
    {
        var values = await AsyncEnumerableExtensions.ToListAsync(
            ExecuteMatchArmAsync(sourceName, sourceText, arm, cancellationToken),
            cancellationToken);

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => values.ToArray(),
        };
    }

    private async IAsyncEnumerable<object?> ExecuteMatchArmAsync(
        string sourceName,
        string sourceText,
        MatchArmSyntax arm,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (arm.Body)
        {
            case MatchArmBlockBodySyntax blockBody:
                await foreach (var value in ExecuteBlockAsync(sourceName, sourceText, blockBody.Block, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    yield return value;
                }
                yield break;

            case MatchArmPipelineBodySyntax pipelineBody:
                await foreach (var value in EvaluatePipelineWithRedirectionAsync(sourceName, sourceText, pipelineBody.Pipeline, cancellationToken)
                                   .WithCancellation(cancellationToken))
                {
                    yield return value;
                }
                yield break;

            default:
                throw new InvalidOperationException($"Unsupported match arm body syntax: {arm.Body.GetType().Name}.");
        }
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

    private async Task<object?> ResolveMethodCallTargetAsync(
        string sourceName,
        string sourceText,
        MethodCallArgumentSyntax methodCall,
        CancellationToken cancellationToken)
    {
        if (!ShouldAutoMaterializeListTarget(methodCall.MethodName) ||
            !TryDecomposeMemberAssignmentTarget(methodCall.Target, out var rootExpression, out var memberPath))
        {
            return await EvaluateArgumentAsync(sourceName, sourceText, methodCall.Target, cancellationToken);
        }

        var rootTarget = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);

        try
        {
            var existingTarget = await Runtime.ObjectAccessor.GetValueAsync(
                rootTarget,
                memberPath,
                cancellationToken);

            if (existingTarget is not null)
            {
                return existingTarget;
            }
        }
        catch (Exception exception) when (
            exception is not ToshDiagnosticException and not OperationCanceledException)
        {
        }

        var materializedList = new List<object?>();

        try
        {
            await Runtime.ObjectAccessor.SetValueAsync(
                rootTarget,
                memberPath,
                materializedList,
                cancellationToken);
            return materializedList;
        }
        catch (Exception exception) when (
            exception is not ToshDiagnosticException and not OperationCanceledException)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.list_materialization_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: methodCall.Target.Span,
                Label: $"while preparing '{memberPath}' for '{methodCall.MethodName}'"));
        }
    }

    private async Task<object?> EvaluateOrMaterializeRootTargetAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax rootExpression,
        CancellationToken cancellationToken)
    {
        if (rootExpression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var existingBinding) &&
            existingBinding.IsAllocatedOnly)
        {
            var target = new System.Dynamic.ExpandoObject();

            TryAssignVariable(
                variableReference.Name,
                existingBinding with
                {
                    Value = target,
                    ReplayAsPipeline = false,
                    IsAllocatedOnly = false,
                });

            return target;
        }

        return await EvaluateArgumentAsync(sourceName, sourceText, rootExpression, cancellationToken);
    }

    /// <summary>
    /// Phase 6.16 — call-site type-argument inference for generic
    /// classes. When the user writes <c>new Box(42)</c> without the
    /// <c>&lt;int&gt;</c> ceremony, walk each generic type parameter
    /// and look for the *first* primary-constructor parameter whose
    /// raw annotation is exactly that type-parameter name (e.g.
    /// <c>x: T</c>). Use the runtime CLR type of the corresponding
    /// constructor argument as the inferred binding for that
    /// type-parameter. All type-parameters must be inferred or the
    /// helper returns false. Limited to bare type-parameter
    /// annotations — nested shapes like <c>list&lt;T&gt;</c> are
    /// out of scope for this phase.
    /// </summary>
    private static bool TryInferTypeArgumentsFromCtorArgs(
        IReadOnlyList<string> typeParameterNames,
        IReadOnlyList<FunctionParameterDefinition> ctorParameters,
        IReadOnlyList<object?> ctorArguments,
        out Type?[] resolved,
        out string[] display)
    {
        var bindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var paramSet = new HashSet<string>(typeParameterNames, StringComparer.Ordinal);

        for (int i = 0; i < ctorParameters.Count && i < ctorArguments.Count; i++)
        {
            var raw = ctorParameters[i].RawTypeName;
            if (raw is null) continue;
            UnifyCtorAnnotationWithValue(paramSet, raw, ctorArguments[i], bindings);
        }

        return FinishCtorInference(typeParameterNames, bindings, out resolved, out display);
    }

    /// <summary>Records mirror class inference but use field annotations
    /// as the primary-constructor surface.</summary>
    private static bool TryInferTypeArgumentsFromRecordFields(
        IReadOnlyList<string> typeParameterNames,
        IReadOnlyList<ToshRecordFieldDefinition> fields,
        IReadOnlyList<object?> ctorArguments,
        out Type?[] resolved,
        out string[] display)
    {
        var bindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var paramSet = new HashSet<string>(typeParameterNames, StringComparer.Ordinal);

        for (int i = 0; i < fields.Count && i < ctorArguments.Count; i++)
        {
            var raw = fields[i].TypeName;
            if (raw is null) continue;
            UnifyCtorAnnotationWithValue(paramSet, raw, ctorArguments[i], bindings);
        }

        return FinishCtorInference(typeParameterNames, bindings, out resolved, out display);
    }

    private static bool FinishCtorInference(
        IReadOnlyList<string> typeParameterNames,
        Dictionary<string, Type> bindings,
        out Type?[] resolved,
        out string[] display)
    {
        resolved = new Type?[typeParameterNames.Count];
        display = new string[typeParameterNames.Count];
        for (int p = 0; p < typeParameterNames.Count; p++)
        {
            if (!bindings.TryGetValue(typeParameterNames[p], out var bound))
            {
                resolved = Array.Empty<Type?>();
                display = Array.Empty<string>();
                return false;
            }
            resolved[p] = bound;
            display[p] = bound.Name;
        }
        return true;
    }

    /// <summary>
    /// Lightweight recursive unifier for ctor / record-field
    /// inference. Handles three shapes:
    ///   • bare T            → bind T to value's runtime type
    ///   • Head&lt;args…&gt; on a generic CLR value → pointwise unify
    ///   • list/array/dict   → peek element / key&amp;value types
    /// First-binding-wins; later inconsistencies are tolerated and
    /// caught by the constraint validator (or the caller's bare-T
    /// fallback when inference is incomplete).
    /// </summary>
    private static void UnifyCtorAnnotationWithValue(
        HashSet<string> typeParameters,
        string annotation,
        object? value,
        Dictionary<string, Type> bindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0 || value is null) return;

        // Bare type-parameter reference.
        if (typeParameters.Contains(annotation))
        {
            if (!bindings.ContainsKey(annotation))
            {
                bindings[annotation] = value.GetType();
            }
            return;
        }

        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return;

        var head = annotation.Substring(0, lt).Trim();
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0) return;

        switch (head.ToLowerInvariant())
        {
            case "list":
            case "array":
            case "ienumerable":
            case "icollection":
            case "ireadonlylist":
            case "ireadonlycollection":
                if (args.Count == 1 && TryGetElementType(value, out var elemType, out var elemSample))
                {
                    UnifyCtorAnnotationWithType(typeParameters, args[0].Trim(), elemType, elemSample, bindings);
                }
                return;

            case "dict":
            case "dictionary":
            case "map":
            case "idictionary":
            case "ireadonlydictionary":
                if (args.Count == 2 && TryGetDictionaryKVTypes(value, out var keyType, out var valType, out var keySample, out var valSample))
                {
                    UnifyCtorAnnotationWithType(typeParameters, args[0].Trim(), keyType, keySample, bindings);
                    UnifyCtorAnnotationWithType(typeParameters, args[1].Trim(), valType, valSample, bindings);
                }
                return;

            default:
                // Generic CLR type: read its bound type-args from the
                // runtime value and unify pointwise.
                var clrType = value.GetType();
                if (clrType.IsGenericType)
                {
                    var clrArgs = clrType.GetGenericArguments();
                    var pairs = Math.Min(clrArgs.Length, args.Count);
                    for (var i = 0; i < pairs; i++)
                    {
                        UnifyCtorAnnotationWithType(typeParameters, args[i].Trim(), clrArgs[i], sample: null, bindings);
                    }
                }
                return;
        }
    }

    private static void UnifyCtorAnnotationWithType(
        HashSet<string> typeParameters,
        string annotation,
        Type? clrType,
        object? sample,
        Dictionary<string, Type> bindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0) return;

        if (typeParameters.Contains(annotation))
        {
            if (bindings.ContainsKey(annotation)) return;
            if (clrType is not null && clrType != typeof(object))
            {
                bindings[annotation] = clrType;
            }
            else if (sample is not null)
            {
                bindings[annotation] = sample.GetType();
            }
            return;
        }

        // Recurse into nested annotations using the CLR type only;
        // we don't have a value to peek at this depth.
        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return;
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0 || clrType is null || !clrType.IsGenericType) return;

        var clrArgs = clrType.GetGenericArguments();
        var pairs = Math.Min(clrArgs.Length, args.Count);
        for (var i = 0; i < pairs; i++)
        {
            UnifyCtorAnnotationWithType(typeParameters, args[i].Trim(), clrArgs[i], sample: null, bindings);
        }
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

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: exception is InvalidOperationException
                ? "tosh.runtime.expression_failed"
                : "tosh.runtime.unexpected_exception",
            Title: exception.Message,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: "while evaluating this expression"));
    }

    /// <summary>
    /// A failed native call already carries a full diagnostic contract — the
    /// symbol that failed, the value it returned, and errno with its symbolic
    /// name. Flattening it to <c>unexpected_exception</c> would discard exactly
    /// the parts worth reading.
    /// </summary>
    private static ToshDiagnosticException? TryCreateNativeErrorDiagnostic(
        string sourceName,
        string sourceText,
        TextSpan span,
        Exception exception)
    {
        return exception is NativeError nativeError
            ? BuildNativeErrorDiagnostic(sourceName, sourceText, span, nativeError)
            : null;
    }

    private static ToshDiagnosticException BuildNativeErrorDiagnostic(
        string sourceName,
        string sourceText,
        TextSpan span,
        NativeError nativeError)
    {
        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: $"tosh.native.{nativeError.Code}",
            Title: nativeError.DiagnosticTitle,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: nativeError.Label,
            Help: nativeError.Help));
    }

    private object? ResolveQualifiedAccessOrFallback(string path)
    {
        return ResolveQualifiedAccess(path);
    }

    /// <summary>
    /// Resolves a dotted-path access like <c>Lib.greeting</c> or
    /// <c>App.Math.add</c> against modules, classes, enums, and CLR
    /// types in scope. Returns the path string itself if no match is
    /// found (matching <see cref="ResolveQualifiedAccessOrFallback"/>'s
    /// fallback). Exposed publicly so compiled tosh (the IL emitter's
    /// host bridge) can resolve module-qualified names without
    /// re-parsing.
    /// </summary>
    public object? ResolveQualifiedAccess(string path)
    {
        if (TryResolveQualifiedAccess(path, out var value, out _))
        {
            return value;
        }

        return path;
    }

    /// <summary>
    /// Resolves and invokes a dotted-path static method like
    /// <c>Lib.greet()</c> or <c>App.Math.add(1, 2)</c>. Public so
    /// compiled tosh's host bridge can dispatch
    /// <c>BoundStaticMethodCall</c> without re-parsing.
    /// </summary>
    public object? InvokeQualifiedMethodPublic(string path, IReadOnlyList<object?> arguments)
        => InvokeQualifiedMethod(path, arguments);

    /// <summary>What a resolved dotted path turns out to name.</summary>
    private enum QualifiedInvocationKind
    {
        /// <summary>A static method on the resolved type.</summary>
        Static,

        /// <summary>An instance method, reached by walking members from the resolved type.</summary>
        Instance,
    }

    /// <summary>
    /// Where a dotted invocation path lands: the type it resolves against, the member chain to
    /// walk from there, and the method to call at the end.
    /// </summary>
    private readonly record struct QualifiedInvocationPlan(
        QualifiedInvocationKind Kind,
        Type DeclaringType,
        string MethodName,
        IReadOnlyList<string> MemberPath);

    /// <summary>
    /// Rejects a path that names a type rather than a method, with the message that says what to
    /// write instead.
    /// </summary>
    private static InvalidOperationException ConstructInsteadOfInvoking(string path) =>
        new($"Construct instances with 'new {path}(...)'.");

    /// <summary>
    /// Works out what <paramref name="path"/> names, without invoking anything.
    /// </summary>
    /// <remarks>
    /// This is what the two <c>InvokeQualifiedMethod</c> twins duplicated: rejecting a path that
    /// is really a type, splitting the dotted path, and the longest-prefix scan that decides
    /// whether the call is static or instance and how much of the tail is a member chain. Both
    /// then did the same thing with the answer, differing only in whether the invocation was
    /// awaited (<c>TS-P1-24</c>).
    /// </remarks>
    private QualifiedInvocationPlan PlanQualifiedInvocation(string path)
    {
        if (ResolveTypeName(path) is not null)
        {
            throw ConstructInsteadOfInvoking(path);
        }

        var segments = SplitQualifiedPath(path);

        // Longest prefix first: `A.B.C.method` prefers a type `A.B.C` over a type `A.B` with a
        // member `C`, which is what makes nested types resolve before member chains.
        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return prefixLength == segments.Length - 1
                ? new QualifiedInvocationPlan(
                    QualifiedInvocationKind.Static,
                    type,
                    segments[^1],
                    Array.Empty<string>())
                : new QualifiedInvocationPlan(
                    QualifiedInvocationKind.Instance,
                    type,
                    segments[^1],
                    segments[prefixLength..^1]);
        }

        throw new InvalidOperationException($"Unable to resolve .NET access path '{path}'.");
    }

    private object? InvokeQualifiedMethod(string path, IReadOnlyList<object?> arguments)
    {
        if (TryResolveShellStaticType(path, out _))
        {
            throw ConstructInsteadOfInvoking(path);
        }

        if (TryInvokeShellSymbol(path, arguments, out var shellResult))
        {
            return shellResult;
        }

        var plan = PlanQualifiedInvocation(path);

        if (plan.Kind == QualifiedInvocationKind.Static)
        {
            var invocation = Runtime.Invoker.InvokeStatic(plan.DeclaringType, plan.MethodName, arguments);
            return invocation.ReturnedVoid ? null : invocation.Value;
        }

        var target = ResolveQualifiedMemberChain(plan.DeclaringType, plan.MemberPath);

        if (target is null)
        {
            throw new InvalidOperationException("Cannot invoke an instance method on null.");
        }

        var instanceInvocation = Runtime.Invoker.InvokeInstance(target, plan.MethodName, arguments);
        return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
    }

    private async ValueTask<object?> InvokeQualifiedMethodAsync(
        string path,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryResolveShellStaticType(path, out _))
        {
            throw ConstructInsteadOfInvoking(path);
        }

        var shellInvocation = await TryInvokeShellSymbolAsync(path, arguments, cancellationToken);

        if (shellInvocation.Matched)
        {
            return shellInvocation.Value;
        }

        var plan = PlanQualifiedInvocation(path);

        if (plan.Kind == QualifiedInvocationKind.Static)
        {
            var invocation = await Runtime.Invoker.InvokeStaticMethodAsync(
                plan.DeclaringType,
                plan.MethodName,
                arguments,
                cancellationToken);
            return invocation.ReturnedVoid ? null : invocation.Value;
        }

        var target = await ResolveQualifiedMemberChainAsync(
            plan.DeclaringType,
            plan.MemberPath,
            cancellationToken);

        if (target is null)
        {
            throw new InvalidOperationException("Cannot invoke an instance method on null.");
        }

        var instanceInvocation = await Runtime.Invoker.InvokeInstanceMethodAsync(
            target,
            plan.MethodName,
            arguments,
            cancellationToken);
        return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
    }

    private bool TryResolveQualifiedAccess(string path, out object? value, out bool matchedType)
    {
        if (TryResolveShellSymbolAccess(path, out value))
        {
            matchedType = true;
            return true;
        }

        var directType = ResolveTypeName(path);

        if (directType is not null)
        {
            matchedType = true;
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);
        matchedType = false;

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            matchedType = true;
            value = ResolveQualifiedMemberChain(type, segments[prefixLength..]);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Which kind of shell symbol a dotted path names, if any.</summary>
    private enum ShellSymbolKind
    {
        /// <summary>Not a shell symbol; the caller should try CLR resolution.</summary>
        None,

        /// <summary>A member reached from a module, possibly through a nested member path.</summary>
        Module,

        /// <summary>A static member of a shell type.</summary>
        ShellStatic,
    }

    /// <summary>
    /// A dotted path resolved against the shell's own symbols, before any invocation happens.
    /// </summary>
    /// <param name="MemberPath">
    /// The dotted path from the module to the object holding the method, or <see langword="null"/>
    /// when the method is on the module itself.
    /// </param>
    private readonly record struct ShellSymbolPlan(
        ShellSymbolKind Kind,
        object? Module,
        IShellStaticType? StaticType,
        string MethodName,
        string? MemberPath);

    /// <summary>
    /// Decides whether <paramref name="path"/> names a shell symbol, and which one.
    /// </summary>
    /// <remarks>
    /// The segment arithmetic here is what the two <c>TryInvokeShellSymbol</c> twins duplicated:
    /// that a module call needs at least two segments, that everything between the module and the
    /// final name is a member path, and that a shell static type is matched only at exactly two
    /// segments. Both twins then invoked the same way, differing only in awaiting
    /// (<c>TS-P1-24</c>).
    /// </remarks>
    private bool TryPlanShellSymbol(string path, out ShellSymbolPlan plan)
    {
        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 2 && TryGetModule(segments[0], out var module))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.Module,
                module,
                StaticType: null,
                segments[^1],
                segments.Length > 2 ? string.Join('.', segments[1..^1]) : null);
            return true;
        }

        if (segments.Length == 2 && TryResolveShellStaticType(segments[0], out var shellType))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.ShellStatic,
                Module: null,
                shellType,
                segments[1],
                MemberPath: null);
            return true;
        }

        plan = default;
        return false;
    }

    private bool TryInvokeShellSymbol(string path, IReadOnlyList<object?> arguments, out object? value)
    {
        if (!TryPlanShellSymbol(path, out var plan))
        {
            value = null;
            return false;
        }

        if (plan.Kind == ShellSymbolKind.ShellStatic)
        {
            var staticInvocation = Runtime.Invoker.InvokeStatic(plan.StaticType!, plan.MethodName, arguments);
            value = staticInvocation.ReturnedVoid ? null : staticInvocation.Value;
            return true;
        }

        var target = plan.Module!;

        if (plan.MemberPath is not null)
        {
            target = Runtime.ObjectAccessor.GetValue(plan.Module, plan.MemberPath)
                     ?? throw CannotInvokeOnNull(plan.MethodName);
        }

        var invocation = Runtime.Invoker.InvokeInstance(target, plan.MethodName, arguments);
        value = invocation.ReturnedVoid ? target : invocation.Value;
        return true;
    }

    private static InvalidOperationException CannotInvokeOnNull(string methodName) =>
        new($"Cannot invoke '{methodName}' on null.");

    private async ValueTask<(bool Matched, object? Value)> TryInvokeShellSymbolAsync(
        string path,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryPlanShellSymbol(path, out var plan))
        {
            return (false, null);
        }

        if (plan.Kind == ShellSymbolKind.ShellStatic)
        {
            var staticInvocation = await Runtime.Invoker.InvokeStaticMethodAsync(
                plan.StaticType!,
                plan.MethodName,
                arguments,
                cancellationToken);
            return (true, staticInvocation.ReturnedVoid ? null : staticInvocation.Value);
        }

        var target = plan.Module!;

        if (plan.MemberPath is not null)
        {
            target = await Runtime.ObjectAccessor.GetValueAsync(
                         plan.Module,
                         plan.MemberPath,
                         cancellationToken)
                     ?? throw CannotInvokeOnNull(plan.MethodName);
        }

        var invocation = await Runtime.Invoker.InvokeInstanceMethodAsync(
            target,
            plan.MethodName,
            arguments,
            cancellationToken);
        return (true, invocation.ReturnedVoid ? target : invocation.Value);
    }

    private bool TryResolveShellSymbolAccess(string path, out object? value)
    {
        if (TryGetNamedType(path, out var directType))
        {
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 1 &&
            TryGetModule(segments[0], out var module))
        {
            value = segments.Length == 1
                ? module
                : Runtime.ObjectAccessor.GetValue(module, string.Join('.', segments[1..]));
            return true;
        }

        if (segments.Length == 2 &&
            TryGetNamedType(segments[0], out var shellType))
        {
            value = Runtime.Invoker.GetStaticMember(shellType, segments[1]);
            return true;
        }

        // Deeper chains through a declared type: `T.prop.field` used to fall
        // through to the bareword fallback and evaluate to the literal string
        // "T.prop.field", silently. The module branch above has always walked
        // arbitrary depth; this brings declared types in line.
        //
        // Failure falls through rather than propagating, because a longer path
        // may still resolve against a CLR type prefix further down — which is
        // how it behaved before this branch existed.
        // Membership is *tested*, not attempted-and-caught. Catching would
        // swallow a real failure inside a property getter — a thrown value, a
        // NativeError, a cancellation — and report "not found" instead of the
        // cause. Once the head resolves, the rest of the walk propagates:
        // there is no plausible CLR type named `T.prop`, so falling through
        // would only replace a precise error with a vaguer one.
        if (segments.Length > 2 &&
            TryGetNamedType(segments[0], out var chainRoot) &&
            chainRoot.TryGetStaticMember(segments[1], out var current))
        {
            for (var index = 2; index < segments.Length; index++)
            {
                current = Runtime.ObjectAccessor.GetValue(current, segments[index]);
            }

            value = current;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Rejects an empty member chain. Shared because the message was written out once per
    /// surface, and a duplicated message drifts the moment someone improves one of them.
    /// </summary>
    private static void RequireMemberPath(Type type, IReadOnlyList<string> memberSegments)
    {
        if (memberSegments.Count == 0)
        {
            throw new InvalidOperationException(
                $"No member path was provided for type '{type.FullName}'.");
        }
    }

    private object? ResolveQualifiedMemberChain(Type type, IReadOnlyList<string> memberSegments)
    {
        RequireMemberPath(type, memberSegments);

        object? current = Runtime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = Runtime.ObjectAccessor.GetValue(current, memberSegments[index]);
        }

        return current;
    }

    private async ValueTask<object?> ResolveQualifiedMemberChainAsync(
        Type type,
        IReadOnlyList<string> memberSegments,
        CancellationToken cancellationToken)
    {
        RequireMemberPath(type, memberSegments);

        object? current = Runtime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = await Runtime.ObjectAccessor.GetValueAsync(
                current,
                memberSegments[index],
                cancellationToken);
        }

        return current;
    }

    private static string[] SplitQualifiedPath(string path)
    {
        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public bool TryGetVariableValue(string name, out object? value)
    {
        if (TryGetVariableBinding(name, out var binding))
        {
            value = binding.Value;
            return true;
        }

        value = null;
        return false;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetVisibleVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<KeyValuePair<string, object?>>();

        foreach (var scope in _scopes)
        {
            foreach (var (name, rawValue) in scope.Variables)
            {
                if (seen.Add(name))
                {
                    var value = rawValue is VariableBinding binding ? binding.Value : rawValue;
                    result.Add(new KeyValuePair<string, object?>(name, value));
                }
            }
        }

        foreach (var (name, rawValue) in Runtime.Variables)
        {
            if (seen.Add(name))
            {
                var value = rawValue is VariableBinding binding ? binding.Value : rawValue;
                result.Add(new KeyValuePair<string, object?>(name, value));
            }
        }

        return result;
    }

    private bool TryGetVariableBinding(string name, out VariableBinding binding)
    {
        if (string.Equals(name, "tosh", StringComparison.Ordinal))
        {
            binding = new VariableBinding(_toshNamespace, ReplayAsPipeline: false, IsAllocatedOnly: false);
            return true;
        }

        if (string.Equals(name, "env", StringComparison.Ordinal))
        {
            binding = new VariableBinding(_environmentNamespace, ReplayAsPipeline: false, IsAllocatedOnly: false);
            return true;
        }

        foreach (var scope in _scopes)
        {
            if (scope.Variables.TryGetValue(name, out var rawValue))
            {
                binding = ToVariableBinding(rawValue);
                return true;
            }
        }

        if (Runtime.Variables.TryGetValue(name, out var globalValue))
        {
            binding = ToVariableBinding(globalValue);
            return true;
        }

        binding = new VariableBinding(null, ReplayAsPipeline: false, IsAllocatedOnly: false);
        return false;
    }

    private VariableBinding RequireMutableVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan span)
    {
        if (!TryGetVariableBinding(name, out var binding))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unknown_variable",
                Title: $"Variable '{name}' has not been declared.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"declare '{name}' with 'var' before assigning to it",
                Help: $"try 'var {name} = ...' the first time you bind this variable."));
        }

        if (binding.IsConst)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.const_reassignment",
                Title: $"Cannot reassign constant '{name}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"'{name}' was declared with 'const' and cannot be modified",
                Help: "use 'var' instead of 'const' if you need to reassign this variable."));
        }

        return binding;
    }

    private VariableBinding CreateAssignedVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan valueSpan,
        VariableBinding existingBinding,
        object? assignedValue)
    {
        if (existingBinding.DeclaredTypeName is not null)
        {
            assignedValue = ConvertAnnotatedValue(
                existingBinding.DeclaredTypeName,
                existingBinding.DeclaredRefinement,
                assignedValue,
                valueSpan,
                sourceName,
                sourceText,
                name);
        }

        return existingBinding with
        {
            Value = assignedValue,
            ReplayAsPipeline = ShouldReplayAsPipeline(assignedValue),
            IsAllocatedOnly = false,
        };
    }

    private static object? CloneStructAssignmentValue(object? value) =>
        value is ToshStructInstance structInstance
            ? structInstance.Clone()
            : value;

    private void AssignExistingVariableBinding(
        string sourceName,
        string sourceText,
        string name,
        TextSpan span,
        VariableBinding binding)
    {
        if (TryAssignVariable(name, binding))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unknown_variable",
            Title: $"Variable '{name}' has not been declared.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"declare '{name}' with 'var' before assigning to it",
            Help: $"try 'var {name} = ...' the first time you bind this variable."));
    }

    private static void EnsureBindingNameIsNotReserved(string sourceName, string sourceText, string name, TextSpan span, string titleSuffix)
    {
        if (!RuntimeNamespaceUtilities.IsReservedRuntimeNamespaceName(name))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.reserved_variable_name",
            Title: $"'{name}' is a {titleSuffix}.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"choose a different name than '{name}'"));
    }


    private async ValueTask<string> FormatInterpolatedValueAsync(
        object? value,
        CancellationToken cancellationToken)
    {
        if (value is ToshClassInstance classInstance && classInstance.HasCustomToString())
        {
            return await ToOperatorStringAsync(classInstance, cancellationToken);
        }

        return Runtime.Formatter.Format(value);
    }

    private static string FormatTraceArgument(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value.ToString() ?? string.Empty;

        if (text.Contains(' ') || text.Contains('"') || text.Length == 0)
        {
            return $"\"{text.Replace("\"", "\\\"")}\"";
        }

        return text;
    }

    private void UpdateLastResultIfAny(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Runtime.SetLastResult(values.Count == 1 ? values[0] : values.ToArray());
    }

    private bool TryBuildVariableReferenceHint(string commandName, out string suggestedReference, out string variableName)
    {
        suggestedReference = string.Empty;
        variableName = string.Empty;

        if (string.IsNullOrWhiteSpace(commandName) ||
            commandName[0] == '$' ||
            commandName == "_" ||
            commandName.StartsWith("_.", StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = commandName.IndexOf('.');
        var rootName = separatorIndex >= 0 ? commandName[..separatorIndex] : commandName;

        if (!IsIdentifier(rootName) || !TryGetVariableBinding(rootName, out _))
        {
            return false;
        }

        suggestedReference = "$" + commandName;
        variableName = rootName;
        return true;
    }

    private static bool IsIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < text.Length; index++)
        {
            var character = text[index];

            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Matched, object? Value)> TryEvaluateRawExpressionPipelineAsync(
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        CancellationToken cancellationToken)
    {
        if (pipeline.Stages.Count == 1 &&
            pipeline.Stages[0] is ExpressionPipelineStageSyntax expressionStage)
        {
            var value = await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken);
            return (true, value);
        }

        return (false, null);
    }

    private static bool ShouldReplayAsPipeline(object? value)
    {
        // Lists and arrays should enumerate their elements into the pipeline.
        // Strings, dictionaries (ExpandoObject / records), and other single objects should not.
        return value is IList or Array;
    }

    private static bool ShouldReplayRuntimeNamespaceCollectionAccess(ArgumentSyntax expression)
    {
        if (!TryGetRuntimeNamespaceMemberPath(expression, out var memberPath))
        {
            return false;
        }

        return memberPath switch
        {
            "Script.Args" => true,
            "Function.Args" => true,
            "Function.Input" => true,
            _ => false,
        };
    }

    private static bool TryGetRuntimeNamespaceMemberPath(ArgumentSyntax expression, out string memberPath)
    {
        var segments = new Stack<string>();
        var current = expression;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is VariableReferenceArgumentSyntax variableReference &&
            string.Equals(variableReference.Name, "tosh", StringComparison.Ordinal) &&
            segments.Count > 0)
        {
            memberPath = string.Join(".", segments);
            return true;
        }

        memberPath = string.Empty;
        return false;
    }

    private static bool TryGetCurrentItemMemberPath(ArgumentSyntax expression, out string memberPath)
    {
        // Current-item-expression commands (sum, min, max, sort, etc.) wrap their
        // single member-path argument in a synthetic block-with-expression-stage.
        // Unwrap it so we can recover the original member path.
        if (expression is BlockArgumentSyntax blockArgument &&
            blockArgument.Block is { Statements: [PipelineStatementSyntax { Pipeline: { Stages: [ExpressionPipelineStageSyntax stage], Redirections: null or { Count: 0 } } }] })
        {
            expression = stage.Expression;
        }

        var segments = new Stack<string>();
        var current = expression;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is VariableReferenceArgumentSyntax variableReference &&
            string.Equals(variableReference.Name, "_", StringComparison.Ordinal))
        {
            memberPath = segments.Count == 0
                ? "_"
                : string.Join(".", segments);
            return true;
        }

        memberPath = string.Empty;
        return false;
    }

    private static bool ShouldSuppressStatementResults(StatementSyntax statement, IReadOnlyList<object?> values)
    {
        if (values.Count == 0 || values.Any(value => value is not null))
        {
            return false;
        }

        return statement is PipelineStatementSyntax
        {
            Pipeline:
            {
                Stages.Count: 1,
                Redirections: null or { Count: 0 },
                Stages: [ExpressionPipelineStageSyntax]
            }
        };
    }

    private ValueTask<object?> ApplyCompoundAssignmentAsync(
        string sourceName,
        string sourceText,
        TextSpan span,
        object? currentValue,
        string assignmentOperator,
        object? incomingValue,
        CancellationToken cancellationToken)
    {
        var binaryOperator = assignmentOperator switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "**=" => "**",
            "/=" => "/",
            "//=" => "//",
            "%=" => "%",
            _ => throw new InvalidOperationException($"Unsupported assignment operator '{assignmentOperator}'."),
        };

        return EvaluateBinaryOperatorAsync(
            sourceName,
            sourceText,
            span,
            currentValue,
            binaryOperator,
            incomingValue,
            cancellationToken);
    }

    private static bool ShouldWrapAssignmentFailure(Exception exception) =>
        exception is not ToshDiagnosticException and
        not OperationCanceledException and
        not ShellControlFlowException &&
        !IsToshThrown(exception);

    private static object? CreateCaughtErrorValue(Exception exception)
    {
        return exception switch
        {
            ThrowSignalException thrown => thrown.Value,
            // A ToshError wrapping a ToshClassInstance was synthesized
            // by RaiseThrownValueAsync when the user threw an instance of
            // `class FooError extends Error`. Inside tosh `catch (err)`
            // the user expects to see the original instance (so
            // `$err is FooError` works); the ToshError wrapper only
            // exists to bridge the CLR boundary.
            ToshError { Cause: ToshClassInstance instance } => instance,
            ToshDiagnosticException diagnostic => diagnostic,
            _ => exception,
        };
    }

    /// <summary>
    /// Raise a tosh <c>throw</c>. When <paramref name="value"/> is itself
    /// an <see cref="Exception"/>, that exception is raised verbatim so
    /// cross-language callers can <c>catch</c> it by its concrete type;
    /// non-exception values are wrapped in a <see cref="ThrowSignalException"/>
    /// so the original payload round-trips through tosh <c>catch (err)</c>
    /// intact. The exception's <c>Data["tosh.thrown"]</c> entry is set
    /// so the engine's pipeline-level catches can let user-thrown
    /// exceptions pass through without being rewrapped as runtime
    /// command diagnostics. Control-flow signals
    /// (<see cref="ShellControlFlowException"/>) are not valid throw
    /// payloads and are wrapped into <see cref="ThrowSignalException"/>
    /// rather than rethrown as control flow.
    /// </summary>
    private async ValueTask RaiseThrownValueAsync(
        TextSpan span,
        object? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A tosh user class declared as `class FooError extends Error`
        // surfaces at runtime as a sealed ToshClassInstance whose
        // definition's ClrBaseType points at ToshError (or another
        // Exception subclass). Wrap such instances in a ToshError so
        // C# consumers see a real CLR exception, with the original
        // tosh instance available via .Cause and the user's class
        // name preserved on the wrapper for diagnostic-code routing.
        if (value is ToshClassInstance instance && DefinitionExtendsException(instance.Definition))
        {
            var message = await TryGetInstanceMessageAsync(instance, cancellationToken)
                ?? instance.Definition.Name;
            var wrapper = new ToshError(message, span, cause: instance);
            wrapper.Data["tosh.thrown"] = true;
            wrapper.Data["tosh.user.type"] = instance.Definition.Name;
            throw wrapper;
        }
        if (value is ToshError tosh)
        {
            // Stamp a span on the ToshError if the user didn't supply one;
            // the renderer needs a span to point at the throw site.
            if (tosh.Span.Length == 0 && tosh.Span.Start == 0)
            {
                tosh.Span = span;
            }
            tosh.Data["tosh.thrown"] = true;
            throw tosh;
        }
        if (value is Exception ex && value is not ShellControlFlowException)
        {
            ex.Data["tosh.thrown"] = true;
            throw ex;
        }
        var signal = new ThrowSignalException(span, value);
        signal.Data["tosh.thrown"] = true;
        throw signal;
    }

    /// <summary>True when any class in <paramref name="definition"/>'s
    /// inheritance chain has a <see cref="ToshClassDefinition.ClrBaseType"/>
    /// that derives from <see cref="Exception"/>.</summary>
    private static bool DefinitionExtendsException(ToshClassDefinition definition)
    {
        for (var d = definition; d is not null; d = d.BaseClass)
        {
            if (d.ClrBaseType is { } clr && typeof(Exception).IsAssignableFrom(clr))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns a tosh class instance's most-likely "message"
    /// (`Message` or `message` property), used to populate the wrapping
    /// <see cref="ToshError.Message"/> when a user throws an instance
    /// of a class that extends <c>Error</c>.
    /// </summary>
    private async ValueTask<string?> TryGetInstanceMessageAsync(
        ToshClassInstance instance,
        CancellationToken cancellationToken)
    {
        var message = await instance.TryGetMemberAsync(
            "Message",
            includeHidden: false,
            cancellationToken);
        if (message is { Found: true, Value: not null })
        {
            return await FormatThrownDiagnosticValueAsync(message.Value, cancellationToken);
        }

        var lowerMessage = await instance.TryGetMemberAsync(
            "message",
            includeHidden: false,
            cancellationToken);
        if (lowerMessage is { Found: true, Value: not null })
        {
            return await FormatThrownDiagnosticValueAsync(lowerMessage.Value, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="exception"/> originated from a tosh
    /// <c>throw</c> statement (either a wrapped <see cref="ThrowSignalException"/>
    /// or a directly raised <see cref="Exception"/> stamped by
    /// <see cref="RaiseThrownValueAsync"/>).
    /// </summary>
    private static bool IsToshThrown(Exception exception)
        => exception is ThrowSignalException
           || (exception is not OperationCanceledException
               && ToshDeferFailures.IsDeferFailure(exception))
           || (exception is not ShellControlFlowException
               && exception.Data.Contains("tosh.thrown"));

    public ShellNameRemovalResult Forget(string name)
    {
        var removedVariable = false;
        var variableScope = string.Empty;
        VariableBinding? removedVariableBinding = null;

        foreach (var scope in _scopes)
        {
            if (!scope.Variables.TryGetValue(name, out var scopedValue))
            {
                continue;
            }

            scope.Variables.Remove(name);
            removedVariable = true;
            variableScope = scope.IsModuleScope ? "Module" : "Local";
            removedVariableBinding = ToVariableBinding(scopedValue);
            break;
        }

        if (!removedVariable && Runtime.Variables.TryGetValue(name, out var globalValue))
        {
            Runtime.Variables.Remove(name);
            removedVariable = true;
            variableScope = "Global";
            removedVariableBinding = ToVariableBinding(globalValue);
        }

        var removedType = false;

        foreach (var scope in _scopes)
        {
            if (!scope.Classes.Remove(name))
            {
                continue;
            }

            removedType = true;
            break;
        }

        if (!removedType)
        {
            Runtime.Classes.Remove(name);
        }

        var removedModule = false;

        foreach (var scope in _scopes)
        {
            if (!scope.Modules.Remove(name))
            {
                continue;
            }

            removedModule = true;
            break;
        }

        if (!removedModule)
        {
            Runtime.Modules.Remove(name);
        }

        var removedCommand = false;
        var commandKind = string.Empty;
        var commandScope = string.Empty;

        foreach (var scope in _scopes)
        {
            if (!scope.Commands.TryGetValue(name, out var scopedCommand))
            {
                continue;
            }

            if (scopedCommand is ICommandResolutionMetadata scopedMetadata &&
                scopedMetadata.ResolutionKind is CommandResolutionKind.Alias or CommandResolutionKind.Function)
            {
                scope.Commands.Remove(name);
                removedCommand = true;
                commandKind = scopedMetadata.ResolutionKind.ToString();
                commandScope = scope.IsModuleScope ? "Module" : "Local";
                break;
            }
        }

        if (!removedCommand &&
            Runtime.Commands.TryGet(name, out var command) &&
            command is ICommandResolutionMetadata metadata &&
            metadata.ResolutionKind is CommandResolutionKind.Alias or CommandResolutionKind.Function)
        {
            removedCommand = Runtime.Commands.Remove(name);
            commandKind = metadata.ResolutionKind.ToString();
            commandScope = "Global";
        }

        var removedEnvironment = Runtime.ExportedEnvironmentVariables.Contains(name) ||
                                 Environment.GetEnvironmentVariable(name) is not null;
        Runtime.RemoveExportedEnvironmentVariable(name);

        var (freedValue, freedValueKind) = TryDisposeForgottenVariableValue(removedVariableBinding?.Value);

        return new ShellNameRemovalResult(
            name,
            removedVariable,
            variableScope,
            removedCommand,
            commandKind,
            commandScope,
            removedEnvironment,
            freedValue,
            freedValueKind);
    }

    public IReadOnlyList<ShellNameRemovalResult> ForgetValue(object? value)
    {
        var removals = new List<ShellNameRemovalResult>();

        foreach (var scope in _scopes)
        {
            var matches = scope.Variables
                .Where(entry => ValuesReferToSameObject(ToVariableBinding(entry.Value).Value, value))
                .Select(entry => entry.Key)
                .ToArray();

            foreach (var name in matches)
            {
                removals.Add(Forget(name));
            }
        }

        var globalMatches = Runtime.Variables
            .Where(entry => ValuesReferToSameObject(ToVariableBinding(entry.Value).Value, value))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var name in globalMatches)
        {
            if (removals.Any(removal => string.Equals(removal.Name, name, StringComparison.Ordinal)))
            {
                continue;
            }

            removals.Add(Forget(name));
        }

        if (removals.Count == 0 && value is NativeBuffer buffer)
        {
            var freed = false;

            if (!buffer.IsFreed)
            {
                buffer.Dispose();
                freed = true;
            }

            removals.Add(new ShellNameRemovalResult(
                Name: buffer.ToString(),
                RemovedVariable: false,
                VariableScope: string.Empty,
                RemovedCommand: false,
                CommandKind: string.Empty,
                CommandScope: string.Empty,
                RemovedEnvironment: false,
                FreedValue: freed,
                FreedValueKind: nameof(NativeBuffer)));
        }
        else if (removals.Count == 0 && value is ManagedFileHandle handle)
        {
            var freed = false;

            if (handle.IsOpen)
            {
                handle.Dispose();
                freed = true;
            }

            removals.Add(new ShellNameRemovalResult(
                Name: handle.ToString(),
                RemovedVariable: false,
                VariableScope: string.Empty,
                RemovedCommand: false,
                CommandKind: string.Empty,
                CommandScope: string.Empty,
                RemovedEnvironment: false,
                FreedValue: freed,
                FreedValueKind: nameof(ManagedFileHandle)));
        }

        return removals;
    }

    private (bool FreedValue, string FreedValueKind) TryDisposeForgottenVariableValue(object? value)
    {
        if (value is NativeBuffer buffer)
        {
            if (buffer.IsFreed || VariableValueStillReferenced(buffer))
            {
                return (false, string.Empty);
            }

            buffer.Dispose();
            return (true, nameof(NativeBuffer));
        }

        if (value is ManagedFileHandle handle)
        {
            if (!handle.IsOpen || VariableValueStillReferenced(handle))
            {
                return (false, string.Empty);
            }

            handle.Dispose();
            return (true, nameof(ManagedFileHandle));
        }

        return (false, string.Empty);
    }

    private bool VariableValueStillReferenced(object? value)
    {
        foreach (var scope in _scopes)
        {
            foreach (var scopedValue in scope.Variables.Values)
            {
                if (ValuesReferToSameObject(ToVariableBinding(scopedValue).Value, value))
                {
                    return true;
                }
            }
        }

        foreach (var runtimeValue in Runtime.Variables.Values)
        {
            if (ValuesReferToSameObject(ToVariableBinding(runtimeValue).Value, value))
            {
                return true;
            }
        }

        foreach (var handler in Runtime.Events.GetHandlers())
        {
            if (handler.CapturedScopes is not { } scopes)
            {
                continue;
            }

            foreach (var scopeObj in scopes)
            {
                if (scopeObj is LexicalScope scope)
                {
                    foreach (var scopedValue in scope.Variables.Values)
                    {
                        if (ValuesReferToSameObject(ToVariableBinding(scopedValue).Value, value))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool ValuesReferToSameObject(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return false;
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
            RegisterCommand(Runtime.Commands, command);
            return;
        }

        if (_scopes.Count > 0)
        {
            RegisterCommand(_scopes.Peek().Commands, command);
            return;
        }

        WarnIfShadowingBuiltin(command.Name);
        RegisterCommand(Runtime.Commands, command);
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

    private IShellCommand RegisterCommand(ShellCommandRegistry commands, IShellCommand command)
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

    private IReadOnlyList<LexicalScope>? CaptureVisibleScopes()
    {
        if (_scopes.Count == 0)
        {
            return null;
        }

        return _scopes.Reverse().ToArray();
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

        var renderer = new DiagnosticRenderer(Runtime.Config.Theme.Diagnostics, Runtime.Config.Diagnostics);
        Runtime.Error.WriteLine(renderer.RenderWarning(title, help, info));
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

        return Runtime.Config.Diagnostics.IsHushed(code, severity);
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

        Runtime.Config.Diagnostics.Hushed.Add(trimmed);
    }

    /// <summary>Public <see cref="IShellEvaluator"/> entry point for the <c>hush</c> builtin.</summary>
    public void HushDiagnosticCode(string code) => AddHushedCode(code);

    private static string ExtractSourceSnippet(string sourceText, TextSpan span)
    {
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
        {
            return "<background job>";
        }

        return sourceText[span.Start..span.End].Trim();
    }

    private IDisposable PushCapturedScopes(IReadOnlyList<LexicalScope>? scopes)
    {
        if (scopes is null || scopes.Count == 0)
        {
            return ScopeFrames.Empty;
        }

        var disposables = new List<IDisposable>(scopes.Count);

        foreach (var scope in scopes)
        {
            disposables.Add(PushScope(scope));
        }

        return new ScopeFrames(disposables);
    }

    private void DeclareVariable(
        string name,
        VariableBinding binding,
        DeclarationModifier modifier,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null)
    {
        EnsureReservedBindingName(name);
        EnsureConstantIsNotRedeclared(name, modifier, sourceName, sourceText, span);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Variables[name] = binding;
            moduleScope.Exports!.Variables[name] = binding.Value;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Variables[name] = binding;
            exportScope.Exports!.Variables[name] = binding.Value;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Variables[name] = binding;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            if (modifier == DeclarationModifier.Global && TryGetNearestModuleScope(out var globalModuleScope))
            {
                globalModuleScope.Variables[name] = binding;
                globalModuleScope.Exports!.Variables[name] = binding.Value;
                return;
            }

            Runtime.Variables[name] = binding;
            Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Variables[name] = binding;
            return;
        }

        Runtime.Variables[name] = binding;
        Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
    }

    /// <summary>
    /// Refuses a declaration that would replace a <c>const</c> already bound in the very table
    /// this declaration is about to be written to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reassignment was already refused, but redeclaration was not, so a constant could be
    /// replaced simply by declaring it again — and `var X = 6` over `const X = 5` laundered it
    /// into a mutable binding that then accepted assignment. A constant that any later line can
    /// quietly redefine is not one.
    /// </para>
    /// <para>
    /// The question is asked of the *target* table rather than of the scope chain, because
    /// shadowing in a nested scope is legitimate and stays legal: an inner block or a function
    /// body may bind its own <c>X</c> without touching the outer constant.
    /// </para>
    /// </remarks>
    private void EnsureConstantIsNotRedeclared(
        string name,
        DeclarationModifier modifier,
        string? sourceName,
        string? sourceText,
        TextSpan? span)
    {
        if (ResolveDeclarationTarget(modifier) is not { } target ||
            !target.TryGetValue(name, out var existing) ||
            !ToVariableBinding(existing).IsConst)
        {
            return;
        }

        // Carries the same weight as the reassignment refusal it sits beside, so the two read as
        // one rule rather than as a real diagnostic and an internal error.
        if (sourceName is not null && sourceText is not null && span is { } declarationSpan)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.const_redeclaration",
                Title: $"Cannot redeclare constant '{name}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: declarationSpan,
                Label: $"'{name}' was already declared with 'const' in this scope",
                Help: $"use a different name, or declare the original with 'var' if '{name}' needs to change."));
        }

        throw new InvalidOperationException(
            $"Cannot redeclare constant '{name}'. It was declared with 'const' in this scope; " +
            "use a different name, or declare the original with 'var' if it needs to change.");
    }

    /// <summary>
    /// The variable table a declaration carrying <paramref name="modifier"/> is written into.
    /// </summary>
    /// <remarks>
    /// Shares its branching with <see cref="DeclareVariable"/> deliberately: a guard that decided
    /// the destination by its own copy of these rules would answer for a different table than the
    /// write, and the two would drift apart the first time a modifier was added (<c>TS-P1-24</c>).
    /// </remarks>
    private IDictionary<string, object?>? ResolveDeclarationTarget(DeclarationModifier modifier)
    {
        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            return moduleScope.Variables;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            return exportScope.Variables;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            return _scopes.Count > 0 ? _scopes.Peek().Variables : null;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            if (modifier == DeclarationModifier.Global && TryGetNearestModuleScope(out var globalModuleScope))
            {
                return globalModuleScope.Variables;
            }

            return Runtime.Variables;
        }

        return _scopes.Count > 0 ? _scopes.Peek().Variables : Runtime.Variables;
    }

    private bool TryAssignVariable(string name, VariableBinding binding)
    {
        EnsureReservedBindingName(name);

        foreach (var scope in _scopes)
        {
            if (!scope.Variables.ContainsKey(name))
            {
                continue;
            }

            scope.Variables[name] = binding;
            return true;
        }

        if (Runtime.Variables.ContainsKey(name))
        {
            Runtime.Variables[name] = binding;
            Runtime.SyncExportedEnvironmentVariable(name, binding.Value);
            return true;
        }

        return false;
    }

    /// <param name="nativeClrType">
    /// Set only for <c>raw struct</c> declarations: the emitted sequential-layout
    /// CLR type. It is registered into the same scope the definition lands in,
    /// so the type resolver can find it by name in a native signature.
    ///
    /// Threading it through here rather than into a separate declare path is
    /// deliberate — one declaration must never register the façade without also
    /// registering the type, or `size-of SysInfo` and `new SysInfo()` would
    /// disagree about whether the name exists.
    /// </param>
    /// <summary>
    /// Evaluates the types declared inside a class body and registers each on the class rather
    /// than in the surrounding scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The declaration is run inside a scope of its own and the resulting type lifted out of it.
    /// That is what keeps the nested name off the enclosing scope: evaluating it in place would
    /// call <see cref="DeclareType"/> against whatever scope the class was declared in, and
    /// <c>Fuel</c> would become visible beside <c>Reactor</c> as though it had been written at
    /// the top level.
    /// </para>
    /// <para>
    /// Running the declarations through the ordinary statement evaluator, rather than a nested
    /// variant of it, is deliberate: a nested enum is the same enum, and every rule that governs
    /// an outer declaration governs this one for free.
    /// </para>
    /// </remarks>
    private async Task EvaluateNestedTypeMembersAsync(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        ToshClassDefinition definition,
        CancellationToken cancellationToken)
    {
        foreach (var member in @class.Members.OfType<ClassNestedTypeMemberSyntax>())
        {
            IShellNamedType? nestedType = null;

            using (PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)))
            {
                await AsyncEnumerableExtensions.ToListAsync(
                    EvaluateStatementAsync(sourceName, sourceText, member.Declaration, cancellationToken),
                    cancellationToken);

                if (_scopes.Count > 0 &&
                    _scopes.Peek().Classes.TryGetValue(member.Name, out var declared) &&
                    declared is IShellNamedType named)
                {
                    nestedType = named;
                }
            }

            if (nestedType is null)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.nested_type_not_declared",
                    Title: $"Nested type '{member.Name}' in class '{@class.Name}' did not produce a type.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: member.Span,
                    Label: $"'{member.Name}' declared no type"));
            }

            definition.SetNestedType(member.Name, nestedType, member.IsShy);
        }
    }

    /// <summary>
    /// Resolves the type argument names written at a call site to CLR types, or returns null
    /// when none were written.
    /// </summary>
    /// <remarks>
    /// An unresolvable name is a diagnostic rather than a silent fallback to inference: the
    /// caller asked for a specific instantiation, and answering with a different one is the
    /// failure this whole path exists to avoid.
    /// </remarks>
    private IReadOnlyList<Type>? ResolveCallSiteTypeArguments(
        IReadOnlyList<string>? typeArgumentNames,
        string methodName,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        if (typeArgumentNames is not { Count: > 0 })
        {
            return null;
        }

        var resolved = new List<Type>(typeArgumentNames.Count);

        foreach (var name in typeArgumentNames)
        {
            var type = TryResolveTypeName(name);
            if (type is null)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_type_argument",
                    Title: $"Type '{name}' could not be resolved as a type argument for '{methodName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"unknown type '{name}'"));
            }

            resolved.Add(type);
        }

        return resolved;
    }

    private void DeclareType(
        string name,
        IShellNamedType definition,
        DeclarationModifier modifier,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null,
        Type? nativeClrType = null)
    {
        EnsureReservedBindingName(name);
        EnsureTypeNameDoesNotConflictWithRefinementAlias(name, sourceName, sourceText, span, "type");

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Classes[name] = definition;
            moduleScope.Exports!.Types[name] = definition;
            if (nativeClrType is not null)
            {
                moduleScope.NativeTypes[name] = nativeClrType;
                moduleScope.Exports!.NativeTypes[name] = nativeClrType;
            }
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Classes[name] = definition;
            exportScope.Exports!.Types[name] = definition;
            if (nativeClrType is not null)
            {
                exportScope.NativeTypes[name] = nativeClrType;
                exportScope.Exports!.NativeTypes[name] = nativeClrType;
            }
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy class declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Classes[name] = definition;
            if (nativeClrType is not null) _scopes.Peek().NativeTypes[name] = nativeClrType;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Classes[name] = definition;
            if (nativeClrType is not null) Runtime.NativeTypes[name] = nativeClrType;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Classes[name] = definition;
            if (nativeClrType is not null) _scopes.Peek().NativeTypes[name] = nativeClrType;
            return;
        }

        Runtime.Classes[name] = definition;
        if (nativeClrType is not null) Runtime.NativeTypes[name] = nativeClrType;
    }

    private void DeclareRefinementType(
        RefinementTypeDefinition definition,
        DeclarationModifier modifier,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null,
        bool allowTypeNameConflict = false)
    {
        EnsureReservedBindingName(definition.Name);
        if (!allowTypeNameConflict)
        {
            EnsureRefinementAliasNameDoesNotConflictWithType(definition.Name, sourceName ?? definition.SourceName, sourceText ?? definition.SourceText, span ?? definition.Span);
        }

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.RefinementTypes[definition.Name] = definition;
            moduleScope.Exports!.RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.RefinementTypes[definition.Name] = definition;
            exportScope.Exports!.RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy type aliases require a function, block, or module scope.");
            }

            _scopes.Peek().RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Classes[definition.Name] = definition;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().RefinementTypes[definition.Name] = definition;
            return;
        }

        Runtime.Classes[definition.Name] = definition;
    }

    private void PreRegisterTypeDefinitions(
        string sourceName,
        string sourceText,
        IReadOnlyList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            var (name, modifier) = statement switch
            {
                ClassDefinitionStatementSyntax c => (c.Name, c.Modifier),
                InterfaceDefinitionStatementSyntax i => (i.Name, i.Modifier),
                UnionDefinitionStatementSyntax u => (u.Name, u.Modifier),
                RecordDefinitionStatementSyntax r => (r.Name, r.Modifier),
                StructDefinitionStatementSyntax s => (s.Name, s.Modifier),
                TraitDefinitionStatementSyntax t => (t.Name, t.Modifier),
                EnumDefinitionStatementSyntax e => (e.Name, e.Modifier),
                _ => (null, DeclarationModifier.Default),
            };

            if (name is null)
            {
                continue;
            }

            var placeholder = new ForwardTypeReference(name);
            DeclareType(name, placeholder, modifier, sourceName, sourceText, statement.Span);
        }
    }

    private void PreRegisterRefinementTypeAliases(
        string sourceName,
        string sourceText,
        IReadOnlyList<StatementSyntax> statements)
    {
        foreach (var statement in statements.OfType<TypeAliasStatementSyntax>())
        {
            DeclareRefinementType(
                CreateRefinementTypeDefinition(sourceName, sourceText, statement),
                statement.Modifier,
                sourceName,
                sourceText,
                statement.Span);
        }
    }

    private void EnsureTypeNameDoesNotConflictWithRefinementAlias(
        string name,
        string? sourceName,
        string? sourceText,
        TextSpan? span,
        string declaredKind)
    {
        if (!TryGetRefinementType(name, out _))
        {
            return;
        }

        ThrowTypeNameConflict(
            sourceName,
            sourceText,
            span,
            code: "tosh.runtime.type_name_conflict",
            title: $"{declaredKind} '{name}' conflicts with an existing refinement alias.",
            label: $"'{name}' is already bound as a refinement alias",
            help: "choose a different name so types and refinement aliases stay distinct.");
    }

    private void EnsureRefinementAliasNameDoesNotConflictWithType(
        string name,
        string? sourceName,
        string? sourceText,
        TextSpan? span)
    {
        // Only block conflicts with user-declared named types
        // (classes, records, structs, enums, interfaces, traits,
        // unions, modules). The wider CLR-resolver fallback used to
        // be consulted here, but that scans every loaded assembly
        // for a type with a matching name and produces spurious
        // collisions for ordinary aliases like `Pair` (which clashes
        // with assorted CLR types such as `System.Web.UI.Pair`).
        // Authors get to pick their own alias names; the CLR
        // resolver only kicks in at use sites where an unqualified
        // type name needs disambiguating.
        if (!TryGetNamedType(name, out _))
        {
            return;
        }

        ThrowTypeNameConflict(
            sourceName,
            sourceText,
            span,
            code: "tosh.runtime.type_name_conflict",
            title: $"Refinement alias '{name}' conflicts with an existing type name.",
            label: $"'{name}' is already bound as a type",
            help: "choose a different alias name so refinements do not shadow real types.");
    }

    private static void ThrowTypeNameConflict(
        string? sourceName,
        string? sourceText,
        TextSpan? span,
        string code,
        string title,
        string label,
        string? help)
    {
        if (sourceName is null || sourceText is null || span is null)
        {
            throw new InvalidOperationException(title);
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span.Value,
            Label: label,
            Help: help));
    }

    private void DeclareModule(string name, object module, DeclarationModifier modifier)
    {
        EnsureReservedBindingName(name);

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.Modules[name] = module;
            moduleScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.Modules[name] = module;
            exportScope.Exports!.Modules[name] = module;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy module declarations require a function, block, or module scope.");
            }

            _scopes.Peek().Modules[name] = module;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Modules[name] = module;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Modules[name] = module;
            return;
        }

        Runtime.Modules[name] = module;
    }

    /// <summary>
    /// Looks up a user-defined named type (class, record, struct, enum,
    /// union, interface, trait) in the engine's scope or runtime registry.
    /// Exposed publicly so compiled tosh (the IL emitter's host bridge)
    /// can resolve types for <c>new</c>-expressions without re-parsing.
    /// </summary>
    /// <summary>
    /// Resolves a module-qualified type name — <c>Outer.Inner.SmallInt</c> — by walking module
    /// exports, so a type declared inside a module can be named from outside it.
    /// </summary>
    /// <remarks>
    /// Without this, a module-qualified name could not be used as an annotation at all:
    /// `var x: ToastLib.Math.IntPercent = 60` reported `annotation_unknown_type` even though
    /// the unqualified `IntPercent` worked, and the same was true of a `class` or `record`
    /// declared in a module. Every lookup on the annotation path — refinement types, named
    /// types, and the CLR resolver — took a flat name (<c>TS-P1-34</c>).
    ///
    /// Deliberately placed beneath the flat lookups in both callers, so an unqualified name
    /// keeps resolving exactly as it did and only a dotted name reaches the walk. Shares the
    /// shape of `TryResolveNestedExport`, which does the same walk for `require` — the third
    /// place this programme has needed "follow a dotted path through modules".
    /// </remarks>
    private bool TryResolveQualifiedModuleMember(string name, out object? member)
    {
        member = null;

        if (!name.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = name.Split('.', StringSplitOptions.None);

        if (segments.Length < 2 || segments.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        if (!TryFindExistingModule(segments[0], out var current))
        {
            return false;
        }

        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                next is not ToshModuleObject nested)
            {
                return false;
            }

            current = nested;
        }

        var leaf = segments[^1];

        if (current.ExportTable.RefinementTypes.TryGetValue(leaf, out var refinement))
        {
            member = refinement;
            return true;
        }

        if (current.ExportTable.Types.TryGetValue(leaf, out var type))
        {
            member = type;
            return true;
        }

        return false;
    }

    public bool TryGetNamedType(string name, out IShellNamedType definition)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Classes.TryGetValue(name, out var scopedDefinition) &&
                scopedDefinition is IShellNamedType shellType)
            {
                definition = shellType;
                return true;
            }
        }

        if (Runtime.Classes.TryGetValue(name, out var rawValue) &&
            rawValue is IShellNamedType runtimeDefinition)
        {
            definition = runtimeDefinition;
            return true;
        }

        if (TryResolveQualifiedModuleMember(name, out var qualified) &&
            qualified is IShellNamedType qualifiedType)
        {
            definition = qualifiedType;
            return true;
        }

        // A type nested in a class, named through the class that declares it. Without this a
        // nested class could be reached as a value (`Outer.Inner`) but not named as a type, so
        // `new Outer.Inner()` and an `Outer.Inner` annotation both failed while the enum case
        // appeared to work — enums are read through member access and never need naming.
        if (TryResolveNestedTypeName(name, out var nestedType))
        {
            definition = nestedType;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// Resolves a dotted name whose leading segments name classes and whose last segment names a
    /// type nested in the one before it, such as <c>Outer.Inner</c> or <c>A.B.C</c>.
    /// </summary>
    private bool TryResolveNestedTypeName(string name, out IShellNamedType definition)
    {
        definition = null!;

        var separator = name.LastIndexOf('.');
        if (separator <= 0 || separator == name.Length - 1)
        {
            return false;
        }

        // Recurses through TryGetNamedType so a chain of any depth resolves by the same rule
        // rather than by a second walk written for the nested case alone.
        if (!TryGetNamedType(name[..separator], out var owner) ||
            owner is not ToshClassDefinition owningClass)
        {
            return false;
        }

        if (!owningClass.TryGetNestedType(name[(separator + 1)..], out var nested))
        {
            return false;
        }

        definition = nested;
        return true;
    }

    /// <summary>
    /// Help text for an unknown variable, naming the shell namespace when the spelling is one
    /// people reach for out of habit from another shell.
    /// </summary>
    /// <remarks>
    /// Script arguments live at <c>$tosh.Script.Args</c>. Someone arriving from bash, Python or
    /// PowerShell writes <c>$args</c> or <c>$argv</c> first, got "declare it first with
    /// 'var args = ...'" — advice that points away from the answer — and had no path to the real
    /// spelling short of piping <c>$tosh.Script</c> through <c>members</c> (<c>TS-P2-44</c>).
    /// </remarks>
    private static string DescribeUnknownVariable(string name)
    {
        foreach (var (spelling, suggestion) in ShellNamespaceSuggestions)
        {
            if (string.Equals(name, spelling, StringComparison.OrdinalIgnoreCase))
            {
                return $"did you mean '{suggestion}'? {spelling} is not a shell variable in TōSh.";
            }
        }

        return $"declare it first with 'var {name} = ...'.";
    }

    /// <summary>
    /// Names that are variables in other shells but namespace members here, paired with the
    /// spelling that works.
    /// </summary>
    private static readonly (string Spelling, string Suggestion)[] ShellNamespaceSuggestions =
    [
        ("args", "$tosh.Script.Args"),
        ("argv", "$tosh.Script.Args"),
        ("ARGV", "$tosh.Script.Args"),
        ("scriptname", "$tosh.Script.Name"),
        ("scriptdir", "$tosh.Script.Directory"),
        ("PWD", "$env.PWD"),
        ("HOME", "$env.HOME"),
        ("PATH", "$env.PATH"),
        ("status", "$tosh.Last.ExitCode"),
    ];

    /// <summary>
    /// Resolves one type argument of a generic instantiation to the CLR type it should be
    /// validated against, or <see langword="null"/> when the name is a ToastScript type and no
    /// CLR type should be enforced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ToastScript class has no CLR type of its own, so `Holder&lt;Circle&gt;` must bind
    /// nominally — a null binding, which the strict check treats as "accept any value". Resolving
    /// the bare name through the CLR resolver instead searched **every loaded assembly** and could
    /// return an unrelated type that merely shares the name.
    /// </para>
    /// <para>
    /// That is not hypothetical: it is <c>TS-P2-39</c>, the suite's longest-running flake. The
    /// compiler's emitter tests compile ToastScript into dynamic assemblies named
    /// <c>ToshTest_{Guid}</c>, one of which declares <c>class Circle</c>. Once such an assembly was
    /// loaded, an interpreted test declaring its own <c>Circle</c> bound its type parameter to the
    /// *emitted* type and then failed strict binding against its own instance —
    /// "produced a value that could not be converted to 'ToshTest_….Circle'". It reproduced only
    /// when the emitting test ran first, which is why it looked like load-dependent flakiness
    /// through seven sightings and always passed in isolation.
    /// </para>
    /// <para>
    /// Checking the shell's own types first is also the right precedence on its own terms: a name
    /// the script declared should mean the thing the script declared.
    /// </para>
    /// </remarks>
    private Type? ResolveTypeArgument(string typeArgument)
    {
        if (TryGetNamedType(typeArgument, out _))
        {
            return null;
        }

        return ResolveTypeName(typeArgument);
    }

    private bool TryGetRefinementType(string name, out RefinementTypeDefinition definition)
    {
        foreach (var scope in _scopes)
        {
            if (scope.RefinementTypes.TryGetValue(name, out var scopedDefinition))
            {
                definition = scopedDefinition;
                return true;
            }
        }

        if (Runtime.Classes.TryGetValue(name, out var rawValue) &&
            rawValue is RefinementTypeDefinition runtimeDefinition)
        {
            definition = runtimeDefinition;
            return true;
        }

        if (TryResolveQualifiedModuleMember(name, out var qualified) &&
            qualified is RefinementTypeDefinition qualifiedRefinement)
        {
            definition = qualifiedRefinement;
            return true;
        }

        definition = null!;
        return false;
    }

    private bool TryResolveShellStaticType(string path, out IShellStaticType definition)
    {
        if (TryGetNamedType(path, out var directType))
        {
            definition = directType;
            return true;
        }

        // Check Runtime.Classes for IShellStaticType instances that don't implement IShellNamedType
        // (e.g. MathShellType which is a pure static type without type descriptor semantics).
        if (Runtime.Classes.TryGetValue(path, out var classValue) && classValue is IShellStaticType runtimeStaticType)
        {
            definition = runtimeStaticType;
            return true;
        }

        if (BuiltInShellTypes.TryResolveStaticType(path, CreateScopedTypeResolver(), out var builtInType))
        {
            definition = builtInType;
            return true;
        }

        var segments = SplitQualifiedPath(path);

        if (segments.Length >= 2 &&
            TryGetModule(segments[0], out var module))
        {
            object? current = module;
            try
            {
                foreach (var segment in segments[1..])
                {
                    current = Runtime.ObjectAccessor.GetValue(current, segment);

                    if (current is null)
                    {
                        definition = null!;
                        return false;
                    }
                }
            }
            catch (Exception exception) when (exception is not ToshDiagnosticException)
            {
                definition = null!;
                return false;
            }

            if (current is IShellStaticType staticType)
            {
                definition = staticType;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    private bool TryGetClassDefinition(string name, out ToshClassDefinition definition)
    {
        if (TryGetNamedType(name, out var shellType) && shellType is ToshClassDefinition classDefinition)
        {
            definition = classDefinition;
            return true;
        }

        definition = null!;
        return false;
    }

    private bool TryGetModule(string name, out ToshModuleObject module)
    {
        foreach (var scope in _scopes)
        {
            if (scope.Modules.TryGetValue(name, out var scopedModule) &&
                scopedModule is ToshModuleObject scopedToshModule)
            {
                module = scopedToshModule;
                return true;
            }
        }

        if (Runtime.Modules.TryGetValue(name, out var rawModule) &&
            rawModule is ToshModuleObject runtimeModule)
        {
            module = runtimeModule;
            return true;
        }

        module = null!;
        return false;
    }

    private bool TryResolveModuleQualifiedCommand(string qualifiedName, out IShellCommand command)
    {
        var segments = qualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !TryGetModule(segments[0], out var module))
        {
            command = null!;
            return false;
        }

        for (var index = 1; index < segments.Length - 1; index++)
        {
            if (!module.ExportTable.Modules.TryGetValue(segments[index], out var nested) ||
                nested is not ToshModuleObject nestedModule)
            {
                command = null!;
                return false;
            }

            module = nestedModule;
        }

        if (module.ExportTable.Commands.TryGetValue(segments[^1], out var resolved))
        {
            command = resolved;
            return true;
        }

        command = null!;
        return false;
    }

    /// <summary>
    /// Every visible module, flattened, each under the name a caller writes.
    /// </summary>
    /// <remarks>
    /// `TS-P2-68`. Introspection needs the same reach the engine has. Recursion is depth-limited
    /// because a `partial module` may be extended anywhere, and a cycle through the export tables
    /// is cheaper to bound than to prove impossible.
    /// </remarks>
    internal IReadOnlyList<ShellModuleSummary> EnumerateVisibleModules()
    {
        var summaries = new List<ShellModuleSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in _scopes)
        {
            foreach (var (name, value) in scope.Modules)
            {
                Collect(name, value, depth: 0);
            }
        }

        foreach (var (name, value) in Runtime.Modules)
        {
            Collect(name, value, depth: 0);
        }

        return summaries;

        void Collect(string qualifiedName, object? value, int depth)
        {
            if (depth > 16 || value is not ToshModuleObject module || !seen.Add(qualifiedName))
            {
                return;
            }

            var nested = module.ExportTable.Modules
                .Select(entry => $"{qualifiedName}.{entry.Key}")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            summaries.Add(new ShellModuleSummary(
                qualifiedName,
                module.ExportTable.Commands.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                nested,
                module.ExportTable.Types.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                module.ExportTable.Variables.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()));

            foreach (var (childName, childValue) in module.ExportTable.Modules)
            {
                Collect($"{qualifiedName}.{childName}", childValue, depth + 1);
            }
        }
    }

    /// <summary>
    /// Finds a module's exported command by its qualified name, walking the same module tree
    /// <see cref="TryResolveModuleQualifiedCommand"/> walks to dispatch the call.
    /// </summary>
    internal bool TryGetModuleCommandByQualifiedName(string qualifiedName, out IShellCommand command)
    {
        var separator = qualifiedName.LastIndexOf('.');

        if (separator > 0)
        {
            var moduleName = qualifiedName[..separator];
            var memberName = qualifiedName[(separator + 1)..];

            foreach (var scope in _scopes)
            {
                if (TryFromModuleTree(scope.Modules, moduleName, memberName, out command))
                {
                    return true;
                }
            }

            if (TryFromModuleTree(Runtime.Modules, moduleName, memberName, out command))
            {
                return true;
            }
        }

        command = null!;
        return false;

        static bool TryFromModuleTree(
            IEnumerable<KeyValuePair<string, object?>> roots,
            string moduleName,
            string memberName,
            out IShellCommand found)
        {
            var segments = moduleName.Split('.', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length > 0)
            {
                var rootValue = roots.FirstOrDefault(entry => entry.Key == segments[0]).Value;

                if (rootValue is ToshModuleObject rootModule)
                {
                    var current = rootModule;

                    for (var index = 1; index < segments.Length; index++)
                    {
                        if (!current.ExportTable.Modules.TryGetValue(segments[index], out var next) ||
                            next is not ToshModuleObject nextModule)
                        {
                            found = null!;
                            return false;
                        }

                        current = nextModule;
                    }

                    if (current.ExportTable.Commands.TryGetValue(memberName, out var resolved))
                    {
                        found = resolved;
                        return true;
                    }
                }
            }

            found = null!;
            return false;
        }
    }

    private bool TryGetNearestModuleScope(out LexicalScope moduleScope)
    {
        foreach (var scope in _scopes)
        {
            if (scope.IsModuleScope)
            {
                moduleScope = scope;
                return true;
            }
        }

        moduleScope = null!;
        return false;
    }

    private Type? ResolveTypeName(string name)
    {
        return CreateScopedTypeResolver().Resolve(name);
    }

    /// <summary>
    /// Public wrapper around the internal scoped type resolver. Used by the
    /// compiled-code host (`ToshHost.NewObject`) to resolve verbatim
    /// type-argument strings (e.g. <c>"int"</c>, <c>"list&lt;string&gt;"</c>)
    /// against the engine's named-type registry and CLR fallback.
    /// </summary>
    public Type? TryResolveTypeName(string name) => ResolveTypeName(name);

    private IDisposable PushScope(IReadOnlyDictionary<string, object?> locals, bool isModuleScope = false)
    {
        _scopes.Push(new LexicalScope(locals, isModuleScope));
        return new ScopeFrame(_scopes, Runtime.Events);
    }

    /// <summary>
    /// Pushes a scope in which a class's nested types are reachable by their own names, for the
    /// duration of code that belongs to that class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Outside the class a nested type is reached through it — <c>Reactor.Fuel</c> — but inside,
    /// the qualification is noise: the code is already in <c>Reactor</c>. Without this a class
    /// could declare an enum and then not name it, so <c>prop Loaded = Fuel.Mox</c> read
    /// <c>Fuel.Mox</c> as a bareword and an annotation of <c>Fuel</c> failed outright.
    /// </para>
    /// <para>
    /// The names go into an ordinary lexical scope rather than into type resolution as a special
    /// case, so they are found by the walk that already looks for types and disappear when the
    /// scope pops.
    /// </para>
    /// </remarks>
    internal IDisposable? PushNestedTypeScope(ToshClassDefinition definition)
    {
        var nested = definition.NestedTypesForScope();
        if (nested.Count == 0)
        {
            return null;
        }

        var scope = new LexicalScope();
        foreach (var (name, type) in nested)
        {
            scope.Classes[name] = type;
        }

        return PushScope(scope);
    }

    /// <summary>UTF-8 without a byte-order mark, for everything redirection writes.</summary>
    /// <remarks>
    /// `TS-P2-64`. <c>Encoding.UTF8</c> is a <c>UTF8Encoding</c> constructed to emit the
    /// identifier, so every redirected file began <c>ef bb bf</c>. On Unix that is three bytes of
    /// noise in front of whatever the file is for — a redirected <c>#!</c> script will not
    /// execute, and a CSV grows a phantom character in its first column name. `write-file` was
    /// already writing clean UTF-8, so this is the redirection path catching up to it rather than
    /// a new decision.
    /// </remarks>
    private static readonly Encoding RedirectionEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Stack<ToshClassDefinition> _executingClasses = new();

    /// <summary>
    /// The class whose code is currently running, or <see langword="null"/> at the top level.
    /// </summary>
    /// <remarks>
    /// `TS-P2-61`. Instance visibility is decided by *how the object was reached*: <c>$this</c>
    /// carries the declaring class as its accessor, and a reference obtained from outside carries
    /// none. A static access has no such carrier — <c>B.S</c> looks identical whether it is
    /// written inside <c>B</c> or anywhere else — so the question "who is asking?" has to be
    /// answered by the engine instead.
    /// </remarks>
    internal ToshClassDefinition? CurrentClass => _executingClasses.Count > 0 ? _executingClasses.Peek() : null;

    /// <summary>Marks the engine as running <paramref name="definition"/>'s own code.</summary>
    internal IDisposable EnterClass(ToshClassDefinition definition)
    {
        _executingClasses.Push(definition);
        return new ExecutingClassFrame(_executingClasses);
    }

    private sealed class ExecutingClassFrame(Stack<ToshClassDefinition> classes) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (classes.Count > 0)
            {
                classes.Pop();
            }
        }
    }

    private IDisposable PushScope(LexicalScope scope)
    {
        _scopes.Push(scope);
        return new ScopeFrame(_scopes, Runtime.Events);
    }

    private static VariableBinding ToVariableBinding(object? value)
    {
        return value is VariableBinding binding
            ? binding
            : new VariableBinding(value, ReplayAsPipeline: ShouldReplayAsPipeline(value), IsAllocatedOnly: false);
    }

    // ================================================================
    // Rune (macro) expansion
    // ================================================================

    private async IAsyncEnumerable<object?> ExpandRuneAsync(
        RuneDefinition rune,
        IReadOnlyList<ArgumentSyntax> arguments,
        string callerSourceName,
        string callerSourceText,
        IAsyncEnumerable<object?> input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Bind arguments to parameters as thunks (unevaluated)
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (int i = 0; i < rune.Parameters.Count; i++)
        {
            var paramName = rune.Parameters[i].Name;

            if (i < arguments.Count)
            {
                // Store the argument as a RuneThunk — it will be lazily evaluated
                // when the rune body references this parameter
                locals[paramName] = new RuneThunk(
                    arguments[i],
                    callerSourceName,
                    callerSourceText,
                    rune.IsSealed ? CaptureVisibleScopes() : null);
            }
        }

        // Validate argument count
        if (arguments.Count < rune.Parameters.Count)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                "RUNE001",
                $"Rune '{rune.Name}' expects {rune.Parameters.Count} argument(s) but received {arguments.Count}.",
                callerSourceName, callerSourceText, rune.Span));
        }

        // Provide pipeline input as $_ inside the rune body
        locals["_input"] = input;

        if (rune.IsSealed)
        {
            // Hygienic: push captured scopes from definition site, then a new scope
            using var captured = PushCapturedScopes(rune.CapturedScopes);
            await foreach (var item in ExecuteBlockAsync(
                rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                locals, initialInput: input))
            {
                yield return item;
            }
        }
        else
        {
            // Leaky: execute in the caller's binding store without a new scope so that
            // variables declared inside the rune become visible after invocation.
            // We restore only temporary parameter bindings afterward.
            if (_scopes.Count > 0)
            {
                var targetVariables = _scopes.Peek().Variables;
                var previousBindings = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (key, value) in locals)
                {
                    previousBindings[key] = targetVariables.TryGetValue(key, out var existing) ? existing : null;
                    targetVariables[key] = new VariableBinding(value, ReplayAsPipeline: false, IsAllocatedOnly: false);
                }

                try
                {
                    await foreach (var item in ExecuteBlockAsync(
                        rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                        pushNewScope: false, initialInput: input))
                    {
                        yield return item;
                    }
                }
                finally
                {
                    foreach (var (key, previous) in previousBindings)
                    {
                        if (previous is null)
                        {
                            targetVariables.Remove(key);
                        }
                        else
                        {
                            targetVariables[key] = previous;
                        }
                    }
                }
            }
            else
            {
                var targetVariables = Runtime.Variables;
                var previousBindings = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (key, value) in locals)
                {
                    previousBindings[key] = targetVariables.TryGetValue(key, out var existing) ? existing : null;
                    targetVariables[key] = value;
                }

                try
                {
                    await foreach (var item in ExecuteBlockAsync(
                        rune.SourceName, rune.SourceText, rune.Body, cancellationToken,
                        pushNewScope: false, initialInput: input))
                    {
                        yield return item;
                    }
                }
                finally
                {
                    foreach (var (key, previous) in previousBindings)
                    {
                        if (previous is null)
                        {
                            targetVariables.Remove(key);
                        }
                        else
                        {
                            targetVariables[key] = previous;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a RuneThunk — executes the captured argument expression
    /// in the appropriate scope (caller's scope for sealed, current scope for leaky).
    /// </summary>
    internal async Task<object?> EvaluateRuneThunkAsync(
        RuneThunk thunk,
        CancellationToken cancellationToken)
    {
        if (thunk.Syntax is BlockArgumentSyntax blockArg)
        {
            // Block arguments: evaluate the block and collect results
            var results = new List<object?>();
            await foreach (var item in ExecuteBlockAsync(
                thunk.SourceName, thunk.SourceText, blockArg.Block, cancellationToken,
                pushNewScope: !IsInsideLeakyRune()))
            {
                results.Add(item);
            }

            return results.Count switch
            {
                0 => null,
                1 => results[0],
                _ => results.ToArray(),
            };
        }

        // Expression arguments: evaluate and return the single value
        if (thunk.CallerScopes is not null)
        {
            // Sealed: evaluate in caller's captured scope
            using var captured = PushCapturedScopes(thunk.CallerScopes);
            return await EvaluateArgumentAsync(
                thunk.SourceName, thunk.SourceText, thunk.Syntax, cancellationToken);
        }

        // Leaky: evaluate in the current scope
        return await EvaluateArgumentAsync(
            thunk.SourceName, thunk.SourceText, thunk.Syntax, cancellationToken);
    }

    private bool IsInsideLeakyRune()
    {
        // Check if we're inside a leaky rune by looking for RuneThunk values in scope
        foreach (var scope in _scopes)
        {
            foreach (var (_, value) in scope.Variables)
            {
                if (value is RuneThunk thunk && thunk.CallerScopes is null)
                    return true;
            }
        }
        return false;
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

    /// <summary>
    /// The diagnostic behind a failed annotated conversion, when the conversion produced one.
    /// </summary>
    /// <remarks>
    /// A refinement that rejects a value reports itself through an
    /// <c>AnnotationRefinementError</c> carried in the converted slot rather than by throwing, so
    /// the caller has to unwrap it. That unwrapping was written out once per surface, and a
    /// surface that forgot it would silently report "conversion failed" with no reason
    /// (<c>TS-P1-24</c>).
    /// </remarks>
    private static ToshDiagnosticException? DescribeAnnotationFailure(object? converted) =>
        converted is AnnotationRefinementError refinementError ? refinementError.Exception : null;

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

    private async ValueTask<(
        bool Success,
        object? Converted,
        ToshDiagnosticException? Failure)> TryConvertParameterValueAsync(
        FunctionParameterDefinition parameter,
        object? value,
        CancellationToken cancellationToken)
    {
        object? converted = value;

        if (parameter.TypeName is not null)
        {
            var typeConversion = await TryConvertAnnotatedValueAsync(
                parameter.TypeName,
                value,
                cancellationToken);
            converted = typeConversion.Converted;
            if (!typeConversion.Success)
            {
                return (false, converted, DescribeAnnotationFailure(converted));
            }
        }

        var refinement = await TryApplyRefinementWithOptionalCoercionAsync(
            parameter.Refinement,
            converted,
            cancellationToken);
        return (refinement.Success, refinement.RefinedValue, refinement.Failure);
    }

    internal async ValueTask<(
        bool Success,
        Dictionary<string, object?> Locals,
        int Score,
        List<FunctionParameterDefinition>? PendingDefaults)> TryBindCallableParametersAsync(
        IReadOnlyList<FunctionParameterDefinition> parameters,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        static (bool, Dictionary<string, object?>, int, List<FunctionParameterDefinition>?) NoMatch()
            => (false, new Dictionary<string, object?>(StringComparer.Ordinal), 0, null);

        if (PlanCallableParameterBinding(parameters, arguments) is not { } steps)
        {
            return NoMatch();
        }

        var locals = new Dictionary<string, object?>(StringComparer.Ordinal) { ["args"] = arguments.ToArray() };
        var score = 0;
        List<FunctionParameterDefinition>? pendingDefaults = null;
        List<object?>? restArguments = null;

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (step.IsMissing)
            {
                ApplyMissingArgumentStep(step, locals, ref score, ref pendingDefaults);
                continue;
            }

            var conversion = await TryConvertParameterValueAsync(step.Parameter, step.Value, cancellationToken);

            if (!conversion.Success)
            {
                if (conversion.Failure is not null)
                {
                    throw conversion.Failure;
                }

                return NoMatch();
            }

            if (step.IsRest)
            {
                (restArguments ??= []).Add(conversion.Converted);
                continue;
            }

            if (!ReferenceEquals(conversion.Converted, step.Value))
            {
                score++;
            }

            locals[step.Parameter.Name] = conversion.Converted;
        }

        if (parameters.Count > 0 && parameters[^1].IsRest)
        {
            locals[parameters[^1].Name] = restArguments ?? [];
        }

        return (true, locals, score, pendingDefaults);
    }

    /// <summary>
    /// Keeps only the lowest-scoring matches seen so far, collecting ties.
    /// </summary>
    /// <remarks>
    /// The rule — a strictly better score replaces everything, an equal score joins the tie — is
    /// what decides which overload wins and which calls are reported ambiguous. It was written
    /// out once per surface, and the two copies had to agree exactly for compiled and interpreted
    /// dispatch to pick the same overload (<c>TS-P1-24</c>).
    /// </remarks>
    private static void AccumulateBestMatch<TCandidate>(
        List<CallableBindingMatch<TCandidate>> bestMatches,
        ref int bestScore,
        CallableBindingMatch<TCandidate> match)
    {
        if (match.Score < bestScore)
        {
            bestMatches.Clear();
            bestMatches.Add(match);
            bestScore = match.Score;
            return;
        }

        if (match.Score == bestScore)
        {
            bestMatches.Add(match);
        }
    }

    internal IReadOnlyList<CallableBindingMatch<TCandidate>> SelectBestCallableMatches<TCandidate>(
        IEnumerable<TCandidate> candidates,
        Func<TCandidate, IReadOnlyList<FunctionParameterDefinition>> parameterSelector,
        IReadOnlyList<object?> arguments)
    {
        ValidateNamedArgumentUniqueness(arguments, "this call");
        var bestMatches = new List<CallableBindingMatch<TCandidate>>();
        var bestScore = int.MaxValue;
        var candidateParameters = new List<IReadOnlyList<FunctionParameterDefinition>>();

        foreach (var candidate in candidates)
        {
            var parameters = parameterSelector(candidate);
            candidateParameters.Add(parameters);

            if (!TryBindCallableParameters(
                    parameters,
                    arguments,
                    out var locals,
                    out var score,
                    out var pendingDefaults))
            {
                continue;
            }

            AccumulateBestMatch(
                bestMatches,
                ref bestScore,
                new CallableBindingMatch<TCandidate>(candidate, locals, score, pendingDefaults));
        }

        if (bestMatches.Count == 0)
        {
            ThrowIfNamedArgumentMatchesNoCandidate(candidateParameters, arguments);
        }

        return bestMatches.ToArray();
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

    internal async ValueTask<IReadOnlyList<CallableBindingMatch<TCandidate>>> SelectBestCallableMatchesAsync<TCandidate>(
        IEnumerable<TCandidate> candidates,
        Func<TCandidate, IReadOnlyList<FunctionParameterDefinition>> parameterSelector,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        ValidateNamedArgumentUniqueness(arguments, "this call");
        var bestMatches = new List<CallableBindingMatch<TCandidate>>();
        var bestScore = int.MaxValue;
        var candidateParameters = new List<IReadOnlyList<FunctionParameterDefinition>>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = parameterSelector(candidate);
            candidateParameters.Add(parameters);
            var binding = await TryBindCallableParametersAsync(
                parameters,
                arguments,
                cancellationToken);
            if (!binding.Success)
            {
                continue;
            }

            AccumulateBestMatch(
                bestMatches,
                ref bestScore,
                new CallableBindingMatch<TCandidate>(
                    candidate,
                    binding.Locals,
                    binding.Score,
                    binding.PendingDefaults));
        }

        if (bestMatches.Count == 0)
        {
            ThrowIfNamedArgumentMatchesNoCandidate(candidateParameters, arguments);
        }

        return bestMatches.ToArray();
    }

    /// <summary>
    /// Evaluates the pending parameter defaults recorded by the callable
    /// binder for a winning overload (TS-P1-05). Defaults run at call
    /// time in the callable's lexical environment, left-to-right, with
    /// earlier bound parameters visible; later parameters are not in
    /// scope. Losing overload candidates never evaluate their defaults.
    /// The evaluated value passes through the same annotation/refinement
    /// conversion as an explicitly supplied argument.
    /// </summary>
    /// <summary>
    /// Whether this parameter's default still has to be evaluated, seeding the visible scope with
    /// already-bound values as it goes.
    /// </summary>
    /// <remarks>
    /// Three rules in one, and all three were written out once per surface: a rest parameter never
    /// takes a default; a parameter that was actually supplied contributes its value to the scope
    /// the *later* defaults are evaluated in, which is what makes `func f(a, b = $a * 2)` work;
    /// and only the pending ones are evaluated, so a losing overload candidate never runs a
    /// default's side effects (<c>TS-P1-24</c>).
    /// </remarks>
    private static bool NeedsPendingDefault(
        FunctionParameterDefinition parameter,
        HashSet<string> pendingNames,
        Dictionary<string, object?> locals,
        Dictionary<string, object?> visible)
    {
        if (parameter.IsRest)
        {
            return false;
        }

        if (pendingNames.Contains(parameter.Name))
        {
            return true;
        }

        if (locals.TryGetValue(parameter.Name, out var boundValue))
        {
            visible[parameter.Name] = boundValue;
        }

        return false;
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

    /// <summary>
    /// Starts a default-value scope. Ambient bindings — `this` and
    /// `super` for an instance method — are visible to every default
    /// (TS-P1-21); parameters are added as they bind.
    /// </summary>
    private static Dictionary<string, object?> SeedDefaultScope(
        IReadOnlyDictionary<string, object?>? ambient)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (ambient is not null)
        {
            foreach (var (name, value) in ambient)
            {
                scope[name] = value;
            }
        }

        return scope;
    }

    /// <summary>
    /// True when a default failed purely because it referenced `$this`
    /// or `$super` where no instance is in scope. Matched on the
    /// unknown-variable diagnostic the evaluator raises; the
    /// constructor-default regression test locks this coupling so a
    /// change to that diagnostic surfaces loudly.
    /// </summary>
    private static bool ReferencesUnavailableSelf(ToshDiagnosticException failure)
    {
        if (failure.Diagnostics.Count == 0)
        {
            return false;
        }

        var diagnostic = failure.Diagnostics[0];
        if (!string.Equals(diagnostic.Code, "tosh.runtime.unknown_variable", StringComparison.Ordinal))
        {
            return false;
        }

        return diagnostic.Title.Contains("'this'", StringComparison.Ordinal)
            || diagnostic.Title.Contains("'super'", StringComparison.Ordinal);
    }

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

    private static HashSet<string> CollectPendingDefaultNames(
        IReadOnlyList<FunctionParameterDefinition> pendingDefaults)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in pendingDefaults)
        {
            names.Add(pending.Name);
        }

        return names;
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

    internal bool TryConvertAnnotatedValue(string typeName, object? value, out object? converted)
        => TryConvertAnnotatedValue(typeName, value, out converted, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string GetEffectiveAnnotatedTypeName(string typeName)
        => GetEffectiveAnnotatedTypeName(typeName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string GetEffectiveAnnotatedTypeName(string typeName, HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (!TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType) ||
            !activeRefinements.Add(refinementType.Name))
        {
            return typeName;
        }

        var effectiveBase = GetEffectiveAnnotatedTypeName(refinementType.BaseTypeName, activeRefinements);
        activeRefinements.Remove(refinementType.Name);
        return allowsNull && !effectiveBase.EndsWith("?", StringComparison.Ordinal)
            ? effectiveBase + "?"
            : effectiveBase;
    }

    private bool TryConvertAnnotatedValue(
        string typeName,
        object? value,
        out object? converted,
        HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (value is ToshClassSelfReference selfReference)
        {
            value = selfReference.Unwrap();
        }

        if (value is null)
        {
            converted = null;
            return allowsNull;
        }

        if (TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            if (!activeRefinements.Add(refinementType.Name))
            {
                converted = null;
                return false;
            }

            if (!TryConvertAnnotatedValue(refinementType.BaseTypeName, value, out var baseConverted, activeRefinements))
            {
                activeRefinements.Remove(refinementType.Name);
                converted = baseConverted;
                return false;
            }

            if (!TryApplyRefinementWithOptionalCoercion(refinementType.Refinement, baseConverted, out var refinedValue, out var failure))
            {
                activeRefinements.Remove(refinementType.Name);
                converted = failure is not null
                    ? new AnnotationRefinementError(failure)
                    : new AnnotationRefinementFailure(baseConverted, refinementType.Refinement);
                return false;
            }

            activeRefinements.Remove(refinementType.Name);
            converted = refinedValue;
            return true;
        }

        if (value is IShellTypedObject directTyped &&
            (string.Equals(directTyped.ShellTypeDescriptor.ShellTypeName, normalizedTypeName, StringComparison.Ordinal) ||
             string.Equals(directTyped.ShellTypeDescriptor.ShellFullName, normalizedTypeName, StringComparison.Ordinal)))
        {
            converted = value;
            return true;
        }

        if (TryGetNamedType(normalizedTypeName, out var shellType))
        {
            var shellDescriptor = (IShellTypeDescriptor)shellType;

            if (value is IShellTypedObject typed &&
                (string.Equals(typed.ShellTypeDescriptor.ShellTypeName, shellDescriptor.ShellTypeName, StringComparison.Ordinal) ||
                 string.Equals(typed.ShellTypeDescriptor.ShellFullName, shellDescriptor.ShellFullName, StringComparison.Ordinal)))
            {
                converted = value;
                return true;
            }

            if (shellType is ToshEnumDefinition enumDefinition &&
                enumDefinition.TryConvertValue(value, out var enumValue))
            {
                converted = enumValue;
                return true;
            }

            if (shellType is ToshClassDefinition classDefinition &&
                value is ToshClassInstance classInstance)
            {
                for (var current = classInstance.Definition; current is not null; current = current.BaseClass)
                {
                    if (ReferenceEquals(current, classDefinition) ||
                        string.Equals(current.Name, classDefinition.Name, StringComparison.Ordinal))
                    {
                        converted = value;
                        return true;
                    }
                }
            }
        }

        // User-defined generic class annotation, e.g. 'Box<int>'. We accept a
        // ToshClassInstance whose definition (or any ancestor in the
        // inheritance chain) names the same generic class. Match before
        // falling through to ResolveTypeName, because angle-bracket-and-
        // comma syntax can throw "given assembly name was invalid" inside
        // the CLR type loader (Type.GetType treats commas as
        // type/assembly separators).
        if (TryGetGenericClassAnnotation(normalizedTypeName, out var genericClass, out _) &&
            value is ToshClassInstance genericInstance)
        {
            var current = genericInstance.Definition;
            while (current is not null)
            {
                if (ReferenceEquals(current, genericClass) ||
                    string.Equals(current.Name, genericClass.Name, StringComparison.Ordinal))
                {
                    converted = value;
                    return true;
                }
                current = current.BaseClass;
            }

            // Bare-name didn't match any ancestor; treat as a hard mismatch
            // rather than falling through to the CLR type-loader (which
            // would choke on the angle-bracketed name anyway).
            converted = null;
            return false;
        }

        var resolvedType = ResolveTypeName(normalizedTypeName);

        if (resolvedType is not null)
        {
            return TypeConversion.TryConvert(value, resolvedType, out converted);
        }

        // Trait-style constraint names (Numeric, Add, Comparable, …) used as
        // a parameter type annotation: accept any value whose CLR type
        // satisfies the constraint predicate. Lets users write
        // `func +(other: Numeric)` to overload against arbitrary numeric
        // operands without having to enumerate every primitive type.
        if (ToshTypeParameterConstraintRegistry.TryGet(normalizedTypeName, out var constraintPredicate))
        {
            var clrType = value?.GetType();
            if (clrType is not null && constraintPredicate(clrType))
            {
                converted = value;
                return true;
            }

            converted = null;
            return false;
        }

        converted = null;
        return false;
    }

    /// <summary>
    /// Returns the textual span covering a pipeline's stages, used to narrow
    /// runtime diagnostics so the underline points at the offending value
    /// rather than the entire <c>var</c>/assignment statement.
    /// </summary>
    private static TextSpan? GetPipelineSpan(PipelineSyntax? pipeline)
    {
        if (pipeline is null || pipeline.Stages.Count == 0)
        {
            return null;
        }

        var first = pipeline.Stages[0].Span;
        var last = pipeline.Stages[^1].Span;
        return TextSpan.FromBounds(first.Start, last.End);
    }

    /// <summary>
    /// Public bridge for compiled-IL refinement enforcement: converts
    /// (and validates) <paramref name="value"/> against the named
    /// annotated type, throwing a diagnostic on failure. Used by
    /// <c>Tosh.Compiler.Runtime.ToshHost.CheckType</c>.
    /// </summary>
    public object? ConvertValueToAnnotatedType(
        string typeName,
        object? value,
        int spanStart,
        int spanLength,
        string sourceName,
        string sourceText,
        string owner)
        => ConvertAnnotatedValue(typeName, value, new TextSpan(spanStart, spanLength), sourceName, sourceText, owner);

    internal object? ConvertAnnotatedValue(
        string? typeName,
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        object? converted = value;

        if (typeName is not null)
        {
            ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

            if (!TryConvertAnnotatedValue(typeName, value, out converted))
            {
                if (converted is AnnotationRefinementFailure refinementFailure)
                {
                    return EnsureRefinementSatisfied(refinementFailure.Refinement, refinementFailure.Value, span, sourceName, sourceText, owner);
                }

                if (converted is AnnotationRefinementError refinementError)
                {
                    throw refinementError.Exception;
                }

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.annotation_conversion_failed",
                    Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"the value does not match '{typeName}'"));
            }
        }

        return EnsureRefinementSatisfied(refinement, converted, span, sourceName, sourceText, owner);
    }

    internal object? ConvertAnnotatedValue(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

        if (TryConvertAnnotatedValue(typeName, value, out var converted))
        {
            return converted;
        }

        if (converted is AnnotationRefinementFailure refinementFailure)
        {
            return EnsureRefinementSatisfied(refinementFailure.Refinement, refinementFailure.Value, span, sourceName, sourceText, owner);
        }

        if (converted is AnnotationRefinementError refinementError)
        {
            throw refinementError.Exception;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
                Span: span,
                Label: $"the value does not match '{typeName}'"));
    }

    internal async ValueTask<object?> ConvertAnnotatedValueAsync(
        string? typeName,
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        object? converted = value;

        if (typeName is not null)
        {
            ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

            var conversion = await TryConvertAnnotatedValueAsync(
                typeName,
                value,
                cancellationToken);
            converted = conversion.Converted;

            if (!conversion.Success)
            {
                if (converted is AnnotationRefinementFailure refinementFailure)
                {
                    return await EnsureRefinementSatisfiedAsync(
                        refinementFailure.Refinement,
                        refinementFailure.Value,
                        span,
                        sourceName,
                        sourceText,
                        owner,
                        cancellationToken);
                }

                if (converted is AnnotationRefinementError refinementError)
                {
                    throw refinementError.Exception;
                }

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.annotation_conversion_failed",
                    Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"the value does not match '{typeName}'"));
            }
        }

        return await EnsureRefinementSatisfiedAsync(
            refinement,
            converted,
            span,
            sourceName,
            sourceText,
            owner,
            cancellationToken);
    }

    internal async ValueTask<object?> ConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

        var conversion = await TryConvertAnnotatedValueAsync(
            typeName,
            value,
            cancellationToken);
        if (conversion.Success)
        {
            return conversion.Converted;
        }

        if (conversion.Converted is AnnotationRefinementFailure refinementFailure)
        {
            return await EnsureRefinementSatisfiedAsync(
                refinementFailure.Refinement,
                refinementFailure.Value,
                span,
                sourceName,
                sourceText,
                owner,
                cancellationToken);
        }

        if (conversion.Converted is AnnotationRefinementError refinementError)
        {
            throw refinementError.Exception;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{typeName}'"));
    }

    /// <summary>
    /// Internal rather than private so the annotated-conversion drift
    /// guard can run one corpus through both this and the synchronous
    /// <see cref="TryConvertAnnotatedValue(string, object?, out object?)"/>
    /// and assert they agree (TS-P1-24).
    /// </summary>
    internal ValueTask<(bool Success, object? Converted)> TryConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        CancellationToken cancellationToken) =>
        TryConvertAnnotatedValueAsync(
            typeName,
            value,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    private async ValueTask<(bool Success, object? Converted)> TryConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        HashSet<string> activeRefinements,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (value is ToshClassSelfReference selfReference)
        {
            value = selfReference.Unwrap();
        }

        if (value is null)
        {
            return (allowsNull, null);
        }

        if (!TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            return TryConvertAnnotatedValue(
                    typeName,
                    value,
                    out var converted,
                    activeRefinements)
                ? (true, converted)
                : (false, converted);
        }

        if (!activeRefinements.Add(refinementType.Name))
        {
            return (false, null);
        }

        try
        {
            var baseConversion = await TryConvertAnnotatedValueAsync(
                refinementType.BaseTypeName,
                value,
                activeRefinements,
                cancellationToken);
            if (!baseConversion.Success)
            {
                return baseConversion;
            }

            var refinement = await TryApplyRefinementWithOptionalCoercionAsync(
                refinementType.Refinement,
                baseConversion.Converted,
                cancellationToken);
            if (!refinement.Success)
            {
                return (
                    false,
                    refinement.Failure is not null
                        ? new AnnotationRefinementError(refinement.Failure)
                        : new AnnotationRefinementFailure(
                            baseConversion.Converted,
                            refinementType.Refinement));
            }

            return (true, refinement.RefinedValue);
        }
        finally
        {
            activeRefinements.Remove(refinementType.Name);
        }
    }

    private async ValueTask<object?> EnsureRefinementSatisfiedAsync(
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        if (refinement is null)
        {
            return value;
        }

        var result = await TryApplyRefinementWithOptionalCoercionAsync(
            refinement,
            value,
            cancellationToken);
        if (result.Failure is not null)
        {
            throw result.Failure;
        }

        if (result.Success)
        {
            return result.RefinedValue;
        }

        throw CreateRefinementFailedDiagnostic(refinement, span, sourceName, sourceText, owner);
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

    private void ThrowIfUnknownAnnotatedType(
        string typeName,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (IsKnownAnnotatedType(typeName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        var suggestion = ResolveNearestAnnotatedTypeSuggestion(typeName);
        var help = suggestion is null
            ? "define the type first, or use a known CLR/shell type name."
            : $"did you mean '{suggestion}'?";

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_unknown_type",
            Title: $"'{owner}' uses unknown type annotation '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"unknown type '{typeName}'",
            Help: help));
    }

    private bool IsKnownAnnotatedType(string typeName, HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            if (!activeRefinements.Add(refinementType.Name))
            {
                return false;
            }

            var known = IsKnownAnnotatedType(refinementType.BaseTypeName, activeRefinements);
            activeRefinements.Remove(refinementType.Name);
            return known;
        }

        if (TryGetNamedType(normalizedTypeName, out _))
        {
            return true;
        }

        // User-defined generic class instantiation: 'Foo<int, string>'. Accept
        // when the bare name resolves to a ToshClassDefinition whose
        // type-parameter arity matches the supplied argument count, and
        // every supplied argument is itself a known annotated type. Check
        // this before ResolveTypeName because the CLR type loader can
        // throw on angle-bracketed names containing commas.
        if (TryGetGenericClassAnnotation(normalizedTypeName, out _, out var genericArgs))
        {
            foreach (var arg in genericArgs)
            {
                if (!IsKnownAnnotatedType(arg, activeRefinements))
                {
                    return false;
                }
            }
            return true;
        }

        return ResolveTypeName(normalizedTypeName) is not null;
    }

    /// <summary>
    /// Recognises the textual form <c>Bare&lt;arg1, arg2, …&gt;</c> as an
    /// instantiation of a user-defined generic <see cref="ToshClassDefinition"/>.
    /// Splits type-arguments at the top angle-bracket level, validates arity,
    /// and returns the matched class definition.
    /// </summary>
    private bool TryGetGenericClassAnnotation(
        string typeName,
        out ToshClassDefinition definition,
        out IReadOnlyList<string> typeArguments)
    {
        definition = null!;
        typeArguments = Array.Empty<string>();

        if (string.IsNullOrEmpty(typeName) || !typeName.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var lt = typeName.IndexOf('<');
        if (lt <= 0)
        {
            return false;
        }

        var bare = typeName[..lt];
        var inner = typeName.Substring(lt + 1, typeName.Length - lt - 2);

        if (!TryGetNamedType(bare, out var named) || named is not ToshClassDefinition cls || cls.TypeParameterNames.Count == 0)
        {
            return false;
        }

        var args = SplitTopLevelTypeArguments(inner);
        if (args.Count != cls.TypeParameterNames.Count)
        {
            return false;
        }

        definition = cls;
        typeArguments = args;
        return true;
    }

    private static IReadOnlyList<string> SplitTopLevelTypeArguments(string inner)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        if (start <= inner.Length)
        {
            var tail = inner[start..].Trim();
            if (tail.Length > 0)
            {
                result.Add(tail);
            }
        }
        return result;
    }

    private string? ResolveNearestAnnotatedTypeSuggestion(string typeName)
    {
        var normalized = typeName.EndsWith("?", StringComparison.Ordinal) ? typeName[..^1] : typeName;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in _scopes)
        {
            foreach (var name in scope.Classes.Keys)
            {
                candidates.Add(name);
            }

            foreach (var name in scope.RefinementTypes.Keys)
            {
                candidates.Add(name);
            }
        }

        foreach (var (name, _) in Runtime.Classes)
        {
            candidates.Add(name);
        }

        var bestMatch = (Name: (string?)null, Distance: int.MaxValue);
        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(normalized, candidate);
            if (distance < bestMatch.Distance)
            {
                bestMatch = (candidate, distance);
            }
        }

        return bestMatch.Name is not null &&
               bestMatch.Distance <= Math.Max(2, Math.Max(normalized.Length, bestMatch.Name.Length) * 2 / 5)
            ? bestMatch.Name
            : null;
    }

    /// <summary>
    /// Synchronous adapter over
    /// <see cref="TryApplyRefinementWithOptionalCoercionAsync"/> for the conversion
    /// callers that are not asynchronous.  The guard, predicate, and fallback-coercion
    /// sequence exists only in the asynchronous implementation (<c>TS-P1-24</c>);
    /// this method previously carried a second copy of it, and the whole synchronous
    /// sub-chain it drove — <c>TryApplyGuardedRefinementCoercion</c>,
    /// <c>TryEvaluateRefinementPredicate</c>, and the three leaf evaluators — has
    /// been removed with it.
    /// </summary>
    private bool TryApplyRefinementWithOptionalCoercion(
        RefinementAnnotation? refinement,
        object? value,
        out object? refinedValue,
        out ToshDiagnosticException? failure)
    {
        var (success, refined, applyFailure) = TryApplyRefinementWithOptionalCoercionAsync(
                refinement,
                value,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        refinedValue = refined;
        failure = applyFailure;
        return success;
    }

    private async ValueTask<(
        bool Success,
        object? RefinedValue,
        ToshDiagnosticException? Failure)> TryApplyRefinementWithOptionalCoercionAsync(
        RefinementAnnotation? refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        if (refinement is null)
        {
            return (true, value, null);
        }

        object? currentValue = value;
        var guarded = await TryApplyGuardedRefinementCoercionAsync(
            refinement,
            currentValue,
            cancellationToken);
        if (!guarded.Success)
        {
            return (false, guarded.RefinedValue, guarded.Failure);
        }

        currentValue = guarded.RefinedValue;
        var predicate = await TryEvaluateRefinementPredicateAsync(
            refinement,
            currentValue,
            cancellationToken);
        if (!predicate.Completed)
        {
            return (false, currentValue, predicate.Failure);
        }

        if (predicate.Satisfied)
        {
            return (true, currentValue, null);
        }

        var fallbackClause = refinement.Clauses
            .OfType<RefinementCoerceClause>()
            .FirstOrDefault(static clause => clause.Guard is null);
        if (fallbackClause is null)
        {
            return (false, currentValue, null);
        }

        object? coerced;
        try
        {
            coerced = await EvaluateRefinementCoercerAsync(
                refinement,
                fallbackClause,
                currentValue,
                cancellationToken);
        }
        catch (ToshDiagnosticException exception)
        {
            return (false, currentValue, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                false,
                currentValue,
                CreateExpressionDiagnostic(
                    refinement.SourceName,
                    refinement.SourceText,
                    fallbackClause.Coercer,
                    exception));
        }

        predicate = await TryEvaluateRefinementPredicateAsync(
            refinement,
            coerced,
            cancellationToken);
        if (!predicate.Completed)
        {
            return (false, coerced, predicate.Failure);
        }

        return (predicate.Satisfied, coerced, null);
    }

    private async ValueTask<(
        bool Success,
        object? RefinedValue,
        ToshDiagnosticException? Failure)> TryApplyGuardedRefinementCoercionAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        object? refinedValue = value;

        // Run every guarded `if … coerce` clause in order, threading the coerced
        // value forward so subsequent guards see the result of earlier coercions.
        // For example, given:
        //
        //   if (not (_ is int)) coerce ((round (Double.Parse(_)) 0) as int)
        //   if (_ < 0)          coerce (Math.Abs(_))
        //
        // an input of "-4.25" becomes -4 (first clause), then 4 (second clause).
        // (Stopping after the first match would skip the negativity fix-up.)
        foreach (var clause in refinement.Clauses
                     .OfType<RefinementCoerceClause>()
                     .Where(static clause => clause.Guard is not null))
        {
            try
            {
                if (!await EvaluateRefinementBooleanExpressionAsync(
                        refinement,
                        clause.Guard!,
                        refinedValue,
                        clause.Span,
                        "Refinement coercion guards",
                        cancellationToken,
                        useTruthiness: true))
                {
                    continue;
                }
            }
            catch (ToshDiagnosticException exception)
            {
                return (false, refinedValue, exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    false,
                    refinedValue,
                    CreateExpressionDiagnostic(
                        refinement.SourceName,
                        refinement.SourceText,
                        clause.Guard!,
                        exception));
            }

            try
            {
                refinedValue = await EvaluateRefinementCoercerAsync(
                    refinement,
                    clause,
                    refinedValue,
                    cancellationToken);
            }
            catch (ToshDiagnosticException exception)
            {
                return (false, refinedValue, exception);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return (
                    false,
                    refinedValue,
                    CreateExpressionDiagnostic(
                        refinement.SourceName,
                        refinement.SourceText,
                        clause.Coercer,
                        exception));
            }
        }

        return (true, refinedValue, null);
    }

    private async ValueTask<(
        bool Completed,
        bool Satisfied,
        ToshDiagnosticException? Failure)> TryEvaluateRefinementPredicateAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        try
        {
            return (
                true,
                await EvaluateRefinementPredicateAsync(
                    refinement,
                    value,
                    cancellationToken),
                null);
        }
        catch (ToshDiagnosticException exception)
        {
            return (false, false, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (
                false,
                false,
                CreateExpressionDiagnostic(
                    refinement.SourceName,
                    refinement.SourceText,
                    GetPrimaryRefinementPredicate(refinement),
                    exception));
        }
    }

    private async ValueTask<object?> EvaluateRefinementCoercerAsync(
        RefinementAnnotation refinement,
        RefinementCoerceClause clause,
        object? value,
        CancellationToken cancellationToken)
    {
        using var captured = PushCapturedScopes(refinement.CapturedScopes);
        using var currentValueScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_"] = value,
        });

        return await EvaluateArgumentAsync(
            refinement.SourceName,
            refinement.SourceText,
            clause.Coercer,
            cancellationToken);
    }

    private async ValueTask<bool> EvaluateRefinementPredicateAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        foreach (var clause in refinement.Clauses.OfType<RefinementWhereClause>())
        {
            if (!await EvaluateRefinementBooleanExpressionAsync(
                    refinement,
                    clause.Predicate,
                    value,
                    clause.Span,
                    "Refinement predicates",
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<bool> EvaluateRefinementBooleanExpressionAsync(
        RefinementAnnotation refinement,
        ArgumentSyntax expression,
        object? value,
        TextSpan span,
        string title,
        CancellationToken cancellationToken,
        bool useTruthiness = false)
    {
        using var captured = PushCapturedScopes(refinement.CapturedScopes);
        using var currentValueScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_"] = value,
        });

        var predicateValue = await EvaluateArgumentAsync(
            refinement.SourceName,
            refinement.SourceText,
            expression,
            cancellationToken);

        if (useTruthiness)
        {
            return ToshTruthiness.IsTruthy(predicateValue);
        }

        if (predicateValue is not bool boolean)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.refinement_requires_boolean",
                Title: $"{title} must evaluate to boolean values.",
                SourceName: refinement.SourceName,
                SourceText: refinement.SourceText,
                Span: span,
                Label: "this refinement did not evaluate to true or false"));
        }

        return boolean;
    }

    /// <summary>
    /// Synchronous adapter over <see cref="EnsureRefinementSatisfiedAsync"/>.
    /// </summary>
    /// <remarks>
    /// This method used to inline its own copy of the guard/predicate/coerce
    /// sequence, and the copy had drifted: a non-diagnostic exception raised by the
    /// predicate <em>after</em> fallback coercion was reported against the coercer's
    /// span here and against the predicate's span on the asynchronous path. The
    /// asynchronous behaviour is canonical, so converging on it moved the
    /// synchronous span to the predicate (<c>TS-P1-24</c>).
    /// </remarks>
    private object? EnsureRefinementSatisfied(
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
        => EnsureRefinementSatisfiedAsync(
                refinement,
                value,
                span,
                sourceName,
                sourceText,
                owner,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private bool TryResolveRefinementTypeForAnnotation(string typeName, out RefinementTypeDefinition definition)
    {
        if (TryGetRefinementType(typeName, out definition))
        {
            return true;
        }

        if (!TrySplitGenericTypeName(typeName, out var genericName, out var typeArguments) ||
            !TryGetRefinementType(genericName, out var genericDefinition))
        {
            definition = null!;
            return false;
        }

        if (genericDefinition.TypeParameters.Count == 0 ||
            genericDefinition.TypeParameters.Count != typeArguments.Count)
        {
            definition = null!;
            return false;
        }

        var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < genericDefinition.TypeParameters.Count; index++)
        {
            substitutions[genericDefinition.TypeParameters[index]] = typeArguments[index];
        }

        definition = new RefinementTypeDefinition(
            Name: typeName,
            TypeParameters: Array.Empty<string>(),
            BaseTypeName: SubstituteTypeParametersInTypeName(genericDefinition.BaseTypeName, substitutions),
            Refinement: SpecializeRefinementAnnotation(genericDefinition, typeName, substitutions),
            SourceName: genericDefinition.SourceName,
            SourceText: genericDefinition.SourceText,
            Modifier: genericDefinition.Modifier,
            Span: genericDefinition.Span);
        return true;
    }

    private static string SubstituteTypeParametersInTypeName(string typeName, IReadOnlyDictionary<string, string> substitutions)
    {
        var result = typeName;
        foreach (var (parameter, replacement) in substitutions)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(parameter)}\b", replacement);
        }

        return result;
    }

    /// <summary>
    /// Returns the bare type name without any '&lt;...&gt;' generic argument
    /// suffix. Used by interface/trait/base-class lookup paths so that
    /// references like 'IPoint&lt;int&gt;' resolve to the registered
    /// 'IPoint' definition.
    /// </summary>
    private static string StripGenericTypeArguments(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }
        var lt = typeName.IndexOf('<');
        return lt < 0 ? typeName : typeName.Substring(0, lt);
    }

    /// <summary>
    /// Validates type-argument arity and constraints on a 'fulfills'
    /// clause referencing a generic interface. Type arguments that
    /// reference the implementing class's own type parameters are
    /// accepted without constraint checks (they're checked when the
    /// class is instantiated). Concrete types are validated against
    /// the interface's where-clauses.
    /// </summary>
    private void ValidateInterfaceTypeArguments(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        ToshInterfaceDefinition ifaceDefinition,
        string ifaceReference)
    {
        var lt = ifaceReference.IndexOf('<');
        var hasArgs = lt >= 0 && ifaceReference.EndsWith(">", StringComparison.Ordinal);
        var ifaceArity = ifaceDefinition.TypeParameterNames.Count;

        if (!hasArgs)
        {
            // Bare reference to an unparameterised or generic
            // interface. Generic interfaces require explicit type
            // arguments at fulfills sites.
            if (ifaceArity > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_interface_type_arguments",
                    Title: $"Class '{@class.Name}' fulfills generic interface '{ifaceDefinition.Name}' without type arguments.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"write 'fulfills {ifaceDefinition.Name}<{string.Join(", ", ifaceDefinition.TypeParameterNames)}>'"));
            }
            return;
        }

        var inner = ifaceReference.Substring(lt + 1, ifaceReference.Length - lt - 2);
        var args = SplitTopLevelTypeArguments(inner);

        if (ifaceArity == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unexpected_interface_type_arguments",
                Title: $"Interface '{ifaceDefinition.Name}' is not generic and does not accept type arguments.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: @class.Span,
                Label: $"remove '<{inner}>'"));
        }

        if (args.Count != ifaceArity)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.interface_type_argument_arity_mismatch",
                Title: $"Generic interface '{ifaceDefinition.Name}' expects {ifaceArity} type argument(s) <{string.Join(", ", ifaceDefinition.TypeParameterNames)}> but received {args.Count}.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: @class.Span,
                Label: $"<{string.Join(", ", args)}> has {args.Count} arg(s)"));
        }

        if (ifaceDefinition.TypeParameterConstraints.Count == 0)
        {
            return;
        }

        // Build map: interface type-param name → supplied argument string.
        var argByParam = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ifaceArity; i++)
        {
            argByParam[ifaceDefinition.TypeParameterNames[i]] = args[i];
        }

        // Set of class's own type-parameter names — args matching one
        // are deferred (validated at instantiation).
        var classTypeParams = new HashSet<string>(
            @class.TypeParameters ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var clause in ifaceDefinition.TypeParameterConstraints)
        {
            if (!argByParam.TryGetValue(clause.TypeParameter, out var argText))
            {
                continue;
            }
            argText = argText.Trim();
            if (classTypeParams.Contains(argText))
            {
                continue; // forwarded — defer to instantiation site
            }
            var bound = TryResolveTypeName(argText);
            if (bound is null)
            {
                continue; // unknown name — accept conservatively
            }

            foreach (var constraintName in clause.ConstraintNames)
            {
                bool satisfied;
                bool known;
                if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                {
                    satisfied = predicate(bound);
                    known = true;
                }
                else
                {
                    var clr = TryResolveTypeName(constraintName);
                    if (clr is not null)
                    {
                        satisfied = clr.IsAssignableFrom(bound);
                        known = true;
                    }
                    else
                    {
                        satisfied = true;
                        known = false;
                    }
                }

                if (satisfied || !known) continue;

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.interface_type_argument_constraint_violation",
                    Title: $"Generic interface '{ifaceDefinition.Name}' requires type parameter '{clause.TypeParameter}' to satisfy '{constraintName}', but '{argText}' (CLR {bound.FullName ?? bound.Name}) does not.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"'{argText}' does not satisfy '{constraintName}'"));
            }
        }
    }

    private RefinementAnnotation? SpecializeRefinementAnnotation(
        RefinementTypeDefinition genericDefinition,
        string closedTypeName,
        IReadOnlyDictionary<string, string> substitutions)
    {
        if (genericDefinition.Refinement is null)
        {
            return null;
        }

        var syntheticSourceName = $"{genericDefinition.SourceName}<{closedTypeName}>";
        var builder = new StringBuilder();
        builder.AppendLine("type __Ref = any {");
        foreach (var clause in genericDefinition.Refinement.Clauses)
        {
            switch (clause)
            {
                case RefinementWhereClause whereClause:
                    builder.Append("    where ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, whereClause.Predicate.Span),
                            substitutions));
                    break;
                case RefinementCoerceClause { Guard: { } guard, Coercer: var coercer }:
                    builder.Append("    if ")
                        .Append(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, guard.Span),
                            substitutions))
                        .Append(" coerce ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, coercer.Span),
                            substitutions));
                    break;
                case RefinementCoerceClause { Guard: null, Coercer: var coercer }:
                    builder.Append("    coerce ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, coercer.Span),
                            substitutions));
                    break;
            }
        }
        builder.Append('}');
        var syntheticSourceText = builder.ToString();

        var parseResult = ToshParser.Parse(syntheticSourceText, syntheticSourceName);
        if (parseResult.Diagnostics.Count > 0 ||
            parseResult.Statement is not TypeAliasStatementSyntax { Refinement: { } specializedRefinement })
        {
            var diagnostic = parseResult.Diagnostics.FirstOrDefault();
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.refinement_specialization_failed",
                Title: $"Refinement alias '{genericDefinition.Name}' could not be specialized for '{closedTypeName}'.",
                SourceName: genericDefinition.SourceName,
                SourceText: genericDefinition.SourceText,
                Span: genericDefinition.Span,
                Label: "this generic refinement alias could not be instantiated",
                Help: diagnostic?.Title));
        }

        return CreateRefinementAnnotation(syntheticSourceName, syntheticSourceText, specializedRefinement)! with
        {
            CapturedScopes = genericDefinition.Refinement.CapturedScopes,
        };
    }

    private static string SubstituteTypeParametersInText(string text, IReadOnlyDictionary<string, string> substitutions)
    {
        var result = text;
        foreach (var (parameter, replacement) in substitutions)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(parameter)}\b", replacement);
        }

        return result;
    }

    private static ArgumentSyntax GetPrimaryRefinementPredicate(RefinementAnnotation refinement)
        => refinement.Clauses.OfType<RefinementWhereClause>().First().Predicate;

    private static bool TrySplitGenericTypeName(
        string typeName,
        out string genericName,
        out IReadOnlyList<string> typeArguments)
    {
        genericName = string.Empty;
        typeArguments = Array.Empty<string>();

        var openIndex = typeName.IndexOf('<');
        if (openIndex <= 0 || !typeName.EndsWith('>'))
        {
            return false;
        }

        genericName = typeName[..openIndex].Trim();
        var argsText = typeName.Substring(openIndex + 1, typeName.Length - openIndex - 2);
        var arguments = SplitGenericTypeArguments(argsText);
        if (arguments.Count == 0)
        {
            return false;
        }

        typeArguments = arguments;
        return true;
    }

    private static IReadOnlyList<string> SplitGenericTypeArguments(string text)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (character)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(text[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        var last = text[start..].Trim();
        if (last.Length > 0)
        {
            arguments.Add(last);
        }

        return arguments;
    }

    private static bool TryGetRefinementSnippet(string sourceText, TextSpan span, out string snippet)
    {
        if (span.Start >= 0 && span.Start + span.Length <= sourceText.Length)
        {
            snippet = sourceText.Substring(span.Start, span.Length).Trim();
            return true;
        }

        snippet = string.Empty;
        return false;
    }

    internal IReadOnlyList<object?> ExecuteClassBlockSync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        BlockSyntax block,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName)
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            Runtime.Config.Shell.MaxRecursionDepth,
            callName,
            sourceName,
            sourceText,
            block.Span);
        using var captured = PushCapturedScopes(capturedScopes);
        _functionCallStack.Push(callName);

        try
        {
            return AsyncEnumerableExtensions.ToListAsync(
                    ExecuteBlockAsync(
                        sourceName,
                        sourceText,
                        block,
                        CancellationToken.None,
                        locals))
                .GetAwaiter()
                .GetResult();
        }
        catch (ReturnSignalException signal)
        {
            return signal.Values;
        }
        finally
        {
            _functionCallStack.Pop();
        }
    }

    internal async Task<IReadOnlyList<object?>> ExecuteClassBlockAsync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        BlockSyntax block,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName,
        CancellationToken cancellationToken)
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        cancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            Runtime.Config.Shell.MaxRecursionDepth,
            callName,
            sourceName,
            sourceText,
            block.Span);
        using var captured = PushCapturedScopes(capturedScopes);
        _functionCallStack.Push(callName);

        try
        {
            return await AsyncEnumerableExtensions.ToListAsync(
                ExecuteBlockAsync(
                    sourceName,
                    sourceText,
                    block,
                    cancellationToken,
                    locals),
                cancellationToken);
        }
        catch (ReturnSignalException signal)
        {
            return signal.Values;
        }
        finally
        {
            _functionCallStack.Pop();
        }
    }

    /// <summary>
    /// Wraps a class-body pipeline as a one-statement block, and projects the values it produced
    /// back into a single result — the two halves of <c>EvaluateClassPipelineValue</c> that do not
    /// depend on how the block is executed.
    /// </summary>
    /// <remarks>
    /// Extracted for <c>TS-P1-24</c>. The <c>Sync</c> and <c>Async</c> forms of this method were
    /// byte-identical apart from the block call, including the unwrapping switch below — and the
    /// twin inventory never counted them, because its discovery rule looked for
    /// <c>Foo</c>/<c>FooAsync</c> and these are named <c>FooSync</c>/<c>FooAsync</c>. The guard has
    /// been taught the second convention.
    /// </remarks>
    private static BlockSyntax BuildClassPipelineBlock(PipelineSyntax pipeline)
    {
        var span = pipeline.Stages.Count == 0
            ? default
            : TextSpan.FromBounds(pipeline.Stages[0].Span.Start, pipeline.Stages[^1].Span.End);

        return new BlockSyntax([new PipelineStatementSyntax(pipeline, span)], span);
    }

    /// <summary>
    /// Collapses a class-body pipeline's results, unwrapping <c>$this</c> self-references so a
    /// property or method returning <c>$this</c> yields the instance rather than the marker.
    /// </summary>
    private static object? ProjectClassPipelineValues(IReadOnlyList<object?> values)
    {
        return values.Count switch
        {
            0 => null,
            1 => values[0] is ToshClassSelfReference selfReference ? selfReference.Unwrap() : values[0],
            _ => values
                .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
                .ToArray(),
        };
    }

    internal object? EvaluateClassPipelineValueSync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName = "<class>")
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        if (TryEvaluateShorthandLocalPipeline(pipeline, locals, out var shorthandValue))
        {
            return shorthandValue;
        }

        var values = ExecuteClassBlockSync(
            declaringClass,
            sourceName,
            sourceText,
            BuildClassPipelineBlock(pipeline),
            locals,
            capturedScopes,
            callName);

        return ProjectClassPipelineValues(values);
    }

    internal async ValueTask<object?> EvaluateClassPipelineValueAsync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        CancellationToken cancellationToken,
        string callName = "<class>")
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        if (TryEvaluateShorthandLocalPipeline(pipeline, locals, out var shorthandValue))
        {
            return shorthandValue;
        }

        var values = await ExecuteClassBlockAsync(
            declaringClass,
            sourceName,
            sourceText,
            BuildClassPipelineBlock(pipeline),
            locals,
            capturedScopes,
            callName,
            cancellationToken);

        return ProjectClassPipelineValues(values);
    }

    private static bool TryEvaluateShorthandLocalPipeline(
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        out object? value)
    {
        if (pipeline.Redirections is { Count: > 0 } || pipeline.Stages.Count != 1)
        {
            value = null;
            return false;
        }

        if (pipeline.Stages[0] is CommandSyntax command &&
            command.Arguments.Count == 0 &&
            locals.TryGetValue(command.Name, out value))
        {
            return true;
        }

        value = null;
        return false;
    }


    internal async IAsyncEnumerable<object?> ExecuteFunctionAsync(
        FunctionDefinition definition,
        CommandContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            Runtime.Config.Shell.MaxRecursionDepth,
            definition.Name,
            context.Invocation?.SourceName ?? definition.SourceName,
            context.Invocation?.SourceText ?? definition.SourceText,
            context.Invocation?.CommandSpan ?? definition.Span);
        using var capturedScopes = PushCapturedScopes(definition.CapturedScopes);
        var inputItems = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var (locals, typeBindings) = BindFunctionParameters(definition, context, inputItems);

        // Collect parameters whose defaults must be evaluated because the
        // caller supplied neither a named nor a positional argument, then
        // apply them through the shared callable default binder so free
        // functions, methods, and constructors share one protocol
        // (TS-P1-05): call time, lexical scope, left-to-right, earlier
        // bound parameters visible.
        var namedArgNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var positionalArgCount = 0;
        foreach (var arg in context.Arguments)
        {
            if (arg is NamedArgument named)
                namedArgNames.Add(named.Name);
            else
                positionalArgCount++;
        }

        List<FunctionParameterDefinition>? pendingDefaults = null;
        var posIdx = 0;
        for (var i = 0; i < definition.Parameters.Count; i++)
        {
            var param = definition.Parameters[i];
            if (param.IsRest) continue;

            var wasProvidedByName = namedArgNames.Contains(param.Name);
            var wasProvidedByPosition = !wasProvidedByName && posIdx < positionalArgCount;
            if (!wasProvidedByName) posIdx++;

            if (param.DefaultValue is not null && !wasProvidedByName && !wasProvidedByPosition)
            {
                (pendingDefaults ??= new List<FunctionParameterDefinition>()).Add(param);
            }
        }

        // The definition's captured scopes are already pushed above, so
        // the defaults see the same lexical environment the body will.
        await ApplyPendingParameterDefaultsAsync(
            definition.Parameters,
            locals,
            pendingDefaults,
            definition.SourceName,
            definition.SourceText,
            capturedScopes: null,
            callName: definition.Name,
            context.CancellationToken);

        // Only seed the function body with an "initial input" enumerable when
        // the caller actually piped data in (or the call site is inside a
        // pipeline). Otherwise EvaluatePipelineAsync would treat every first
        // statement in the body as pipelined (because initialInput != null),
        // which forces external commands into RedirectStandardInput=true mode
        // and breaks interactive children like `sudo pacman -Syu` that need a
        // real TTY for password / confirmation prompts.
        IAsyncEnumerable<object?>? initialInput = (inputItems.Count > 0 || context.IsPipelined)
            ? AsyncEnumerableExtensions.FromEnumerable(inputItems)
            : null;
        var firstCommandArguments = definition.IsCommandWrapper && definition.Parameters.Count == 0
            ? context.Arguments
            : null;

        _functionCallStack.Push(definition.Name);
        _functionArgumentsStack.Push(context.Arguments.ToArray());
        _functionInputStack.Push(inputItems.Count switch
        {
            0 => null,
            1 => inputItems[0],
            _ => inputItems.ToArray(),
        });

        if (definition.IsGenerator)
        {
            // Generator functions stream values as they are produced.
            // C# does not allow yield inside try-with-catch, so we use a manual enumerator.
            var enumerator = ExecuteBlockAsync(
                definition.SourceName,
                definition.SourceText,
                definition.Body,
                context.CancellationToken,
                locals,
                initialInput,
                firstCommandArguments)
                .GetAsyncEnumerator(context.CancellationToken);

            Exception? pendingException = null;
            IReadOnlyList<object?>? returnValues = null;

            try
            {
                while (true)
                {
                    object? current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                            break;
                        current = enumerator.Current;
                    }
                    catch (ReturnSignalException signal)
                    {
                        returnValues = signal.Values;
                        break;
                    }
                    catch (BreakSignalException signal)
                    {
                        pendingException = CreateLoopControlDiagnostic(
                            definition.SourceName,
                            definition.SourceText,
                            signal.Span,
                            keyword: "break",
                            code: "tosh.runtime.break_outside_loop",
                            title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
                        break;
                    }
                    catch (ContinueSignalException signal)
                    {
                        pendingException = CreateLoopControlDiagnostic(
                            definition.SourceName,
                            definition.SourceText,
                            signal.Span,
                            keyword: "continue",
                            code: "tosh.runtime.continue_outside_loop",
                            title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
                        break;
                    }

                    yield return ConvertFunctionReturnValue(definition, context, current, typeBindings);
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
                _functionInputStack.Pop();
                _functionArgumentsStack.Pop();
                _functionCallStack.Pop();
            }

            if (pendingException is not null)
                throw pendingException;

            if (returnValues is not null)
            {
                foreach (var value in returnValues)
                {
                    yield return ConvertFunctionReturnValue(definition, context, value, typeBindings);
                }
            }
        }
        else
        {
            // Non-generator functions buffer all output before yielding.
            var values = new List<object?>();

            try
            {
                await foreach (var value in ExecuteBlockAsync(
                                   definition.SourceName,
                                   definition.SourceText,
                                   definition.Body,
                                   context.CancellationToken,
                                   locals,
                                   initialInput,
                                   firstCommandArguments)
                                   .WithCancellation(context.CancellationToken))
                {
                    values.Add(value);
                }
            }
            catch (ReturnSignalException signal)
            {
                values.AddRange(signal.Values);
            }
            catch (BreakSignalException signal)
            {
                throw CreateLoopControlDiagnostic(
                    definition.SourceName,
                    definition.SourceText,
                    signal.Span,
                    keyword: "break",
                    code: "tosh.runtime.break_outside_loop",
                    title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
            }
            catch (ContinueSignalException signal)
            {
                throw CreateLoopControlDiagnostic(
                    definition.SourceName,
                    definition.SourceText,
                    signal.Span,
                    keyword: "continue",
                    code: "tosh.runtime.continue_outside_loop",
                    title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
            }
            finally
            {
                _functionInputStack.Pop();
                _functionArgumentsStack.Pop();
                _functionCallStack.Pop();
            }

            foreach (var value in values)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return ConvertFunctionReturnValue(definition, context, value, typeBindings);
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteBlockAsync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? locals = null,
        IAsyncEnumerable<object?>? initialInput = null,
        IReadOnlyList<object?>? firstCommandArguments = null,
        bool pushNewScope = true)
    {
        using var _ = pushNewScope
            ? PushScope(locals ?? new Dictionary<string, object?>(StringComparer.Ordinal))
            : ScopeFrames.Empty;
        var pendingInput = initialInput;
        var pendingFirstCommandArguments = firstCommandArguments;

        var hasDeferStatements = false;

        foreach (var statement in block.Statements)
        {
            if (statement is DeferStatementSyntax)
            {
                hasDeferStatements = true;
                break;
            }
        }

        if (hasDeferStatements)
        {
            var deferredBlocks = new List<BlockSyntax>();
            var outputValues = new List<object?>();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingException = null;
            var deferFailures = new ToshDeferFailureState();

            try
            {
                await foreach (var value in ExecuteBlockStatementsAsync(
                    sourceName, sourceText, block, cancellationToken,
                    pendingInput, pendingFirstCommandArguments, deferredBlocks)
                    .WithCancellation(cancellationToken))
                {
                    outputValues.Add(value);
                }
            }
            catch (ShellControlFlowException ex)
            {
                // Return/break/continue are pending exits, not body failures.
                // A cleanup failure still supersedes the pending jump below.
                pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
            catch (Exception ex)
            {
                pendingException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                ToshDeferFailures.AttachSourceContext(ex, sourceName, sourceText);
                deferFailures.CaptureBodyFailure(ex);
            }

            for (var i = deferredBlocks.Count - 1; i >= 0; i--)
            {
                try
                {
                    // Cleanup is shielded from the token that caused the
                    // enclosing scope to exit. This ensures every reached
                    // defer gets one chance to run; cancellation is resumed
                    // after cleanup has completed.
                    await foreach (var value in ExecuteBlockAsync(
                        sourceName, sourceText, deferredBlocks[i], CancellationToken.None)
                        .WithCancellation(CancellationToken.None))
                    {
                        // Deferred blocks execute for side effects only; output is discarded.
                    }
                }
                catch (ShellControlFlowException)
                {
                    // Control flow signals from deferred blocks are suppressed.
                }
                catch (Exception cleanupFailure)
                {
                    // A failed cleanup must not prevent any earlier cleanup
                    // from running. The shared state retains the original
                    // exception instances and their deterministic LIFO order.
                    ToshDeferFailures.AttachSourceContext(
                        cleanupFailure,
                        sourceName,
                        sourceText);
                    deferFailures.CaptureCleanupFailure(cleanupFailure);
                }
            }

            // Match the ordinary streaming path: values produced before an
            // exit remain visible, even though defer requires buffering them
            // until cleanup has run.
            foreach (var value in outputValues)
            {
                yield return value;
            }

            // Cleanup failures supersede a pending return/break/continue. If
            // there is also a body failure, the helper throws the documented
            // ordered aggregate (or preserves cancellation as cancellation).
            deferFailures.ThrowIfCleanupFailed();
            pendingException?.Throw();
        }
        else
        {
            await foreach (var value in ExecuteBlockStatementsAsync(
                sourceName, sourceText, block, cancellationToken,
                pendingInput, pendingFirstCommandArguments, deferredBlocks: null)
                .WithCancellation(cancellationToken))
            {
                yield return value;
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteBlockStatementsAsync(
        string sourceName,
        string sourceText,
        BlockSyntax block,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IAsyncEnumerable<object?>? pendingInput,
        IReadOnlyList<object?>? pendingFirstCommandArguments,
        List<BlockSyntax>? deferredBlocks)
    {
        foreach (var statement in block.Statements)
        {
            // `exit` stops the work, not just the session. It recorded an exit code and set a
            // flag that only the REPL loop ever read, so a script ran on regardless: `echo one`,
            // `exit 0`, `echo two` printed both lines, and any script using `exit` for an early
            // return silently carried on doing what it meant to skip.
            //
            // Checked here because every body — a script, a function, a loop, a branch — runs
            // through this loop, so one check ends them all rather than each remembering to.
            // Deferred blocks are still run: they are unwinding, which is exactly what should
            // happen on the way out.
            if (Runtime.ExitRequested)
            {
                break;
            }

            if (statement is DeferStatementSyntax deferStatement)
            {
                deferredBlocks?.Add(deferStatement.Body);
                continue;
            }

            // Debug hook / script trace: fire before each statement executes.
            if (DebugHook is not null || Runtime.Config.Shell.ScriptTrace)
            {
                var action = await InvokeDebugHookAsync(sourceName, sourceText, statement, cancellationToken);
                if (action == DebugAction.Abort)
                {
                    throw new DebugAbortException { Span = statement.Span };
                }
            }

            if (statement is ReturnStatementSyntax returnStatement)
            {
                IReadOnlyList<object?> returnValues;

                if (returnStatement.Value is null)
                {
                    returnValues = Array.Empty<object?>();
                }
                else if (returnStatement.Value.Stages.Count == 1 &&
                         returnStatement.Value.Stages[0] is ExpressionPipelineStageSyntax expressionStage)
                {
                    returnValues = [await EvaluateArgumentAsync(sourceName, sourceText, expressionStage.Expression, cancellationToken)];
                }
                else
                {
                    returnValues = await AsyncEnumerableExtensions.ToListAsync(
                        EvaluatePipelineAsync(sourceName, sourceText, returnStatement.Value, cancellationToken, pendingInput, outputIsCaptured: true),
                        cancellationToken);
                }

                UpdateLastResultIfAny(returnValues);
                throw new ReturnSignalException(returnStatement.Span, returnValues);
            }

            if (statement is YieldStatementSyntax yieldStatement)
            {
                if (yieldStatement.Value is not null)
                {
                    await foreach (var yieldValue in EvaluatePipelineAsync(
                        sourceName, sourceText, yieldStatement.Value, cancellationToken, pendingInput)
                        .WithCancellation(cancellationToken))
                    {
                        yield return yieldValue;
                    }
                }

                pendingInput = null;
                continue;
            }

            if (statement is BreakStatementSyntax breakStatement)
            {
                throw new BreakSignalException(breakStatement.Span);
            }

            if (statement is ContinueStatementSyntax continueStatement)
            {
                throw new ContinueSignalException(continueStatement.Span);
            }

            // Fast path: variable assignments never produce output. Run via a Task-returning
            // method to avoid the IAsyncEnumerable state machine + ToListAsync overhead.
            if (statement is VariableAssignmentStatementSyntax varAssign)
            {
                await EvaluateVariableAssignmentCoreAsync(sourceName, sourceText, varAssign, cancellationToken);
                UpdateLastResultIfAny(Array.Empty<object?>());
                pendingInput = null;
                continue;
            }

            var statementResults = statement switch
            {
                PipelineStatementSyntax pipelineStatement => EvaluatePipelineWithRedirectionAsync(
                    sourceName,
                    sourceText,
                    pipelineStatement.Pipeline,
                    cancellationToken,
                    pendingInput,
                    pendingFirstCommandArguments),
                _ => EvaluateStatementAsync(sourceName, sourceText, statement, cancellationToken),
            };

            // A statement that yields is generator output, so stream it instead of draining it
            // to a list. Draining is what made an infinite generator hang: `while (true) { yield
            // 7 }` is a single statement, and materializing every value it will ever produce
            // never finishes. A bare `yield` already streams through its own branch above; this
            // extends the same rule to a yield nested in a loop, `if`, `try`, or `switch`.
            //
            // Neither step below is skipped in spirit. Suppression cannot apply — it fires only
            // for a single-stage pipeline statement, which never contains a yield — and the last
            // result is already maintained by the body's own block execution, so leaving it alone
            // here matches what a bare `yield` does.
            if (StatementStreamsOutput(statement))
            {
                await foreach (var value in statementResults.WithCancellation(cancellationToken))
                {
                    yield return value;
                }

                pendingInput = null;
                continue;
            }

            IReadOnlyList<object?> values = await AsyncEnumerableExtensions.ToListAsync(statementResults, cancellationToken);

            if (ShouldSuppressStatementResults(statement, values))
            {
                values = Array.Empty<object?>();
            }

            UpdateLastResultIfAny(values);

            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }

            pendingInput = null;

            if (statement is PipelineStatementSyntax && pendingFirstCommandArguments is not null)
            {
                pendingFirstCommandArguments = null;
            }
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

    /// <summary>
    /// <summary>
    /// Lightweight descriptor used by the generic-inference helpers
    /// so they don't depend on <see cref="FunctionDefinition"/>
    /// directly. Both free-function calls and class-method calls
    /// build one of these per call to share the same nested-shape
    /// unification, constraint validation, and diagnostic codes.
    /// </summary>
    internal sealed record GenericInferenceTarget(
        string OwnerLabel,
        IReadOnlyList<string> TypeParameters,
        IReadOnlyList<ToshTypeParameterConstraint>? TypeParameterConstraints);

    /// <summary>
    /// Infers / validates type-parameter bindings for one parameter.
    /// Walks the raw annotation tree alongside the runtime value's
    /// shape so nested forms (<c>list&lt;T&gt;</c>, <c>dict&lt;K,V&gt;</c>,
    /// <c>T[]</c>) contribute to inference, not just bare <c>T</c>.
    /// </summary>
    private void ApplyGenericBinding(
        FunctionDefinition definition,
        FunctionParameterDefinition parameter,
        object? value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type>? typeBindings)
    {
        if (typeBindings is null) return;
        if (definition.TypeParameters is not { Count: > 0 } typeParams) return;
        var raw = parameter.RawTypeName;
        if (raw is null) return;
        if (value is null) return;

        var target = new GenericInferenceTarget(
            definition.Name,
            typeParams,
            definition.TypeParameterConstraints);
        UnifyAnnotationWithValue(
            target,
            parameter.Name,
            raw,
            value,
            context,
            argumentIndex,
            typeBindings);
    }

    /// <summary>
    /// Method-level type-parameter inference for class methods.
    /// Mirrors <see cref="ApplyGenericBinding"/> but driven by a
    /// <see cref="ToshClassMethodDefinition"/> instead of a free
    /// function. Returns the populated binding table so callers can
    /// strict-validate parameter values and the return type against
    /// the inferred substitutions.
    /// </summary>
    internal Dictionary<string, Type>? InferMethodTypeBindings(
        ToshClassMethodDefinition method,
        IReadOnlyList<object?> argumentValues,
        CommandContext context,
        string ownerLabel)
    {
        if (method.TypeParameters is not { Count: > 0 } typeParams) return null;

        var typeBindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var target = new GenericInferenceTarget(
            ownerLabel,
            typeParams,
            method.TypeParameterConstraints);

        var count = Math.Min(method.Parameters.Count, argumentValues.Count);
        for (var i = 0; i < count; i++)
        {
            var parameter = method.Parameters[i];
            var raw = parameter.RawTypeName;
            if (raw is null) continue;
            var value = argumentValues[i];
            if (value is null) continue;

            UnifyAnnotationWithValue(
                target,
                parameter.Name,
                raw,
                value,
                context,
                argumentIndex: i,
                typeBindings);
        }

        return typeBindings.Count == 0 ? null : typeBindings;
    }

    /// <summary>
    /// Phase 4.5 — substitute type-parameter references inside a
    /// constraint annotation. The currently-binding parameter
    /// (<paramref name="currentBindingName"/>) is replaced with
    /// <paramref name="currentBindingType"/>; other type parameters
    /// of <paramref name="target"/> are replaced with whatever
    /// <paramref name="typeBindings"/> holds for them.
    /// </summary>
    private static string SubstituteTypeParametersInAnnotation(
        string annotation,
        GenericInferenceTarget target,
        Dictionary<string, Type> typeBindings,
        string currentBindingName,
        Type currentBindingType)
    {
        if (annotation.IndexOf('<') < 0) return annotation;

        var sb = new StringBuilder();
        var i = 0;
        while (i < annotation.Length)
        {
            // Greedy identifier scan.
            if (char.IsLetter(annotation[i]) || annotation[i] == '_')
            {
                var start = i;
                while (i < annotation.Length && (char.IsLetterOrDigit(annotation[i]) || annotation[i] == '_'))
                {
                    i++;
                }
                var ident = annotation.Substring(start, i - start);
                if (string.Equals(ident, currentBindingName, StringComparison.Ordinal))
                {
                    sb.Append(currentBindingType.FullName ?? currentBindingType.Name);
                }
                else if (target.TypeParameters.Contains(ident, StringComparer.Ordinal)
                         && typeBindings.TryGetValue(ident, out var bound))
                {
                    sb.Append(bound.FullName ?? bound.Name);
                }
                else
                {
                    sb.Append(ident);
                }
                continue;
            }
            sb.Append(annotation[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 3.2 — annotation-vs-annotation unification. Used to
    /// seed type-parameter bindings from a target type (LHS of
    /// `var x: T = …`) against a function's declared return type.
    /// Best-effort: silently no-ops on shape mismatches so a wrong
    /// guess at the call site doesn't poison the binding table.
    /// </summary>
    private void UnifyAnnotationWithAnnotation(
        GenericInferenceTarget target,
        string returnAnnotation,
        string targetAnnotation,
        Dictionary<string, Type> typeBindings)
    {
        returnAnnotation = returnAnnotation.Trim();
        targetAnnotation = targetAnnotation.Trim();
        if (returnAnnotation.Length == 0 || targetAnnotation.Length == 0) return;

        // Bare type-parameter reference: resolve target as a CLR type
        // and bind. Unresolvable target types are silently ignored.
        if (target.TypeParameters.Contains(returnAnnotation, StringComparer.Ordinal))
        {
            if (typeBindings.ContainsKey(returnAnnotation)) return;
            var resolved = TryResolveTypeName(targetAnnotation);
            if (resolved is not null)
            {
                typeBindings[returnAnnotation] = resolved;
            }
            return;
        }

        // Decompose `Head<args>` on both sides; heads must match
        // (case-insensitive).
        var rLt = returnAnnotation.IndexOf('<');
        var rGt = returnAnnotation.LastIndexOf('>');
        var tLt = targetAnnotation.IndexOf('<');
        var tGt = targetAnnotation.LastIndexOf('>');
        if (rLt <= 0 || rGt != returnAnnotation.Length - 1) return;
        if (tLt <= 0 || tGt != targetAnnotation.Length - 1) return;

        var rHead = returnAnnotation.Substring(0, rLt).Trim();
        var tHead = targetAnnotation.Substring(0, tLt).Trim();
        if (!string.Equals(rHead, tHead, StringComparison.OrdinalIgnoreCase)) return;

        var rArgs = SplitTopLevelCommas(returnAnnotation.Substring(rLt + 1, rGt - rLt - 1));
        var tArgs = SplitTopLevelCommas(targetAnnotation.Substring(tLt + 1, tGt - tLt - 1));
        if (rArgs.Count != tArgs.Count) return;
        for (var i = 0; i < rArgs.Count; i++)
        {
            UnifyAnnotationWithAnnotation(target, rArgs[i], tArgs[i], typeBindings);
        }
    }

    /// <summary>
    /// Recursive driver: parse one annotation node, match its head
    /// against the value's runtime shape, then recurse into nested
    /// type arguments using the value's element / key / value types.
    /// </summary>
    private void UnifyAnnotationWithValue(
        GenericInferenceTarget target,
        string parameterName,
        string annotation,
        object? value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0 || value is null) return;

        // Bare type-parameter reference: bind / validate directly.
        if (target.TypeParameters.Contains(annotation, StringComparer.Ordinal))
        {
            BindOrValidateTypeParameter(target, parameterName, annotation, value, context, argumentIndex, typeBindings);
            return;
        }

        // Decompose `Head<arg1, arg2, ...>`.
        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return; // no nested args — nothing to infer
        var head = annotation.Substring(0, lt).Trim();
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0) return;

        // Unify each annotation arg with the matching shape from
        // the runtime value. Heads we recognise: list, array, dict,
        // map, tuple. Unknown heads fall back to a single-arg
        // element-type peek if the value is enumerable.
        var headLower = head.ToLowerInvariant();
        switch (headLower)
        {
            case "list":
            case "array":
            case "ienumerable":
            case "icollection":
            case "ireadonlylist":
            case "ireadonlycollection":
                if (args.Count == 1 && TryGetElementType(value, out var elemType, out var sample))
                {
                    UnifyShapeArg(target, parameterName, args[0].Trim(), sample, elemType, context, argumentIndex, typeBindings);
                }
                break;

            case "dict":
            case "dictionary":
            case "map":
            case "idictionary":
            case "ireadonlydictionary":
                if (args.Count == 2 && TryGetDictionaryKVTypes(value, out var keyType, out var valType, out var keySample, out var valSample))
                {
                    UnifyShapeArg(target, parameterName, args[0].Trim(), keySample, keyType, context, argumentIndex, typeBindings);
                    UnifyShapeArg(target, parameterName, args[1].Trim(), valSample, valType, context, argumentIndex, typeBindings);
                }
                break;

            default:
                // Generic CLR type: try to read its bound type-args
                // from the runtime instance's GetType() and unify
                // pointwise.
                var clrType = value.GetType();
                if (clrType.IsGenericType)
                {
                    var clrArgs = clrType.GetGenericArguments();
                    var pairs = Math.Min(clrArgs.Length, args.Count);
                    for (var i = 0; i < pairs; i++)
                    {
                        UnifyShapeArg(target, parameterName, args[i].Trim(), null, clrArgs[i], context, argumentIndex, typeBindings);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Unify an annotation argument with either a concrete CLR type
    /// (when the runtime value's shape was reflected) or a sample
    /// element value (when we only saw it through enumeration).
    /// </summary>
    private void UnifyShapeArg(
        GenericInferenceTarget target,
        string parameterName,
        string annotation,
        object? sample,
        Type? clrType,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        if (target.TypeParameters.Contains(annotation, StringComparer.Ordinal))
        {
            if (clrType is not null)
            {
                BindOrValidateBoundType(target, parameterName, annotation, clrType, context, argumentIndex, typeBindings);
            }
            else if (sample is not null)
            {
                BindOrValidateTypeParameter(target, parameterName, annotation, sample, context, argumentIndex, typeBindings);
            }
            return;
        }

        if (sample is not null)
        {
            UnifyAnnotationWithValue(target, parameterName, annotation, sample, context, argumentIndex, typeBindings);
        }
    }

    /// <summary>
    /// Tries to peek at an enumerable's element type and the first
    /// sample value (used for further nested unification).
    /// </summary>
    private static bool TryGetElementType(object? value, out Type elementType, out object? sample)
    {
        elementType = typeof(object);
        sample = null;
        if (value is null) return false;

        var clrType = value.GetType();
        if (clrType.IsArray)
        {
            elementType = clrType.GetElementType() ?? typeof(object);
            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (var first in enumerable) { sample = first; break; }
            }
            return true;
        }

        // IEnumerable<T> on the runtime type.
        foreach (var iface in clrType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                if (value is System.Collections.IEnumerable enumerable)
                {
                    foreach (var first in enumerable) { sample = first; break; }
                }
                return true;
            }
        }

        // Loose enumerable (e.g. ArrayList, IList) — peek at the
        // first element to derive a runtime type.
        if (value is System.Collections.IEnumerable loose)
        {
            foreach (var first in loose) { sample = first; break; }
            if (sample is not null)
            {
                elementType = sample.GetType();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to peek at a dictionary's key/value types (and one
    /// sample of each, when available).
    /// </summary>
    private static bool TryGetDictionaryKVTypes(
        object? value,
        out Type keyType,
        out Type valueType,
        out object? keySample,
        out object? valueSample)
    {
        keyType = typeof(object);
        valueType = typeof(object);
        keySample = null;
        valueSample = null;
        if (value is null) return false;

        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                var args = iface.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                if (value is System.Collections.IDictionary loose)
                {
                    foreach (System.Collections.DictionaryEntry e in loose)
                    {
                        keySample = e.Key;
                        valueSample = e.Value;
                        break;
                    }
                }
                return true;
            }
        }

        if (value is System.Collections.IDictionary plain)
        {
            foreach (System.Collections.DictionaryEntry e in plain)
            {
                keySample = e.Key;
                valueSample = e.Value;
                if (keySample is not null) keyType = keySample.GetType();
                if (valueSample is not null) valueType = valueSample.GetType();
                return true;
            }
            return true; // empty dict — leave types as object
        }

        return false;
    }

    /// <summary>
    /// First-bind / strict-validate path keyed on a runtime value
    /// (uses <c>value.GetType()</c> as the inferred CLR type).
    /// </summary>
    private void BindOrValidateTypeParameter(
        GenericInferenceTarget target,
        string parameterName,
        string typeParameterName,
        object value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        BindOrValidateBoundType(
            target, parameterName, typeParameterName,
            value.GetType(), context, argumentIndex, typeBindings,
            mismatchValue: value);
    }

    /// <summary>
    /// First-bind / strict-validate path keyed on a CLR type
    /// (used when the value's element type was reflected, not
    /// observed directly).
    /// </summary>
    private void BindOrValidateBoundType(
        GenericInferenceTarget target,
        string parameterName,
        string typeParameterName,
        Type clrType,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings,
        object? mismatchValue = null)
    {
        if (typeBindings.TryGetValue(typeParameterName, out var bound))
        {
            var ok = mismatchValue is not null
                ? bound.IsInstanceOfType(mismatchValue)
                : bound.IsAssignableFrom(clrType);
            if (!ok)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.generic_argument_type_mismatch",
                    title: $"'{target.OwnerLabel}' inferred type parameter '{typeParameterName}' as '{bound.Name}', but argument '{parameterName}' is '{clrType.Name}'.",
                    argumentIndex: argumentIndex,
                    label: $"'{parameterName}' must be a {bound.Name} ({typeParameterName} was bound earlier in this call)");
            }
            return;
        }

        // First binding — verify any `where` constraints declared
        // for this type parameter.
        if (target.TypeParameterConstraints is { Count: > 0 } constraints)
        {
            foreach (var clause in constraints)
            {
                if (!string.Equals(clause.TypeParameter, typeParameterName, StringComparison.Ordinal)) continue;
                foreach (var constraintName in clause.ConstraintNames)
                {
                    if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                    {
                        if (predicate(clrType)) continue;
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.generic_constraint_failed",
                            title: $"'{target.OwnerLabel}' requires '{typeParameterName}' to satisfy '{constraintName}', but '{clrType.Name}' does not.",
                            argumentIndex: argumentIndex,
                            label: $"'{parameterName}' (CLR {clrType.Name}) does not satisfy '{constraintName}'");
                    }

                    // Phase 4.5 — substitute type-parameter references
                    // in the constraint name (e.g. `IComparable<T>`
                    // becomes `IComparable<Int32>` once T binds to int).
                    var resolvedConstraintName = SubstituteTypeParametersInAnnotation(
                        constraintName, target, typeBindings, typeParameterName, clrType);
                    var constraintType = TryResolveTypeName(resolvedConstraintName);
                    if (constraintType is not null)
                    {
                        if (!constraintType.IsAssignableFrom(clrType))
                        {
                            throw context.CreateDiagnostic(
                                code: "tosh.runtime.generic_constraint_failed",
                                title: $"'{target.OwnerLabel}' requires '{typeParameterName}' to satisfy '{constraintName}', but '{clrType.Name}' does not.",
                                argumentIndex: argumentIndex,
                                label: $"'{parameterName}' (CLR {clrType.Name}) is not assignable to '{constraintName}'");
                        }
                        continue;
                    }
                    // Unknown name — accept conservatively.
                }
            }
        }

        typeBindings[typeParameterName] = clrType;
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

    private static void EnsureReservedBindingName(string name)
    {
        if (RuntimeNamespaceUtilities.IsReservedRuntimeNamespaceName(name))
        {
            throw new InvalidOperationException($"'{name}' is a reserved runtime namespace.");
        }
    }

    private object? ConvertFunctionReturnValue(
        FunctionDefinition definition,
        CommandContext context,
        object? value)
    {
        return ConvertFunctionReturnValue(definition, context, value, typeBindings: null);
    }

    private object? ConvertFunctionReturnValue(
        FunctionDefinition definition,
        CommandContext context,
        object? value,
        Dictionary<string, Type>? typeBindings)
    {
        // Generic return type bound at call site: validate against the
        // inferred CLR type rather than the (erased) annotation.
        if (typeBindings is { Count: > 0 } &&
            definition.RawReturnTypeName is { } rawReturn &&
            definition.TypeParameters is { Count: > 0 } typeParams &&
            typeParams.Contains(rawReturn, StringComparer.Ordinal))
        {
            if (typeBindings.TryGetValue(rawReturn, out var bound) && value is not null && !bound.IsInstanceOfType(value))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.generic_return_type_mismatch",
                    title: $"Function '{definition.Name}' inferred '{rawReturn}' as '{bound.Name}', but returned a '{value.GetType().Name}'.",
                    label: $"return value must be a {bound.Name} (T was bound from the arguments)",
                    span: definition.Span);
            }
            return value;
        }

        if (definition.ReturnTypeName is null)
        {
            return value;
        }

        try
        {
            return ConvertAnnotatedValue(
                definition.ReturnTypeName,
                refinement: null,
                value,
                definition.Span,
                definition.SourceName,
                definition.SourceText,
                $"{definition.Name} return");
        }
        catch (ToshDiagnosticException exception)
        {
            if (!exception.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "tosh.runtime.annotation_conversion_failed", StringComparison.Ordinal)))
            {
                throw;
            }
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.return_type_conversion_failed",
            title: $"Function '{definition.Name}' returned a value that could not be converted to '{definition.ReturnTypeName}'.",
            label: $"the returned value does not match '{definition.ReturnTypeName}'",
            span: definition.Span);
    }

    private async Task<bool> EvaluateConditionAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax condition,
        CancellationToken cancellationToken)
    {
        object? conditionValue;

        try
        {
            conditionValue = await EvaluateArgumentAsync(sourceName, sourceText, condition, cancellationToken);
        }
        catch (ToshDiagnosticException)
        {
            throw;
        }
        catch (Tosh.Runtime.ShellControlFlowException)
        {
            throw;
        }
        catch (Exception exception) when (IsToshThrown(exception))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateExpressionDiagnostic(sourceName, sourceText, condition, exception);
        }

        return ToshTruthiness.IsTruthy(conditionValue);
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

    /// <summary>
    /// Picks the argument span the diagnostic should underline. Returns
    /// <c>null</c> when no argument-level fix is available (caller falls back
    /// to the full command span).
    /// </summary>
    private static TextSpan? NarrowToArgumentSpan(
        string sourceText,
        CommandSyntax commandSyntax,
        Exception exception)
    {
        if (exception is CommandArgumentException argException &&
            argException.ArgumentIndex >= 0 &&
            argException.ArgumentIndex < commandSyntax.Arguments.Count)
        {
            return commandSyntax.Arguments[argException.ArgumentIndex].Span;
        }

        if (string.IsNullOrEmpty(exception.Message) || commandSyntax.Arguments.Count == 0)
        {
            return null;
        }

        // Pull out the FIRST single-quoted token from the message — by
        // convention, command throws name the offending argument that way:
        //   "Unsupported ls option '--foo'.", "Unknown user 'alice'."
        var token = ExtractQuotedToken(exception.Message);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        foreach (var argument in commandSyntax.Arguments)
        {
            var argSpan = argument.Span;
            if (argSpan.Start < 0 || argSpan.End > sourceText.Length || argSpan.End <= argSpan.Start)
            {
                continue;
            }
            var argText = sourceText[argSpan.Start..argSpan.End];
            if (string.Equals(argText, token, StringComparison.Ordinal))
            {
                return argSpan;
            }
        }

        return null;
    }

    private static string? ExtractQuotedToken(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0 || start == message.Length - 1) return null;
        var end = message.IndexOf('\'', start + 1);
        if (end <= start + 1) return null;
        return message[(start + 1)..end];
    }

    private void ImportRequiredArtifact(
        string sourceName,
        string sourceText,
        ToshRequiredScriptArtifact artifact,
        RequireStatementSyntax statement)
    {
        if (statement.Imports.Count == 0)
        {
            // `TS-P2-62`. `require` imports a file's exports, and a file with none imports
            // nothing — which used to happen in silence, so a missing `export` looked exactly
            // like a missing file until something later failed to resolve. `source` is the
            // spelling for running a file's every declaration in the current scope.
            if (artifact.Exports.Variables.Count == 0 &&
                artifact.Exports.Commands.Count == 0 &&
                artifact.Exports.Types.Count == 0 &&
                artifact.Exports.RefinementTypes.Count == 0 &&
                artifact.Exports.Modules.Count == 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.require_exports_nothing",
                    Title: $"'{statement.Target}' declares no exports, so this require imports nothing.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: statement.Span,
                    Label: "nothing in this file is marked 'export'",
                    Help: "mark a declaration with 'export' to make it importable, or use "
                        + $"'source \"{statement.Target}\"' to run the file in the current scope."));
            }

            foreach (var (name, value) in artifact.Exports.Variables)
            {
                DeclareVariable(name, ToVariableBinding(value), statement.Modifier);
            }

            foreach (var (name, command) in artifact.Exports.Commands)
            {
                DeclareCommand(command, statement.Modifier);
            }

            foreach (var (name, type) in artifact.Exports.Types)
            {
                DeclareType(name, type, statement.Modifier, sourceName, sourceText, statement.Span);
            }

            foreach (var (_, refinementType) in artifact.Exports.RefinementTypes)
            {
                DeclareRefinementType(refinementType, statement.Modifier, sourceName, sourceText, statement.Span);
            }

            foreach (var (name, module) in artifact.Exports.Modules)
            {
                if (module is not null)
                {
                    DeclareModule(name, module, statement.Modifier);
                }
            }

            return;
        }

        foreach (var import in statement.Imports)
        {
            var bindingName = import.Alias ?? import.Name;

            // Both overloads route through the same resolver. The first fix landed
            // only on the other one, which is dead for this path — `require X.Y from
            // …` comes through here, so the dotted form kept failing while the code
            // to support it sat twelve thousand lines away, compiled and unreachable.
            if (import.Name.Contains('.', StringComparison.Ordinal) &&
                TryResolveNestedExport(artifact, import.Name, bindingName, statement.Modifier))
            {
                continue;
            }

            if (artifact.Exports.Modules.TryGetValue(import.Name, out var module))
            {
                if (module is null)
                {
                    throw new InvalidOperationException($"Export '{import.Name}' in '{artifact.Path}' was null.");
                }

                DeclareModule(bindingName, module, statement.Modifier);
                continue;
            }

            if (artifact.Exports.Types.TryGetValue(import.Name, out var type))
            {
                DeclareType(bindingName, type, statement.Modifier, sourceName, sourceText, import.Span);
                continue;
            }

            if (artifact.Exports.RefinementTypes.TryGetValue(import.Name, out var refinementType))
            {
                DeclareRefinementType(refinementType with { Name = bindingName }, statement.Modifier, sourceName, sourceText, import.Span);
                continue;
            }

            if (artifact.Exports.Commands.TryGetValue(import.Name, out var command))
            {
                DeclareCommand(
                    string.Equals(bindingName, command.Name, StringComparison.Ordinal)
                        ? command
                        : new RenamedCommand(bindingName, command),
                    statement.Modifier);
                continue;
            }

            if (artifact.Exports.Variables.TryGetValue(import.Name, out var value))
            {
                DeclareVariable(bindingName, ToVariableBinding(value), statement.Modifier);
                continue;
            }

            throw new InvalidOperationException($"Export '{import.Name}' was not found in '{artifact.Path}'.");
        }
    }

    private async Task<ToshRequiredScriptArtifact> ExecuteRequiredScriptAsync(
        string source,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var parseResult = Parse(source, sourceName);

        if (parseResult.Diagnostics.Count > 0)
        {
            throw new ToshDiagnosticException(parseResult.Diagnostics
                .Select(diagnostic => new ToshDiagnostic(
                    Code: diagnostic.Code,
                    Title: diagnostic.Title,
                    SourceName: parseResult.SourceName,
                    SourceText: parseResult.SourceText,
                    Span: diagnostic.Span,
                    Label: diagnostic.Label,
                    Help: diagnostic.Help))
                .ToArray());
        }

        var moduleScope = new LexicalScope(new Dictionary<string, object?>(StringComparer.Ordinal), isModuleScope: true);
        _scriptNameStack.Push(parseResult.SourceName);
        using var _ = PushScope(moduleScope);

        try
        {
            await foreach (var __ in EvaluateStatementAsync(
                               parseResult.SourceName,
                               parseResult.SourceText,
                               parseResult.Statement,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
            }
        }
        catch (ReturnSignalException signal)
        {
            UpdateLastResultIfAny(signal.Values);
        }
        catch (BreakSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "break",
                code: "tosh.runtime.break_outside_loop",
                title: "'break' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        catch (ContinueSignalException signal)
        {
            throw CreateLoopControlDiagnostic(
                parseResult.SourceName,
                parseResult.SourceText,
                signal.Span,
                keyword: "continue",
                code: "tosh.runtime.continue_outside_loop",
                title: "'continue' can only be used inside 'for', 'while', or 'each' blocks.");
        }
        finally
        {
            _scriptNameStack.Pop();
        }

        return new ToshRequiredScriptArtifact(sourceName, moduleScope.Exports ?? new ModuleExportTable());
    }

    private static bool IsNumericEnumUnderlyingType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong);
    }

    private static RequireTarget ResolveRequirement(string target, string currentDirectory)
    {
        var candidate = PathUtilities.ResolvePath(currentDirectory, target);

        if (!Path.HasExtension(candidate))
        {
            var toshCandidate = candidate + ".tosh";

            if (File.Exists(toshCandidate))
            {
                return new RequireTarget(RequireTargetKind.Script, toshCandidate, toshCandidate);
            }
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"Required target '{candidate}' was not found.", candidate);
        }

        return Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".tosh" => new RequireTarget(RequireTargetKind.Script, candidate, candidate),
            ".dll" => new RequireTarget(RequireTargetKind.Assembly, candidate, candidate),
            ".csproj" => new RequireTarget(RequireTargetKind.Project, candidate, candidate),
            _ => throw new InvalidOperationException($"Unsupported require target '{candidate}'. ToSh currently supports .tosh, .dll, and .csproj targets."),
        };
    }

    private static RequireTarget ResolveNativeRequirement(string target, string currentDirectory)
    {
        if (target.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            target.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            target.StartsWith(".", StringComparison.Ordinal) ||
            target.StartsWith("~", StringComparison.Ordinal))
        {
            var candidate = PathUtilities.ResolvePath(currentDirectory, target);

            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException($"Native library '{candidate}' was not found.", candidate);
            }

            return new RequireTarget(RequireTargetKind.Assembly, candidate, "native:" + candidate);
        }

        return new RequireTarget(RequireTargetKind.Assembly, target, "native:" + target);
    }

    private static string GetDefaultNativeModuleName(string target)
    {
        var fileName = Path.GetFileNameWithoutExtension(target);
        var candidate = string.IsNullOrWhiteSpace(fileName) ? target : fileName;
        var sanitized = new StringBuilder(candidate.Length);

        foreach (var ch in candidate)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sanitized.Append(ch);
            }
        }

        return sanitized.Length == 0 ? "Native" : sanitized.ToString();
    }

    /// <summary>
    /// Runs a native binding declared inside a class body. Mirrors
    /// <c>ToshModuleObject.InvokeInstanceMethod</c>, which does the same for a
    /// module's bound commands — the difference is only where the command was
    /// registered, not how it runs.
    /// </summary>
    internal async ValueTask<IReadOnlyList<object?>> InvokeNativeMemberAsync(
        IShellCommand command,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var context = new CommandContext(
            Runtime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            ScopedCommands: CreateScopedCommandView(), ShellTypes: this);

        return await AsyncEnumerableExtensions.ToListAsync(command.ExecuteAsync(context), cancellationToken);
    }

    /// <summary>
    /// Builds the <c>where (…)</c> success contract as a closure over the
    /// declaring scopes, so the predicate can reference anything visible where
    /// the binding was written.
    ///
    /// Reuses the refinement evaluator rather than introducing a second one:
    /// `_` is bound to the native return value exactly as it is bound to a value
    /// under test in <c>type Port = int where (…)</c>.
    /// </summary>
    private Func<object?, CancellationToken, ValueTask<bool>>? CreateNativeSuccessPredicate(
        string sourceName,
        string sourceText,
        NativeFunctionBindingSyntax function)
    {
        if (function.SuccessPredicate is null)
        {
            return null;
        }

        var annotation = CreateRefinementAnnotation(sourceName, sourceText, function.SuccessPredicate);

        // The clause parser wraps its predicates, so take them from the
        // annotation rather than evaluating the wrapper itself. `coerce` has no
        // meaning for a pass/fail contract, so only `where` clauses apply.
        var predicates = annotation?.Clauses
            .OfType<RefinementWhereClause>()
            .Select(static clause => clause.Predicate)
            .ToArray();

        if (annotation is null || predicates is null || predicates.Length == 0)
        {
            return null;
        }

        var span = function.Span;
        var title = $"The success predicate for '{function.Name}'";

        return async (value, cancellationToken) =>
        {
            // Multiple `where` clauses conjoin, matching block refinements.
            foreach (var predicate in predicates)
            {
                if (!await EvaluateRefinementBooleanExpressionAsync(
                        annotation, predicate, value, span, title, cancellationToken))
                {
                    return false;
                }
            }

            return true;
        };
    }

    /// <summary>
    /// Recognises an out-array parameter, <c>T[n]</c>.
    /// </summary>
    /// <param name="elementTypeName">
    /// The element type, or <c>null</c> for <c>buffer[n]</c> — the collapsed
    /// form of C's output-string idiom, which decodes to a string and carries an
    /// implicit length argument. A bare <c>buffer</c> is not recognised: the
    /// capacity is the whole point and there is nothing to infer it from.
    /// </param>
    private static bool TryParseOutArrayParameter(string? typeName, out string? elementTypeName, out int length)
    {
        elementTypeName = null;
        length = 0;

        if (string.IsNullOrWhiteSpace(typeName)) return false;

        var trimmed = typeName.Trim();
        var bracket = trimmed.IndexOf('[');

        if (bracket <= 0 || !trimmed.EndsWith(']')) return false;
        if (!int.TryParse(trimmed[(bracket + 1)..^1], out length) || length <= 0) return false;

        var element = trimmed[..bracket];

        if (!element.Equals("buffer", StringComparison.OrdinalIgnoreCase))
        {
            elementTypeName = element;
        }

        return true;
    }

    private Type ResolveNativeInteropParameterType(
        string? typeName,
        NativeParameterPassingMode passingMode,
        string sourceName,
        string sourceText,
        TextSpan span,
        string owner)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.native_binding_requires_type",
                Title: $"Native {owner} requires an explicit CLR type.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "write a CLR type like 'int', 'double', or 'string'"));
        }

        var normalized = typeName.Trim();

        var isByRef = passingMode != NativeParameterPassingMode.In;

        if (NativeTypeLexicon.IsCStringName(normalized))
        {
            if (NativeTypeLexicon.ValidateByRef(normalized, isByRef, sourceName, sourceText, span) is { } cstringRejection)
            {
                throw ToshDiagnosticException.Create(cstringRejection);
            }

            return typeof(string);
        }

        var resolved = ResolveTypeName(normalized);

        if (resolved is null || !IsSupportedNativeInteropType(resolved))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unsupported_native_interop_type",
                Title: $"Native interop does not currently support '{typeName}'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"'{typeName}' is not supported here",
                Help: "start with primitive CLR types like int, long, float, double, bool, string, IntPtr, UIntPtr, or a struct with sequential/explicit layout."));
        }

        if (isByRef && resolved == typeof(string) &&
            NativeTypeLexicon.ValidateByRef("string", isByRef, sourceName, sourceText, span) is { } stringRejection)
        {
            throw ToshDiagnosticException.Create(stringRejection);
        }

        return resolved;
    }

    private NativeFunctionReturnDefinition ResolveNativeInteropReturnType(
        string? typeName,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return new NativeFunctionReturnDefinition("void", typeof(void), NativeFunctionReturnKind.Default);
        }

        var normalized = typeName.Trim();

        // Named return conventions. These are `where` with its two commonest
        // predicates given names — the convention is still stated explicitly at
        // the declaration site, so this is not sentinel inference.
        if (string.Equals(normalized, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new NativeFunctionReturnDefinition(
                normalized, typeof(int), NativeFunctionReturnKind.Default, NativeErrorConvention.Ok);
        }

        if (string.Equals(normalized, "count", StringComparison.OrdinalIgnoreCase))
        {
            // ssize_t is pointer-sized, which covers read/write/readlink as well
            // as sysconf's long on LP64.
            return new NativeFunctionReturnDefinition(
                normalized, typeof(IntPtr), NativeFunctionReturnKind.Default, NativeErrorConvention.Count);
        }

        // `auto` projects the out parameters of a call that cannot fail. The
        // native return value, if any, is discarded — which is safe on every
        // supported calling convention.
        if (string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new NativeFunctionReturnDefinition(normalized, typeof(void), NativeFunctionReturnKind.Default);
        }

        if (string.Equals(normalized, "cstring", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "cstr", StringComparison.OrdinalIgnoreCase))
        {
            return new NativeFunctionReturnDefinition(normalized, typeof(IntPtr), NativeFunctionReturnKind.CString);
        }

        if (string.Equals(normalized, "string", StringComparison.OrdinalIgnoreCase))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unsupported_native_string_return",
                Title: "Native string returns need an explicit interop string type.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "use 'cstring' for a borrowed NUL-terminated C string, or 'nint' for a raw pointer",
                Help: "plain 'string' is supported for native parameters, but return values need explicit ownership semantics."));
        }

        var resolved = ResolveNativeInteropParameterType(typeName, NativeParameterPassingMode.In, sourceName, sourceText, span, "return type");
        return new NativeFunctionReturnDefinition(normalized, resolved, NativeFunctionReturnKind.Default);
    }

    private static CallingConvention ResolveNativeCallingConvention(
        string? name,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        if (NativeTypeLexicon.TryResolveCallingConvention(name, out var convention))
        {
            return convention;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unsupported_native_calling_convention",
            Title: $"Native interop does not support calling convention '{name}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: "use cdecl, stdcall, thiscall, fastcall, or winapi"));
    }

    private static bool IsSupportedNativeInteropType(Type type)
    {
        return NativeInteropUtilities.IsSupportedInteropType(type);
    }

    private string GetExecutionDirectory(string sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName) &&
            !sourceName.StartsWith('<') &&
            !sourceName.StartsWith("repl_entry", StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSource = PathUtilities.ResolvePath(Runtime.CurrentDirectory, sourceName);
            var directory = Path.GetDirectoryName(resolvedSource);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return Runtime.CurrentDirectory;
    }

    private static async Task<string> BuildProjectAndResolveAssemblyPathAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException($"Project '{projectPath}' was not found.", projectPath);
        }

        var targetPath = await RunDotNetForOutputAsync(
            $"msbuild {QuoteArgument(projectPath)} -nologo -getProperty:TargetPath",
            Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
            cancellationToken);

        if (!File.Exists(targetPath))
        {
            await RunDotNetAsync(
                $"build {QuoteArgument(projectPath)} -nologo -clp:ErrorsOnly",
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
                cancellationToken);

            targetPath = await RunDotNetForOutputAsync(
                $"msbuild {QuoteArgument(projectPath)} -nologo -getProperty:TargetPath",
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
            var expanded = PathUtilities.ResolvePath(Runtime.CurrentDirectory, name);

            if (Directory.Exists(expanded))
            {
                resolvedPath = expanded;
                return true;
            }
        }

        var candidate = Path.Combine(Runtime.CurrentDirectory, name);

        if (Directory.Exists(candidate))
        {
            resolvedPath = Path.GetFullPath(candidate);
            return true;
        }

        resolvedPath = string.Empty;
        return false;
    }

    private sealed class AutoCdCommand : IShellCommand
    {
        private readonly string _resolvedPath;

        public AutoCdCommand(string resolvedPath)
        {
            _resolvedPath = resolvedPath;
        }

        public string Name => "cd";
        public string Description => "Auto-cd into a directory.";
        public string Usage => "cd [path]";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            var directoryInfo = new DirectoryInfo(_resolvedPath);

            if (!directoryInfo.Exists)
            {
                throw new InvalidOperationException($"Directory '{_resolvedPath}' does not exist.");
            }

            var oldDirectory = FileSystemEntry.From(new DirectoryInfo(context.Runtime.CurrentDirectory));
            context.Runtime.CurrentDirectory = directoryInfo.FullName;
            context.Runtime.PushDirectory(directoryInfo.FullName);

            var newDirectory = FileSystemEntry.From(directoryInfo);
            var sender = context.Runtime.EventSenderFactory?.Invoke()
                ?? new ShellEventSender(Function: null, Script: null, Line: null);
            var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
            await context.Runtime.Events.RaiseAsync(evt, context.CancellationToken);

            yield return newDirectory;
        }
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

            // Merge any captures recorded by the IL emitter under the
            // host-supplied locals (host locals win on collision so a
            // command can shadow an outer-scope name with $_ etc.).
            IReadOnlyDictionary<string, object?> effectiveLocals = locals;
            if (block.Captures is { Count: > 0 } captures)
            {
                var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in captures) merged[k] = v;
                foreach (var (k, v) in locals) merged[k] = v;
                effectiveLocals = merged;
            }

            await foreach (var value in _engine.ExecuteBlockAsync(block.SourceName, block.SourceText, syntax, cancellationToken, effectiveLocals)
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

    private sealed record RequireTarget(RequireTargetKind Kind, string ResolvedPath, string CacheKey);

    private static bool TryDecomposeMemberAssignmentTarget(
        ArgumentSyntax target,
        out ArgumentSyntax rootExpression,
        out string memberPath)
    {
        var segments = new Stack<string>();
        var current = target;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (segments.Count == 0)
        {
            rootExpression = target;
            memberPath = string.Empty;
            return false;
        }

        rootExpression = current;
        memberPath = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// Joins a <c>$</c>-less assignment target back into one dotted path.
    /// </summary>
    /// <remarks>
    /// The parser leaves the head as a <see cref="StaticMemberAccessArgumentSyntax"/> holding
    /// whatever it lexed as a single token, and any further <c>.Member</c> tokens arrive as
    /// wrappers around it. Rejoining them means <c>ResolveStaticAssignmentTarget</c> sees one
    /// path however the source happened to be tokenized.
    /// </remarks>
    private static bool TryDecomposeStaticAssignmentTarget(ArgumentSyntax target, out string path)
    {
        var segments = new Stack<string>();
        var current = target;

        while (current is MemberAccessArgumentSyntax memberAccess)
        {
            segments.Push(memberAccess.MemberPath);
            current = memberAccess.Target;
        }

        if (current is not StaticMemberAccessArgumentSyntax staticAccess)
        {
            path = string.Empty;
            return false;
        }

        segments.Push(staticAccess.Path);
        path = string.Join(".", segments);
        return true;
    }

    /// <summary>
    /// Splits a static assignment path into the type it names and the member path to assign
    /// from there, or explains why it names no type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Longest prefix first, matching <c>TryResolveQualifiedAccess</c>: <c>System.Console.Title</c>
    /// must find the type <c>System.Console</c> rather than stopping at the namespace, while
    /// <c>B.S.Length</c> must find the class <c>B</c> and leave <c>S.Length</c> as the path to
    /// walk. One rule serves both, and it is the rule reads already use.
    /// </para>
    /// <para>
    /// A variable of that name is asked about first, and wins. This is the forgotten-<c>$</c>
    /// case the parser used to reject before it could tell the two apart, and the hint is
    /// rebuilt here, where the variable table exists, so <c>person.Name = "x"</c> still names
    /// the real problem.
    /// </para>
    /// </remarks>
    private (object Target, string MemberPath) ResolveStaticAssignmentTarget(
        string sourceName,
        string sourceText,
        string path,
        TextSpan span)
    {
        // A variable of that name wins, and is asked first. Type resolution matches simple
        // names across every loaded assembly and ignores case, so `var person = …` followed by
        // `person.Name = "x"` found an unrelated CLR `Person` and wrote a static instead of
        // naming the forgotten `$` — measured, not imagined: it is what the full suite hit once
        // a compiler test had emitted a type by that name. A wrong hint costs a message; a
        // wrong static write mutates shared state, so the safe reading goes first.
        if (TryBuildVariableReferenceHint(path, out var suggestedReference, out var variableName))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.variable_reference_requires_dollar",
                Title: $"Variable '{variableName}' exists, but variable references must start with '$'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"did you mean '{suggestedReference} = ...'?",
                Help: "declare variables with 'var name', then assign through them as '$name.Member = value'."));
        }

        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var prefix = string.Join('.', segments.Take(prefixLength));
            var remainder = string.Join('.', segments.Skip(prefixLength));

            if (TryGetNamedType(prefix, out var namedType))
            {
                return (namedType, remainder);
            }

            if (ResolveTypeName(prefix) is { } clrType)
            {
                return (clrType, remainder);
            }
        }

        var head = segments.Length > 0 ? segments[0] : path;

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.unknown_static_assignment_target",
            Title: $"'{path}' does not name a static member, because '{head}' names no type.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"'{head}' is not a class, enum, module, or CLR type in scope",
            Help: "assign a static member as 'TypeName.Member = value', or a variable's member as '$name.Member = value'."));
    }

    private static bool ShouldAutoMaterializeListTarget(string methodName)
    {
        return methodName switch
        {
            "Add" => true,
            "AddRange" => true,
            "Insert" => true,
            "InsertRange" => true,
            _ => false,
        };
    }

    private sealed record VariableBinding(
        object? Value,
        bool ReplayAsPipeline,
        bool IsAllocatedOnly,
        bool IsConst = false,
        string? DeclaredTypeName = null,
        RefinementAnnotation? DeclaredRefinement = null);

    private sealed record AnnotationRefinementFailure(object? Value, RefinementAnnotation? Refinement);
    private sealed record AnnotationRefinementError(ToshDiagnosticException Exception);

    private static object CreateTypedArray(List<object?> items)
    {
        if (items.Count == 0)
        {
            return Array.Empty<object?>();
        }

        Type? commonType = null;

        foreach (var item in items)
        {
            if (item is null)
            {
                return items.ToArray();
            }

            var itemType = item.GetType();

            if (commonType is null)
            {
                commonType = itemType;
            }
            else if (commonType != itemType)
            {
                commonType = FindCommonNumericType(commonType, itemType);

                if (commonType is null)
                {
                    return items.ToArray();
                }
            }
        }

        if (commonType is null || commonType == typeof(object))
        {
            return items.ToArray();
        }

        var typedArray = Array.CreateInstance(commonType, items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            typedArray.SetValue(Convert.ChangeType(items[i], commonType), i);
        }

        return typedArray;
    }

    private static object CreateTypedDictionary(Dictionary<object, object?> source)
    {
        return source;
    }

    private static Type? FindCommonNumericType(Type a, Type b)
    {
        // Widen compatible numeric types to a common type
        if (a == b)
        {
            return a;
        }

        // int -> long -> double widening
        if (IsIntegerType(a) && IsIntegerType(b))
        {
            return typeof(long);
        }

        if (IsNumericType(a) && IsNumericType(b))
        {
            return typeof(double);
        }

        return null;
    }

    private static bool IsIntegerType(Type t)
    {
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);
    }

    private static bool IsNumericType(Type t)
    {
        return IsIntegerType(t) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);
    }

    private async Task<DebugAction> InvokeDebugHookAsync(
        string sourceName,
        string sourceText,
        StatementSyntax statement,
        CancellationToken cancellationToken)
    {
        var span = statement.Span;
        string? statementText = null;

        if (span.Start >= 0 && span.Start + span.Length <= sourceText.Length)
        {
            statementText = sourceText.Substring(span.Start, span.Length).Trim();
        }

        // Compute 1-based line number.
        int? line = null;
        if (span.Start >= 0 && span.Start <= sourceText.Length)
        {
            var lineNumber = 1;
            for (var i = 0; i < span.Start && i < sourceText.Length; i++)
            {
                if (sourceText[i] == '\n')
                {
                    lineNumber++;
                }
            }
            line = lineNumber;
        }

        // Script trace: emit "+ <line>: <statement>" to stderr (like set -x).
        if (Runtime.Config.Shell.ScriptTrace && statementText is not null)
        {
            var prefix = line.HasValue ? $"+ {sourceName}:{line}" : $"+ {sourceName}";
            await Runtime.Error.WriteLineAsync($"{prefix}: {statementText}");
        }

        // Debug hook: invoke the delegate if present.
        if (DebugHook is not null)
        {
            var context = new DebugStepContext
            {
                SourceName = sourceName,
                SourceText = sourceText,
                Statement = statement,
                Span = span,
                Line = line,
                StatementText = statementText,
            };

            return await DebugHook(context);
        }

        return DebugAction.Continue;
    }

    private async Task EvaluateComprehensionClauseAsync(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax clause,
        Func<CancellationToken, Task> bodyAction,
        CancellationToken cancellationToken)
    {
        var sourceValue = await EvaluateArgumentAsync(sourceName, sourceText, clause.Source, cancellationToken);

        // Eager comprehensions (list/set/dict) cannot iterate infinite sources
        if (IsInfiniteSource(sourceValue))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.infinite_eager_comprehension",
                Title: "Cannot use an infinite source in a list, set, or dict comprehension. Use a generator comprehension (...) instead of [...] and pipe to '| first N'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: clause.Source.Span,
                Label: "this source is infinite"));
        }

        // Parallel/zip: bind both clause variables per step; terminate when either source ends
        if (clause.InnerClause is not null && clause.InnerIsParallel)
        {
            var innerSourceValue = await EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, cancellationToken);
            using var outerEnum = ShellIterationUtilities.ExpandIterationItems(sourceValue).GetEnumerator();
            using var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSourceValue).GetEnumerator();

            while (outerEnum.MoveNext() && innerEnum.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = outerEnum.Current };
                AddClauseBindings(vars, clause, outerEnum.Current);
                AddClauseBindings(vars, clause.InnerClause, innerEnum.Current);
                using var _ = PushScope(vars);

                var skip = false;
                foreach (var modifier in clause.Modifiers)
                {
                    switch (modifier)
                    {
                        case ComprehensionWhereSyntax where:
                            if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                                skip = true;
                            break;
                        case ComprehensionLetSyntax let:
                            var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                            _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                            break;
                    }
                    if (skip) break;
                }
                if (!skip)
                {
                    foreach (var modifier in clause.InnerClause.Modifiers)
                    {
                        switch (modifier)
                        {
                            case ComprehensionWhereSyntax where:
                                if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                                    skip = true;
                                break;
                            case ComprehensionLetSyntax let:
                                var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                                _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                                break;
                        }
                        if (skip) break;
                    }
                }
                if (skip) continue;

                if (clause.InnerClause.InnerClause is not null)
                    await EvaluateComprehensionClauseAsync(sourceName, sourceText, clause.InnerClause.InnerClause, bodyAction, cancellationToken);
                else
                    await bodyAction(cancellationToken);
            }
            return;
        }

        foreach (var current in ShellIterationUtilities.ExpandIterationItems(sourceValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = current };
            AddClauseBindings(vars, clause, current);
            using var _ = PushScope(vars);

            // Walk where/let modifiers in declared order: later clauses observe earlier lets.
            var skip = false;
            foreach (var modifier in clause.Modifiers)
            {
                switch (modifier)
                {
                    case ComprehensionWhereSyntax where:
                        if (!await EvaluateConditionAsync(sourceName, sourceText, where.Condition, cancellationToken))
                        {
                            skip = true;
                        }
                        break;
                    case ComprehensionLetSyntax let:
                        var letValue = await EvaluateArgumentAsync(sourceName, sourceText, let.Value, cancellationToken);
                        _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                        break;
                }
                if (skip) break;
            }
            if (skip) continue;

            // Recurse into inner clause or execute body
            if (clause.InnerClause is not null)
            {
                await EvaluateComprehensionClauseAsync(sourceName, sourceText, clause.InnerClause, bodyAction, cancellationToken);
            }
            else
            {
                await bodyAction(cancellationToken);
            }
        }
    }

    /// Walks comprehension where/let modifiers in declared order.
    /// Returns true if iteration should be skipped (a where returned false).
    private bool ApplyComprehensionModifiersSync(
        string sourceName,
        string sourceText,
        IReadOnlyList<ComprehensionModifierSyntax> modifiers)
    {
        foreach (var modifier in modifiers)
        {
            switch (modifier)
            {
                case ComprehensionWhereSyntax where:
                    if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                        .GetAwaiter().GetResult())
                    {
                        return true;
                    }
                    break;
                case ComprehensionLetSyntax let:
                    var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                    break;
            }
        }
        return false;
    }

    private static void AddClauseBindings(Dictionary<string, object?> vars, ComprehensionClauseSyntax clause, object? current)
    {
        if (clause.DestructureNames is { } names)
        {
            var elements = ExtractDestructureElements(current);
            for (var i = 0; i < names.Count; i++)
                vars[names[i]] = elements is not null && i < elements.Count ? elements[i] : null;
        }
        else
        {
            vars[clause.VariableName] = current;
        }
    }

    private static IReadOnlyList<object?>? ExtractDestructureElements(object? value) =>
        value switch
        {
            IReadOnlyList<object?> list => list,
            KeyValuePair<object, object?> kvp => [kvp.Key, kvp.Value],
            KeyValuePair<string, object?> kvp => [kvp.Key, kvp.Value],
            _ => null,
        };

    /// <summary>
    /// Produces a lazy IEnumerable that evaluates comprehension body items on demand.
    /// The source is already evaluated; body evaluation blocks on async via GetAwaiter().GetResult().
    /// </summary>
    private IEnumerable<object?> EnumerateComprehensionLazily(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax clause,
        ArgumentSyntax body,
        object? sourceValue)
    {
        // If there's a non-parallel inner clause and the outer source is infinite, use diagonal enumeration
        if (clause.InnerClause is not null && !clause.InnerIsParallel && IsInfiniteSource(sourceValue))
        {
            foreach (var item in EnumerateComprehensionDiagonal(sourceName, sourceText, clause, clause.InnerClause, body, sourceValue))
            {
                yield return item;
            }
            yield break;
        }

        // Parallel/zip: bind both clause variables per step; terminate when either source ends
        if (clause.InnerClause is not null && clause.InnerIsParallel)
        {
            var innerSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, CancellationToken.None)
                .GetAwaiter().GetResult();
            using var outerEnum = ShellIterationUtilities.ExpandIterationItems(sourceValue).GetEnumerator();
            using var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSource).GetEnumerator();

            while (outerEnum.MoveNext() && innerEnum.MoveNext())
            {
                var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = outerEnum.Current };
                AddClauseBindings(vars, clause, outerEnum.Current);
                AddClauseBindings(vars, clause.InnerClause, innerEnum.Current);
                using var scope = PushScope(vars);

                var skip = false;
                foreach (var modifier in clause.Modifiers)
                {
                    switch (modifier)
                    {
                        case ComprehensionWhereSyntax where:
                            if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                                .GetAwaiter().GetResult())
                                skip = true;
                            break;
                        case ComprehensionLetSyntax let:
                            var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                                .GetAwaiter().GetResult();
                            _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                            break;
                    }
                    if (skip) break;
                }
                if (!skip)
                {
                    foreach (var modifier in clause.InnerClause.Modifiers)
                    {
                        switch (modifier)
                        {
                            case ComprehensionWhereSyntax where:
                                if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                                    .GetAwaiter().GetResult())
                                    skip = true;
                                break;
                            case ComprehensionLetSyntax let:
                                var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                                    .GetAwaiter().GetResult();
                                _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                                break;
                        }
                        if (skip) break;
                    }
                }
                if (skip) continue;

                if (clause.InnerClause.InnerClause is not null)
                {
                    var deepSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.InnerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, clause.InnerClause.InnerClause, body, deepSource))
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            yield break;
        }

        foreach (var current in ShellIterationUtilities.ExpandIterationItems(sourceValue))
        {
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_"] = current };
            AddClauseBindings(vars, clause, current);
            using var scope = PushScope(vars);

            // Walk modifiers in declared order (blocking on async each time)
            var skip = false;
            foreach (var modifier in clause.Modifiers)
            {
                switch (modifier)
                {
                    case ComprehensionWhereSyntax where:
                        if (!EvaluateConditionAsync(sourceName, sourceText, where.Condition, CancellationToken.None)
                            .GetAwaiter().GetResult())
                        {
                            skip = true;
                        }
                        break;
                    case ComprehensionLetSyntax let:
                        var letValue = EvaluateArgumentAsync(sourceName, sourceText, let.Value, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        _scopes.Peek().Variables[let.VariableName] = ToVariableBinding(letValue);
                        break;
                }
                if (skip) break;
            }
            if (skip) continue;

            // Recurse into inner clause or yield body result
            if (clause.InnerClause is not null)
            {
                // Evaluate inner source eagerly for this iteration
                var innerSource = EvaluateArgumentAsync(sourceName, sourceText, clause.InnerClause.Source, CancellationToken.None)
                    .GetAwaiter().GetResult();

                foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, clause.InnerClause, body, innerSource))
                {
                    yield return item;
                }
            }
            else
            {
                yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Returns true if the given value represents a known-infinite source
    /// (e.g. an infinite ToshRange or an unevaluated LazySequence).
    /// </summary>
    private static bool IsInfiniteSource(object? value) =>
        value is ToshRange { IsInfinite: true } or LazySequence { IsFiniteKnown: false };

    /// <summary>
    /// Produces lazy diagonal (Cantor) enumeration for nested comprehension clauses
    /// where at least one source is infinite. Walks anti-diagonals so that every
    /// (outer, inner) pair is reached in finite time.
    /// </summary>
    private IEnumerable<object?> EnumerateComprehensionDiagonal(
        string sourceName,
        string sourceText,
        ComprehensionClauseSyntax outerClause,
        ComprehensionClauseSyntax innerClause,
        ArgumentSyntax body,
        object? outerSourceValue)
    {
        // We cache outer/inner items as we advance through diagonals
        var outerCache = new List<object?>();
        using var outerEnum = ShellIterationUtilities.ExpandIterationItems(outerSourceValue).GetEnumerator();
        var outerDone = false;

        // For each outer item, we'll lazily evaluate the inner source and cache its items
        // But the inner source depends on the outer variable, so we need to cache the
        // (outerValue, innerCache, innerEnumerator) triple per outer index.
        var innerCaches = new List<(object? OuterValue, List<object?> Cache, IEnumerator<object?> Enum, bool Done)>();

        for (var diagonal = 0; ; diagonal++)
        {
            // Expand outer cache to cover this diagonal if possible
            while (!outerDone && outerCache.Count <= diagonal)
            {
                if (outerEnum.MoveNext())
                {
                    var outerVal = outerEnum.Current;
                    outerCache.Add(outerVal);

                    // Evaluate the inner source in the scope of this outer value
                    using var scope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [outerClause.VariableName] = outerVal,
                        ["_"] = outerVal,
                    });

                    // Apply outer modifiers (where/let in declared order)
                    if (ApplyComprehensionModifiersSync(sourceName, sourceText, outerClause.Modifiers))
                    {
                        // Mark as skipped with empty inner
                        innerCaches.Add((outerVal, new List<object?>(), EmptyEnumerator(), true));
                        continue;
                    }

                    var innerSource = EvaluateArgumentAsync(sourceName, sourceText, innerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var innerEnum = ShellIterationUtilities.ExpandIterationItems(innerSource).GetEnumerator();
                    innerCaches.Add((outerVal, new List<object?>(), innerEnum, false));
                }
                else
                {
                    outerDone = true;
                }
            }

            // Expand inner caches to cover this diagonal where needed
            for (var i = 0; i < innerCaches.Count && i <= diagonal; i++)
            {
                var j = diagonal - i;
                var entry = innerCaches[i];
                while (!entry.Done && entry.Cache.Count <= j)
                {
                    if (entry.Enum.MoveNext())
                    {
                        entry.Cache.Add(entry.Enum.Current);
                    }
                    else
                    {
                        innerCaches[i] = (entry.OuterValue, entry.Cache, entry.Enum, true);
                        entry = innerCaches[i];
                    }
                }
            }

            // Check termination: both outer and all inners exhausted
            if (outerDone)
            {
                var allInnersDone = true;
                var maxReachable = -1;
                for (var i = 0; i < innerCaches.Count; i++)
                {
                    if (!innerCaches[i].Done) allInnersDone = false;
                    var reach = i + innerCaches[i].Cache.Count - 1;
                    if (reach > maxReachable) maxReachable = reach;
                }
                if (allInnersDone && diagonal > maxReachable)
                    yield break;
            }

            // Walk anti-diagonal: i + j == diagonal
            var iMax = Math.Min(diagonal, outerCache.Count - 1);
            for (var i = 0; i <= iMax; i++)
            {
                var j = diagonal - i;
                if (i >= innerCaches.Count) continue;
                var entry = innerCaches[i];
                if (j >= entry.Cache.Count) continue;

                var outerVal = outerCache[i];
                var innerVal = entry.Cache[j];

                // Evaluate body in scope of both variables
                using var bodyScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [outerClause.VariableName] = outerVal,
                    ["_"] = outerVal,
                });

                // Re-apply outer modifiers in the body scope so body expressions can see outer lets.
                // Skip-logic is irrelevant here because the outer where was already checked above.
                ApplyComprehensionModifiersSync(sourceName, sourceText, outerClause.Modifiers);

                // Push inner scope
                using var innerScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [innerClause.VariableName] = innerVal,
                    ["_"] = innerVal,
                });

                // Apply inner modifiers in declared order
                if (ApplyComprehensionModifiersSync(sourceName, sourceText, innerClause.Modifiers))
                {
                    continue;
                }

                if (innerClause.InnerClause is not null)
                {
                    // Three+ levels of nesting: recurse normally for the deeper levels
                    var deepSource = EvaluateArgumentAsync(sourceName, sourceText, innerClause.InnerClause.Source, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    foreach (var item in EnumerateComprehensionLazily(sourceName, sourceText, innerClause.InnerClause, body, deepSource))
                    {
                        yield return item;
                    }
                }
                else
                {
                    yield return EvaluateArgumentAsync(sourceName, sourceText, body, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
        }
    }

    private static IEnumerator<object?> EmptyEnumerator()
    {
        yield break;
    }
}
