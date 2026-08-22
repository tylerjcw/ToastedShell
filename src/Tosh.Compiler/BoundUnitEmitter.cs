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
using Tosh.Runtime.Units;

namespace Tosh.Compiler;

/// <summary>
/// IL emitter for Tosh's bound IR. Walks <see cref="BoundUnit"/> and
/// produces a runnable .NET assembly. Coverage grows incrementally;
/// shapes that aren't yet handled are recorded as
/// <see cref="EmitResult.UnsupportedShapes"/> diagnostics so callers
/// can choose to fall back to the tree-walking evaluator for those
/// programs.
///
/// Currently supported:
/// • <c>BoundScript</c> with <c>BoundPipelineStatement</c> children
/// • <c>BoundCommandCall</c> named <c>echo</c> with literal/expression args
/// • <c>BoundLiteral</c> of int/long/double/bool/string/StorageSize/Quantity/null
/// • <c>BoundVariableDeclaration</c> + <c>BoundVariableReference</c>
/// • <c>BoundBinaryOperator</c> through the canonical runtime operator
///   dispatcher, including polymorphic arithmetic, comparison, matching,
///   and equality
/// • <c>BoundUnaryOperator</c> for <c>-x</c> and <c>!x</c>
/// • <c>BoundExpressionStage</c> as a pipeline stage
/// </summary>
public static class BoundUnitEmitter
{
    public static EmitResult Emit(BoundUnit unit, string assemblyName, Stream output)
        => Emit(unit, assemblyName, output, CompileProfile.Permissive, referenceAssembly: false);

    public static EmitResult Emit(
        BoundUnit unit,
        string assemblyName,
        Stream output,
        CompileProfile profile)
        => Emit(unit, assemblyName, output, profile, referenceAssembly: false);

    /// <summary>
    /// Emits a CLR assembly for <paramref name="unit"/>. When
    /// <paramref name="referenceAssembly"/> is <c>true</c>, the
    /// resulting assembly is stamped with
    /// <see cref="System.Runtime.CompilerServices.ReferenceAssemblyAttribute"/>
    /// so the C# / F# compilers will accept it as a metadata-only
    /// reference but refuse to load it for execution. Method
    /// bodies are post-processed to a uniform
    /// <c>ldnull; throw;</c> tiny-format stub so implementation
    /// details cannot leak into the contract surface; the
    /// assembly entry point and embedded PDB are also stripped.
    /// </summary>
    public static EmitResult Emit(
        BoundUnit unit,
        string assemblyName,
        Stream output,
        CompileProfile profile,
        bool referenceAssembly)
        => Emit(unit, assemblyName, output, profile, referenceAssembly, compilationSources: null);

    /// <summary>
    /// Full-fat <c>Emit</c> overload. <paramref name="compilationSources"/>
    /// is the set of source files that have been merged into
    /// <paramref name="unit"/> (as the CLI does when invoked with
    /// multiple <c>.tosh</c> inputs, and as the MSBuild SDK does
    /// for a project's compile items). When non-null, top-level
    /// <c>require</c> statements that resolve to one of those
    /// sibling sources are treated as build-time-satisfied and do
    /// not force Tier-3 source replay — their symbols are already
    /// part of this assembly. Pass <c>null</c> for single-file or
    /// in-memory test compilations.
    /// </summary>
    public static EmitResult Emit(
        BoundUnit unit,
        string assemblyName,
        Stream output,
        CompileProfile profile,
        bool referenceAssembly,
        IReadOnlyList<string>? compilationSources)
    {
        using var emitter = new EmitterImpl(unit, assemblyName, profile, referenceAssembly, compilationSources);
        emitter.Run();

        // `TOAST-0038`. Nothing is serialized once a shape has been refused.
        //
        // A refusal happens part-way through a method, so the IL it leaves behind is
        // incomplete by construction — a branch whose target was never marked, a stack that
        // never balanced. Serializing that raised `InvalidOperationException: Label 5 has
        // not been marked` from deep inside `PersistedAssemblyBuilder`, which is both a
        // crash and a lie: the emitter had already recorded exactly what it could not
        // handle, and that diagnostic was thrown away with the stack trace on top of it.
        //
        // Every caller already checks `IsClean` before touching the stream, so refusing to
        // write is what they expect. What changes is that they now get the reason.
        if (emitter.Diagnostics.Count == 0)
        {
            emitter.SerializeTo(output);
        }

        return new EmitResult(emitter.Diagnostics);
    }
}

/// <summary>
/// Result of an emit pass. <see cref="UnsupportedShapes"/> is empty
/// on a clean emit.
/// </summary>
public sealed record EmitResult(IReadOnlyList<string> UnsupportedShapes)
{
    public bool IsClean => UnsupportedShapes.Count == 0;
}

internal sealed partial class EmitterImpl : IDisposable
{
    /// <summary>
    /// Public CLR ABI version stamped on every emitted assembly via
    /// <see cref="global::Tosh.Runtime.ToshAbiAttribute"/>. Bumping
    /// this is a breaking change and requires a matching revision
    /// of <c>docs/CLR_ABI_v1.md</c>.
    /// </summary>
    public const int ToshClrAbiVersion = 1;

    private readonly BoundUnit _unit;
    private readonly string _assemblyName;
    private readonly CompileProfile _profile;
    private readonly bool _referenceAssembly;
    private readonly MetadataLoadContext? _metadataLoadContext;
    private readonly Assembly[] _metadataAssemblies;
    private readonly Dictionary<string, Type?> _metadataTypeCache = new(StringComparer.Ordinal);
    private readonly PersistedAssemblyBuilder _ab;
    private readonly ModuleBuilder _moduleBuilder;
    private readonly TypeBuilder _program;
    private readonly MethodBuilder _main;
    private ILGenerator _il;
    private Dictionary<BoundSymbol, LocalSlot> _locals = new();
    private Dictionary<BoundSymbol, int> _paramSlots = new();
    /// <summary>
    /// Per-typed-function parameter object-locals. When emitting the
    /// body of a fully-typed user function, the prologue copies each
    /// typed parameter into a <see cref="object"/>-typed local so
    /// the rest of the body emitter — which assumes parameter loads
    /// produce <see cref="object"/> — can stay unchanged. Populated
    /// only inside <see cref="EmitUserFunctionBody"/>; empty for
    /// untyped funcs (those keep using <see cref="_paramSlots"/>).
    /// </summary>
    private Dictionary<BoundSymbol, LocalBuilder> _typedParamLocals = new();
    /// <summary>
    /// Declared CLR return type of the user function currently being
    /// emitted, or <c>null</c> when the active method is the
    /// top-level <c>Main</c> / a closure / a clr-module body. Used
    /// by <see cref="EmitReturnStatement"/> to coerce the returned
    /// value to the typed return slot for fully-typed funcs.
    /// </summary>
    private Type? _currentFunctionReturnType;
    /// <summary>
    /// Original source spelling of the active return annotation. CLR reference
    /// types erase ToastScript's trailing <c>?</c>, so conversion diagnostics and
    /// nullability checks must not reconstruct this solely from <see cref="Type"/>.
    /// </summary>
    private string? _currentFunctionReturnTypeName;
    /// <summary>
    /// When the active typed user function declares a refinement
    /// return type (e.g. <c>-> Positive</c>), this carries the
    /// refinement so <see cref="EmitReturnStatement"/> can emit a
    /// <c>ToshHost.CheckType</c> guard before the actual <c>Ret</c>.
    /// <c>null</c> when the return is plain CLR / dynamic.
    /// </summary>
    private RefinementType? _currentFunctionReturnRefinement;
    /// <summary>
    /// CLR type of <c>this</c> while emitting an instance method on
    /// a class shell, or <c>null</c> outside such a context. Used by
    /// <see cref="EmitVariableReference"/> to lower <c>$this</c> to
    /// <c>Ldarg_0</c> with the shell's static type, which in turn
    /// lets <see cref="EmitMemberAccess"/> lower <c>$this.Field</c>
    /// to a direct <c>ldfld</c> and <see cref="EmitMethodCall"/>
    /// lower <c>$this.method(...)</c> to a direct <c>callvirt</c>.
    /// </summary>
    private Type? _currentThisType;
    /// <summary>
    /// Lower-cased keys derived from sibling source paths passed
    /// to <c>BoundUnitEmitter.Emit(...)</c>. Includes each input's
    /// full path, basename, and basename without the <c>.tosh</c>
    /// extension. Empty when no sibling list was supplied.
    /// Consulted by <see cref="RequireTargetIsSatisfiedAtBuildTime"/>.
    /// </summary>
    private readonly HashSet<string> _compilationSiblingKeys;
    private readonly Dictionary<string, List<UserFunction>> _userFunctions = new(StringComparer.Ordinal);
    /// <summary>
    /// Tracks which CLR signatures have already been claimed by a
    /// previous overload of the same tosh function name. Lets the
    /// emitter give every overload its plain CLR name (no mangling
    /// suffix) whenever the resulting signature is distinct from
    /// every other overload of that name — so ToastScript libraries
    /// expose overloaded methods to C# / F# / Roslyn the same way
    /// hand-written .NET libraries do. Keyed by tosh name; the inner
    /// hash set holds canonicalised signature keys produced by
    /// <see cref="BuildOverloadSignatureKey"/>.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _seenOverloadSignatures = new(StringComparer.Ordinal);
    // Keyed by function name: how many BoundFunctionDefinitions exist at
    // top level with that name.  Populated by a count pre-pass that runs
    // before Pre-pass B so DeclareUserFunction can suffix CLR method names
    // when there are multiple overloads.
    private Dictionary<string, int>? _topLevelFunctionOverloadCounts;
    /// <summary>
    /// Single document writer for the source the unit was lowered
    /// from. Used by <see cref="MarkSeqPoint"/> to produce
    /// statement-granularity sequence points so debuggers and stack
    /// traces can map IL offsets back to <c>.tosh</c> lines. Null
    /// when the unit has no source name (e.g. synthetic units in
    /// tests).
    /// </summary>
    private ISymbolDocumentWriter? _doc;
    /// <summary>
    /// Cached line-start offsets for <see cref="BoundUnit.ParseResult"/>'s
    /// SourceText. Lazily built by <see cref="GetLineColumn"/>; the
    /// element at index <c>i</c> is the source-text offset at which
    /// line <c>i</c> begins (1-based externally — index 0 here is
    /// line 1 in PDB coordinates).
    /// </summary>
    private int[]? _lineStarts;
    /// <summary>
    /// Per-module CLR shell state. Keyed by qualified module path
    /// ("Foo", "Foo.Bar"). Top-level modules become top-level CLR
    /// types; nested modules become nested types under their parent.
    /// All are emitted as <c>public static partial class</c> (sealed
    /// + abstract). Multiple <c>partial module</c> declarations
    /// across files reuse the same <see cref="TypeBuilder"/> so
    /// declarations accumulate into one type.
    /// </summary>
    private readonly Dictionary<string, ClrModuleShell> _clrModules = new(StringComparer.Ordinal);
    /// <summary>
    /// CLR shell types emitted for top-level <c>class</c> /
    /// <c>record</c> declarations. Keyed by the tosh type name.
    /// Registered during pre-pass D and finalized after the main
    /// program type is created. Used by
    /// <see cref="IsClrShellEmittedTypeDefinition"/> to suppress
    /// the Tier-3 source-replay diagnostic for these shapes, and
    /// by <see cref="EmitNewObject"/> / <see cref="EmitMemberAccess"/>
    /// / <see cref="EmitMethodCall"/> to dispatch directly to
    /// <c>newobj</c> / <c>ldfld</c> / <c>callvirt</c> instead of
    /// going through <see cref="global::Tosh.Compiler.Runtime.ToshHost"/>.
    /// </summary>
    private readonly Dictionary<string, ClrTypeShell> _clrTypeShells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoundRecordDefinition> _clrRecordDefinitions = new(StringComparer.Ordinal);

