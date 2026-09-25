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
    /// <remarks>
    /// <para>
    /// `TOAST-0133`. This guarded two names and was reached from union declarations alone,
    /// so `union Option` warned while `class Option` did not, and neither did `class
    /// Box&lt;int&gt;` — where a property annotated `int` then holds a string, with no
    /// warning, no note and nothing in the hover. The argument the warning was written for
    /// applies word for word to a built-in alias, and more strongly: `Option` is a name a
    /// user might reasonably take, while `int` is one they almost certainly did not mean to
    /// redefine.
    /// </para>
    /// <para>
    /// Both halves were measured before either was changed. Across 121 `.tosh` files —
    /// ToastLib, the repository's examples and its test corpus — no declaration and no type
    /// parameter takes any of these names, so the widening costs nothing in noise. The
    /// `func double` case the code calls out as intended is a *function*, not a type, and is
    /// untouched: this warns about type declarations only.
    /// </para>
    /// <para>
    /// The resolution rule is unchanged and deliberate — a declaration still wins. What was
    /// missing is the notice the language had already decided this displacement deserves.
    /// </para>
    /// </remarks>
    private void WarnIfShadowingCoreType(string? typeName)
    {
        if (_loadingCorePrelude || string.IsNullOrEmpty(typeName))
        {
            return;
        }

        var isCore = CorePrelude.TypeNames.Contains(typeName);
        var isBuiltIn = !isCore && Binding.TypeNameResolver.IsPrimitiveAlias(typeName);

        if (!isCore && !isBuiltIn)
        {
            return;
        }

        WriteWarning(
            code: "tosh.naming.shadowed_core_type",
            title: isCore
                ? $"'{typeName}' shadows the core type '{typeName}'."
                : $"'{typeName}' shadows the built-in type '{typeName}'.",
            help: "Rename it, or hush this code: hush tosh.naming.shadowed_core_type",
            category: ToshDiagnosticCategory.Naming);
    }

    /// <summary>
    /// Warns for each type parameter that displaces a type name — <c>TOAST-0133</c>.
    /// </summary>
    /// <remarks>
    /// A type parameter is the case the item was filed for: inside
    /// <c>class Box&lt;int&gt;</c> the name <c>int</c> means the parameter, so every
    /// annotation written <c>int</c> in that body means it too.
    /// </remarks>
    private void WarnIfTypeParametersShadow(IReadOnlyList<string>? typeParameters)
    {
        if (typeParameters is null)
        {
            return;
        }

        foreach (var parameter in typeParameters)
        {
            WarnIfShadowingCoreType(parameter);
        }
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
    /// Parses a fragment of <paramref name="enclosingSourceText"/> that begins at
    /// <paramref name="spanOffset"/>, so its diagnostics point into the enclosing source.
    /// </summary>
    private ParseResult ParseFragment(
        string source,
        string sourceName,
        int spanOffset,
        string enclosingSourceText)
    {
        var result = ToshParser.ParseFragment(
            source,
            sourceName,
            CreateParseContext(),
            spanOffset,
            enclosingSourceText);
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
            // `TOAST-0084`. The prelude's types are in scope for every script, so the checker has
            // to know them: without this, destructuring an `Option` gave its payload binding no
            // type while destructuring a union declared in the same file did. The parse is the
            // cached one the engine already loads the prelude from.
            var unit = Tosh.Language.Binding.Lowerer.Lower(
                parseResult,
                LanguageRuntime.Commands,
                ambientTypes: _corePreludeParseResult.Statement);

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

    /// <summary>
    /// Every command name the running engine can already reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>func</c> is declared into a lexical scope rather than the command registry, so
    /// the binder — which is handed the registry — cannot see one. That is why a source
    /// containing a <c>require</c> has its unknown-command check suppressed wholesale.
    /// </para>
    /// <para>
    /// An interpolation hole is parsed at its first evaluation, by which time the requires
    /// above it have run, so there the engine simply knows: <c>$"{ Whence() }"</c> reported
    /// a function that <c>which</c> could find and the call itself ran. Handing the names
    /// over keeps the typo check working instead of turning it off.
    /// </para>
    /// </remarks>
    private IReadOnlyCollection<string> VisibleCommandNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scope in _scopes)
        {
            if (scope.HasCommands)
            {
                names.UnionWith(scope.Commands.Keys);
            }
        }

        return names;
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
            isKnownTypeName: IsKnownTypeNameForBinder,
            declaredElsewhere: VisibleCommandNames());
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
    /// Evaluate a previously-lowered <see cref="BoundUnit"/>.
    /// v1 delegates to the parse-tree evaluator using the unit's
    /// <see cref="BoundUnit.ParseResult"/>;
    /// future commits will fast-path individual carved-out bound
    /// shapes without changing this public seam.
    /// </summary>
    public IAsyncEnumerable<object?> EvaluateAsync(
        BoundUnit unit,
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
        string sourceName,
        string sourceText)
    {
        if (hole.PreparedProgram is { } prepared && ReferenceEquals(hole.PreparedBy, this))
        {
            return prepared;
        }

        // Parsed as a fragment of the enclosing source rather than as a source of its own,
        // so a failure inside the hole reports the line the hole is written on. The hole's
        // text is re-lexed either way; only the coordinates differ.
        var parseResult = ParseFragment(
            hole.Expression,
            sourceName,
            hole.ExpressionSpan.Start,
            sourceText);

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

    /// <summary>
    /// Resolves a name relative to a module's export table, walking nested modules for a
    /// dotted remainder — <c>Geometry.Rectangle</c> inside <c>ToastLib.Math</c>.
    /// </summary>
    private static bool TryResolveWithinExports(
        ModuleExportTable exports,
        string relativeName,
        out IShellNamedType definition)
    {
        definition = null!;

        while (true)
        {
            if (exports.Types.TryGetValue(relativeName, out var found))
            {
                definition = found;
                return true;
            }

            var dot = relativeName.IndexOf('.');
            if (dot <= 0 ||
                !exports.Modules.TryGetValue(relativeName[..dot], out var nested) ||
                nested is not ToshModuleObject nestedModule)
            {
                return false;
            }

            exports = nestedModule.ExportTable;
            relativeName = relativeName[(dot + 1)..];
        }
    }

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

        // `TOAST-0122`. The same lookup for a declaration that annotates its own types by
        // their *full* path — `amount: ToastLib.Math.Vector2D<T>`, written inside
        // `ToastLib.Math`, which is good practice and what the author's library does. The
        // table above is keyed on bare names, so the qualified spelling missed it and fell
        // through to the ambient scope; under `require … as M` the caller has no
        // `ToastLib`, the parameter type resolved to nothing, and the failure surfaced as
        // "no overload matched with 1 argument(s)" — a message about arity for a problem
        // about names.
        //
        // Only this module's own prefix is stripped. Matching on the last segment instead
        // would resolve `Other.Vector2D` to this module's `Vector2D`, which is a wrong
        // answer where an error is the right one.
        if (AnnotationResolutionExports is { QualifiedName: { Length: > 0 } declaringPath } ownExports &&
            name.Length > declaringPath.Length + 1 &&
            name[declaringPath.Length] == '.' &&
            name.StartsWith(declaringPath, StringComparison.OrdinalIgnoreCase) &&
            TryResolveWithinExports(ownExports, name[(declaringPath.Length + 1)..], out var selfQualified))
        {
            definition = selfQualified;
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
        // A type parameter of the class whose method is running shadows a type of the same
        // name, as it does in C#. Checked first because otherwise `T` is looked up among the
        // session's types, is not one, and binds null — which is what left a rebuilt instance
        // reporting `A<T>` and, because a null binding is read as "nominal only, accept
        // anything", no longer enforcing its own `where` clause (`TOAST-0116`).
        if (TryResolveTypeParameterFromReceiver(typeArgument, out var fromReceiver))
        {
            return fromReceiver;
        }

        if (TryGetNamedType(typeArgument, out _))
        {
            return null;
        }

        return ResolveTypeName(typeArgument);
    }

    /// <summary>
    /// Resolves a type-argument name against the type parameters of the class whose method is
    /// currently running — <c>TOAST-0116</c>.
    /// </summary>
    /// <remarks>
    /// The receiver is reached through <c>this</c> in scope, so the lookup follows the same
    /// path the body itself does and needs no separate notion of a "current class". A lambda
    /// written inside a method sees the same <c>this</c>, and so resolves the same way.
    /// </remarks>
    /// <inheritdoc cref="TryResolveTypeParameterFromReceiver"/>
    internal bool TryResolveReceiverTypeParameter(string name, out Type? bound) =>
        TryResolveTypeParameterFromReceiver(name, out bound);

    /// <summary>
    /// Whether <paramref name="name"/> names a type parameter in scope, and what it is
    /// bound to — as a CLR <see cref="Type"/> where there is one, and otherwise by name
    /// (<c>TOAST-0131</c>).
    /// </summary>
    /// <remarks>
    /// A type argument that names a ToastScript class has no CLR type to bind, so it is
    /// recorded nominally instead. `T is Thing` has to answer for that case: declining it
    /// would send the question to the *value* comparison, where the left side is the
    /// bareword string "T" and the answer comes back a confident, wrong `false`.
    /// </remarks>
    internal bool TryResolveTypeParameterBinding(string name, out Type? bound, out string? nominal)
    {
        bound = null;
        nominal = null;

        if (_currentTypeParameterBindings is { } fromCall && fromCall.TryGetValue(name, out var callBound))
        {
            bound = callBound;
            return true;
        }

        if (!TryGetVariableBinding("this", out var binding))
        {
            return false;
        }

        var (arguments, nominals) = binding.Value switch
        {
            ToshClassSelfReference self => (self.TypeArgumentBindings, self.NominalTypeArgumentBindings),
            ToshClassInstance instance => (instance.TypeArguments, instance.NominalTypeArguments),
            _ => (null, null),
        };

        if (arguments is null || !arguments.TryGetValue(name, out bound))
        {
            return false;
        }

        if (bound is null && nominals is not null)
        {
            nominals.TryGetValue(name, out nominal);
        }

        return true;
    }

    /// <summary>
    /// The type parameters of the call currently running, if it declared any —
    /// <c>TOAST-0118</c>.
    /// </summary>
    private IReadOnlyDictionary<string, Type>? _currentTypeParameterBindings;

    /// <summary>
    /// Makes a generic call's own type arguments visible to its body.
    /// </summary>
    /// <remarks>
    /// The bindings were already worked out — from the call site, the target annotation, or
    /// inference — and then used for one thing, converting the return value. Nothing
    /// evaluated *inside* the body could see them, so <c>new A&lt;T&gt;</c> in a
    /// <c>func Make&lt;T&gt;</c> resolved <c>T</c> against the session's types, found
    /// nothing, and bound null.
    ///
    /// Saved and restored rather than pushed on a stack, matching
    /// <c>_currentReturnAnnotation</c> beside it: a call has at most one set, and a nested
    /// call replaces it for its own duration.
    /// </remarks>
    internal IDisposable? EnterTypeParameterBindings(IReadOnlyDictionary<string, Type>? bindings)
    {
        if (bindings is not { Count: > 0 })
        {
            return null;
        }

        var frame = new TypeParameterBindingFrame(this, _currentTypeParameterBindings);
        _currentTypeParameterBindings = bindings;
        return frame;
    }

    private sealed class TypeParameterBindingFrame(
        ToshEngine engine,
        IReadOnlyDictionary<string, Type>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            engine._currentTypeParameterBindings = previous;
        }
    }

    private bool TryResolveTypeParameterFromReceiver(string name, out Type? bound)
    {
        bound = null;

        // `TOAST-0118`. A method's own type parameter shadows the enclosing class's, as it
        // does in C#, so the call's bindings are consulted first. A method that declares
        // none has no bindings here and the receiver answers, which is `TOAST-0116`.
        if (_currentTypeParameterBindings is { } fromCall &&
            fromCall.TryGetValue(name, out var callBound))
        {
            bound = callBound;
            return true;
        }

        if (!TryGetVariableBinding("this", out var binding))
        {
            return false;
        }

        var bindings = binding.Value switch
        {
            ToshClassSelfReference self => self.TypeArgumentBindings,
            ToshClassInstance instance => instance.TypeArguments,
            _ => null,
        };

        return bindings is not null && bindings.TryGetValue(name, out bound);
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

}
