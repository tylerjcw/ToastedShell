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

public sealed partial class ToshEngine : IShellEvaluator, IShellNamedTypeView, IToshScriptHost
{
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

    /// <summary>
    /// How many deferred-cleanup blocks are running, so the `exit` guard can let
    /// them finish (`TS-P2-115`).
    /// </summary>
    /// <remarks>
    /// A counter rather than a flag because cleanup nests: a deferred block may
    /// call a function that has deferred blocks of its own.
    /// </remarks>
    private int _deferredCleanupDepth;
    private int _commandEventDepth;
    private sealed class UnhostedRuntimeNamespace : IShellRecordObject
    {
        internal static readonly UnhostedRuntimeNamespace Instance = new();

        public string ShellTypeName => "ToastRuntime";

        public bool TryGetMember(string name, out object? value, bool includeHidden = false)
        {
            value = null;
            return false;
        }

        public bool TrySetMember(string name, object? value) => false;

        public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) => [];
    }

    private readonly IShellRecordObject _toshNamespace;
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
    /// The type annotation the value under evaluation is destined for, when one is known —
    /// <c>TOAST-0096</c>.
    /// </summary>
    /// <remarks>
    /// Pushed around an annotated initialiser and a typed return, so a generic construction can
    /// seed its type arguments from where the value is going rather than only from what it was
    /// handed. That is the whole of what makes `Option::None()` writable: a unit variant has no
    /// argument to infer from, so without a target there is nothing to read.
    /// </remarks>
    internal string? TargetTypeAnnotation => _targetTypeAnnotation.Value;

    /// <summary>
    /// The declared return type of the function currently executing, when it has one —
    /// <c>TOAST-0096</c>. A `return` reads it as its target, so
    /// `func f() -> Option&lt;int&gt; { return Option::None() }` infers what the signature
    /// already said.
    /// </summary>
    /// <remarks>
    /// A plain field rather than an <c>AsyncLocal</c>, saved and restored around the body the
    /// way <c>_functionCallStack</c> already is. As an `AsyncLocal` the value was invisible to
    /// the body the moment any statement in it ran a command — `echo "x"` before a `return` was
    /// enough — because the write did not reach the execution context the body's continuations
    /// resumed on. The symptom was that a `return` at the top of a function inferred and the
    /// same `return` after one unrelated line did not.
    /// </remarks>
    private string? _currentReturnAnnotation;

    internal string? CurrentReturnAnnotation => _currentReturnAnnotation;

    internal IDisposable PushTargetTypeAnnotation(string? annotation)
    {
        var previous = _targetTypeAnnotation.Value;
        _targetTypeAnnotation.Value = annotation;
        return new TargetTypeAnnotationScope(this, previous);
    }

    private sealed class TargetTypeAnnotationScope(ToshEngine engine, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            engine._targetTypeAnnotation.Value = previous;
        }
    }

    /// <summary>
    /// Reads type arguments for <paramref name="unionName"/> out of the current target
    /// annotation, when it names that union — <c>TOAST-0096</c>.
    /// </summary>
    internal bool TryBindUnionTypeArgumentsFromTarget(
        string unionName,
        IReadOnlyList<string> typeParameterNames,
        Dictionary<string, string> bindings)
    {
        if (_targetTypeAnnotation.Value is not { } annotation ||
            !TrySplitGenericTypeName(annotation, out var bareName, out var arguments) ||
            !string.Equals(bareName, unionName, StringComparison.Ordinal) ||
            arguments.Count != typeParameterNames.Count)
        {
            return false;
        }

        for (var index = 0; index < typeParameterNames.Count; index++)
        {
            // The target wins over what the arguments inferred. An annotation is a declaration;
            // inference from a value can only report the CLR type it happens to have, which for
            // any declared record is `ToshRecordInstance` and never the name the annotation
            // uses. A real mismatch is still refused by the variant field's own type check.
            bindings[typeParameterNames[index]] = arguments[index];
        }

        return true;
    }

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

    /// <summary>
    /// Creates a language engine without constructing a TōSh session runtime.
    /// </summary>
    /// <remarks>
    /// Shell-only operations remain unavailable unless the supplied <see cref="ToastRuntime"/>
    /// exposes the corresponding host capability. Ordinary parsing, binding, declarations,
    /// expressions, and language streams use this runtime directly (`TOAST-0006`).
    /// </remarks>
    public ToshEngine(ToastRuntime runtime)
    {
        LanguageRuntime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        LanguageRuntime.BlockExecutor = new EngineBlockExecutor(this);
        LanguageRuntime.Evaluator = this;
        LanguageRuntime.EventSenderFactory = CreateEventSender;
        LanguageRuntime.Invoker.ExtensionResolver = TryInvokeExtensionAsync;
        _toshNamespace = CreateRuntimeNamespace();
        _environmentNamespace = new ShellEnvironmentNamespace(LanguageRuntime.EnvironmentExporter);

        LoadBuiltinRunesAsync().GetAwaiter().GetResult();

        Tosh.Runtime.OperatorEvaluator.ResolveTraitConstraint ??= static (name, type) =>
            ToshTypeParameterConstraintRegistry.TryGet(name, out var predicate) && predicate(type);
    }

    /// <summary>
    /// Creates a forked child engine that shares the same <see cref="ToastRuntime"/> but has
    /// its own isolated scope stack pre-seeded with cloned copies of <paramref name="capturedScopes"/>.
    /// The fork does NOT write back to <c>LanguageRuntime.BlockExecutor</c> /
    /// <c>LanguageRuntime.Evaluator</c> /
    /// <c>LanguageRuntime.EventSenderFactory</c>; instead it propagates its executor via
    /// <see cref="CommandContext.BlockExecutor"/>.
    /// </summary>
    private ToshEngine(ToastRuntime runtime, IReadOnlyList<LexicalScope>? capturedScopes)
    {
        LanguageRuntime = runtime;
        _toshNamespace = CreateRuntimeNamespace();
        _environmentNamespace = new ShellEnvironmentNamespace(LanguageRuntime.EnvironmentExporter);

        if (capturedScopes is not null)
        {
            foreach (var scope in capturedScopes)
            {
                _scopes.Push(scope.Clone());
            }
        }

        _ownBlockExecutor = new EngineBlockExecutor(this);
        LanguageRuntime.Invoker.ExtensionResolver = TryInvokeExtensionAsync;
    }

    /// <summary>
    /// Creates an isolated child engine that shares the same runtime but has its own
    /// execution state. Pass <see cref="CaptureVisibleScopes"/> as the snapshot.
    /// </summary>
    internal ToshEngine Fork(IReadOnlyList<LexicalScope>? capturedScopes)
        => new ToshEngine(LanguageRuntime, capturedScopes);

    private IShellRecordObject CreateRuntimeNamespace()
        => LanguageRuntime.RuntimeNamespaceFactory?.CreateRuntimeNamespace(
            new ToshScriptNamespace(this),
            new ToshFunctionNamespace(this))
           ?? UnhostedRuntimeNamespace.Instance;

    private static readonly ParseResult _builtinRunesParseResult =
        ToshParser.Parse(BuiltinRunes.Source, "<builtin-runes>");

    /// <summary><c>TOAST-0083</c>. Core types, loaded the way the built-in runes are.</summary>
    private static readonly ParseResult _corePreludeParseResult =
        ToshParser.Parse(CorePrelude.Source, "<core-prelude>");

    /// <summary>
    /// True while the prelude itself is being evaluated, so its own declarations are not
    /// reported as shadowing themselves — <c>TOAST-0083</c>.
    /// </summary>
    private bool _loadingCorePrelude;

    private async Task LoadBuiltinRunesAsync()
    {
        _loadingCorePrelude = true;
        try
        {
            await foreach (var _ in EvaluateAsync(_corePreludeParseResult, CancellationToken.None)) { }
        }
        finally
        {
            _loadingCorePrelude = false;
        }

        await foreach (var _ in EvaluateAsync(_builtinRunesParseResult, CancellationToken.None)) { }
    }

    /// <summary>
    /// Reports a declaration that takes a core type's name — <c>TOAST-0083</c>.
    /// </summary>
    /// <remarks>
    /// The declaration wins: resolution follows the rule the parser already documents, that a
    /// bare name is where a declaration should win, and the same precedence by which a user
    /// `func double` beats the `double` alias. It is warned about rather than accepted silently
    /// because `Option` and `Result` are names a user may take without meaning to displace
    /// anything, and the displacement is otherwise invisible.
    /// </remarks>
    private void WarnIfShadowingCoreType(string typeName)
    {
        if (_loadingCorePrelude || !CorePrelude.TypeNames.Contains(typeName))
        {
            return;
        }

        WriteWarning(
            code: "tosh.naming.shadowed_core_type",
            title: $"'{typeName}' shadows the core type '{typeName}'.",
            help: "Rename it, or hush this code: hush tosh.naming.shadowed_core_type",
            category: ToshDiagnosticCategory.Naming);
    }

    /// <summary>
    /// The language-owned runtime state used by parsing, binding, and evaluation.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Runtime"/> so language services do not reach their
    /// state through shell forwarding properties. A language-only host supplies this type
    /// directly; TōSh supplies the same object through composition.
    /// </remarks>
    public ToastRuntime LanguageRuntime { get; }

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

    internal IReadOnlyList<object?> GetCurrentScriptArguments() =>
        _scriptArgumentsStack.Count > 0
            ? _scriptArgumentsStack.Peek()
            : LanguageRuntime.InvocationArguments;

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
        // LanguageRuntime.NativeTypes holds globally-declared `raw struct` types. It sits
        // under the lexical scopes but above the CLR resolver, so a global raw
        // struct is nameable even with no scope on the stack.
        var baseResolver = LanguageRuntime.NativeTypes.Count == 0 && LanguageRuntime.Modules.Count == 0
            ? LanguageRuntime.TypeResolver
            : new NativeTypeRegistryResolver(LanguageRuntime.TypeResolver, LanguageRuntime.NativeTypes, LanguageRuntime.Modules);

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

        if (_scopes.Count == 0 && modules.Count == 0 &&
            LanguageRuntime.Commands is IScopedCommandView directView)
        {
            return directView;
        }

        return new ScopedCommandView(
            _scopes.ToArray(),
            LanguageRuntime.Commands,
            modules,
            this);
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

        foreach (var name in LanguageRuntime.Classes.Keys)
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

        if (LanguageRuntime.TypeResolver is DotNetTypeResolver resolver)
        {
            foreach (var alias in resolver.GetAliases().Keys)
            {
                typeNames.Add(alias);
            }
        }

        return ParseContext.Create(
            commandNames: LanguageRuntime.Commands.AllNames,
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
            var unit = Tosh.Language.Binding.Lowerer.Lower(parseResult, LanguageRuntime.Commands);

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
                    foreach (var d in typeDiagnostics)
                    {
                        Diagnostics.ReportWarning(d);
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

        var diagnostics = Tosh.Language.Binding.Binder.Bind(
            parseResult,
            LanguageRuntime.Commands,
            IsInteractiveSession,
            isExecutableOnPath: null,
            ambientUnions: CollectAmbientUnionShapes(),
            isKnownTypeName: IsKnownTypeNameForBinder);
        if (diagnostics.Count == 0) return;

        switch (BinderStrictness)
        {
            case BinderStrictness.Lenient:
                return;
            case BinderStrictness.Warn:
                foreach (var diagnostic in diagnostics)
                {
                    Diagnostics.ReportWarning(diagnostic);
                }
                return;
            case BinderStrictness.Strict:
                // `TOAST-0053`. Severity is honoured here, not just in the renderer. Throwing
                // the whole batch meant a warning-only run rendered as a warning, exited 0, and
                // never executed the program — the one outcome worse than not warning at all.
                var errors = new List<ToshDiagnostic>();

                foreach (var diagnostic in diagnostics)
                {
                    if (diagnostic.Severity == ToshDiagnosticSeverity.Error)
                    {
                        errors.Add(diagnostic);
                    }
                    else
                    {
                        Diagnostics.ReportWarning(diagnostic);
                    }
                }

                if (errors.Count > 0)
                {
                    throw new ToshDiagnosticException(errors.ToArray());
                }

                return;
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

    public IAsyncEnumerable<object?> ExecuteScriptFileAsync(
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
        var resolvedPath = PathUtilities.ResolvePath(LanguageRuntime.CurrentDirectory, path);

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

    /// <summary>
    /// The program behind an interpolation hole, parsed once and kept on the node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hole's text is fixed by the source, so re-parsing it per evaluation
    /// re-derived a constant. `$"x{$i}"` in a loop ran the lexer, parser, binder and
    /// lowering pass a million times over `$i`, which is why interpolation cost 84x
    /// a string concatenation (<c>TS-P2-121</c>).
    /// </para>
    /// <para>
    /// The hole is still parsed lazily, at its first evaluation rather than when the
    /// enclosing string is parsed. That keeps *when* a bad hole is reported exactly
    /// where it was — a hole in a branch never taken still never reports — and it
    /// keeps the parse context the one live at that point, which is what the old
    /// code used. Parsing every hole eagerly would change both, and neither change
    /// belongs in a performance fix.
    /// </para>
    /// </remarks>
    private ParseResult PrepareInterpolationHole(
        InterpolatedStringExpressionPart hole,
        string sourceName)
    {
        if (hole.PreparedProgram is { } prepared && ReferenceEquals(hole.PreparedBy, this))
        {
            return prepared;
        }

        var parseResult = Parse(hole.Expression, sourceName);

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

        hole.PreparedProgram = parseResult;
        hole.PreparedBy = this;
        return parseResult;
    }

    /// <summary>
    /// The single pipeline a hole's program consists of, when it consists of exactly one.
    /// </summary>
    /// <remarks>
    /// `TOAST-0023`. Used to decide whether a hole is an *expression* — one value — or a
    /// pipeline whose results join. Anything that is not a lone pipeline statement (a
    /// declaration, several statements, a control-flow form) returns null and takes the
    /// pipeline path, which is what it did before.
    /// </remarks>
    private static PipelineSyntax? TryGetHolePipeline(ParseResult hole) => hole.Statement switch
    {
        // A hole's program is a bare pipeline statement, not a script wrapping one — which
        // an earlier attempt at this assumed, and the assumption failed silently: the
        // pattern simply never matched and the hole kept its old behaviour.
        PipelineStatementSyntax pipeline => pipeline.Pipeline,
        ScriptStatementSyntax { Statements: [PipelineStatementSyntax single] } => single.Pipeline,
        _ => null,
    };

    private async IAsyncEnumerable<object?> EvaluateParseResultAsync(
        ParseResult parseResult,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        bool outputIsCaptured = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            $"script {parseResult.SourceName}",
            parseResult.SourceName,
            parseResult.SourceText,
            parseResult.Statement.Span);

        var isTopLevel = _commandEventDepth == 0;
        var values = new List<object?>();
        var stopwatch = isTopLevel ? System.Diagnostics.Stopwatch.StartNew() : null;

        // Raise CommandStarting for top-level user input only
        if (isTopLevel && LanguageRuntime.Events.GetHandlers(BuiltInEventNames.CommandStarting).Count > 0)
        {
            _commandEventDepth++;
            try
            {
                var sender = LanguageRuntime.EventSenderFactory?.Invoke()
                    ?? new ShellEventSender(Function: null, Script: null, Line: null);
                var inputText = parseResult.SourceText.Trim();
                var startingEvent = new CommandStartingEvent(
                    inputText, [], inputText, sender);
                await LanguageRuntime.Events.RaiseAsync(startingEvent, cancellationToken);

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
            if (isTopLevel && LanguageRuntime.Events.GetHandlers(BuiltInEventNames.CommandCompleted).Count > 0)
            {
                stopwatch?.Stop();
                _commandEventDepth++;
                try
                {
                    var sender = LanguageRuntime.EventSenderFactory?.Invoke()
                        ?? new ShellEventSender(Function: null, Script: null, Line: null);
                    var inputText = parseResult.SourceText.Trim();
                    var completedEvent = new CommandCompletedEvent(
                        inputText, exitCode, stopwatch?.Elapsed ?? TimeSpan.Zero,
                        values.Count > 0 ? values[^1] : null, sender);
                    await LanguageRuntime.Events.RaiseAsync(completedEvent, cancellationToken);
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

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StatementSyntax, System.Runtime.CompilerServices.StrongBox<bool>> YieldingStatements = new();

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
                        LanguageRuntime,
                        EmptyAsyncEnumerable(),
                        new object?[] { shellEvent },
                        cancellationToken,
                        BlockExecutor: _ownBlockExecutor,
                        ScopedCommands: CreateScopedCommandView(),
                        ShellTypes: this);

                    await foreach (var value in functionCommand.ExecuteAsync(context))
                    {
                        result = value;
                    }

                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await LanguageRuntime.Error.WriteTextLineAsync(
                        $"Event handler '{functionCommand.Name}' for '{eventName}' failed: {ex.Message}",
                        CancellationToken.None);
                    return null;
                }
            },
            priority,
            once,
            capturedScopes?.Cast<object>().ToArray());

        LanguageRuntime.Events.Register(handler);
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

    /// <summary>
    /// Methods added to types by <c>extend</c>, keyed by the type name written.
    /// </summary>
    /// <remarks>
    /// Registered when the declaration executes, which gives the visibility rule for
    /// free: a `require`d module runs its statements in this engine, so importing a
    /// library brings its extensions with it, the way a `using` brings C#'s
    /// (<c>TS-P3-27</c>).
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, FunctionDefinition>> _extensionMethods =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names an <c>extend</c> declaration may have used for this value.</summary>
    /// <summary>
    /// Union names this engine already knows, mapped to their variants, for the binder's
    /// exhaustiveness check — <c>TOAST-0083</c>.
    /// </summary>
    /// <remarks>
    /// The check was built from the source being bound, which is right for a union declared
    /// there and wrong for every other one: a `match` over the prelude's `Result` was neither
    /// judged exhaustive nor reported incomplete. Anything the engine holds — the core types,
    /// and whatever an import brought in — is offered here, and a declaration in the source
    /// still overrides it.
    /// </remarks>
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? CollectAmbientUnionShapes()
    {
        Dictionary<string, IReadOnlyList<string>>? shapes = null;

        void Add(object? candidate)
        {
            if (candidate is not ToshUnionDefinition union || union.Variants.Count == 0)
            {
                return;
            }

            shapes ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            shapes[union.Name] = union.Variants.Select(variant => variant.Name).ToArray();
        }

        foreach (var value in LanguageRuntime.Classes.Values)
        {
            Add(value);
        }

        foreach (var scope in _scopes)
        {
            foreach (var value in scope.Classes.Values)
            {
                Add(value);
            }
        }

        return shapes;
    }

    private static IEnumerable<string> EnumerateReceiverTypeNames(object receiver)
    {
        if (receiver is IShellTypedObject typed)
        {
            yield return typed.ShellTypeDescriptor.ShellTypeName;
            yield return typed.ShellTypeDescriptor.ShellFullName;
        }

        // `TOAST-0083`. A *bound* generic union names itself with its arguments — `Option<int>`
        // — while `extend Option { … }` registers under the bare name, so an extension on a
        // generic union could never be found. The declaration has no arguments to write and
        // `extend Option<T>` does not parse, so the bare name is the only thing an author can
        // key on and the receiver has to answer to it.
        if (receiver is ToshUnionVariantInstance unionVariant)
        {
            yield return unionVariant.UnionDefinition.Name;
        }

        var clr = receiver.GetType();
        yield return clr.Name;

        if (clr.FullName is { } full)
        {
            yield return full;
        }
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

    private async IAsyncEnumerable<object?> EvaluateUnionDefinitionAsync(
        string sourceName,
        string sourceText,
        UnionDefinitionStatementSyntax union,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, union.Name, union.Span, "reserved runtime namespace");
        WarnIfShadowingCoreType(union.Name);

        var variants = union.Variants
            .Select(v => new UnionVariantDefinition(
                v.Name,
                v.Fields.Select(f => new UnionVariantFieldDefinition(
                    f.Name,
                    f.TypeName,
                    f.Span)).ToArray()))
            .ToArray();

        var definition = new ToshUnionDefinition(
            this,
            union.Name,
            variants,
            union.TypeParameters,
            sourceName,
            sourceText,
            union.Span);

        DeclareType(union.Name, definition, union.Modifier, sourceName, sourceText, union.Span);
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
        definition.Documentation = record.DocComment;

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

    /// <summary>
    /// <c>raw callback Name(…) -&gt; ret</c> — emits the delegate type a native
    /// signature names when it takes a C function pointer, and registers it
    /// under <paramref name="rawCallback"/>'s name so
    /// <see cref="ResolveNativeInteropParameterType"/> finds it like any other
    /// native type.
    /// </summary>
    private IAsyncEnumerable<object?> EvaluateRawCallbackDefinitionAsync(
        string sourceName,
        string sourceText,
        RawCallbackDefinitionStatementSyntax rawCallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rawCallback.Name, rawCallback.Span, "reserved runtime namespace");

        var parameters = new List<NativeFunctionParameterDefinition>(rawCallback.Parameters.Count);

        foreach (var parameter in rawCallback.Parameters)
        {
            // A `buffer[n]` collapses into two ABI arguments and is decoded
            // after the call — an inbound convention with no meaning for a
            // callback, whose arguments arrive already formed.
            if (TryParseOutArrayParameter(parameter.TypeName, out _, out _))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_callback_buffer_parameter",
                    Title: $"Callback '{rawCallback.Name}' cannot take a buffer parameter.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: $"'{parameter.Name}' declares '{parameter.TypeName}'",
                    Help: "a callback receives whatever the caller passes; declare the pointer and length separately."));
            }

            // A ref/out callback parameter would need the value written back
            // into the caller's memory after the ToSh body ran. That write-back
            // has no design yet, and a silently-ignored `out` is worse than a
            // rejected one.
            if (parameter.PassingMode != NativeParameterPassingMode.In)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_callback_by_reference_parameter",
                    Title: $"Callback '{rawCallback.Name}' cannot take a by-reference parameter.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: $"'{parameter.Name}' is declared {parameter.PassingMode.ToString().ToLowerInvariant()}",
                    Help: "declare it as a pointer and use native-read / native-write inside the callback."));
            }

            parameters.Add(new NativeFunctionParameterDefinition(
                parameter.Name,
                parameter.TypeName ?? string.Empty,
                ResolveNativeInteropParameterType(
                    parameter.TypeName, parameter.PassingMode, sourceName, sourceText, parameter.Span,
                    $"callback parameter '{parameter.Name}'"),
                parameter.PassingMode));
        }

        var returnType = ResolveNativeInteropReturnType(rawCallback.ReturnTypeName, sourceName, sourceText, rawCallback.Span);

        // `ok` / `count` decide whether a native call *failed*. A callback's
        // return value is one we produce, so there is nothing to check and the
        // convention would silently do nothing.
        if (returnType.Convention != NativeErrorConvention.None)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.native_callback_return_convention",
                Title: $"Callback '{rawCallback.Name}' cannot declare a success convention.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: rawCallback.Span,
                Label: $"'{rawCallback.ReturnTypeName}' checks a result rather than producing one",
                Help: "write the concrete return type, such as '-> int'."));
        }

        var callingConvention = ResolveNativeCallingConvention(
            rawCallback.CallingConventionName, sourceName, sourceText, rawCallback.Span);

        var clrType = Bridge.NativeDelegateTypeFactory.GetOrCreate(parameters, returnType.ClrType, callingConvention);

        var definition = new ToshNativeCallbackDefinition(
            rawCallback.Name, clrType, parameters, returnType, callingConvention);

        DeclareType(rawCallback.Name, definition, rawCallback.Modifier, sourceName, sourceText, rawCallback.Span, clrType);
        return AsyncEnumerableExtensions.Empty<object?>();
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

        // Build properties, methods and constructors from body members
        var properties = new List<ToshClassPropertyDefinition>();
        var methods = new List<ToshClassMethodDefinition>();
        var constructors = new List<ToshClassConstructorDefinition>();

        foreach (var member in @struct.Members)
        {
            switch (member)
            {
                // `TS-P2-83`. This switch had no constructor case, so a declared struct
                // constructor was parsed and then silently dropped.
                case ClassConstructorMemberSyntax ctor:
                    constructors.Add(new ToshClassConstructorDefinition(
                        ctor.Parameters
                            .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                            .ToArray(),
                        ctor.Body,
                        sourceName,
                        sourceText,
                        ctor.Span,
                        CaptureVisibleScopes()));
                    break;
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
            CaptureVisibleScopes(),
            constructors);

        definition.Documentation = @struct.DocComment;
        definition.IsSealed = @struct.IsSealed;
        definition.IsFluid = @struct.IsFluid;
        definition.IsPartial = @struct.IsPartial;

        DeclareType(@struct.Name, definition, @struct.Modifier, sourceName, sourceText, @struct.Span);
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
            LanguageRuntime.Events.MarkRequired(definition.Name);
        }

        if (definition.IsLocal && _scopes.Count > 0)
        {
            _scopes.Peek().LocalEventNames.Add(definition.Name);
        }

        DeclareVariable(definition.Name, new VariableBinding(definition, ReplayAsPipeline: false, IsAllocatedOnly: false), @event.Modifier);
        yield break;
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

    private static async IAsyncEnumerable<object?> SingleItemAsync(object? item)
    {
        await Task.CompletedTask;
        yield return item;
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
            // `TOAST-0018`. A string does not contain nothing. `null` rendered as the
            // empty string, and every string contains that, so `"abc" contains null` was
            // true. Collection membership is unaffected: `[1, null] contains null` asks a
            // different question and still answers true.
            if (expected is null)
            {
                return false;
            }

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
        // `TOAST-0018`. The shared comparer, not a local copy of it. The copy this
        // replaced compared only values of an identical type and otherwise ordered by
        // type *name*, so `[1, "a", 2.5] | sort` answered `1, 2.5, "a"` while
        // `| sort | first 3` answered `2.5, 1, "a"` — a fused pipeline disagreeing with
        // the unfused one it is supposed to be indistinguishable from.
        IComparer<object?> ascending = ShellSortComparer.Ordinal;
        IComparer<object?> descending = new ReverseComparer(ascending);
        var comparer = fusion.Reverse ? descending : ascending;

        // Heap orders by the OPPOSITE direction so its top is the
        // candidate to evict. For ascending top-N (N smallest), we keep
        // a max-heap; for reverse (N largest), a min-heap.
        var evictionComparer = fusion.Reverse ? ascending : descending;
        var heap = new PriorityQueue<object?, object?>(fusion.Count, evictionComparer);

        // `TOAST-0025`. The stages this fusion replaced each expanded their input, and
        // the fusion did not — so a pipeline head yielding a lone collection reached the
        // heap as **one item**, and `[3,1,2] | sort | first` answered `3, 1, 2`: the
        // whole array, unsorted, with no error. `TS-P2-74` is why the head yields one
        // value ("it is each stage that decides whether a collection means itself or its
        // elements"), and this stands in for two stages that had both decided.
        //
        // The same helper `FirstCommand` calls, rather than a bare expansion: it honours
        // `PreExpandedSequence` (`TS-P2-113`), so a replayed variable is not expanded a
        // second time, and it expands only a *lone* collection, so a stream of several
        // collections keeps them as items. Both cases are pinned in the corpus.
        var expanded = ShellIterationUtilities.ReplaySingleInputCollectionAsync(source, cancellationToken);

        await foreach (var item in expanded.WithCancellation(cancellationToken))
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
    /// Runs a pipeline stage that is an expression.
    /// </summary>
    /// <remarks>
    /// Not an iterator itself, so the variable-replay branch can return a
    /// <see cref="PreExpandedSequence"/> — a stream that has already had its
    /// collection enumerated into it, and must not be expanded again downstream
    /// (`TS-P2-113`). The rest of the work stays in the iterator below.
    /// </remarks>
    private IAsyncEnumerable<object?> ExecuteExpressionStageAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        CancellationToken cancellationToken)
    {
        if (expressionStage.Expression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var binding) &&
            binding.ReplayAsPipeline &&
            binding.Value is IEnumerable enumerable &&
            binding.Value is not string)
        {
            return new PreExpandedSequence(ReplayBindingAsync(enumerable, cancellationToken));
        }

        // `TOAST-0028`. An expression head is where a collection *literal* — or a variable
        // holding one, or a range — reaches a pipeline, and those are sequences: spreading
        // them is what `[1, 2, 3] | where { … }` has always meant. Marking says so at the
        // producer, which is the point of the change; downstream no longer has to guess it
        // from how many items happen to arrive.
        var core = ExecuteExpressionStageCoreAsync(sourceName, sourceText, expressionStage, cancellationToken);

        return HeadIsACall(expressionStage.Expression) ? core : new SpreadableSequence(core);
    }

    /// <summary>
    /// Whether a pipeline head <em>calls</em> something — `TOAST-0039`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TOAST-0028` marked every expression head as a sequence, which made the rule
    /// syntactic in a way authors could not see. A function returning a collection answered
    /// 1 because a bare name parses as a command; a method returning the identical
    /// collection answered 3 because `$c.m()` parses as an expression. Nothing about the
    /// author's intent differed.
    /// </para>
    /// <para>
    /// The rule is now one sentence: a collection <em>written</em> as an expression is a
    /// sequence, and a collection <em>returned by a call</em> is a value. A property read
    /// stays a sequence, because `$obj.Items` <em>is</em> the collection in the same way a
    /// variable is — it is the calling that produces one.
    /// </para>
    /// </remarks>
    private static bool HeadIsACall(ArgumentSyntax expression) => expression switch
    {
        MethodCallArgumentSyntax => true,
        StaticMethodCallArgumentSyntax => true,
        CallableInvocationArgumentSyntax => true,
        // `new` is deliberately **not** a call here. It constructs a value the way a
        // literal writes one, and treating it as a call would make `new array(1, 2, 3)`
        // answer 1 while the identical `[1, 2, 3]` answers 3 — the same defect this item
        // exists to remove, reintroduced one spelling over.
        _ => false,
    };

    private static async IAsyncEnumerable<object?> ReplayBindingAsync(
        IEnumerable source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private async IAsyncEnumerable<object?> ExecuteExpressionStageCoreAsync(
        string sourceName,
        string sourceText,
        ExpressionPipelineStageSyntax expressionStage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {

        // `TS-P2-73`. A ternary arm could not invoke a multi-value command, because the
        // parentheses it *requires* are the same parentheses that impose single-value
        // collapse: unparenthesised arms are a parse error, and a parenthesised arm is a
        // subexpression, which `EvaluateArgumentAsync` reduces to one value or rejects.
        // So `func svc(a, s) => ($a == journal) ? (sudo journalctl -u $s) : (...)` failed
        // with "this subexpression produced 20 values" while the identical `if`/`else`
        // block streamed all twenty.
        //
        // Parentheses mean two things here — grouping and collapse — and an arm needs
        // only the first. The rule from `TS-P1-20` is unchanged and still applies
        // wherever a single value is genuinely required; this is a *pipeline stage*, so
        // the surrounding context streams, exactly as `for x in (pipeline)` already does
        // under rule 3 of that same list. An argument list is untouched: `echo ($a ? $b :
        // $c)` still reaches `EvaluateArgumentAsync` and still collapses.
        var effective = expressionStage.Expression;

        while (effective is ConditionalArgumentSyntax conditional)
        {
            var condition = await EvaluateArgumentAsync(sourceName, sourceText, conditional.Condition, cancellationToken);
            effective = OperatorEvaluator.ToBoolean(condition) ? conditional.WhenTrue : conditional.WhenFalse;
        }

        if (!ReferenceEquals(effective, expressionStage.Expression) &&
            effective is SubexpressionArgumentSyntax chosenSubexpression)
        {
            await foreach (var item in EvaluatePipelineAsync(
                sourceName, sourceText, chosenSubexpression.Pipeline, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        // `TOAST-0032`. `...$xs` sends the collection's elements, one item each, and says
        // so at the point it is written. Everything else here decides shape by inspecting
        // the value; this is the one form where the author has already said what they
        // meant, so nothing is inferred.
        if (effective is SpreadElementArgumentSyntax spread)
        {
            var spreadValue = await EvaluateArgumentAsync(sourceName, sourceText, spread.Value, cancellationToken);

            foreach (var item in ShellIterationUtilities.ExpandIterationItems(spreadValue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        object? value;

        try
        {
            value = await EvaluateArgumentAsync(sourceName, sourceText, effective, cancellationToken);
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

        // `TS-P2-74`: this gate stays. Spreading every list-valued expression head was
        // tried and is wrong — `[] | to json` must serialize the empty array rather than
        // send nothing downstream, and eight tests said so, across `to json`, format
        // round-trips and comprehensions. A pipeline head yields one value; it is each
        // stage that decides whether a collection means itself or its elements.
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
            ShellTypes: this);

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
            var existingTarget = await LanguageRuntime.ObjectAccessor.GetValueAsync(
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
            await LanguageRuntime.ObjectAccessor.SetValueAsync(
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

    public object? InvokeQualifiedMethodWithTypeArgumentsPublic(
        string path,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string> typeArgumentNames)
    {
        if (TryInvokeGenericUnionVariant(path, arguments, typeArgumentNames, out var unionValue))
        {
            return unionValue;
        }

        var resolved = ResolveExplicitTypeArguments(
            typeArgumentNames,
            sourceName: "<compiled>",
            sourceText: string.Empty,
            span: new TextSpan(0, 0));
        return InvokeQualifiedMethodAsync(path, arguments, CancellationToken.None, resolved)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private bool TryInvokeGenericUnionVariant(
        string path,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string> typeArgumentNames,
        out object? value)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == path.Length - 1 ||
            !TryGetNamedType(path[..lastDot], out var named) ||
            named is not ToshUnionDefinition union)
        {
            value = null;
            return false;
        }

        var invocation = union.InvokeGenericVariant(path[(lastDot + 1)..], arguments, typeArgumentNames);
        value = invocation.ReturnedVoid ? null : invocation.Value;
        return true;
    }

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
    /// <summary>
    /// Plans a static access whose head is a CLR type spelled differently from the shell alias it
    /// collides with — <c>TS-P2-37</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>File.ReadAllText(...)</c> reported "No overload matched static method 'ReadAllText' on
    /// 'System.IO.FileInfo'", naming a type the reader never wrote: the alias table is matched
    /// case-insensitively, so <c>File</c> found <c>file</c>. <c>Array.IndexOf</c> and
    /// <c>Tuple.Create</c> failed the same way against the shell's own <c>array</c> and
    /// <c>tuple</c> static types, which are reached through a different lookup — so the check
    /// runs ahead of both, at the one point they share.
    /// </para>
    /// <para>
    /// Only the *head* of a dotted path is treated this way, which is where a type is used as a
    /// type. Annotations resolve elsewhere and are untouched, so <c>var f: file</c> still binds
    /// <c>FileInfo</c>, as does <c>var f: File</c>.
    /// </para>
    /// </remarks>
    private bool TryPlanAliasCaseVariantAccess(string path, out Type type, out string[] members)
    {
        type = null!;
        members = System.Array.Empty<string>();

        var segments = SplitQualifiedPath(path);
        if (segments.Length < 2) return false;

        if (CreateScopedTypeResolver().ResolveAliasCaseVariant(segments[0]) is not { } resolved)
        {
            return false;
        }

        type = resolved;
        members = segments[1..];
        return true;
    }

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
        if (TryPlanAliasCaseVariantAccess(path, out var caseVariantType, out var caseVariantMembers) &&
            caseVariantMembers.Length == 1)
        {
            var caseVariantCall = LanguageRuntime.Invoker.InvokeStatic(
                caseVariantType,
                caseVariantMembers[0],
                arguments);
            return caseVariantCall.ReturnedVoid ? null : caseVariantCall.Value;
        }

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
            var invocation = LanguageRuntime.Invoker.InvokeStatic(plan.DeclaringType, plan.MethodName, arguments);
            return invocation.ReturnedVoid ? null : invocation.Value;
        }

        var target = ResolveQualifiedMemberChain(plan.DeclaringType, plan.MemberPath);

        if (target is null)
        {
            throw new InvalidOperationException("Cannot invoke an instance method on null.");
        }

        var instanceInvocation = LanguageRuntime.Invoker.InvokeInstance(target, plan.MethodName, arguments);
        return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
    }

    /// <summary>
    /// Resolves call-site type-argument names to CLR types — <c>TS-P2-82</c>.
    /// </summary>
    private IReadOnlyList<Type> ResolveExplicitTypeArguments(
        IReadOnlyList<string> names,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        var resolved = new List<Type>(names.Count);

        foreach (var name in names)
        {
            if (ResolveTypeName(name) is not { } type)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_type",
                    Title: $"Type '{name}' was not found.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"'{name}' is not a type this session can resolve",
                    Help: "use a fully qualified name, or add a 'using' for its namespace."));
            }

            resolved.Add(type);
        }

        return resolved;
    }

    /// <summary>
    /// Invokes a callable that was found in a property rather than declared as a
    /// method (`TS-P2-93`).
    /// </summary>
    /// <remarks>
    /// Returns an <see cref="InvocationResult"/> because the class dispatch paths
    /// it serves are method-invocation paths: from the caller's side
    /// <c>$obj.Fn(9)</c> is a call, and which side of the class the callable was
    /// stored on is not something the call site should have to know.
    /// </remarks>
    internal async ValueTask<InvocationResult> InvokeHeldCallableAsync(
        IShellCallable callable,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            Invocation: null,
            IsPipelined: false,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);

        var results = await AsyncEnumerableExtensions.ToListAsync(
            callable.InvokeAsync(context),
            cancellationToken);

        return results.Count switch
        {
            0 => new InvocationResult(null, ReturnedVoid: true),
            1 => new InvocationResult(results[0], ReturnedVoid: false),
            _ => new InvocationResult(results, ReturnedVoid: false),
        };
    }

    /// <summary>
    /// Invokes a callable in expression position, where exactly one value is
    /// expected.
    /// </summary>
    /// <remarks>
    /// One helper for the two parses that reach it. `f() + 1` builds a
    /// <c>CallableInvocationArgumentSyntax</c>; the same text inside an
    /// interpolation hole is re-parsed as a pure expression and builds a
    /// <c>StaticMethodCallArgumentSyntax</c> instead. They are the same call and
    /// must behave the same way, including the "produced N values" diagnostic —
    /// duplicating that here is the `TS-P1-24` shape.
    /// </remarks>
    private async ValueTask<object?> InvokeCallableInExpressionAsync(
        IShellCallable callable,
        IReadOnlyList<object?> arguments,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<TextSpan> argumentSpans,
        CancellationToken cancellationToken)
    {
        var invocation = new CommandInvocation(
            sourceName,
            sourceText,
            callable.CallableName,
            span,
            argumentSpans);

        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            invocation,
            IsPipelined: false,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);

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
            Span: span,
            Label: results.Count == 0
                ? "this invocation produced no values"
                : $"this invocation produced {results.Count} values",
            Help: "ensure the callable returns exactly one value, or use 'invoke' in pipeline context for multi-value output."));
    }

    private async ValueTask<object?> InvokeQualifiedMethodAsync(
        string path,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? typeArguments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryPlanAliasCaseVariantAccess(path, out var caseVariantType, out var caseVariantMembers) &&
            caseVariantMembers.Length == 1)
        {
            var caseVariantCall = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                caseVariantType,
                caseVariantMembers[0],
                arguments,
                typeArguments,
                cancellationToken);
            return caseVariantCall.ReturnedVoid ? null : caseVariantCall.Value;
        }

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
            var invocation = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                plan.DeclaringType,
                plan.MethodName,
                arguments,
                typeArguments,
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

        var instanceInvocation = await LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
            target,
            plan.MethodName,
            arguments,
            cancellationToken);
        return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
    }

    private bool TryResolveQualifiedAccess(string path, out object? value, out bool matchedType)
    {
        if (TryPlanAliasCaseVariantAccess(path, out var caseVariantType, out var caseVariantMembers))
        {
            matchedType = true;
            value = ResolveQualifiedMemberChain(caseVariantType, caseVariantMembers);
            return true;
        }

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

    private bool TryPlanShellSymbol(string path, out ShellSymbolPlan plan)
    {
        var segments = SplitQualifiedPath(path);

        // `TS-P2-100`. A module claims a dotted path only when it actually exports
        // the next segment. Claiming it unconditionally made a module named after a
        // CLR namespace swallow that namespace for the whole session: a profile
        // declaring `module System { … }` turned `System.Convert.ToInt32(…)` in an
        // unrelated file into "Member 'Convert' was not found on type
        // 'ToshModuleObject'".
        //
        // This is the rule the specification already states for a module named
        // after a CLR *type* — its own exports win, and a name it does not export
        // is looked up on the shadowed type — applied to namespaces, which had no
        // fall-through at all.
        if (segments.Length >= 2 &&
            TryGetModule(segments[0], out var module) &&
            ModuleClaimsPath(module, segments[0], segments[1]))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.Module,
                module,
                StaticType: null,
                segments[^1],
                segments.Length > 2 ? string.Join('.', segments[1..^1]) : null);
            return true;
        }

        // `TS-P2-92`. This required *exactly* two segments, so `C.Method(...)`
        // resolved and `C.Prop.Method(...)` did not — a static property's value
        // could be read (`C.Text.Length` works) but never called. A module has
        // carried a member chain here all along; a shell static type now does too,
        // and the two invokers walk it the same way.
        if (segments.Length >= 2 && TryResolveShellStaticType(segments[0], out var shellType))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.ShellStatic,
                Module: null,
                shellType,
                segments[^1],
                segments.Length > 2 ? string.Join('.', segments[1..^1]) : null);
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
            // With no member chain the name is a static method on the type itself;
            // with one, the chain is walked from the static member and the call
            // lands on whatever it produced (`TS-P2-92`).
            if (plan.MemberPath is null)
            {
                var staticInvocation = LanguageRuntime.Invoker.InvokeStatic(plan.StaticType!, plan.MethodName, arguments);
                value = staticInvocation.ReturnedVoid ? null : staticInvocation.Value;
                return true;
            }

            var staticTarget = LanguageRuntime.ObjectAccessor.GetValue(plan.StaticType, plan.MemberPath)
                               ?? throw CannotInvokeOnNull(plan.MethodName);
            var chained = LanguageRuntime.Invoker.InvokeInstance(staticTarget, plan.MethodName, arguments);
            value = chained.ReturnedVoid ? staticTarget : chained.Value;
            return true;
        }

        var target = plan.Module!;

        if (plan.MemberPath is not null)
        {
            target = LanguageRuntime.ObjectAccessor.GetValue(plan.Module, plan.MemberPath)
                     ?? throw CannotInvokeOnNull(plan.MethodName);
        }

        var invocation = LanguageRuntime.Invoker.InvokeInstance(target, plan.MethodName, arguments);
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
            if (plan.MemberPath is null)
            {
                var staticInvocation = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                    plan.StaticType!,
                    plan.MethodName,
                    arguments,
                    cancellationToken);
                return (true, staticInvocation.ReturnedVoid ? null : staticInvocation.Value);
            }

            var staticTarget = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                                   plan.StaticType,
                                   plan.MemberPath,
                                   cancellationToken)
                               ?? throw CannotInvokeOnNull(plan.MethodName);
            var chained = await LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
                staticTarget,
                plan.MethodName,
                arguments,
                cancellationToken);
            return (true, chained.ReturnedVoid ? staticTarget : chained.Value);
        }

        var target = plan.Module!;

        if (plan.MemberPath is not null)
        {
            target = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                         plan.Module,
                         plan.MemberPath,
                         cancellationToken)
                     ?? throw CannotInvokeOnNull(plan.MethodName);
        }

        var invocation = await LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
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

        // The same rule as `TryPlanShellSymbol` — see `TS-P2-100` there. A bare
        // module name still resolves to the module; only a dotted path whose next
        // segment the module does not export is left for CLR resolution.
        if (segments.Length >= 1 &&
            TryGetModule(segments[0], out var module) &&
            (segments.Length == 1 || ModuleClaimsPath(module, segments[0], segments[1])))
        {
            value = segments.Length == 1
                ? module
                : LanguageRuntime.ObjectAccessor.GetValue(module, string.Join('.', segments[1..]));
            return true;
        }

        if (segments.Length == 2 &&
            TryGetNamedType(segments[0], out var shellType))
        {
            value = LanguageRuntime.Invoker.GetStaticMember(shellType, segments[1]);
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
                current = LanguageRuntime.ObjectAccessor.GetValue(current, segments[index]);
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

        object? current = LanguageRuntime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = LanguageRuntime.ObjectAccessor.GetValue(current, memberSegments[index]);
        }

        return current;
    }

    private async ValueTask<object?> ResolveQualifiedMemberChainAsync(
        Type type,
        IReadOnlyList<string> memberSegments,
        CancellationToken cancellationToken)
    {
        RequireMemberPath(type, memberSegments);

        object? current = LanguageRuntime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = await LanguageRuntime.ObjectAccessor.GetValueAsync(
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


    /// <summary>
    /// The text of an interpolation hole — <c>$"{x}"</c> and <c>$"{x:F2}"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TOAST-0014` stage 2. This produced its text through <c>Runtime.Formatter</c>, which
    /// is built from a <c>DisplayProfileRegistry</c> — so the string a *program* built moved
    /// when the *shell's* display settings did, changeable mid-script. It now renders
    /// through <see cref="ToastRenderer"/>, which has no way to reach a profile.
    /// </para>
    /// <para>
    /// Three behaviours moved with it, and each was its own defect. The clause path used
    /// <c>CultureInfo.CurrentCulture</c>, so <c>$"{3.14159:F2}"</c> was <c>3.14</c> here and
    /// <c>3,14</c> on a German machine; rendering is invariant. A clause the value could not
    /// honour was silently dropped and the value printed plainly, which is a program
    /// succeeding while producing text nobody asked for; it now raises. And the custom
    /// <c>ToString</c> special case is gone because the renderer already dispatches to
    /// <c>Display</c> and then <c>ToString</c>, so the rule lives in one place.
    /// </para>
    /// </remarks>
    private ValueTask<string> FormatInterpolatedValueAsync(
        object? value,
        CancellationToken cancellationToken,
        string? format = null,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ValueTask.FromResult(ToastRenderer.Render(value, format));
        }
        catch (FormatException error)
        {
            // A refused clause is a decision, not an accident, so it reports as one. Left
            // to escape it surfaced as `tosh.runtime.unexpected_exception` — "unexpected"
            // being exactly what an error the language chose to raise is not, and the
            // reader would reasonably read that as a bug in the shell rather than in their
            // format string.
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.invalid_format_clause",
                Title: error.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: $"'{format}' does not apply to this value",
                Help: "use a format the value's type supports, or drop the clause. A clause "
                    + "that cannot be honoured is refused rather than ignored, so a "
                    + "mistyped one is not silently dropped."));
        }
    }

    /// <summary>Pads a formatted hole to its declared field width.</summary>
    /// <remarks>
    /// Positive pads on the left, negative on the right, as .NET composite formatting
    /// has it — <c>$"{$n,8}"</c> right-aligns in eight columns, <c>$"{$n,-8}"</c>
    /// left-aligns. A value wider than the field is never truncated.
    /// </remarks>
    /// <summary>Applies an interpolation hole's alignment, through the shared renderer.</summary>
    /// <remarks>
    /// `TOAST-0022`. The padding rule lives in `ToastRenderer` so the compiled backend applies
    /// the same one rather than a copy of it.
    /// </remarks>
    private static string ApplyInterpolationClauses(string text, int? alignment)
        => ToastRenderer.Align(text, alignment ?? 0);

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

    /// <summary>
    /// How many values a streamed statement keeps so <c>$tosh.Last.Result</c> can still
    /// report them (<c>TS-P1-45</c>).
    /// </summary>
    /// <remarks>
    /// Chosen far above any statement whose output someone would go on to inspect, so
    /// that in practice the last result is unchanged and only a producer large enough to
    /// have hung the shell before loses it. The budget is what makes streaming free
    /// rather than a trade: without it the choice was between materializing every
    /// statement and redefining <c>$tosh.Last.Result</c>.
    /// </remarks>
    private const int LastResultRetentionLimit = 10_000;

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

        if (!removedVariable && LanguageRuntime.Variables.TryGetValue(name, out var globalValue))
        {
            LanguageRuntime.Variables.Remove(name);
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
            LanguageRuntime.Classes.Remove(name);
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
            LanguageRuntime.Modules.Remove(name);
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
            LanguageRuntime.Commands.TryGet(name, out var command) &&
            command is ICommandResolutionMetadata metadata &&
            metadata.ResolutionKind is CommandResolutionKind.Alias or CommandResolutionKind.Function)
        {
            removedCommand = LanguageRuntime.Commands.Remove(name);
            commandKind = metadata.ResolutionKind.ToString();
            commandScope = "Global";
        }

        var removedEnvironment = Host.IsExported(name) ||
                                 Environment.GetEnvironmentVariable(name) is not null;
        Host.RemoveExportedEnvironmentVariable(name);

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

        var globalMatches = LanguageRuntime.Variables
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

    private static string ExtractSourceSnippet(string sourceText, TextSpan span)
    {
        if (span.Start < 0 || span.End <= span.Start || span.End > sourceText.Length)
        {
            return "<background job>";
        }

        return sourceText[span.Start..span.End].Trim();
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

            return LanguageRuntime.Variables;
        }

        return _scopes.Count > 0 ? _scopes.Peek().Variables : LanguageRuntime.Variables;
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
            LanguageRuntime.Classes[name] = definition;
            if (nativeClrType is not null) LanguageRuntime.NativeTypes[name] = nativeClrType;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().Classes[name] = definition;
            if (nativeClrType is not null) _scopes.Peek().NativeTypes[name] = nativeClrType;
            return;
        }

        LanguageRuntime.Classes[name] = definition;
        if (nativeClrType is not null) LanguageRuntime.NativeTypes[name] = nativeClrType;
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

    /// <summary>
    /// The module whose exported types an annotation should resolve against while
    /// a class member is running. Set by <see cref="ToshClassDefinition"/> around
    /// member invocation, because by then the declaring module's scope has left
    /// the stack.
    /// </summary>
    internal ModuleExportTable? AnnotationResolutionExports { get; set; }

    public bool TryGetNamedType(string name, out IShellNamedType definition)
    {
        // `TOAST-0090`. The shell's own named types — classes, enums, unions, nested types — are
        // looked up here by every path that is not the CLR resolver, so `Outer::Inner` is retired
        // to its dotted spelling before any of the tables below are consulted.
        name = Parsing.StaticPathSyntax.Canonicalize(name);

        if (AnnotationResolutionExports is { } declaringExports &&
            declaringExports.Types.TryGetValue(name, out var declaredSibling))
        {
            definition = declaredSibling;
            return true;
        }

        foreach (var scope in _scopes)
        {
            if (scope.Classes.TryGetValue(name, out var scopedDefinition) &&
                scopedDefinition is IShellNamedType shellType)
            {
                definition = shellType;
                return true;
            }
        }

        if (LanguageRuntime.Classes.TryGetValue(name, out var rawValue) &&
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

    private bool TryResolveShellStaticType(string path, out IShellStaticType definition)
    {
        if (TryGetNamedType(path, out var directType))
        {
            definition = directType;
            return true;
        }

        // Check LanguageRuntime.Classes for IShellStaticType instances that don't implement IShellNamedType
        // (e.g. MathShellType which is a pure static type without type descriptor semantics).
        if (LanguageRuntime.Classes.TryGetValue(path, out var classValue) && classValue is IShellStaticType runtimeStaticType)
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
                    current = LanguageRuntime.ObjectAccessor.GetValue(current, segment);

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

    private Type? ResolveTypeName(string name)
    {
        try
        {
            // `TOAST-0090`. Every spelling of a type name reaches the resolver through here —
            // annotations, casts, `is`, constructor targets — so the path operator is retired to
            // its dotted form once, at the boundary, and no resolver below needs to know it exists.
            return CreateScopedTypeResolver().Resolve(Parsing.StaticPathSyntax.Canonicalize(name));
        }
        catch (Exception exception) when (
            exception is FileLoadException or FileNotFoundException or TypeLoadException or
                         BadImageFormatException or ArgumentException)
        {
            // `TOAST-0050`. The CLR type-name parser *throws* on a name it cannot tokenise
            // rather than declining it — `(int, string)` arrives as "The given assembly name
            // was invalid" — and every caller here is asking "is this a CLR type?", to which
            // the answer is no. A `Try…` that throws is the defect; the tuple annotation
            // merely reached it first. `IsKnownAnnotatedType` already had a comment saying
            // the loader "can throw on angle-bracketed names containing commas" and worked
            // around it by checking generics earlier, which is the same bug avoided rather
            // than fixed.
            return null;
        }
    }

    /// <summary>
    /// Public wrapper around the internal scoped type resolver. Used by the
    /// compiled-code host (`ToshHost.NewObject`) to resolve verbatim
    /// type-argument strings (e.g. <c>"int"</c>, <c>"list&lt;string&gt;"</c>)
    /// against the engine's named-type registry and CLR fallback.
    /// </summary>
    /// <summary>
    /// Whether a qualified name in a type test resolves to anything — <c>TOAST-0105</c>.
    /// </summary>
    /// <remarks>
    /// Supplied to the binder the way the ambient unions are: the binder has no types of its own,
    /// and every table this consults lives in engine scope. Deliberately generous — a CLR type, a
    /// declared type, a refinement — because the diagnostic is only worth having if it never fires
    /// on a name that does resolve.
    /// </remarks>
    private bool IsKnownTypeNameForBinder(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { return true; }

        return ResolveTypeName(name) is not null ||
               TryGetNamedType(name, out _) ||
               TryResolveShellStaticType(name, out _) ||
               TryGetRefinementType(name, out _);
    }

    public Type? TryResolveTypeName(string name) => ResolveTypeName(name);

    /// <summary>
    /// Resolves a constructable shell type by its complete source spelling. The compiled-code
    /// host uses this for collection factories such as <c>list&lt;float&gt;</c>, whose variadic
    /// construction semantics cannot be reproduced by invoking the CLR <c>List&lt;T&gt;</c>
    /// constructors directly.
    /// </summary>
    public bool TryResolveShellStaticTypeName(string name, out IShellStaticType definition) =>
        TryResolveShellStaticType(name, out definition);

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

    private async IAsyncEnumerable<object?> ExpandRuneAsync(
        RuneDefinition rune,
        IReadOnlyList<ArgumentSyntax> arguments,
        string callerSourceName,
        string callerSourceText,
        IAsyncEnumerable<object?> input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // `TOAST-0069`. A rune that calls itself expands forever, and expansion is not one of
        // the paths the depth guard already covered — a recursive *function* reported
        // `tosh.runtime.recursion_limit_exceeded`, while a recursive rune overflowed the stack
        // and took the process with it. The compiled backend declines past its own expansion
        // depth and falls back; this is the interpreted half of the same limit.
        using var expansionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            $"rune {rune.Name}",
            callerSourceName,
            callerSourceText,
            rune.Span);

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
                    rune.IsSealed ? CaptureVisibleScopes() : null,
                    rune.IsSealed);
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
                var targetVariables = LanguageRuntime.Variables;
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
            // Block arguments: evaluate the block and collect results.
            //
            // A block runs where it was *written*, exactly as an expression argument does.
            // This path used to ignore `CallerScopes` and run in whatever scope was current,
            // which inside an expansion is the rune's own parameter scope — so a block
            // forwarded through a second rune saw that rune's parameters instead of the ones
            // it was written against. `rune f(b) { t { t $b } }` made `$b` mean `t`'s `b`
            // rather than `f`'s, and re-entered until the recursion limit stopped it.
            var pushNewScope = !IsInsideLeakyRune();
            var results = new List<object?>();

            using (thunk.IsSealed ? UseScopes(thunk.CallerScopes) : null)
            {
                await foreach (var item in ExecuteBlockAsync(
                    thunk.SourceName, thunk.SourceText, blockArg.Block, cancellationToken,
                    pushNewScope: pushNewScope))
                {
                    results.Add(item);
                }
            }

            return results.Count switch
            {
                0 => null,
                1 => results[0],
                _ => results.ToArray(),
            };
        }

        // Expression arguments: evaluate and return the single value
        if (thunk.IsSealed)
        {
            // Sealed: evaluate in the caller's scope — *as* that stack, not layered over the
            // current one. Layering leaves the rune's own parameter scope underneath, and an
            // argument that names the parameter it is bound to then resolves to itself.
            using var caller = UseScopes(thunk.CallerScopes);
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
                if (value is RuneThunk thunk && !thunk.IsSealed)
                    return true;
            }
        }
        return false;
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

    private async ValueTask<(
        bool Success,
        object? RefinedValue,
        ToshDiagnosticException? Failure)> TryApplyRefinementWithOptionalCoercionAsync(
        RefinementAnnotation? refinement,
        object? value,
        CancellationToken cancellationToken,
        string? baseTypeName = null)
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

        // `TOAST-0068`. The coercer's result is converted back to the refinement's base type
        // before the predicate is asked again.
        //
        // Without this a coercer can put another type in a refined slot and the predicate
        // will not notice, because a predicate tests the *value* and says nothing about its
        // type: `type TimeoutMs = int where (_ > 0 and _ <= 300000) coerce Math.Clamp(_, 0,
        // 300000)` accepted 999999 and left a `System.Double` in a slot declared `int`,
        // while the uncoerced path held an `Int32`. Two values, one annotation, two types.
        //
        // Only for a *named* refinement type, which is the case that has a declared base to
        // convert to. An inline `where` on a variable has already been converted against its
        // own annotation before reaching here.
        if (baseTypeName is not null)
        {
            if (!TryConvertAnnotatedValue(baseTypeName, coerced, out var reconverted))
            {
                return (false, coerced, null);
            }

            coerced = reconverted;
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

    private static string SubstituteTypeParametersInText(string text, IReadOnlyDictionary<string, string> substitutions)
    {
        var result = text;
        foreach (var (parameter, replacement) in substitutions)
        {
            result = Regex.Replace(result, $@"\b{Regex.Escape(parameter)}\b", replacement);
        }

        return result;
    }

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

    internal async IAsyncEnumerable<object?> ExecuteFunctionAsync(
        FunctionDefinition definition,
        CommandContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
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

        // `TS-P1-07`. Every function streams, generator or not. The buffering branch
        // that used to sit here was labelled "Non-generator functions buffer all output
        // before yielding" and existed to work around a C# restriction rather than to
        // express a semantic: `return` raises a signal that has to be caught around the
        // whole enumeration, and `yield return` is not allowed inside a try-with-catch.
        // The generator branch had already solved that with a manual enumerator, so both
        // now share it.
        //
        // Measured before and after with an unbounded producer: `gen | first` used to run
        // the loop forever — 800,000 values in ten seconds without ever short-circuiting —
        // while `seq 1 20000000 | first` and `yes | first` both returned in 0.25s. A
        // user-defined function was the only thing in a pipeline that could not be
        // short-circuited.
        // `TOAST-0096`. Set for the duration of the body, so a `return` inside it can seed a
        // generic construction from the signature.
        //
        // **Before the iterator is built, not after.** An async iterator captures the ambient
        // `ExecutionContext`, and an `AsyncLocal` assigned after that capture is invisible to
        // the body the moment anything inside it awaits — so the annotation survived a `return`
        // at the top of a function and vanished after a statement like `$list.Add(x)`, which was
        // as arbitrary as it sounds to debug.
        var previousReturnAnnotation = _currentReturnAnnotation;
        _currentReturnAnnotation = definition.RawReturnTypeName;

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

                // `TOSH-0010`. A return annotation describes the returned value. When the
                // function has a `return` of its own, these are the values it *emitted* on the
                // way there — output, not the result — and checking them against the
                // annotation refuses perfectly good functions that happen to log.
                //
                // A generator is the exception: its yielded values *are* its result, so they
                // are still checked.
                yield return definition.ReturnsExplicitly && !definition.IsGenerator
                    ? current
                    : ConvertFunctionReturnValue(definition, context, current, typeBindings);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            _functionInputStack.Pop();
            _functionArgumentsStack.Pop();
            _functionCallStack.Pop();
            _currentReturnAnnotation = previousReturnAnnotation;
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

        // `TOAST-0046`. `void` and `nothing` are the same bound type and now behave the
        // same, which they did not: `-> void` tried to convert the value to the CLR's
        // `System.Void` and failed with "could not be converted to 'void'", while
        // `-> nothing` was not a type the runtime resolver had heard of at all.
        //
        // Neither is a conversion question. A void function declares that it produces
        // nothing, so the only thing to check is whether it did.
        if (IsNothingAnnotation(definition.ReturnTypeName))
        {
            if (value is null)
            {
                return null;
            }

            throw context.CreateDiagnostic(
                code: "tosh.runtime.void_function_produced_value",
                title: $"Function '{definition.Name}' returns 'void' but produced a value.",
                label: $"'{definition.Name}' declares that it produces nothing",
                span: definition.Span);
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
            title: ToastMessages.FunctionReturnConversionFailure(
                definition.Name,
                definition.ReturnTypeName),
            label: ToastMessages.FunctionReturnConversionLabel(definition.ReturnTypeName),
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

    /// <summary>Whether a CLR type can carry a native count and its -1 failure.</summary>
    private static bool IsIntegerReturnWidth(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) ||
        type == typeof(sbyte) || type == typeof(IntPtr) || type == typeof(nint);

    /// <summary>
    /// The file <c>source</c> means by a relative path — <c>TS-P2-29</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>source "./x.tosh"</c> resolved against the working directory, so a script that sourced
    /// a sibling ran only from its own directory: from anywhere else it looked for the sibling
    /// beside the *caller*, and reported the file missing. <c>require</c> has always resolved
    /// against the requiring script and is the behaviour being matched.
    /// </para>
    /// <para>
    /// Unlike <c>require</c>, which resolves against the script directory and stops, a path that
    /// is not there falls back to the old working-directory resolution — so a caller that
    /// deliberately sourced something relative to where it was invoked keeps working, and the
    /// "file not found" message still comes from the same place it used to.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Session facts the language observes — see <see cref="IToastHostSignals"/>.
    /// </summary>
    /// <remarks>
    /// Routed through the interface rather than read off <c>Runtime</c> directly so the
    /// dependency is the three members the language actually needs, not the whole runtime.
    /// When `TOAST-0006` stage 2d introduces a ToastRuntime, only this line changes.
    /// </remarks>
    private IToastHostSignals Host => LanguageRuntime.HostSignals;

    /// <summary>
    /// Where warnings and trace lines go. The language decides there is something to say;
    /// the host decides how it looks and where it lands (`TOAST-0006`).
    /// </summary>
    private IToastDiagnosticSink Diagnostics => LanguageRuntime.Diagnostics;

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
        if (LanguageRuntime.Options.ScriptTrace && statementText is not null)
        {
            var prefix = line.HasValue ? $"+ {sourceName}:{line}" : $"+ {sourceName}";
            await Diagnostics.TraceAsync($"{prefix}: {statementText}", cancellationToken);
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