    /// <summary>
    /// Union-specific shell data for tosh <c>union</c> declarations that have
    /// been promoted to real CLR type hierarchies. Keyed by the tosh union
    /// name. The base abstract class and all sealed variant classes are also
    /// registered in <see cref="_clrTypeShells"/> / <see cref="_clrShellsByType"/>
    /// so that <see cref="EmitMemberAccess"/> can lower <c>$r.Variant</c>
    /// to a direct <c>ldfld</c>.
    /// </summary>
    private readonly Dictionary<string, ClrUnionShell> _clrUnionShells = new(StringComparer.Ordinal);

    /// <summary>
    /// Reverse lookup: TypeBuilder → ClrTypeShell. Used by
    /// expression-emit sites that have already produced a typed
    /// stack value (e.g. a local of the shell type) and need the
    /// shell's field / method metadata to dispatch directly.
    /// </summary>
    private readonly Dictionary<Type, ClrTypeShell> _clrShellsByType = new();
    /// <summary>
    /// Top-level integral <c>enum</c> declarations emitted as real CLR enum
    /// metadata. Besides identifying native declarations for the replay gate,
    /// each entry retains the emitted type and literal values so
    /// <see cref="EmitStaticMemberAccess"/> can lower
    /// <c>EnumName.Member</c> directly without host name resolution.
    /// </summary>
    private readonly Dictionary<string, ClrIntegralEnumShell> _clrEnumTypes =
        new(StringComparer.Ordinal);
    /// <summary>
    /// Enum declarations that cannot be expressed as a CLR <c>enum</c>
    /// (non-integral underlying type, or one or more members carrying a
    /// non-literal/non-integral value) but that <em>can</em> be represented
    /// as a CLR static class shell (<c>public sealed abstract class</c>)
    /// with one <c>public static readonly object</c> field per member,
    /// initialised in the type's <c>.cctor</c>. Keyed by the tosh enum
    /// name; values map each member name to its emitted
    /// <see cref="FieldBuilder"/> so <see cref="EmitStaticMemberAccess"/>
    /// can lower <c>EnumName.Member</c> to a direct <c>ldsfld</c>.
    /// </summary>
    private readonly Dictionary<string, ClrEnumStaticShell> _clrEnumStaticShells = new(StringComparer.Ordinal);
    /// <summary>
    /// Top-level <c>type alias</c> declarations for which a CLR sealed-class
    /// shell implementing <see cref="global::Tosh.Runtime.IShellRefinementTypeDescriptor"/>
    /// has been emitted. Keyed by the tosh alias name.
    /// Simple (non-refinement) aliases are fully CLR-represented here; refinement
    /// aliases still register a source-replay slice for predicate evaluation, but
    /// also emit a CLR shell for reflection discoverability.
    /// </summary>
    private readonly HashSet<string> _clrAliasTypes = new(StringComparer.Ordinal);
    /// <summary>
    /// Bind statements (<c>bind native "lib" as Module { ... }</c>)
    /// that the emitter has lifted into a real CLR static class with
    /// <c>[DllImport]</c> P/Invoke methods. Used by
    /// <see cref="TopLevelDeclarationNeedsSourceReplay"/> to skip
    /// engine-side source replay for these statements — first-class
    /// .NET plan, step 7 (phase 1).
    /// </summary>
    private readonly HashSet<BoundBindStatement> _clrNativeBinds = new();
    /// <summary>
    /// Module-scope methods, keyed by qualified path
    /// (<c>"Foo.greet"</c>). Method bodies are emitted in a second
    /// pass after the main top-level IL is laid out.
    /// </summary>
    private readonly List<ClrModuleMethodPending> _clrModuleMethodBodies = new();
    /// <summary>
    /// Pending class-method body emissions, deferred until after
    /// the top-level <c>Main</c> body is finalized. Each entry
    /// pairs a class shell + the <see cref="MethodBuilder"/>
    /// declared on it with the bound function definition whose
    /// body will be lowered into that method's IL.
    /// </summary>
    /// <summary>
    /// A function whose body ends in a bare expression returns that expression's value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parser desugars `=> expr` into a block whose last statement is a pipeline, so by
    /// the time it reaches the emitter it is indistinguishable from a block that simply ends
    /// in an expression. Both must produce the value; without this the block is emitted for
    /// effect, its value dropped, and the fall-through returns `default(T)` — `-> int` gives
    /// 0, `-> string` gives "", a class-returning method gives null. Silently, and for the
    /// most idiomatic way to write a function in the language.
    /// </para>
    /// <para>
    /// `TOAST-0043`. This was applied to free functions and not to class methods, so
    /// `class E { func M() -> int =&gt; 7 }` returned **null** compiled and 7 interpreted,
    /// while the block-bodied `{ return 7 }` was correct. It is shared now rather than
    /// written twice, which is what let the two drift.
    /// </para>
    /// <para>
    /// Gated on the *declaration* carrying a return type, not on whether the emitter could
    /// map that name to a CLR type — the latter is false for a user class, which would leave
    /// class-returning functions still yielding null. Gating on neither is worse: an
    /// unannotated function yields a stream, and collapsing its trailing pipeline into a
    /// single return breaks multi-value yields. `dynamic` is excluded deliberately, being
    /// the documented way to opt out of an annotation, so it keeps stream semantics.
    /// </para>
    /// </remarks>
    private static BoundBlock CollapseTrailingExpressionIntoReturn(BoundFunctionDefinition func)
    {
        var body = func.Body;

        if (func.ReturnTypeName is null ||
            string.Equals(func.ReturnTypeName, "dynamic", StringComparison.OrdinalIgnoreCase) ||
            body.Statements.Count == 0 ||
            body.Statements[^1] is not BoundPipelineStatement trailing)
        {
            return body;
        }

        var leading = new BoundStatement[body.Statements.Count - 1];
        for (var i = 0; i < leading.Length; i++) { leading[i] = body.Statements[i]; }

        return new BoundBlock(
            [.. leading, new BoundReturnStatement(trailing.Pipeline, trailing.Span)],
            body.Span);
    }

    private readonly List<ClrClassMethodPending> _clrClassMethodBodies = new();
    /// <summary>
    /// Module-scope variable declarations queued for emission into
    /// the owning module's static constructor. The owning module's
    /// field is already registered in <see cref="_staticFields"/> so
    /// the generic <see cref="EmitVariableDeclaration"/> path Just
    /// Works once <c>_il</c> is pointed at the <c>.cctor</c>.
    /// </summary>
    private readonly List<ClrModuleFieldPending> _clrModuleFieldInits = new();
    private Stack<LoopFrame> _loopStack = new();
    /// <summary>
    /// Stack of synthetic <c>_</c> bindings active in the current
    /// emission context. Match-arm pattern tests, guard predicates,
    /// and (eventually) block-argument bodies push the current
    /// scrutinee here so a name-only <c>_</c> reference (which has
    /// no <see cref="BoundSymbol"/>) can still be loaded by the
    /// emitter. Top-of-stack wins; popped on arm completion.
    /// </summary>
    private Stack<LocalBuilder> _underscoreStack = new();
    private int _suppressStatementOutputDepth;
    /// <summary>
    /// Non-null while emitting a compiled block-body method. Holds the
    /// <c>List&lt;object?&gt;</c> local (index 0) that the block body
    /// writes its output items into.
    /// </summary>
    private LocalBuilder? _blockOutputLocal;
    /// <summary>
    /// Maps <see cref="BoundSymbol"/> captures to their zero-based index
    /// in the <c>_captureValues</c> array parameter (arg 1) of the
    /// currently-emitting compiled block-body method. Empty outside
    /// a block-body context.
    /// </summary>
    private Dictionary<BoundSymbol, int> _blockCaptureIndices = new();
    /// <summary>
    /// Maps a script-input parameter symbol to its zero-based binding
    /// index within the bindings array passed to the compiled
    /// subcommand body method.  Populated during
    /// <see cref="EmitSubcommandBodyMethod"/> and empty outside that
    /// context.  Flags come first (in declaration order), then args.
    /// </summary>
    private Dictionary<BoundSymbol, int> _subcommandBindingIndices = new();
    /// <summary>
    /// Counter for unique subcommand body method names (appended as
    /// suffix to avoid collisions when two subcommands have the same
    /// name at different nesting levels).
    /// </summary>
    private int _subcommandBodyCounter;
    /// <summary>
    /// Captured top-level symbols promoted to static fields on the
    /// program type. Populated once in the prologue from every
    /// nested <see cref="BoundFunctionDefinition.Captures"/> list,
    /// then consulted by <see cref="EmitVariableReference"/> /
    /// <see cref="EmitVariableAssignment"/> ahead of the per-method
    /// local map. Promotion gives nested funcs by-reference access
    /// to the enclosing scope without synthesizing a display class
    /// per capture frame.
    /// </summary>
    private readonly Dictionary<BoundSymbol, FieldBuilder> _staticFields = new();
    /// <summary>
    /// Names of every top-level <c>func</c> declaration. Used to
    /// short-circuit capture analysis: if a captured symbol's name
    /// matches a top-level user function, the inner reference is
    /// already resolved through <see cref="_userFunctions"/> and
    /// no static field is needed.
    /// </summary>
    private readonly HashSet<string> _topLevelFunctionNames = new(StringComparer.Ordinal);
    public List<string> Diagnostics { get; } = new();

    /// <summary>
    /// Records that the emitter needs at least <paramref name="tier"/>
    /// runtime support for <paramref name="feature"/>. If the active
    /// <see cref="_profile"/> doesn't permit that tier, a diagnostic
    /// is added that fails the compile via the standard
    /// <see cref="EmitResult.IsClean"/> check.
    /// </summary>
    /// <remarks>
    /// Diagnostics are deduplicated per (tier, feature) pair so a
    /// program with many calls to the same builtin only reports once.
    /// </remarks>
    private readonly HashSet<(int Tier, string Feature)> _tierViolationsSeen = new();

    private void RequireTier(int tier, string feature)
    {
        var maxAllowed = _profile switch
        {
            CompileProfile.Pure => 1,
            CompileProfile.Runtime => 2,
            _ => 3,
        };
        if (tier <= maxAllowed) return;
        if (!_tierViolationsSeen.Add((tier, feature))) return;
        var profileName = _profile.ToString().ToLowerInvariant();
        Diagnostics.Add(
            $"profile '{profileName}' rejects tier {tier} feature: {feature}");
    }

    private static readonly MethodInfo s_writeLineString =
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!;
    private static readonly MethodInfo s_writeLineObject =
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(object) })!;
    private static readonly MethodInfo s_formatValue =
        typeof(ToshValueFormatter).GetMethod(
            nameof(ToshValueFormatter.Format),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_stringJoin =
        typeof(string).GetMethod(nameof(string.Join), new[] { typeof(string), typeof(string[]) })!;
    private static readonly MethodInfo s_objectToString =
        typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;
    private static readonly MethodInfo s_convertToInt32 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToInt64 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt64), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToDouble =
        typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(object) })!;
    private static readonly MethodInfo s_storageSizeFromBytes =
        typeof(StorageSize).GetMethod(
            nameof(StorageSize.FromBytes),
            new[] { typeof(long) })!;
    private static readonly MethodInfo s_quantityFromLiteral =
        typeof(Quantity).GetMethod(
            nameof(Quantity.FromLiteral),
            new[] { typeof(double), typeof(string) })!;

    private static readonly Type s_toshHost = typeof(global::Tosh.Compiler.Runtime.ToshHost);
    private static readonly MethodInfo s_hostInitialize =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.Initialize),
            new[] { typeof(global::Tosh.Runtime.ToshRuntime) })!;
    private static readonly MethodInfo s_hostEnterExecutionFrame =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.EnterExecutionFrame),
            new[] { typeof(string) })!;

    /// <summary>
    /// The recursion guard as a <c>Tosh.Runtime</c> primitive, used by the pure
    /// profile (<c>TS-P1-25</c>).
    /// </summary>
    /// <remarks>
    /// <c>ToshHost.EnterExecutionFrame</c> is a thin wrapper that supplies the
    /// session's configured limit before delegating here. A pure artifact has no
    /// host to read configuration from, so it guards at the documented default
    /// instead — the same ceiling, minus the ability to lower it per session.
    /// </remarks>
    private static readonly MethodInfo s_guardEnterExecutionFrame =
        typeof(global::Tosh.Runtime.ToshExecutionDepthGuard).GetMethod(
            nameof(global::Tosh.Runtime.ToshExecutionDepthGuard.Enter),
            new[]
            {
                typeof(int),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(global::Tosh.Runtime.TextSpan?),
            })!;
    private static readonly MethodInfo s_hostInvokeStatement =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeStatement),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostInvokeValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeValue),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostGetMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetMember),
            new[] { typeof(object), typeof(string), typeof(bool) })!;
    private static readonly MethodInfo s_hostSetMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SetMember),
            new[] { typeof(object), typeof(string), typeof(object), typeof(bool) })!;
    private static readonly MethodInfo s_hostGetIndex =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetIndex),
            new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_hostDestructureArray =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DestructureArray),
            new[] { typeof(object), typeof(int) })!;
    private static readonly MethodInfo s_hostDestructureRecord =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DestructureRecord),
            new[] { typeof(object), typeof(string[]) })!;
    private static readonly MethodInfo s_hostSetIndex =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SetIndex),
            new[] { typeof(object), typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_hostThrowValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ThrowValue),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostThrownValueOf =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ThrownValueOf),
            new[] { typeof(global::Tosh.Runtime.ThrowSignalException) })!;
    private static readonly MethodInfo s_hostCaughtValueOf =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.CaughtValueOf),
            new[] { typeof(global::System.Exception) })!;
    private static readonly MethodInfo s_hostSpreadArgs =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SpreadArgs),
            new[] { typeof(List<object?>), typeof(object) })!;
    private static readonly ConstructorInfo s_namedArgumentCtor =
        typeof(global::Tosh.Language.NamedArgument).GetConstructor(
            new[] { typeof(string), typeof(object) })!;
    private static readonly ConstructorInfo s_toshRangeCtor =
        typeof(global::Tosh.Runtime.ToshRange).GetConstructor(
            new[] { typeof(int), typeof(int?), typeof(int?) })!;
    private static readonly ConstructorInfo s_nullableInt32Ctor =
        typeof(int?).GetConstructor(new[] { typeof(int) })!;
    private static readonly MethodInfo s_listToArray =
        typeof(List<object?>).GetMethod(nameof(List<object?>.ToArray), Type.EmptyTypes)!;
    private static readonly MethodInfo s_hostEchoArgs =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.EchoArgs),
            new[] { typeof(object[]) })!;
    private static readonly MethodInfo s_hostRunUserFuncStage =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RunUserFuncStage),
            new[] { typeof(MethodInfo), typeof(int), typeof(IAsyncEnumerable<object?>), typeof(object[]) })!;
    private static readonly MethodInfo s_methodBaseGetFromHandle =
        typeof(MethodBase).GetMethod(nameof(MethodBase.GetMethodFromHandle),
            new[] { typeof(RuntimeMethodHandle) })!;
    private static readonly MethodInfo s_opMatches =
        typeof(global::Tosh.Runtime.OperatorEvaluator).GetMethod(
            nameof(global::Tosh.Runtime.OperatorEvaluator.Matches),
            new[] { typeof(object), typeof(string), typeof(object), typeof(bool) })!;
    private static readonly MethodInfo s_opAreEqual =
        typeof(global::Tosh.Runtime.OperatorEvaluator).GetMethod(
            nameof(global::Tosh.Runtime.OperatorEvaluator.AreEqual),
            new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_opToBoolean =
        typeof(global::Tosh.Runtime.OperatorEvaluator).GetMethod(
            nameof(global::Tosh.Runtime.OperatorEvaluator.ToBoolean),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_opEvaluateBinaryWithDiagnostics =
        typeof(global::Tosh.Runtime.OperatorEvaluator).GetMethod(
            nameof(global::Tosh.Runtime.OperatorEvaluator.EvaluateBinaryWithDiagnostics),
            new[]
            {
                typeof(object),
                typeof(string),
                typeof(object),
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(int),
            })!;
    private static readonly MethodInfo s_opEvaluateUnary =
        typeof(global::Tosh.Runtime.OperatorEvaluator).GetMethod(
            nameof(global::Tosh.Runtime.OperatorEvaluator.EvaluateUnary),
            new[] { typeof(string), typeof(object) })!;
    private static readonly MethodInfo s_hostToEnumerable =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ToEnumerable),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostResolveCommand =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ResolveCommand),
            new[] { typeof(string) })!;
    private static readonly MethodInfo s_hostEmptyInput =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.EmptyInput),
            Type.EmptyTypes)!;
    private static readonly MethodInfo s_hostRunStage =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RunStage),
            new[] {
                typeof(global::Tosh.Runtime.IShellCommand),
                typeof(IAsyncEnumerable<object?>),
                typeof(object[]),
            })!;
    private static readonly MethodInfo s_hostDrainStatement =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DrainStatement),
            new[] { typeof(IAsyncEnumerable<object?>) })!;
    private static readonly MethodInfo s_hostDrainSubexpressionValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DrainSubexpressionValue),
            new[] { typeof(IAsyncEnumerable<object?>) })!;
    private static readonly MethodInfo s_hostDrainValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DrainValue),
            new[] { typeof(IAsyncEnumerable<object?>) })!;
    private static readonly MethodInfo s_hostRegisterSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterSource),
            new[] { typeof(string), typeof(string) })!;
    private static readonly MethodInfo s_hostMakeBlock =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeBlock),
            new[] { typeof(int), typeof(int), typeof(Dictionary<string, object?>) })!;
    private static readonly MethodInfo s_hostSeedFromSpread =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SeedFromSpread),
            BindingFlags.Public | BindingFlags.Static)!;

    private static readonly MethodInfo s_hostSeedFromValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SeedFromValue),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostCheckType =
        s_toshHost.GetMethod(
            nameof(global::Tosh.Compiler.Runtime.ToshHost.CheckType),
            new[] { typeof(object), typeof(string), typeof(int), typeof(int), typeof(string) })!;
    private static readonly MethodInfo s_hostCheckTypeAtSource =
        s_toshHost.GetMethod(
            nameof(global::Tosh.Compiler.Runtime.ToshHost.CheckTypeAtSource),
            new[]
            {
                typeof(object),
                typeof(string),
                typeof(int),
                typeof(int),
                typeof(string),
                typeof(string),
                typeof(string),
            })!;

    private static readonly MethodInfo s_hostNormalizePackedArguments =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.NormalizePackedArguments),
            new[] { typeof(object?[]), typeof(string[]), typeof(bool) })!;
    private static readonly MethodInfo s_hostRegisterTypeFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterTypeFromSource),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRegisterDeclarationFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterDeclarationFromSource),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRegisterModuleFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterModuleFromSource),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRegisterCompiledTypeAlias =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterCompiledTypeAlias),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRegisterRuneFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterRuneFromSource),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRequireModule =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RequireModule),
            new[] { typeof(string), typeof(string[]), typeof(string[]) })!;
    private static readonly MethodInfo s_hostRegisterCompiledAssembly =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterCompiledAssembly),
            new[] { typeof(System.Reflection.Assembly) })!;
    private static readonly MethodInfo s_runtimeTypeHandle_GetTypeFromHandle =
        typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle),
            new[] { typeof(RuntimeTypeHandle) })!;
    private static readonly MethodInfo s_type_get_Assembly =
        typeof(Type).GetProperty(nameof(Type.Assembly))!.GetGetMethod()!;
    private static readonly MethodInfo s_hostRunSubcommandScript =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RunSubcommandScript),
            new[] { typeof(string[]) })!;
    private static readonly MethodInfo s_hostRunScriptFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RunScriptFromSource),
            new[] { typeof(string[]) })!;
    private static readonly MethodInfo s_hostResolveQualifiedAccess =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ResolveQualifiedAccess),
            new[] { typeof(string) })!;
    private static readonly MethodInfo s_hostInvokeQualifiedMethod =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeQualifiedMethod),
            new[] { typeof(string), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostNewObject =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.NewObject),
            new[] { typeof(string), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostNewObjectGeneric =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.NewObject),
            new[] { typeof(string), typeof(string), typeof(string[]), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostInvokeMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeMember),
            new[] { typeof(object), typeof(string), typeof(object?[]), typeof(bool) })!;

    private static readonly MethodInfo s_hostInvokeCallable =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeCallable),
            new[] { typeof(object), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostInvokeUserOverload =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeUserOverload),
            new[] { typeof(MethodInfo[]), typeof(object[]) })!;
    private static readonly MethodInfo s_hostBeginRedirection =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.BeginRedirection),
            new[] { typeof(int[]), typeof(int[]), typeof(string[]), typeof(string) })!;
    private static readonly MethodInfo s_hostAsRedirectionPath =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.AsRedirectionPath),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostIsTruthy =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.IsTruthy),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostThrowAsException =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ThrowAsException),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostMakeFunctionReference =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeFunctionReference),
            new[] { typeof(string) })!;
    private static readonly MethodInfo s_hostMakeFunctionReferenceFromMethod =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeFunctionReferenceFromMethod),
            new[] { typeof(MethodInfo), typeof(string) })!;
    private static readonly MethodInfo s_hostMakeFunctionReferenceFromMethods =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeFunctionReferenceFromMethods),
            new[] { typeof(MethodInfo[]), typeof(string) })!;
    private static readonly MethodInfo s_hostMakeMemberProjection =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeMemberProjection),
            new[] { typeof(string[]) })!;
    private static readonly MethodInfo s_hostToArray =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ToArray),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostIndexOrNull =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.IndexOrNull),
            new[] { typeof(object[]), typeof(int) })!;
    private static readonly MethodInfo s_hostSpreadRecord =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SpreadRecord),
            new[] { typeof(IDictionary<string, object?>), typeof(object) })!;

    private static readonly Type s_listOfObject = typeof(List<object?>);
    private static readonly ConstructorInfo s_listCtor =
        s_listOfObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_listAdd =
        s_listOfObject.GetMethod(nameof(List<object?>.Add))!;
    private static readonly MethodInfo s_listAddRange =
        s_listOfObject.GetMethod(nameof(List<object?>.AddRange))!;
    private static readonly MethodInfo s_delegateCombine =
        typeof(Delegate).GetMethod(nameof(Delegate.Combine),
            new[] { typeof(Delegate), typeof(Delegate) })!;
    private static readonly MethodInfo s_delegateRemove =
        typeof(Delegate).GetMethod(nameof(Delegate.Remove),
            new[] { typeof(Delegate), typeof(Delegate) })!;

    // ── Compiled subcommand dispatch support ────────────────────────────
    private static readonly Type s_compiledSubcommandParamType =
        typeof(global::Tosh.Compiler.Runtime.CompiledSubcommandParam);
    private static readonly Type s_compiledSubcommandNodeType =
        typeof(global::Tosh.Compiler.Runtime.CompiledSubcommandNode);
    private static readonly Type s_actionOfObjArray =
        typeof(Action<object?[]>);
    private static readonly ConstructorInfo s_actionOfObjArrayCtor =
        typeof(Action<object?[]>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })!;
    private static readonly MethodInfo s_hostMakeSubcommandParam =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeSubcommandParam))!;
    private static readonly MethodInfo s_hostMakeSubcommandNode =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeSubcommandNode))!;
    private static readonly MethodInfo s_hostRunCompiledSubcommandDispatch =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RunCompiledSubcommandDispatch))!;

    // ── Compiled-block support ──────────────────────────────────────────
    private static readonly MethodInfo s_hostInvokeCollect =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeCollect),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostMakeCompiledBlock =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeCompiledBlock))!;
    private static readonly Type s_funcBlockBodyType =
        typeof(Func<,,>).MakeGenericType(typeof(object), typeof(object[]), typeof(List<object>));
    private static readonly ConstructorInfo s_funcBlockBodyCtor =
        typeof(Func<,,>).MakeGenericType(typeof(object), typeof(object[]), typeof(List<object>))
            .GetConstructor(new[] { typeof(object), typeof(IntPtr) })!;

    // ── Compiled-lambda support ─────────────────────────────────────────
    private static readonly MethodInfo s_hostMakeCompiledLambda =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeCompiledLambda))!;
    private static readonly FieldInfo s_compiledLambdaMissingArgument =
        typeof(global::Tosh.Runtime.CompiledLambdaCallable).GetField(
            nameof(global::Tosh.Runtime.CompiledLambdaCallable.MissingArgument))!;
    private static readonly Type s_funcLambdaBodyType =
        typeof(Func<,,>).MakeGenericType(typeof(object[]), typeof(object[]), typeof(List<object>));
    private static readonly ConstructorInfo s_funcLambdaBodyCtor =
        typeof(Func<,,>).MakeGenericType(typeof(object[]), typeof(object[]), typeof(List<object>))
            .GetConstructor(new[] { typeof(object), typeof(IntPtr) })!;

    private static readonly Type s_dictOfStringObject = typeof(Dictionary<string, object?>);
    private static readonly ConstructorInfo s_dictCtor =
        s_dictOfStringObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictSetItem =
        s_dictOfStringObject.GetMethod("set_Item", new[] { typeof(string), typeof(object) })!;

    /// <summary>
    /// A record literal is an <see cref="System.Dynamic.ExpandoObject"/> — `TOAST-0045`.
    /// </summary>
    /// <remarks>
    /// The interpreter builds one, and the shell type of an `ExpandoObject` is `record`
    /// while the shell type of a `Dictionary&lt;string, object?&gt;` is `dict`. The emitter
    /// built the dictionary, so `{| a = 1 |}` was a record interpreted and a dict compiled —
    /// which `func f() -> record` then refused, from a function returning a record literal.
    /// </remarks>
    private static readonly ConstructorInfo s_expandoCtor =
        typeof(System.Dynamic.ExpandoObject).GetConstructor(Type.EmptyTypes)!;

    private static readonly MethodInfo s_expandoSetItem =
        typeof(IDictionary<string, object?>).GetMethod("set_Item", new[] { typeof(string), typeof(object) })!;

    private static readonly Type s_dictOfObjectObject = typeof(Dictionary<object, object?>);
    private static readonly ConstructorInfo s_dictObjCtor =
        s_dictOfObjectObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictObjSetItem =
        s_dictOfObjectObject.GetMethod("set_Item", new[] { typeof(object), typeof(object) })!;
    private static readonly Type s_hashSetOfObject = typeof(HashSet<object?>);
    private static readonly ConstructorInfo s_hashSetCtor =
        s_hashSetOfObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_hashSetAdd =
        s_hashSetOfObject.GetMethod(nameof(HashSet<object?>.Add), new[] { typeof(object) })!;
    private static readonly Type s_toshTupleType = typeof(global::Tosh.Runtime.ToshTuple);
    private static readonly ConstructorInfo s_toshTupleCtor =
        s_toshTupleType.GetConstructor(new[] { typeof(IEnumerable<object?>) })!;

    private static readonly Type s_enumerableOfObject = typeof(IEnumerable<object?>);
    private static readonly MethodInfo s_enumerableGetEnumerator =
        s_enumerableOfObject.GetMethod(nameof(IEnumerable<object?>.GetEnumerator), Type.EmptyTypes)!;
    private static readonly MethodInfo s_enumeratorMoveNext =
        typeof(System.Collections.IEnumerator).GetMethod(nameof(System.Collections.IEnumerator.MoveNext), Type.EmptyTypes)!;
    private static readonly MethodInfo s_enumeratorOfObjectGetCurrent =
        typeof(IEnumerator<object?>).GetProperty(nameof(IEnumerator<object?>.Current))!.GetGetMethod()!;
    private static readonly MethodInfo s_disposableDispose =
        typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes)!;

    public EmitterImpl(BoundUnit unit, string assemblyName, CompileProfile profile)
        : this(unit, assemblyName, profile, referenceAssembly: false, compilationSources: null) { }

    public EmitterImpl(BoundUnit unit, string assemblyName, CompileProfile profile, bool referenceAssembly)
        : this(unit, assemblyName, profile, referenceAssembly, compilationSources: null) { }

    public EmitterImpl(
        BoundUnit unit,
        string assemblyName,
        CompileProfile profile,
        bool referenceAssembly,
        IReadOnlyList<string>? compilationSources)
    {
        _unit = unit;
        _assemblyName = assemblyName;
        _profile = profile;
        _referenceAssembly = referenceAssembly;
        _compilationSiblingKeys = BuildCompilationSiblingKeys(compilationSources);
        var metadataContext = referenceAssembly
            ? CreateMetadataLoadContext()
            : (Context: (MetadataLoadContext?)null, CoreAssembly: typeof(object).Assembly, Assemblies: Array.Empty<Assembly>());
        _metadataLoadContext = metadataContext.Context;
        _metadataAssemblies = metadataContext.Assemblies;
        var coreAssembly = metadataContext.CoreAssembly;
        _ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), coreAssembly);

        // Stamp the public CLR ABI version. Cross-language consumers
        // (and tooling) read this via reflection to decide which
        // contract promises they can rely on. v1 is documented in
        // docs/CLR_ABI_v1.md; bumping this constant is a breaking
        // change and requires a corresponding spec revision.
        var toshAbiCtor = typeof(global::Tosh.Runtime.ToshAbiAttribute)
            .GetConstructor(new[] { typeof(int) })!;
        _ab.SetCustomAttribute(new CustomAttributeBuilder(
            toshAbiCtor,
            new object[] { ToshClrAbiVersion }));

        if (referenceAssembly)
        {
            // Stamp [assembly: ReferenceAssembly] so the C# / F#
            // compiler treats this as a metadata-only reference.
            // Bodies remain populated (fat refasm) — equivalent to
            // what F# emitted for years before refasm support
            // landed; downstream consumers ignore method IL when
            // resolving symbols.
            var refAsmCtor = ResolveReferenceAssemblyAttributeConstructor(coreAssembly);
            _ab.SetCustomAttribute(new CustomAttributeBuilder(refAsmCtor, Array.Empty<object>()));
        }
        _moduleBuilder = _ab.DefineDynamicModule("MainModule");
        _program = _moduleBuilder.DefineType(
            $"{assemblyName}.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract,
            MetadataType(typeof(object)));

        _main = _program.DefineMethod(
            "Main",
            MethodAttributes.Public | MethodAttributes.Static,
            MetadataType(typeof(void)),
            MetadataTypes(typeof(string[])));

        _il = _main.GetILGenerator();

        // Initialize the symbol document so sequence points can be
        // attached as we lay out IL. SourceName may be a synthetic
        // identifier for unit tests / REPL fragments — that's fine,
        // PDB consumers tolerate non-filesystem paths and a debugger
        // simply won't be able to step through them. The language
        // GUID is the well-known Roslyn "Tosh" reservation; if/when
        // a tosh-specific GUID is registered with the debugger
        // ecosystem this can be swapped without changing call sites.
        var sourceName = ((ParseResult)unit.ParseResult).SourceName;
        if (!string.IsNullOrEmpty(sourceName))
        {
            // Use Guid.Empty for the language so debuggers fall back
            // to text rendering rather than misidentifying the file
            // as a known language.
            _doc = _moduleBuilder.DefineDocument(sourceName, Guid.Empty);
        }
    }

    public void Dispose()
    {
        _metadataLoadContext?.Dispose();
    }

    private static (MetadataLoadContext? Context, Assembly CoreAssembly, Assembly[] Assemblies) CreateMetadataLoadContext()
    {
        var referenceAssemblyDirectory = ResolveReferenceAssemblyDirectory();
        if (referenceAssemblyDirectory is null)
        {
            return (null, typeof(object).Assembly, Array.Empty<Assembly>());
        }

        var paths = Directory.GetFiles(referenceAssemblyDirectory, "*.dll");
        var resolver = new PathAssemblyResolver(paths);
        var context = new MetadataLoadContext(resolver, "System.Runtime");
        try
        {
            var assemblies = new List<Assembly>();
            foreach (var path in paths)
            {
                try
                {
                    assemblies.Add(context.LoadFromAssemblyPath(path));
                }
                catch
                {
                    // Ignore malformed or unsupported files; the resolver can
                    // still load dependencies on demand when another assembly needs them.
                }
            }

            var coreAssembly = context.CoreAssembly
                ?? context.LoadFromAssemblyName("System.Runtime");
            if (!assemblies.Contains(coreAssembly))
            {
                assemblies.Insert(0, coreAssembly);
            }
            return (context, coreAssembly, assemblies.ToArray());
        }
        catch
        {
            context.Dispose();
            return (null, typeof(object).Assembly, Array.Empty<Assembly>());
        }
    }

    private static ConstructorInfo ResolveReferenceAssemblyAttributeConstructor(Assembly coreAssembly)
    {
        return coreAssembly
            .GetType("System.Runtime.CompilerServices.ReferenceAssemblyAttribute", throwOnError: false)
            ?.GetConstructor(Type.EmptyTypes)
            ?? typeof(System.Runtime.CompilerServices.ReferenceAssemblyAttribute)
                .GetConstructor(Type.EmptyTypes)!;
    }

    private static string? ResolveReferenceAssemblyDirectory()
    {
        var coreLibraryPath = typeof(object).Assembly.Location;
        if (string.IsNullOrEmpty(coreLibraryPath)) return null;

        var runtimeDirectory = Directory.GetParent(coreLibraryPath);
        var runtimePackDirectory = runtimeDirectory?.Parent;
        var sharedDirectory = runtimePackDirectory?.Parent;
        var dotnetRoot = sharedDirectory?.Parent;
        if (runtimeDirectory is null || dotnetRoot is null) return null;

        var runtimeVersion = runtimeDirectory.Name;
        var targetFramework = $"net{Environment.Version.Major}.0";
        var exact = Path.Combine(
            dotnetRoot.FullName,
            "packs",
            "Microsoft.NETCore.App.Ref",
            runtimeVersion,
            "ref",
            targetFramework);
        if (Directory.Exists(exact)) return exact;

        var refPackRoot = Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(refPackRoot)) return null;
        return Directory.GetDirectories(refPackRoot)
            .Select(versionDirectory => Path.Combine(versionDirectory, "ref", targetFramework))
            .Where(Directory.Exists)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private Type MetadataType(Type type)
    {
        if (_metadataLoadContext is null) return type;

        if (type.IsArray)
        {
            var element = MetadataType(type.GetElementType()!);
            return type.GetArrayRank() == 1
                ? element.MakeArrayType()
                : element.MakeArrayType(type.GetArrayRank());
        }
        if (type.IsByRef) return MetadataType(type.GetElementType()!).MakeByRefType();
        if (type.IsPointer) return MetadataType(type.GetElementType()!).MakePointerType();
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var definition = MetadataType(type.GetGenericTypeDefinition());
            var args = type.GetGenericArguments().Select(MetadataType).ToArray();
            return definition.MakeGenericType(args);
        }

        if (type.FullName is null) return type;
        if (_metadataTypeCache.TryGetValue(type.FullName, out var cached))
        {
            return cached ?? type;
        }

        foreach (var assembly in _metadataAssemblies)
        {
            var candidate = assembly.GetType(type.FullName, throwOnError: false, ignoreCase: false);
            if (candidate is not null)
            {
                _metadataTypeCache[type.FullName] = candidate;
                return candidate;
            }
        }

        _metadataTypeCache[type.FullName] = null;
        return type;
    }

    private Type[] MetadataTypes(params Type[] types)
    {
        var result = new Type[types.Length];
        for (var i = 0; i < types.Length; i++)
        {
            result[i] = MetadataType(types[i]);
        }
        return result;
    }

    /// <summary>
    /// Convert a source-text offset into 1-based (line, column) for
    /// PDB sequence points. Builds a sorted line-start cache the
    /// first time it's called.
    /// </summary>
    private (int Line, int Column) GetLineColumn(int offset)
    {
        var src = ((ParseResult)_unit.ParseResult).SourceText;
        if (_lineStarts is null)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i] == '\n') starts.Add(i + 1);
            }
            _lineStarts = starts.ToArray();
        }

        // Clamp offset into source range so out-of-bounds spans
        // (e.g. emitter-synthesized end-of-file points) don't trip
        // the binary search.
        if (offset < 0) offset = 0;
        if (offset > src.Length) offset = src.Length;

        int idx = Array.BinarySearch(_lineStarts, offset);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) idx = 0;
        return (idx + 1, offset - _lineStarts[idx] + 1);
    }

    /// <summary>
    /// Emit a PDB sequence point covering <paramref name="span"/>
    /// at the current IL offset. No-op when no document is active
    /// (synthetic units) or when the span is empty / out-of-range.
    /// Spans crossing line boundaries are emitted faithfully so the
    /// debugger highlights the exact statement extent.
    /// </summary>
    private void MarkSeqPoint(TextSpan span)
    {
        if (_doc is null) return;
        if (span.Length <= 0) return;
        var (startLine, startCol) = GetLineColumn(span.Start);
        var (endLine, endCol) = GetLineColumn(span.Start + span.Length);
        // PDB requires endLine > startLine OR endCol > startCol on
        // the same line. Sanitize degenerate spans by widening end
        // to the line's next column.
        if (endLine == startLine && endCol <= startCol) endCol = startCol + 1;
        _il.MarkSequencePoint(_doc, startLine, startCol, endLine, endCol);
    }

    public void Run()
    {
        // Pre-pass 0: detect name-mangling collisions between
        // distinct tosh identifiers that map to the same CLR
        // identifier (e.g. `foo-bar` + `foo_bar` → `foo_bar`). Done
        // before any DefineType / DefineMethod call so we surface a
        // clean tosh.compile.name_mangling_collision diagnostic
        // instead of the inscrutable "duplicate type" / "duplicate
        // method" runtime exception from the metadata builder.
        DetectNameManglingCollisions();

        // Pre-pass A: collect every captured top-level symbol across
        // all nested function definitions and promote each to a
        // static field. Done before the function MethodBuilders are
        // declared so the user-function bodies can reach the field
        // map directly.
        PromoteCapturedSymbols();

        // Pre-pass A2: promote every script-input parameter (flag /
        // arg) at all subcommand levels to a static field so that
        // compiled body methods can read them via ldsfld after the
        // dispatch runtime has called the body with the bindings
        // array.  Cross-subcommand references (e.g. a child body
        // reading a parent flag) work naturally because the parent
        // body sets the static field before the child runs.
        if (ProgramHasSubcommandDispatch() && CanCompileSubcommandDispatch())
            PromoteSubcommandInputsAsStaticFields(_unit.Root.Statements);

        // Pre-pass B0: count overloads per function name so DeclareUserFunction
        // can give each overload a distinct CLR method name when there are
        // multiple definitions for the same name.
        _topLevelFunctionOverloadCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundFunctionDefinition fn0)
            {
                _topLevelFunctionOverloadCounts.TryGetValue(fn0.Name, out var c);
                _topLevelFunctionOverloadCounts[fn0.Name] = c + 1;
            }
        }

        // Pre-pass B: declare a MethodBuilder for every top-level
        // function definition so call sites can resolve them even
        // when the call appears textually before the definition.
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundFunctionDefinition func)
            {
                if (FunctionNeedsSourceReplay(func)) continue;
                DeclareUserFunction(func);
            }
        }

        // Pre-pass C: declare CLR shells for every top-level module.
        // Each module becomes a `public static partial class` (sealed
        // + abstract) so external .NET callers can reach the module's
        // members via ordinary reflection / static-member access. Body
        // walking accumulates static fields for `var` declarations and
        // `MethodBuilder`s for `func` declarations; method bodies are
        // emitted in a second pass after the main IL is laid out.
        // Source-replay registration still happens below so the engine
        // view stays in sync (compiled tosh code calling into modules
        // routes through the engine; only external CLR callers see
        // the static-class shell).
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundModuleDefinition mod)
            {
                DeclareClrModuleShell(mod, parent: null, qualifiedName: mod.Name);
            }
        }

        // Pre-pass D: declare CLR shells for every "simple" top-level
        // class / record. The shell is a real CLR `public sealed class`
        // with public mutable fields for each storage property /
        // record field and a constructor matching the primary ctor /
        // record fields. Method bodies and complex initializers are
        // intentionally not lowered yet — source-replay still handles
        // the dynamic semantics. The shell exists so external .NET
        // callers can reflect over compiled tosh types.
        foreach (var statement in _unit.Root.Statements)
        {
            switch (statement)
            {
                case BoundEnumDefinition en when CanEmitClrEnumType(en):
                    DeclareClrEnumType(en);
                    break;
                case BoundEnumDefinition enStatic when CanEmitClrEnumStaticShell(enStatic):
                    DeclareClrEnumStaticShell(enStatic);
                    break;
                case BoundClassDefinition cls when CanEmitClrClassShell(cls):
                    DeclareClrClassShell(cls);
                    break;
                case BoundRecordDefinition rec when CanEmitClrRecordShell(rec):
                    DeclareClrRecordShell(rec);
                    break;
                case BoundInterfaceDefinition iface:
                    DeclareClrInterfaceShell(iface);
                    break;
                case BoundStructDefinition st when CanEmitClrStructShell(st):
                    DeclareClrStructShell(st);
                    break;
                case BoundEventDefinition ev:
                    DeclareClrEventTypeShell(ev);
                    break;
                case BoundUnionDefinition un:
                    DeclareClrUnionShell(un);
                    break;
                case BoundTraitDefinition trait:
                    DeclareClrTraitShell(trait);
                    break;
                case BoundTypeAliasStatement ta when CanEmitClrAliasShell(ta):
                    DeclareClrAliasShell(ta);
                    break;
            }
        }

        // Pre-pass D2: declare CLR shells for simple class declarations
        // nested inside `module` bodies. They become top-level CLR types
        // (one per declaration) and remain reachable from the module via
        // the existing source-registration path so dynamic call sites
        // keep working. This is what lifts module-nested classes out of
        // Tier-3 source replay (first-class .NET plan, step 1).
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundModuleDefinition mod)
            {
                DeclareClrShellsInsideModule(mod);
            }
        }

        // Pre-pass E: lift `bind native "lib" as Module { ... }`
        // statements with primitive-typed signatures into real CLR
        // static classes carrying [DllImport] P/Invoke methods.
        // Eliminates Tier-3 source replay for the most common shape
        // of native bindings (first-class .NET plan, step 7 phase 1).
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is not BoundBindStatement bind) continue;

            if (CanEmitNativeBindShell(bind))
            {
                DeclareNativeBindShell(bind);
            }
            else
            {
                // Say *why* this one stayed Tier 3 when the reason is a real
                // error rather than an unlifted shape.
                ReportInvalidNativeBindSignatures(bind);
            }
        }

        // Main prologue: wire up the ambient ToshRuntime once so any
        // builtin command dispatched through ToshHost has a runtime
        // available. Idempotent on the host side.
        //
        // Omitted under the pure profile (TS-P1-25): its whole purpose is to
        // give ToshHost a runtime, and a pure artifact dispatches nothing
        // through ToshHost — builtin dispatch is a tier-2 feature the profile
        // already rejects, so the bootstrap would initialise a host that is
        // never called while forcing the reference that makes the artifact
        // impure.
        if (_profile != CompileProfile.Pure)
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Call, s_hostInitialize);
        }

        // Phase 2: register the source text + name so that block
        // arguments embedded in pipeline stages can be re-bound to
        // the original BlockSyntax by span at runtime. Skipped when
        // the program contains no block expressions — leaves the
        // emitted IL byte-for-byte identical to Phase 1 in that case.
        var hasSubcommandDispatch = ProgramHasSubcommandDispatch();
        var subcommandNeedsReplay = hasSubcommandDispatch && !CanCompileSubcommandDispatch();
        var wholeScriptNeedsReplay = ProgramNeedsWholeScriptReplay();
        if (ProgramHasBlockExpressionsNeedingReplay()
            || ProgramHasTypeDefinitionsNeedingReplay()
            || ProgramHasModuleDefinitionsNeedingReplay()
            || ProgramHasTopLevelDeclarationsNeedingReplay()
            || ProgramHasCompiledAliasRegistration()
            || ProgramHasRuneDefinitionsForTier2()
            || ProgramHasUnsatisfiedNonNativeRequires()
            || wholeScriptNeedsReplay
            || subcommandNeedsReplay)
        {
            _il.Emit(OpCodes.Ldstr, ((ParseResult)_unit.ParseResult).SourceText);
            _il.Emit(OpCodes.Ldstr, ((ParseResult)_unit.ParseResult).SourceName);
            _il.Emit(OpCodes.Call, s_hostRegisterSource);
        }

        // Register compiled type aliases (including refinement aliases)
        // without replaying executable source. The host parses just the
        // alias slice and inserts the runtime alias definition directly.
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundTypeAliasStatement ta
                && _clrAliasTypes.Contains(ta.Name)
                && (ta.Refinement is not null || ta.TypeParameters.Count > 0))
            {
                var (start, length) = ExtendTypeDefinitionSpan(ta.Span);
                _il.Emit(OpCodes.Ldc_I4, start);
                _il.Emit(OpCodes.Ldc_I4, length);
                _il.Emit(OpCodes.Call, s_hostRegisterCompiledTypeAlias);
            }
        }

        // Register every user-defined type that still needs source
        // replay (struct / enum / union / interface / alias, and
        // non-shell class/record forms) by replaying its source
        // slice through the engine. CLR-shell-emitted class/record
        // declarations intentionally skip this: construction and
        // member dispatch resolve through CLR metadata at runtime.
        foreach (var statement in _unit.Root.Statements)
        {
            if (!wholeScriptNeedsReplay && TypeDefinitionNeedsSourceReplay(statement, out var span))
            {
                RequireTier(3, $"user-defined type ({statement.GetType().Name})");
                var (start, length) = ExtendTypeDefinitionSpan(span);
                _il.Emit(OpCodes.Ldc_I4, start);
                _il.Emit(OpCodes.Ldc_I4, length);
                _il.Emit(OpCodes.Call, s_hostRegisterTypeFromSource);
            }
        }

        // Register top-level declarations whose behavior is still
        // interpreter-owned. This keeps the permissive profile able
        // to build the full language surface while runtime/pure
        // profiles continue to reject the Tier 3 replay dependency.
        foreach (var statement in _unit.Root.Statements)
        {
            if (!wholeScriptNeedsReplay && TopLevelDeclarationNeedsSourceReplay(statement, out var span))
            {
                RequireTier(3, $"top-level declaration ({statement.GetType().Name})");
                var (start, length) = ExtendTypeDefinitionSpan(span);
                _il.Emit(OpCodes.Ldc_I4, start);
                _il.Emit(OpCodes.Ldc_I4, length);
                _il.Emit(OpCodes.Call, s_hostRegisterDeclarationFromSource);
            }
        }

        // Register rune (macro) definitions via direct parsing — Tier 2.
        // Each rune's source slice is parsed synchronously into a RuneDefinition
        // and declared via ToshHost, avoiding full interpreter evaluation.
        foreach (var statement in _unit.Root.Statements)
        {
            if (!wholeScriptNeedsReplay && statement is BoundRuneDefinition rune)
            {
                RequireTier(2, "rune definition (runtime registration)");
                _il.Emit(OpCodes.Ldc_I4, rune.Span.Start);
                _il.Emit(OpCodes.Ldc_I4, rune.Span.Length);
                _il.Emit(OpCodes.Call, s_hostRegisterRuneFromSource);
            }
        }

        // Register unsatisfied require statements via the Tier-2 RequireModule bridge.
        // Native requires remain Tier 3 (bind blocks are not yet CLR-emittable).
        foreach (var statement in _unit.Root.Statements)
        {
            if (!wholeScriptNeedsReplay &&
                statement is BoundRequireStatement require &&
                !require.IsNative &&
                !RequireTargetIsSatisfiedAtBuildTime(require))
            {
                RequireTier(2, "require statement (runtime module load)");
                // Emit: ToshHost.RequireModule(target, importedNames[], importedAliases[])
                _il.Emit(OpCodes.Ldstr, require.Target);

                // importedNames[]
                _il.Emit(OpCodes.Ldc_I4, require.Imports.Count);
                _il.Emit(OpCodes.Newarr, typeof(string));
                for (var i = 0; i < require.Imports.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    _il.Emit(OpCodes.Ldstr, require.Imports[i].Name);
                    _il.Emit(OpCodes.Stelem_Ref);
                }

                // importedAliases[]
                _il.Emit(OpCodes.Ldc_I4, require.Imports.Count);
                _il.Emit(OpCodes.Newarr, typeof(string));
                for (var i = 0; i < require.Imports.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    _il.Emit(OpCodes.Ldstr, require.Imports[i].Alias ?? "");
                    _il.Emit(OpCodes.Stelem_Ref);
                }

                _il.Emit(OpCodes.Call, s_hostRequireModule);
            }
        }

        // Register top-level modules that still need source replay (modules whose bodies
        // contain declarations the CLR shell cannot represent natively). Pure modules —
        // those whose bodies consist only of vars, CLR-emittable funcs, and pure nested
        // modules — are *not* replayed here: their static types are already compiled into
        // the assembly and are discovered by the runtime via CLR reflection. Each module
        // (whether replayed or not) also emits a [ToshModule] assembly attribute so that
        // external tooling can enumerate modules via reflection.
        //
        // Register the compiled assembly with ToshHost up front so host-backed
        // resolution paths (module static access and host NewObject fallback)
        // resolve CLR shells from this assembly first in long-running processes.
        // Omitted under the pure profile, which promises an artifact carrying no
        // reference to Tosh.Compiler.Runtime (TS-P1-25). The registration only
        // serves host-backed resolution — module static access and the host
        // NewObject fallback — and a pure artifact reaches neither, since those
        // shapes are already rejected as tier violations before emission.
        if (_profile != CompileProfile.Pure)
        {
            // typeof(Program).Assembly
            _il.Emit(OpCodes.Ldtoken, _program);
            _il.Emit(OpCodes.Call, s_runtimeTypeHandle_GetTypeFromHandle);
            _il.Emit(OpCodes.Callvirt, s_type_get_Assembly);
            _il.Emit(OpCodes.Call, s_hostRegisterCompiledAssembly);
        }

        var hasModuleDefinitions = _unit.Root.Statements.OfType<BoundModuleDefinition>().Any();
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundModuleDefinition mod)
            {
                if (!wholeScriptNeedsReplay && ModuleNeedsSourceReplay(mod))
                {
                    RequireTier(3, $"module body with non-trivial declarations ({mod.Name})");
                    var (start, length) = ExtendTypeDefinitionSpan(mod.Span);
                    _il.Emit(OpCodes.Ldc_I4, start);
                    _il.Emit(OpCodes.Ldc_I4, length);
                    _il.Emit(OpCodes.Call, s_hostRegisterModuleFromSource);
                }
                EmitToshModuleAttributes(mod, parentPath: null);
            }
        }

        // Main pass: top-level executable statements go into one
        // defer-aware lexical block. Function definitions are emitted into
        // their own MethodBuilders and omitted from that block.
        ReturnEmissionFrame? mainReturnFrame = null;
        if (hasSubcommandDispatch)
        {
            if (CanCompileSubcommandDispatch())
            {
                // Compiled dispatch: emit a static body method per subcommand,
                // build the CompiledSubcommandNode tree in Main, and call
                // ToshHost.RunCompiledSubcommandDispatch — no source replay.
                EmitCompiledSubcommandDispatch();
            }
            else
            {
                // Subcommand-dispatch shortcut: argv parsing, nested
                // flag/arg binding, eager/hollow/vital semantics, and
                // auto-help are intricate enough that re-implementing
                // them in IL would dwarf the rest of the emitter.
                // Instead the compiled `Main` forwards args to
                // `ToshHost.RunSubcommandScript`, which sets
                // `runtime.InvocationArguments` and replays the
                // already-registered source through the engine. Other
                // top-level statements (functions, modules, classes)
                // are *also* part of that source, so they execute via
                // the replay too — emitting them into Main as well
                // would double-evaluate them. This is Tier 3, so the
                // `pure`/`runtime` profiles correctly reject it; the
                // permissive default succeeds.
                RequireTier(3, "subcommand-tree dispatch (argv-driven entry point)");
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Call, s_hostRunSubcommandScript);
            }
        }
        else if (wholeScriptNeedsReplay)
        {
            // Runes are expansion-oriented: invoking a rune is an engine
            // rewrite/evaluation step, not a regular command dispatch. Until
            // the compiler has a rune-expansion model, permissive builds replay
            // the whole script and stricter profiles reject the Tier 3 fallback.
            RequireTier(3, "whole-script replay (rune expansion)");
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Call, s_hostRunScriptFromSource);
        }
        else
        {
            var executableStatements = new List<BoundStatement>();
            foreach (var statement in _unit.Root.Statements)
            {
                if (statement is BoundFunctionDefinition func)
                {
                    if (FunctionNeedsSourceReplay(func))
                    {
                        // Registered in the source-replay prologue.
                        continue;
                    }
                    EmitUserFunctionBody(func);
                    continue;
                }
                if (TopLevelDeclarationNeedsSourceReplay(statement, out _))
                {
                    // Registered in the source-replay prologue.
                    continue;
                }
                if (statement is BoundRuneDefinition)
                {
                    // Registered in the Tier-2 rune prologue.
                    continue;
                }
                if (statement is BoundRequireStatement)
                {
                    // Require statements are either build-time-satisfied (sibling
                    // compilation), or registered via the Tier-2 RequireModule
                    // prologue call; either way they have no remaining effect here.
                    continue;
                }
                if (statement is BoundBindStatement bindStmt && _clrNativeBinds.Contains(bindStmt))
                {
                    // First-class .NET plan, step 7 phase 1: bind
                    // statements lifted into a real CLR P/Invoke
                    // class have no remaining runtime effect — the
                    // metadata is fully present on the type itself.
                    continue;
                }
                if (IsTypeDefinitionStatement(statement, out _))
                {
                    // Already registered in the prologue.
                    continue;
                }
                if (statement is BoundModuleDefinition)
                {
                    // Already registered in the prologue.
                    continue;
                }
                executableStatements.Add(statement);
            }

            var savedReturnEmissionFrame = _returnEmissionFrame;
            var savedDeferredCleanupFrames = _deferredCleanupFrames;
            _deferredCleanupFrames = new();
            mainReturnFrame = CreateReturnEmissionFrame(typeof(void));
            _returnEmissionFrame = mainReturnFrame;
            EmitBlock(CreateSyntheticBlock(executableStatements, _unit.Root.Span));
            _returnEmissionFrame = savedReturnEmissionFrame;
            _deferredCleanupFrames = savedDeferredCleanupFrames;
        }

        if (mainReturnFrame is { } frame)
        {
            EmitReturnEpilogue(frame);
        }
        else
        {
            _il.Emit(OpCodes.Ret);
        }
        _program.CreateType();

        // Emit module-method bodies (collected during DeclareClrModuleShell)
        // and close every module's static constructor. Done after
        // _program is finalized so cross-references between Program
        // members and module members are well-defined.
        EmitClrModuleMethodBodies();
        FinalizeClrModuleCctors();
        FinalizeClrModuleTypes();
        EmitClrClassMethodBodies();
        FinalizeClrClassTypes();
    }

    public void SerializeTo(Stream output)
    {
        // Two-stream metadata generation gives us a side-channel for
        // PDB rows. When the unit has an active document, build a
        // portable PDB and embed it in the PE — single-file output
        // keeps `tosh --compile foo.tosh -o foo.dll` from leaking a
        // companion .pdb. Embedding costs a few KB and is what
        // modern .NET tooling defaults to (`<DebugType>embedded</…>`).
        DebugDirectoryBuilder? debugDir = null;
        BlobBuilder ilStream;
        BlobBuilder mappedFieldData;
        MetadataBuilder metadataBuilder;
        // Reference assemblies have no executable behaviour; emitting
        // a portable PDB into them just bloats the contract surface
        // with sequence points pointing at fat method bodies that
        // will be stubbed out below. C#/F# refasms ship without PDBs.
        if (_doc is not null && !_referenceAssembly)
        {
            metadataBuilder = _ab.GenerateMetadata(out ilStream, out mappedFieldData, out var pdbBuilder);
            var entryPointHandle = MetadataTokens.MethodDefinitionHandle(_main.MetadataToken);
            var pdbBlob = new BlobBuilder();
            var portablePdbBuilder = new PortablePdbBuilder(
                pdbBuilder,
                metadataBuilder.GetRowCounts(),
                entryPointHandle);
            var pdbContentId = portablePdbBuilder.Serialize(pdbBlob);
            debugDir = new DebugDirectoryBuilder();
            debugDir.AddEmbeddedPortablePdbEntry(pdbBlob, portablePdbBuilder.FormatVersion);
        }
        else
        {
            metadataBuilder = _ab.GenerateMetadata(out ilStream, out mappedFieldData);
        }

        var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
        // Reference assemblies have no entry point: stripping it stops
        // the runtime from ever attempting to invoke `Main` on a
        // metadata-only DLL and matches Roslyn-emitted refasms.
        var entryPoint = _referenceAssembly
            ? default
            : MetadataTokens.MethodDefinitionHandle(_main.MetadataToken);
        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: mappedFieldData,
            debugDirectoryBuilder: debugDir,
            entryPoint: entryPoint);

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);

        if (_referenceAssembly)
        {
            // Metadata-only reference assembly: rewrite every method
            // body to a uniform `ldnull; throw;` tiny-format stub so
            // implementation details cannot leak into the contract
            // surface. The metadata (signatures, custom attributes,
            // type relationships) is preserved verbatim — that is
            // what C#/F# consume; method IL is never read by a
            // language compiler resolving symbols against a refasm.
            // We patch the bytes in place rather than rebuilding the
            // PE because the body offsets are already final and a
            // 3-byte tiny header (`0x0A 0x14 0x7A`) fits inside any
            // existing method body slot (tiny or fat).
            var bytes = ToArray(blob);
            StripMethodBodies(bytes);
            output.Write(bytes, 0, bytes.Length);
        }
        else
        {
            blob.WriteContentTo(output);
        }
    }

    private static byte[] ToArray(BlobBuilder blob)
    {
        using var ms = new MemoryStream();
        blob.WriteContentTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Rewrites every method body in <paramref name="peBytes"/> to a
    /// 3-byte <c>ldnull; throw;</c> tiny-format stub. The header
    /// byte <c>0x0A</c> is <c>(codeSize=2 &lt;&lt; 2) | TinyFormat</c>;
    /// the IL is <c>0x14</c> (<c>ldnull</c>) followed by <c>0x7A</c>
    /// (<c>throw</c>). Any bytes past the stub remain in the file
    /// but are never read because the method header advertises
    /// <c>codeSize=2</c>.
    /// </summary>
    private static void StripMethodBodies(byte[] peBytes)
    {
        using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(peBytes));
        var md = pe.GetMetadataReader();
        var headers = pe.PEHeaders;
        foreach (var handle in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(handle);
            var rva = def.RelativeVirtualAddress;
            if (rva == 0) continue; // abstract / pinvoke / runtime-implemented
            var fileOffset = RvaToFileOffset(headers, rva);
            if (fileOffset < 0 || fileOffset + 3 > peBytes.Length) continue;
            peBytes[fileOffset] = 0x0A;     // TinyFormat header, codeSize=2
            peBytes[fileOffset + 1] = 0x14; // ldnull
            peBytes[fileOffset + 2] = 0x7A; // throw
        }
    }

    private static int RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var sectionStart = section.VirtualAddress;
            var sectionEnd = sectionStart + section.VirtualSize;
            if (rva >= sectionStart && rva < sectionEnd)
            {
                return section.PointerToRawData + (rva - sectionStart);
            }
        }
        return -1;
    }

    // ─── Statements ───────────────────────────────────────────────

    private readonly record struct LocalSlot(LocalBuilder Local, Type Type);

    /// <summary>
    /// Per-user-function emission record.
    /// <list type="bullet">
    /// <item><c>Method</c> — the canonical method internal call sites
    /// and pipeline-stage dispatch should target. For fully-typed
    /// functions this is the typed <c>&lt;name&gt;(T1, …) -&gt; TR</c>
    /// primary; otherwise it's the dynamic
    /// <c>Func_&lt;name&gt;(object, …) -&gt; object</c> entry. There is
    /// no separate object-shim — pipeline dispatch coerces args
    /// per parameter type at the host (see
    /// <c>ToshHost.InvokeUserFunc</c>).</item>
    /// <item><c>ParamClrTypes</c> — declared CLR type per parameter,
    /// in declaration order. <c>typeof(object)</c> for dynamic /
    /// untyped slots.</item>
    /// <item><c>ReturnClrType</c> — declared return CLR type, or
    /// <c>typeof(object)</c> when unannotated.</item>
    /// </list>
    /// </summary>
    private readonly record struct UserFunction(
        MethodBuilder Method,
        BoundFunctionDefinition Definition,
        bool IsTyped,
        bool UsesPackedArguments,
        Type[] ParamClrTypes,
        Type ReturnClrType);

    /// <summary>
    /// Metadata for one emitted CLR union variant type.
    /// </summary>
    private readonly record struct ClrUnionVariantInfo(
        string Name,
        TypeBuilder Type,
        ConstructorBuilder Ctor,
        Dictionary<string, FieldBuilder> Fields,
        FieldBuilder? UnitSingletonField);

    /// <summary>
    /// Metadata for one emitted CLR union base shell.
    /// </summary>
    private readonly record struct ClrUnionShell(
        string Name,
        TypeBuilder BaseType,
        FieldBuilder VariantField,
        Dictionary<string, ClrUnionVariantInfo> Variants);

    /// <summary>
    /// CLR shell for a tosh <c>module</c>. Carries the
    /// <see cref="TypeBuilder"/> we accumulate fields/methods/nested
    /// types onto, plus the lazily-defined <c>.cctor</c> used to
    /// initialize static fields.
    /// </summary>
    private sealed class ClrModuleShell
    {
        public ClrModuleShell(string qualifiedName, TypeBuilder type, ClrModuleShell? parent)
        {
            QualifiedName = qualifiedName;
            Type = type;
            Parent = parent;
        }

        public string QualifiedName { get; }
        public TypeBuilder Type { get; }
        public ClrModuleShell? Parent { get; }
        public ConstructorBuilder? Cctor { get; set; }
        public ILGenerator? CctorIl { get; set; }
        /// <summary>
        /// Static fields declared on this module type, keyed by tosh
        /// variable name. Used to redirect module-scope variable
        /// stores in func bodies to <c>stsfld</c>.
        /// </summary>
        public Dictionary<string, FieldBuilder> Fields { get; } = new(StringComparer.Ordinal);
        public List<ClrModuleShell> Nested { get; } = new();
    }

    private readonly record struct ClrModuleMethodPending(
        ClrModuleShell Module,
        MethodBuilder Method,
        BoundFunctionDefinition Definition);

    /// <summary>
    /// Pending class-method body emission. The body is deferred so
    /// it runs after <c>Program</c> and the modules are finalized,
    /// keeping the cross-reference order well-defined.
    /// </summary>
    private readonly record struct ClrClassMethodPending(
        ClrTypeShell Shell,
        MethodBuilder Method,
        BoundFunctionDefinition Definition);

    private readonly record struct ClrModuleFieldPending(
        ClrModuleShell Module,
        BoundVariableDeclaration Declaration);

    /// <summary>
    /// CLR shell metadata for a tosh <c>class</c> or <c>record</c>.
    /// Carries the <see cref="TypeBuilder"/> we accumulate fields /
    /// methods onto, the primary <see cref="ConstructorBuilder"/>
    /// (used by <see cref="EmitNewObject"/> to lower
    /// <c>new TypeName(...)</c> to a direct <c>newobj</c>), and the
    /// field map so <see cref="EmitMemberAccess"/> can lower
    /// <c>$x.Field</c> to a direct <c>ldfld</c>.
    /// </summary>
    private sealed class ClrTypeShell
    {
        public ClrTypeShell(
            string name,
            TypeBuilder type,
            Dictionary<string, MethodBuilder> methods)
        {
            Name = name;
            Type = type;
            Ctor = null!;
            CtorParamTypes = [];
            CtorParamNames = [];
            Fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
            SupportsDirectNewObj = false;
            Methods = methods;
        }

        public ClrTypeShell(
            string name,
            TypeBuilder type,
            ConstructorBuilder ctor,
            Type[] ctorParamTypes,
            string[] ctorParamNames,
            Dictionary<string, FieldBuilder> fields,
            bool supportsDirectNewObj)
        {
            Name = name;
            Type = type;
            Ctor = ctor;
            CtorParamTypes = ctorParamTypes;
            CtorParamNames = ctorParamNames;
            Fields = fields;
            SupportsDirectNewObj = supportsDirectNewObj;
            Methods = new Dictionary<string, MethodBuilder>(StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }
        public TypeBuilder Type { get; }
        public ConstructorBuilder Ctor { get; }
        public Type[] CtorParamTypes { get; }
        public string[] CtorParamNames { get; }
        public Dictionary<string, FieldBuilder> Fields { get; }
        /// <summary>
        /// Public instance methods defined on the shell as
        /// trampolines into <c>ToshHost.InvokeMember</c>. Each entry
        /// has a positional <c>object</c> parameter per tosh
        /// parameter and an <c>object</c> return type. The body
        /// loads <c>this</c> + the method name + boxes the args
        /// into an <c>object[]</c> + calls <c>InvokeMember</c>, so
        /// behavior matches the engine while the CLR shape is real
        /// and reflectable. Method names with named/optional/rest
        /// parameters are skipped (they continue to dispatch
        /// through <c>ToshHost.InvokeMember</c> at the call site).
        /// </summary>
        public Dictionary<string, MethodBuilder> Methods { get; }
        /// <summary>
        /// True when the shell is a complete representation of the
        /// type's runtime semantics, so <c>new TypeName(...)</c>
        /// can be lowered to a direct <c>newobj</c>. False when the
        /// type has methods (or other not-yet-lowered members) whose
        /// behavior still lives in the engine — those types must
        /// route through <see cref="global::Tosh.Compiler.Runtime.ToshHost.NewObject"/>
        /// so the engine produces an <c>IShellInvocableObject</c>
        /// that can dispatch tosh-defined methods.
        /// </summary>
        public bool SupportsDirectNewObj { get; }
    }

    /// <summary>
    /// CLR shell metadata for a tosh <c>enum</c> declaration that could not
    /// be expressed as a real CLR <c>enum</c> (non-integral underlying type
    /// or non-literal member values) and was instead emitted as a
    /// <c>public sealed abstract class</c> with <c>public static readonly object</c>
    /// fields populated in <c>.cctor</c>.
    /// </summary>
    private sealed class ClrEnumStaticShell
    {
        public ClrEnumStaticShell(TypeBuilder type, Dictionary<string, FieldBuilder> fields)
        {
            Type = type;
            Fields = fields;
        }

        public TypeBuilder Type { get; }
        public Dictionary<string, FieldBuilder> Fields { get; }
    }

    /// <summary>
    /// CLR metadata retained for one integral enum. Literal fields have no
    /// runtime storage, so member access emits the underlying constant and
    /// treats the resulting stack value as <see cref="Type"/>.
    /// </summary>
    private sealed class ClrIntegralEnumShell
    {
        public ClrIntegralEnumShell(
            Type type,
            Type underlyingType,
            Dictionary<string, object> members)
        {
            Type = type;
            UnderlyingType = underlyingType;
            Members = members;
        }

        public Type Type { get; }
        public Type UnderlyingType { get; }
        public Dictionary<string, object> Members { get; }
    }

    /// <summary>
    /// One entry on <c>_loopStack</c>. <see cref="ContinueLabel"/> is
    /// where <c>continue</c> branches (typically the loop's
    /// test/increment); <see cref="BreakLabel"/> is the loop's exit.
    /// We always emit <c>leave</c> (not <c>br</c>) so the same
    /// branches work whether or not the loop body is wrapped in a
    /// protected (try) region.
    /// </summary>
    private readonly record struct LoopFrame(Label ContinueLabel, Label BreakLabel);
}
