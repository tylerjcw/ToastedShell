using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;
using Tosh.Language.Parsing;
using Tosh.Runtime;

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
/// • <c>BoundLiteral</c> of int/long/double/bool/string/null
/// • <c>BoundVariableDeclaration</c> + <c>BoundVariableReference</c>
/// • <c>BoundBinaryOperator</c> on numeric/string operands (<c>+ - * / %</c>,
///   plus <c>== != &lt; &gt;</c>)
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
    /// bodies are still emitted in full (fat reference assembly);
    /// body-stripping is a follow-up optimisation.
    /// </summary>
    public static EmitResult Emit(
        BoundUnit unit,
        string assemblyName,
        Stream output,
        CompileProfile profile,
        bool referenceAssembly)
    {
        var emitter = new EmitterImpl(unit, assemblyName, profile, referenceAssembly);
        emitter.Run();
        emitter.SerializeTo(output);
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

internal sealed class EmitterImpl
{
    private readonly BoundUnit _unit;
    private readonly string _assemblyName;
    private readonly CompileProfile _profile;
    private readonly bool _referenceAssembly;
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
    private readonly Dictionary<string, UserFunction> _userFunctions = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Reverse lookup: TypeBuilder → ClrTypeShell. Used by
    /// expression-emit sites that have already produced a typed
    /// stack value (e.g. a local of the shell type) and need the
    /// shell's field / method metadata to dispatch directly.
    /// </summary>
    private readonly Dictionary<Type, ClrTypeShell> _clrShellsByType = new();
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
    private static readonly MethodInfo s_stringJoin =
        typeof(string).GetMethod(nameof(string.Join), new[] { typeof(string), typeof(string[]) })!;
    private static readonly MethodInfo s_objectToString =
        typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;
    private static readonly MethodInfo s_objectEquals =
        typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_convertToInt32 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToInt64 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt64), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToDouble =
        typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(object) })!;

    private static readonly Type s_toshHost = typeof(global::Tosh.Compiler.Runtime.ToshHost);
    private static readonly MethodInfo s_hostInitialize =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.Initialize),
            new[] { typeof(global::Tosh.Runtime.ToshRuntime) })!;
    private static readonly MethodInfo s_hostInvokeStatement =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeStatement),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostInvokeValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeValue),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostGetMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetMember),
            new[] { typeof(object), typeof(string), typeof(bool) })!;
    private static readonly MethodInfo s_hostGetIndex =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetIndex),
            new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_hostThrowValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ThrowValue),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostThrownValueOf =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ThrownValueOf),
            new[] { typeof(global::Tosh.Runtime.ThrowSignalException) })!;
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
    private static readonly MethodInfo s_hostDrainValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.DrainValue),
            new[] { typeof(IAsyncEnumerable<object?>) })!;
    private static readonly MethodInfo s_hostRegisterSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterSource),
            new[] { typeof(string), typeof(string) })!;
    private static readonly MethodInfo s_hostMakeBlock =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.MakeBlock),
            new[] { typeof(int), typeof(int), typeof(Dictionary<string, object?>) })!;
    private static readonly MethodInfo s_hostSeedFromValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.SeedFromValue),
            new[] { typeof(object) })!;
    private static readonly MethodInfo s_hostCheckType =
        s_toshHost.GetMethod(
            nameof(global::Tosh.Compiler.Runtime.ToshHost.CheckType),
            new[] { typeof(object), typeof(string), typeof(int), typeof(int), typeof(string) })!;

    private static readonly MethodInfo s_hostRegisterTypeFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterTypeFromSource),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo s_hostRegisterModuleFromSource =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.RegisterModuleFromSource),
            new[] { typeof(int), typeof(int) })!;
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
    private static readonly MethodInfo s_hostResolveQualifiedAccess =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ResolveQualifiedAccess),
            new[] { typeof(string) })!;
    private static readonly MethodInfo s_hostInvokeQualifiedMethod =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeQualifiedMethod),
            new[] { typeof(string), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostNewObject =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.NewObject),
            new[] { typeof(string), typeof(object?[]) })!;
    private static readonly MethodInfo s_hostInvokeMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeMember),
            new[] { typeof(object), typeof(string), typeof(object?[]), typeof(bool) })!;

    private static readonly Type s_listOfObject = typeof(List<object?>);
    private static readonly ConstructorInfo s_listCtor =
        s_listOfObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_listAdd =
        s_listOfObject.GetMethod(nameof(List<object?>.Add))!;
    private static readonly MethodInfo s_listAddRange =
        s_listOfObject.GetMethod(nameof(List<object?>.AddRange))!;

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

    private static readonly Type s_dictOfStringObject = typeof(Dictionary<string, object?>);
    private static readonly ConstructorInfo s_dictCtor =
        s_dictOfStringObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictSetItem =
        s_dictOfStringObject.GetMethod("set_Item", new[] { typeof(string), typeof(object) })!;

    private static readonly Type s_dictOfObjectObject = typeof(Dictionary<object, object?>);
    private static readonly ConstructorInfo s_dictObjCtor =
        s_dictOfObjectObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictObjSetItem =
        s_dictOfObjectObject.GetMethod("set_Item", new[] { typeof(object), typeof(object) })!;

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
        : this(unit, assemblyName, profile, referenceAssembly: false) { }

    public EmitterImpl(BoundUnit unit, string assemblyName, CompileProfile profile, bool referenceAssembly)
    {
        _unit = unit;
        _assemblyName = assemblyName;
        _profile = profile;
        _referenceAssembly = referenceAssembly;
        var coreAssembly = typeof(object).Assembly;
        _ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), coreAssembly);
        if (referenceAssembly)
        {
            // Stamp [assembly: ReferenceAssembly] so the C# / F#
            // compiler treats this as a metadata-only reference.
            // Bodies remain populated (fat refasm) — equivalent to
            // what F# emitted for years before refasm support
            // landed; downstream consumers ignore method IL when
            // resolving symbols.
            var refAsmCtor = typeof(System.Runtime.CompilerServices.ReferenceAssemblyAttribute)
                .GetConstructor(Type.EmptyTypes)!;
            _ab.SetCustomAttribute(new CustomAttributeBuilder(refAsmCtor, Array.Empty<object>()));
        }
        _moduleBuilder = _ab.DefineDynamicModule("MainModule");
        _program = _moduleBuilder.DefineType(
            $"{assemblyName}.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        _main = _program.DefineMethod(
            "Main",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            new[] { typeof(string[]) });

        _il = _main.GetILGenerator();

        // Initialize the symbol document so sequence points can be
        // attached as we lay out IL. SourceName may be a synthetic
        // identifier for unit tests / REPL fragments — that's fine,
        // PDB consumers tolerate non-filesystem paths and a debugger
        // simply won't be able to step through them. The language
        // GUID is the well-known Roslyn "Tosh" reservation; if/when
        // a tosh-specific GUID is registered with the debugger
        // ecosystem this can be swapped without changing call sites.
        var sourceName = unit.ParseResult.SourceName;
        if (!string.IsNullOrEmpty(sourceName))
        {
            // Use Guid.Empty for the language so debuggers fall back
            // to text rendering rather than misidentifying the file
            // as a known language.
            _doc = _moduleBuilder.DefineDocument(sourceName, Guid.Empty);
        }
    }

    /// <summary>
    /// Convert a source-text offset into 1-based (line, column) for
    /// PDB sequence points. Builds a sorted line-start cache the
    /// first time it's called.
    /// </summary>
    private (int Line, int Column) GetLineColumn(int offset)
    {
        var src = _unit.ParseResult.SourceText;
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

        // Pre-pass B: declare a MethodBuilder for every top-level
        // function definition so call sites can resolve them even
        // when the call appears textually before the definition.
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundFunctionDefinition func)
            {
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
                case BoundClassDefinition cls when CanEmitClrClassShell(cls):
                    DeclareClrClassShell(cls);
                    break;
                case BoundRecordDefinition rec when CanEmitClrRecordShell(rec):
                    DeclareClrRecordShell(rec);
                    break;
            }
        }

        // Main prologue: wire up the ambient ToshRuntime once so any
        // builtin command dispatched through ToshHost has a runtime
        // available. Idempotent on the host side.
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Call, s_hostInitialize);

        // Phase 2: register the source text + name so that block
        // arguments embedded in pipeline stages can be re-bound to
        // the original BlockSyntax by span at runtime. Skipped when
        // the program contains no block expressions — leaves the
        // emitted IL byte-for-byte identical to Phase 1 in that case.
        var hasSubcommandDispatch = ProgramHasSubcommandDispatch();
        var subcommandNeedsReplay = hasSubcommandDispatch && !CanCompileSubcommandDispatch();
        if (ProgramUsesBlockExpressions() || ProgramHasTypeDefinitionsNeedingReplay() || ProgramHasModuleDefinitionsNeedingReplay() || hasSubcommandDispatch)
            if (ProgramHasBlockExpressionsNeedingReplay() || ProgramHasTypeDefinitionsNeedingReplay() || ProgramHasModuleDefinitionsNeedingReplay() || subcommandNeedsReplay)
            {
                _il.Emit(OpCodes.Ldstr, _unit.ParseResult.SourceText);
                _il.Emit(OpCodes.Ldstr, _unit.ParseResult.SourceName);
                _il.Emit(OpCodes.Call, s_hostRegisterSource);
            }

        // Register every user-defined type that still needs source
        // replay (struct / enum / union / interface / alias, and
        // non-shell class/record forms) by replaying its source
        // slice through the engine. CLR-shell-emitted class/record
        // declarations intentionally skip this: construction and
        // member dispatch resolve through CLR metadata at runtime.
        foreach (var statement in _unit.Root.Statements)
        {
            if (TypeDefinitionNeedsSourceReplay(statement, out var span))
            {
                RequireTier(3, $"user-defined type ({statement.GetType().Name})");
                var (start, length) = ExtendTypeDefinitionSpan(span);
                _il.Emit(OpCodes.Ldc_I4, start);
                _il.Emit(OpCodes.Ldc_I4, length);
                _il.Emit(OpCodes.Call, s_hostRegisterTypeFromSource);
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
        // Before the per-module loop, register the compiled assembly with ToshHost so
        // that ResolveQualifiedAccess / InvokeQualifiedMethod can find module shell types
        // by their qualified name without relying on AppDomain-wide type scanning (which
        // can match the wrong type when multiple test assemblies have modules with the
        // same name).
        var hasModuleDefinitions = _unit.Root.Statements.OfType<BoundModuleDefinition>().Any();
        if (hasModuleDefinitions)
        {
            RequireTier(2, "module definitions (CLR shell registration)");
            // typeof(Program).Assembly
            _il.Emit(OpCodes.Ldtoken, _program);
            _il.Emit(OpCodes.Call, s_runtimeTypeHandle_GetTypeFromHandle);
            _il.Emit(OpCodes.Callvirt, s_type_get_Assembly);
            _il.Emit(OpCodes.Call, s_hostRegisterCompiledAssembly);
        }
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundModuleDefinition mod)
            {
                if (ModuleNeedsSourceReplay(mod))
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

        // Main pass: top-level statements go into Main; function
        // definitions are emitted into their own MethodBuilders and
        // skipped here.
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
        else
        {
            foreach (var statement in _unit.Root.Statements)
            {
                if (statement is BoundFunctionDefinition func)
                {
                    EmitUserFunctionBody(func);
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
                EmitStatement(statement);
            }
        }
        _il.Emit(OpCodes.Ret);
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

    /// <summary>
    /// True if a module body contains any declaration that the CLR
    /// shell can't represent natively (classes, records, structs,
    /// unions, enums, traits, side-effectful top-level statements,
    /// or funcs with unsupported parameter shapes / captures). Such
    /// modules still get a <see cref="TypeBuilder"/> shell, but the
    /// body is also re-evaluated via source replay at runtime — and
    /// that replay is the part the <c>runtime</c> /<c>pure</c>
    /// profiles need to reject.
    /// </summary>
    private bool ModuleNeedsSourceReplay(BoundModuleDefinition mod)
    {
        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundVariableDeclaration:
                    continue;
                case BoundFunctionDefinition fn when CanEmitClrModuleMethod(fn):
                    continue;
                case BoundModuleDefinition nested when !ModuleNeedsSourceReplay(nested):
                    continue;
                default:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Declare a real CLR static-class shell for one module
    /// definition. Top-level modules become top-level types; nested
    /// modules become nested types under their parent. Multiple
    /// <c>partial module</c> declarations sharing the same qualified
    /// name reuse the same <see cref="TypeBuilder"/>.
    /// </summary>
    private void DeclareClrModuleShell(BoundModuleDefinition mod, ClrModuleShell? parent, string qualifiedName)
    {
        if (!_clrModules.TryGetValue(qualifiedName, out var shell))
        {
            const TypeAttributes baseAttrs =
                TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract;
            TypeBuilder typeBuilder;
            if (parent is null)
            {
                typeBuilder = _moduleBuilder.DefineType(
                    $"{_assemblyName}.{MangleClrIdentifier(mod.Name)}",
                    TypeAttributes.Public | baseAttrs);
            }
            else
            {
                typeBuilder = parent.Type.DefineNestedType(
                    MangleClrIdentifier(mod.Name),
                    TypeAttributes.NestedPublic | baseAttrs);
            }
            StampOriginalNameIfMangled(typeBuilder, mod.Name);
            // Stamp the type with its qualified tosh module name so ToshHost
            // can build a qualifiedName → Type map from the compiled assembly
            // without relying on name-mangling correlation.
            var moduleShellAttrCtor = typeof(global::Tosh.Runtime.ToshModuleShellAttribute)
                .GetConstructor(new[] { typeof(string) })!;
            typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                moduleShellAttrCtor, new object[] { qualifiedName }));
            shell = new ClrModuleShell(qualifiedName, typeBuilder, parent);
            _clrModules[qualifiedName] = shell;
            parent?.Nested.Add(shell);
        }

        // Walk the body twice so all module-scope fields are
        // registered before any method's capture validation runs.
        // Pass 1: vars (registers static fields). Pass 2: funcs and
        // nested modules. Anything else (classes, records, top-level
        // statements with side effects) stays unrepresented in the
        // CLR shell — source-replay handles those.
        foreach (var stmt in mod.Body.Statements)
        {
            if (stmt is BoundVariableDeclaration vd)
            {
                DeclareModuleField(shell, vd);
            }
        }
        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundFunctionDefinition fn when CanEmitClrModuleMethod(fn):
                    DeclareModuleMethod(shell, fn);
                    break;

                case BoundModuleDefinition nested:
                    DeclareClrModuleShell(nested, shell, $"{qualifiedName}.{nested.Name}");
                    break;
            }
        }
    }

    /// <summary>
    /// True if the function definition can be safely emitted as a
    /// real CLR static method on a module type. Every capture must
    /// resolve to either a peer top-level user function (dispatched
    /// through <see cref="_userFunctions"/>) or a symbol that has
    /// been promoted to a static field (top-level or module-scope).
    /// </summary>
    private bool CanEmitClrModuleMethod(BoundFunctionDefinition fn)
    {
        foreach (var p in fn.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null) return false;
        }
        foreach (var capture in fn.Captures)
        {
            if (_staticFields.ContainsKey(capture)) continue;
            if (_topLevelFunctionNames.Contains(capture.Name)) continue;
            return false;
        }
        return true;
    }

    private void DeclareModuleField(ClrModuleShell shell, BoundVariableDeclaration vd)
    {
        if (shell.Fields.ContainsKey(vd.Symbol.Name)) return;
        if (_staticFields.ContainsKey(vd.Symbol)) return;

        var field = shell.Type.DefineField(
            MangleClrIdentifier(vd.Symbol.Name),
            typeof(object),
            FieldAttributes.Public | FieldAttributes.Static);
        StampOriginalNameIfMangled(field, vd.Symbol.Name);
        shell.Fields[vd.Symbol.Name] = field;
        // Registering on _staticFields lets the standard
        // EmitVariableDeclaration / EmitVariableReference paths
        // emit `stsfld` / `ldsfld` against the right field token
        // automatically — no module-aware special-casing needed
        // in the expression emitter.
        _staticFields[vd.Symbol] = field;
        _clrModuleFieldInits.Add(new ClrModuleFieldPending(shell, vd));
    }

    private void DeclareModuleMethod(ClrModuleShell shell, BoundFunctionDefinition fn)
    {
        var paramTypes = new Type[fn.Parameters.Count];
        for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = typeof(object);
        var method = shell.Type.DefineMethod(
            MangleClrIdentifier(fn.Name),
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(object),
            paramTypes);
        StampOriginalNameIfMangled(method, fn.Name);
        for (var i = 0; i < fn.Parameters.Count; i++)
        {
            method.DefineParameter(i + 1, ParameterAttributes.None, fn.Parameters[i].Name);
        }
        _clrModuleMethodBodies.Add(new ClrModuleMethodPending(shell, method, fn));
    }

    private void EmitClrModuleMethodBodies()
    {
        foreach (var pending in _clrModuleMethodBodies)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                {
                    _paramSlots[pending.Definition.Parameters[i].Symbol] = i;
                }
                foreach (var stmt in pending.Definition.Body.Statements)
                {
                    EmitStatement(stmt);
                }
                // Implicit `return null` for fall-through.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ret);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
            }
        }
    }

    private void FinalizeClrModuleCctors()
    {
        // Emit each pending var initializer into its owning module's
        // .cctor. The field is already registered in `_staticFields`,
        // so EmitVariableDeclaration takes the static-field path
        // (stsfld) automatically.
        foreach (var pending in _clrModuleFieldInits)
        {
            var shell = pending.Module;
            if (shell.Cctor is null)
            {
                shell.Cctor = shell.Type.DefineTypeInitializer();
                shell.CctorIl = shell.Cctor.GetILGenerator();
            }
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            try
            {
                _il = shell.CctorIl!;
                _locals = new();
                _paramSlots = new();
                EmitVariableDeclaration(pending.Declaration);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
            }
        }

        foreach (var shell in _clrModules.Values)
        {
            if (shell.CctorIl is { } cctorIl)
            {
                cctorIl.Emit(OpCodes.Ret);
            }
        }
    }

    private void FinalizeClrModuleTypes()
    {
        // Nested types must be created before their declaring type.
        // Walk roots, recurse depth-first, then create on the way back.
        foreach (var shell in _clrModules.Values)
        {
            if (shell.Parent is null) CreateClrModuleType(shell);
        }
    }

    private static void CreateClrModuleType(ClrModuleShell shell)
    {
        foreach (var nested in shell.Nested)
        {
            CreateClrModuleType(nested);
        }
        shell.Type.CreateType();
    }

    /// <summary>
    /// True if <paramref name="cls"/> is "simple" enough for v1 CLR
    /// lowering: no base class, no interfaces, no traits, not
    /// abstract / hermit / partial, no custom constructors, every
    /// member is either a non-static / non-computed / non-lazy
    /// storage property or a method (methods are skipped from the
    /// shell), and every primary-ctor parameter is positional and
    /// non-rest. Failure to match means the type stays Tier 3
    /// (source-replay only) so external callers won't see a
    /// half-formed CLR shell that lacks members the tosh runtime
    /// uses.
    /// </summary>
    private static bool CanEmitClrClassShell(BoundClassDefinition cls)
    {
        if (cls.IsAbstract || cls.IsHermit || cls.IsPartial) return false;
        if (cls.BaseClassName is not null) return false;
        if (cls.ImplementedInterfaces is { Count: > 0 }) return false;
        if (cls.UsedTraits is { Count: > 0 }) return false;
        foreach (var p in cls.PrimaryConstructorParameters)
        {
            if (p.IsRest) return false;
        }
        // At most one user-declared constructor is supported. Its
        // parameters drive the shell ctor signature when no primary
        // ctor is declared; otherwise the primary ctor wins and the
        // explicit ctor's body still gets lowered into the shell
        // ctor IL after field copies.
        var ctorCount = 0;
        foreach (var m in cls.Members)
        {
            switch (m)
            {
                case BoundClassPropertyMember prop:
                    if (prop.IsStatic) return false;
                    if (prop.IsLazy) return false;
                    if (prop.GetterBody is not null) return false;
                    if (prop.SetterBody is not null) return false;
                    if (prop.IsAbstract) return false;
                    continue;
                case BoundClassMethodMember:
                    // Methods are not lowered to the shell yet, but
                    // their presence doesn't disqualify the type.
                    continue;
                case BoundClassConstructorMember ctor:
                    if (++ctorCount > 1) return false;
                    foreach (var p in ctor.Parameters)
                    {
                        if (p.IsRest) return false;
                    }
                    continue;
                default:
                    return false;
            }
        }
        // If both a primary ctor and an explicit ctor exist, only
        // the primary ctor's parameters drive the shell signature;
        // we don't try to model overloaded CLR ctors yet.
        return true;
    }

    /// <summary>
    /// True if <paramref name="rec"/> is a plain record (no
    /// <c>partial</c>); records are pure data shapes so almost any
    /// declaration qualifies. Default-value initializers are
    /// intentionally not lowered \u2014 the shell exposes the field
    /// names, source-replay still owns initial-value semantics.
    /// </summary>
    private static bool CanEmitClrRecordShell(BoundRecordDefinition rec)
    {
        return !rec.IsPartial;
    }

    /// <summary>
    /// True if <paramref name="stmt"/> is a type definition that
    /// the emitter has produced a CLR shell for. The Tier-3
    /// diagnostic is suppressed for these.
    /// </summary>
    private bool IsClrShellEmittedTypeDefinition(BoundStatement stmt) =>
        stmt switch
        {
            BoundClassDefinition c => _clrTypeShells.ContainsKey(c.Name),
            BoundRecordDefinition r => _clrTypeShells.ContainsKey(r.Name),
            _ => false,
        };

    /// <summary>
    /// Emit a real CLR <c>public sealed class</c> for one tosh
    /// <c>class</c> declaration. Storage properties become
    /// public mutable instance fields typed <c>object</c>; the
    /// constructor takes one <c>object</c> parameter per primary
    /// constructor parameter and copies each one into the matching-
    /// name field (case-insensitive) when one exists. Method bodies
    /// are not lowered \u2014 callers go through <c>ToshHost</c> for
    /// behavior, the CLR type is reflectable for shape only.
    /// </summary>
    private void DeclareClrClassShell(BoundClassDefinition cls)
    {
        if (_clrTypeShells.ContainsKey(cls.Name)) return;

        var attrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed;
        var typeBuilder = _moduleBuilder.DefineType($"{_assemblyName}.{MangleClrIdentifier(cls.Name)}", attrs);
        StampToshTypeAttribute(typeBuilder, "class", cls.Span);
        StampOriginalNameIfMangled(typeBuilder, cls.Name);

        // Public mutable instance field per storage property.
        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in cls.Members)
        {
            if (member is BoundClassPropertyMember prop)
            {
                if (fields.ContainsKey(prop.Name)) continue;
                var fieldAttrs = prop.IsShy
                    ? FieldAttributes.Private
                    : FieldAttributes.Public;
                if (prop.IsFixed) fieldAttrs |= FieldAttributes.InitOnly;
                var fb = typeBuilder.DefineField(MangleClrIdentifier(prop.Name), typeof(object), fieldAttrs);
                StampOriginalNameIfMangled(fb, prop.Name);
                fields[prop.Name] = fb;
            }
        }

        // Locate the (at most one) explicit user-declared constructor.
        // When the class header has no primary-ctor parameters, the
        // explicit ctor's parameters drive the shell ctor signature
        // (e.g. `class Greeter { Greeter(name: string) { ... } }`).
        // When both forms exist, the primary ctor wins for the
        // signature and the explicit body still gets lowered into
        // the shell ctor IL after field copies.
        BoundClassConstructorMember? explicitCtor = null;
        foreach (var member in cls.Members)
        {
            if (member is BoundClassConstructorMember c) { explicitCtor = c; break; }
        }
        IReadOnlyList<BoundParameter> ctorSigParams =
            cls.PrimaryConstructorParameters.Count > 0 || explicitCtor is null
                ? cls.PrimaryConstructorParameters
                : explicitCtor.Parameters;

        // Start optimistic: a class without methods, or one whose
        // every method we can lower to real IL on the shell, supports
        // direct newobj. We flip back to host-dispatch newobj when
        // we encounter a member shape we can't represent on the
        // shell (static/abstract methods, named/optional/rest params,
        // captures, inheritance, computed props, etc.) — those still
        // need the engine-side ToshClassObject to own dispatch.
        var supportsDirectNewObj = true;
        // Inheritance, abstract base, and traits/interfaces aren't
        // representable on a flat shell yet. Be conservative.
        if (cls.BaseClassName is not null
            || (cls.UsedTraits is { Count: > 0 })
            || (cls.ImplementedInterfaces is { Count: > 0 })
            || cls.IsAbstract
            || cls.IsHermit)
        {
            supportsDirectNewObj = false;
        }

        // Constructor matching the chosen ctor signature; each
        // parameter is `object`. For each parameter that names a
        // declared property (case-insensitive), copy the parameter
        // value into the backing field. Other parameters are ignored
        // by the shell ctor's prologue but remain visible to a
        // lowered explicit-ctor body via _paramSlots.
        var paramTypes = new Type[ctorSigParams.Count];
        for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = typeof(object);
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, ctorSigParams[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        // base..ctor()
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            var pname = ctorSigParams[i].Name;
            if (!fields.TryGetValue(pname, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }
        // Lower the explicit ctor body (if any) inline. Statements
        // are net-zero stack so we can append straight to ctorIl,
        // then emit Ret. _currentThisType is set so $this resolves;
        // _paramSlots maps each parameter symbol to its arg slot.
        if (explicitCtor is not null)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedTypedLocals = _typedParamLocals;
            var savedReturnType = _currentFunctionReturnType;
            var savedThis = _currentThisType;
            try
            {
                _il = ctorIl;
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;
                _currentThisType = typeBuilder;
                // When the primary ctor drives the signature but the
                // explicit ctor's parameters need to be visible to
                // its body, they don't have arg slots. In that case
                // we currently don't support body lowering — guarded
                // by ctorSigParams == explicitCtor.Parameters above.
                if (ReferenceEquals(ctorSigParams, explicitCtor.Parameters))
                {
                    for (var i = 0; i < explicitCtor.Parameters.Count; i++)
                    {
                        _paramSlots[explicitCtor.Parameters[i].Symbol] = i + 1;
                    }
                    foreach (var stmt in explicitCtor.Body.Statements)
                    {
                        EmitStatement(stmt);
                    }
                }
                // else: explicit ctor coexists with primary ctor; its
                // parameters aren't bound to CLR args. Leave body
                // unlowered for now — host dispatch still owns it.
                else
                {
                    supportsDirectNewObj = false;
                }
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _typedParamLocals = savedTypedLocals;
                _currentFunctionReturnType = savedReturnType;
                _currentThisType = savedThis;
            }
        }
        ctorIl.Emit(OpCodes.Ret);

        var paramNames = new string[ctorSigParams.Count];
        for (var i = 0; i < paramNames.Length; i++) paramNames[i] = ctorSigParams[i].Name;
        // First pass: declare MethodBuilders for every lowerable
        // method. We collect into a side list so the body emit can
        // happen after Program is finalized — same pattern as the
        // CLR module methods.
        var pendingMethods = new List<(BoundClassMethodMember Member, MethodBuilder Builder)>();
        var methods = new Dictionary<string, MethodBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in cls.Members)
        {
            if (member is BoundClassMethodMember m)
            {
                if (!CanLowerClassMethod(m))
                {
                    supportsDirectNewObj = false;
                    continue;
                }
                if (methods.ContainsKey(m.Method.Name))
                {
                    // Defensive — duplicate method names shouldn't
                    // exist after lowering, but if they do, leave
                    // dispatch to the host.
                    supportsDirectNewObj = false;
                    continue;
                }
                var arity = m.Method.Parameters.Count;
                var mParamTypes = new Type[arity];
                for (var i = 0; i < arity; i++) mParamTypes[i] = typeof(object);
                var mb = typeBuilder.DefineMethod(
                    MangleClrIdentifier(m.Method.Name),
                    MethodAttributes.Public | MethodAttributes.HideBySig,
                    CallingConventions.HasThis,
                    returnType: typeof(object),
                    parameterTypes: mParamTypes);
                StampOriginalNameIfMangled(mb, m.Method.Name);
                for (var i = 0; i < arity; i++)
                {
                    mb.DefineParameter(i + 1, ParameterAttributes.None, m.Method.Parameters[i].Name);
                }
                methods[m.Method.Name] = mb;
                pendingMethods.Add((m, mb));
            }
            else if (member is BoundClassPropertyMember prop)
            {
                // Computed properties (with getter/setter bodies)
                // and lazy props aren't lowered onto the shell yet —
                // routing such instances through host dispatch keeps
                // the engine's evaluator owning that behavior.
                if (prop.GetterBody is not null || prop.SetterBody is not null || prop.IsLazy)
                {
                    supportsDirectNewObj = false;
                }
            }
            else if (member is BoundClassConstructorMember)
            {
                // The (single) explicit ctor is lowered inline by the
                // ctor IL above when it drives the shell signature.
                // When a primary ctor co-exists, supportsDirectNewObj
                // was already disabled by that path.
            }
        }
        var shell = new ClrTypeShell(cls.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: supportsDirectNewObj);
        foreach (var (k, v) in methods) shell.Methods[k] = v;
        _clrTypeShells[cls.Name] = shell;
        _clrShellsByType[typeBuilder] = shell;
        foreach (var (member, builder) in pendingMethods)
        {
            _clrClassMethodBodies.Add(new ClrClassMethodPending(shell, builder, member.Method));
        }
    }

    /// <summary>
    /// Predicate: can this class method's body be lowered to real
    /// IL on the class shell? Conservative — falls back to engine
    /// dispatch for anything we don't yet model on the IL side.
    /// </summary>
    private static bool CanLowerClassMethod(BoundClassMethodMember m)
    {
        if (m.IsStatic) return false;        // static-method support is a follow-up
        if (m.IsAbstract) return false;      // no body
        if (m.IsOverride) return false;      // would need real virtual chains
        if (m.Method.Captures.Count > 0) return false;  // closures over outer scope
        foreach (var p in m.Method.Parameters)
        {
            if (p.IsRest || p.IsOptional) return false;
        }
        return true;
    }

    /// <summary>
    /// Emits the deferred body of every class method declared during
    /// <see cref="DeclareClrClassShell"/>. Mirrors the pattern used
    /// by <see cref="EmitClrModuleMethodBodies"/> but reserves
    /// <c>arg 0</c> for <c>this</c> (typed as the shell's
    /// <see cref="TypeBuilder"/>), and exposes that slot to the
    /// expression emitter via <see cref="_currentThisType"/> so
    /// <c>$this</c> references resolve correctly.
    /// </summary>
    private void EmitClrClassMethodBodies()
    {
        foreach (var pending in _clrClassMethodBodies)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedTypedLocals = _typedParamLocals;
            var savedReturnType = _currentFunctionReturnType;
            var savedThis = _currentThisType;
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;       // object-typed return
                _currentThisType = pending.Shell.Type;    // arg 0 is `this`
                // Declared params start at slot 1 (slot 0 is this).
                for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                {
                    _paramSlots[pending.Definition.Parameters[i].Symbol] = i + 1;
                }
                foreach (var stmt in pending.Definition.Body.Statements)
                {
                    EmitStatement(stmt);
                }
                // Fall-through: implicit `return null`.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ret);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _typedParamLocals = savedTypedLocals;
                _currentFunctionReturnType = savedReturnType;
                _currentThisType = savedThis;
            }
        }
    }

    /// <summary>
    /// Emit a real CLR <c>public sealed class</c> for one tosh
    /// <c>record</c> declaration. Each field becomes a public
    /// mutable instance field typed <c>object</c>. A positional
    /// constructor matching the record's field order is emitted so
    /// <c>new Rec(a, b, c)</c> can be lowered to a direct
    /// <c>newobj</c> in <see cref="EmitNewObject"/>. Default-value
    /// semantics for fields with explicit defaults are still owned
    /// by source-replay (the engine populates them on construction
    /// through its own record machinery); the positional form here
    /// matches the explicit-construction case used by compiled call
    /// sites.
    /// </summary>
    private void DeclareClrRecordShell(BoundRecordDefinition rec)
    {
        if (_clrTypeShells.ContainsKey(rec.Name)) return;

        var attrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed;
        var typeBuilder = _moduleBuilder.DefineType($"{_assemblyName}.{MangleClrIdentifier(rec.Name)}", attrs);
        StampToshTypeAttribute(typeBuilder, "record", rec.Span);
        StampOriginalNameIfMangled(typeBuilder, rec.Name);

        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in rec.Fields)
        {
            if (fields.ContainsKey(f.Name)) continue;
            var fb = typeBuilder.DefineField(MangleClrIdentifier(f.Name), typeof(object), FieldAttributes.Public);
            StampOriginalNameIfMangled(fb, f.Name);
            fields[f.Name] = fb;
        }

        // Positional ctor: one `object` parameter per field, each
        // copied into the matching field. Records are pure data
        // shapes so this is the natural construction shape.
        var paramTypes = new Type[rec.Fields.Count];
        var paramNames = new string[rec.Fields.Count];
        for (var i = 0; i < paramTypes.Length; i++)
        {
            paramTypes[i] = typeof(object);
            paramNames[i] = rec.Fields[i].Name;
        }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < rec.Fields.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, rec.Fields[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        for (var i = 0; i < rec.Fields.Count; i++)
        {
            if (!fields.TryGetValue(rec.Fields[i].Name, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }
        ctorIl.Emit(OpCodes.Ret);

        var shell = new ClrTypeShell(rec.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: true);
        _clrTypeShells[rec.Name] = shell;
        _clrShellsByType[typeBuilder] = shell;
    }

    private static void StampToshTypeAttribute(TypeBuilder typeBuilder, string kind, TextSpan span)
    {
        var ctor = typeof(global::Tosh.Runtime.ToshTypeAttribute)
            .GetConstructor(new[] { typeof(string), typeof(int), typeof(int) })!;
        typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            ctor,
            new object[] { kind, span.Start, span.Length }));
    }

    private static readonly ConstructorInfo s_toshOriginalNameCtor =
        typeof(global::Tosh.Runtime.ToshOriginalNameAttribute)
            .GetConstructor(new[] { typeof(string) })!;

    /// <summary>
    /// When <paramref name="original"/> would have to be mangled
    /// to land in a valid CLR identifier (i.e. <c>MangleClrIdentifier</c>
    /// returns a different string), stamps the supplied builder
    /// with <c>[ToshOriginalNameAttribute(original)]</c> so tooling
    /// can recover the user's original spelling. No-ops when the
    /// name was already a valid CLR identifier — keeps metadata
    /// lean for the common case.
    /// </summary>
    private static void StampOriginalNameIfMangled(TypeBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }
    private static void StampOriginalNameIfMangled(FieldBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }
    private static void StampOriginalNameIfMangled(MethodBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }

    private void FinalizeClrClassTypes()
    {
        foreach (var shell in _clrTypeShells.Values)
        {
            shell.Type.CreateType();
        }
    }

    /// <summary>
    /// Walks the bound tree once looking for any
    /// <see cref="BoundBlockExpression"/>. Used to decide whether to
    /// emit the source-registration prologue.
    /// </summary>
    /// <summary>
    /// Extends a type-definition span forward to include any trailing
    /// brace or paren the parser left out. Tosh's parser sometimes
    /// reports a class/record/struct span that ends just before its
    /// closing <c>}</c> or <c>)</c>; the engine needs the full balanced
    /// source to re-parse. Walks forward counting brace/paren nesting
    /// (starting from the slice's own running count) until the
    /// outermost closer is consumed.
    /// </summary>
    private (int Start, int Length) ExtendTypeDefinitionSpan(TextSpan span)
    {
        var src = _unit.ParseResult.SourceText;
        var sliceEnd = span.Start + span.Length;

        // Compute running brace/paren depth across the original slice
        // so we know how many closers we still need.
        int braceDepth = 0;
        int parenDepth = 0;
        for (int i = span.Start; i < sliceEnd && i < src.Length; i++)
        {
            char ch = src[i];
            if (ch == '{') braceDepth++;
            else if (ch == '}') braceDepth--;
            else if (ch == '(') parenDepth++;
            else if (ch == ')') parenDepth--;
        }
        if (braceDepth <= 0 && parenDepth <= 0) return (span.Start, span.Length);

        int probe = sliceEnd;
        while (probe < src.Length && (braceDepth > 0 || parenDepth > 0))
        {
            char ch = src[probe];
            if (ch == '{') braceDepth++;
            else if (ch == '}') braceDepth--;
            else if (ch == '(') parenDepth++;
            else if (ch == ')') parenDepth--;
            probe++;
        }
        return (span.Start, probe - span.Start);
    }

    private bool ProgramUsesBlockExpressions()
    {
        return ContainsBlockExpression(_unit.Root);

        static bool ContainsBlockExpression(BoundNode? node)
        {
            if (node is null) return false;
            if (node is BoundBlockExpression) return true;
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    if (ContainsBlockExpression(child)) return true;
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn && ContainsBlockExpression(bn)) return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> only when the program contains at least one
    /// block expression whose body <em>cannot</em> be compiled to CLR
    /// IL — i.e. that would fall back to
    /// <see cref="EmitMakeBlockFallback"/>. When every block in the
    /// program can be compiled, the <c>RegisterSource</c> prologue is
    /// unnecessary and this returns <c>false</c>.
    /// </summary>
    private bool ProgramHasBlockExpressionsNeedingReplay()
    {
        return ContainsNonCompilableBlock(_unit.Root);

        static bool ContainsNonCompilableBlock(BoundNode? node)
        {
            if (node is null) return false;
            if (node is BoundBlockExpression block && !CanCompileBlockBody(block)) return true;
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    if (ContainsNonCompilableBlock(child)) return true;
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn && ContainsNonCompilableBlock(bn)) return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if the bound unit contains any user-defined type
    /// declaration that still relies on source replay (Tier 3).
    /// CLR-shell-emitted classes/records are excluded.
    /// </summary>
    private bool ProgramHasTypeDefinitionsNeedingReplay()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (TypeDefinitionNeedsSourceReplay(stmt, out _)) return true;
        }
        return false;
    }

    private bool TypeDefinitionNeedsSourceReplay(BoundStatement stmt, out TextSpan span)
    {
        if (!IsTypeDefinitionStatement(stmt, out span)) return false;
        return !IsClrShellEmittedTypeDefinition(stmt);
    }

    /// <summary>
    /// True if the bound unit contains any top-level <c>module</c>
    /// declaration whose body requires source replay
    /// (i.e. <see cref="ModuleNeedsSourceReplay"/> returns
    /// <see langword="true"/>). Pure-shell modules — bodies that
    /// contain only vars, CLR-emittable funcs and pure nested
    /// modules — do not register source replay and therefore do
    /// not force the source-registration prologue.
    /// </summary>
    private bool ProgramHasModuleDefinitionsNeedingReplay()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundModuleDefinition mod && ModuleNeedsSourceReplay(mod)) return true;
        }
        return false;
    }

    /// <summary>
    /// True if the bound unit declares any subcommand block or
    /// top-level script-input (<c>flag</c> / <c>arg</c>) statement.
    /// Such scripts need argv-driven dispatch, which is handled
    /// entirely by the engine; the emitter delegates the whole
    /// program to <c>ToshHost.RunSubcommandScript</c> rather than
    /// emitting per-statement IL. (Tier 3.)
    /// </summary>
    private bool ProgramHasSubcommandDispatch()
    {
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundSubcommandStatement or BoundScriptInputStatement) return true;
        }
        return false;
    }

    // ─── Compiled subcommand dispatch ─────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when all <see cref="BoundScriptInputStatement"/>
    /// parameters in the entire subcommand tree have either no default
    /// or a literal default (simple <see cref="BoundLiteral"/>), and
    /// the root level contains no regular-variable declarations that
    /// would need cross-subcommand scope threading.
    /// Falls back to source-replay otherwise.
    /// </summary>
    private bool CanCompileSubcommandDispatch()
        => CanCompileSubcommandTopLevelStatements(_unit.Root.Statements);

    // Validates that TOP-LEVEL statements (root scope) are all acceptable
    // for compiled subcommand dispatch. Subcommand bodies may contain anything.
    private static bool CanCompileSubcommandTopLevelStatements(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                    if (param.Default is not null && !TryGetLiteralDefaultValue(param.Default, out _))
                        return false;
                continue;
            }
            if (stmt is BoundSubcommandStatement sub)
            {
                // Recursively check nested subcommand's inputs/nested-subcommands only.
                if (!CanCompileSubcommandBodyStructure(sub.Body.Statements))
                    return false;
                continue;
            }
            if (stmt is BoundFunctionDefinition) continue;
            if (IsTypeDefinitionStatement(stmt, out _)) continue;
            if (stmt is BoundModuleDefinition) continue;
            // Any other statement at root level (var decl, pipeline, etc.)
            // cannot currently be emitted safely in the subcommand context.
            return false;
        }
        return true;
    }

    // Validates the inside of a subcommand body: only validates input declarations
    // and nested subcommands (their inputs). Body statements are arbitrary and accepted.
    private static bool CanCompileSubcommandBodyStructure(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                    if (param.Default is not null && !TryGetLiteralDefaultValue(param.Default, out _))
                        return false;
                continue;
            }
            if (stmt is BoundSubcommandStatement sub)
            {
                if (!CanCompileSubcommandBodyStructure(sub.Body.Statements))
                    return false;
                continue;
            }
            // All other body statements (pipeline, var decl, etc.) are accepted.
        }
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="pipeline"/> reduces to a
    /// single <see cref="BoundLiteral"/> expression, extracting the
    /// literal value into <paramref name="value"/>.
    /// </summary>
    private static bool TryGetLiteralDefaultValue(BoundPipeline pipeline, out object? value)
    {
        value = null;
        if (pipeline.Stages.Count != 1) return false;
        if (pipeline.Stages[0] is not BoundExpressionStage { Value: BoundLiteral lit }) return false;
        value = lit.Value;
        return true;
    }

    /// <summary>
    /// Promotes every <see cref="BoundScriptInputStatement"/> parameter
    /// symbol in <paramref name="stmts"/> (and recursively in nested
    /// <see cref="BoundSubcommandStatement"/> bodies) to a static field
    /// on <see cref="_program"/>.  Registered in
    /// <see cref="_staticFields"/> so that <see cref="EmitVariableReference"/>
    /// emits <c>ldsfld</c> for them without further changes.
    /// </summary>
    private void PromoteSubcommandInputsAsStaticFields(IReadOnlyList<BoundStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                foreach (var param in input.Parameters)
                {
                    if (_staticFields.ContainsKey(param.Symbol)) continue;
                    var field = _program.DefineField(
                        $"_scriptinput_{param.Name}_{_staticFields.Count}",
                        typeof(object),
                        FieldAttributes.Private | FieldAttributes.Static);
                    _staticFields[param.Symbol] = field;
                }
            }
            else if (stmt is BoundSubcommandStatement sub)
            {
                PromoteSubcommandInputsAsStaticFields(sub.Body.Statements);
            }
        }
    }

    /// <summary>
    /// Emits the compiled subcommand dispatch path in <c>Main</c>:
    /// for each subcommand a private static body method is defined,
    /// then a <see cref="CompiledSubcommandNode"/> tree is built
    /// inline and passed to
    /// <see cref="ToshHost.RunCompiledSubcommandDispatch"/>.
    /// Also emits any top-level user-function bodies needed by the
    /// subcommand bodies.
    /// </summary>
    private void EmitCompiledSubcommandDispatch()
    {
        // Emit top-level function definitions (callable from bodies).
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundFunctionDefinition func)
                EmitUserFunctionBody(func);
        }

        // Require Tier 2 for the ToshHost dispatch call.
        RequireTier(2, "subcommand-tree dispatch (compiled)");

        // Build the root CompiledSubcommandNode on the IL stack,
        // then call ToshHost.RunCompiledSubcommandDispatch(argv, root).
        var rootSubcommands = _unit.Root.Statements
            .OfType<BoundSubcommandStatement>()
            .ToList();
        var rootInputs = GetScriptInputParams(_unit.Root.Statements);

        // Build child nodes into IL locals (bottom-up).
        var childLocals = new Dictionary<BoundSubcommandStatement, LocalBuilder>();
        foreach (var sub in rootSubcommands)
        {
            var local = _il.DeclareLocal(s_compiledSubcommandNodeType);
            EmitSubcommandNodeToLocal(sub, sub.Name, local);
            childLocals[sub] = local;
        }

        // Root body method (binds root flags + runs root setup).
        MethodBuilder? rootBodyMethod = EmitSubcommandBodyMethodForStatements(
            rootInputs.flags, rootInputs.args,
            _unit.Root.Statements
                .Where(static s => s is not BoundSubcommandStatement
                                  && s is not BoundScriptInputStatement
                                  && s is not BoundFunctionDefinition
                                  && !IsTypeDefinitionStatement(s, out _)
                                  && s is not BoundModuleDefinition)
                .ToList(),
            qualName: "__subcommand_root");

        // Build root node on IL stack.
        // argv
        _il.Emit(OpCodes.Ldarg_0);
        // root node
        EmitSubcommandNodeExpression(
            name: null,
            modifiers: SubcommandModifier.None,
            userDeclaredHelpFlag: false,
            flags: rootInputs.flags,
            args: rootInputs.args,
            childNames: rootSubcommands.Select(s => s.Name).ToArray(),
            childLocals: childLocals.Values.ToArray(),
            bodyMethod: rootBodyMethod);

        _il.Emit(OpCodes.Call, s_hostRunCompiledSubcommandDispatch);
    }

    /// <summary>
    /// Emits a <see cref="CompiledSubcommandNode"/> for a nested
    /// <paramref name="sub"/> into a fresh IL local (so it can be
    /// consumed by the parent's <c>children[]</c> array).  Recurses
    /// into any nested subcommands first.
    /// </summary>
    private void EmitSubcommandNodeToLocal(
        BoundSubcommandStatement sub,
        string qualName,
        LocalBuilder targetLocal)
    {
        var inputs = GetScriptInputParams(sub.Body.Statements);
        var children = sub.Body.Statements.OfType<BoundSubcommandStatement>().ToList();

        // Recurse first (bottom-up construction).
        var childLocals = new Dictionary<BoundSubcommandStatement, LocalBuilder>();
        foreach (var child in children)
        {
            var childLocal = _il.DeclareLocal(s_compiledSubcommandNodeType);
            EmitSubcommandNodeToLocal(child, $"{qualName}_{child.Name}", childLocal);
            childLocals[child] = childLocal;
        }

        // Emit body method.
        MethodBuilder? bodyMethod = EmitSubcommandBodyMethodForStatements(
            inputs.flags, inputs.args,
            sub.Body.Statements
                .Where(static s => s is not BoundSubcommandStatement
                                  && s is not BoundScriptInputStatement)
                .ToList(),
            qualName: $"__subcommand_{qualName}_{_subcommandBodyCounter++}");

        // Determine if user declared their own --help flag.
        var userDeclaredHelp = inputs.flags.Any(
            static p => string.Equals(p.Name, "help", StringComparison.OrdinalIgnoreCase));

        // Push node onto stack, then store in targetLocal.
        EmitSubcommandNodeExpression(
            name: sub.Name,
            modifiers: sub.Modifiers,
            userDeclaredHelpFlag: userDeclaredHelp,
            flags: inputs.flags,
            args: inputs.args,
            childNames: children.Select(c => c.Name).ToArray(),
            childLocals: childLocals.Values.ToArray(),
            bodyMethod: bodyMethod);
        _il.Emit(OpCodes.Stloc, targetLocal);
    }

    /// <summary>
    /// Extracts all <see cref="BoundScriptInputStatement"/> parameters
    /// from <paramref name="stmts"/>, returning flags (Kind=Flag) and
    /// args (Kind=Argument) in declaration order.
    /// </summary>
    private static (List<BoundParameter> flags, List<BoundParameter> args)
        GetScriptInputParams(IReadOnlyList<BoundStatement> stmts)
    {
        var flags = new List<BoundParameter>();
        var args = new List<BoundParameter>();
        foreach (var stmt in stmts)
        {
            if (stmt is BoundScriptInputStatement input)
            {
                if (input.Kind == ScriptInputDeclarationKind.Flag)
                    flags.AddRange(input.Parameters);
                else
                    args.AddRange(input.Parameters);
            }
        }
        return (flags, args);
    }

    /// <summary>
    /// Emits a private static method
    /// <c>__subcommand_&lt;qualName&gt;(object?[] bindings)</c>
    /// that initialises each flag/arg static field from the bindings
    /// array and then runs <paramref name="bodyStatements"/>.
    /// Returns the <see cref="MethodBuilder"/> if any real work is
    /// needed (flags/args to bind OR statements to execute), else
    /// <c>null</c> (caller may pass a null body to
    /// <see cref="ToshHost.MakeSubcommandNode"/>).
    /// </summary>
    private MethodBuilder? EmitSubcommandBodyMethodForStatements(
        IReadOnlyList<BoundParameter> flags,
        IReadOnlyList<BoundParameter> args,
        IReadOnlyList<BoundStatement> bodyStatements,
        string qualName)
    {
        var hasBindings = flags.Count > 0 || args.Count > 0;
        var hasBody = bodyStatements.Count > 0;
        if (!hasBindings && !hasBody) return null;

        // Save emitter state (mirrors EmitBlockBodyMethod pattern).
        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedParams = _typedParamLocals;
        var savedBlockCaptureIndices = _blockCaptureIndices;
        var savedBlockOutputLocal = _blockOutputLocal;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        var savedThisType = _currentThisType;
        var savedUnderscoreStack = _underscoreStack;
        var savedLoopStack = _loopStack;

        var method = _program.DefineMethod(
            qualName,
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(void),
            new[] { typeof(object?[]) });

        _il = method.GetILGenerator();
        _locals = new Dictionary<BoundSymbol, LocalSlot>();
        _paramSlots = new Dictionary<BoundSymbol, int>();
        _typedParamLocals = new Dictionary<BoundSymbol, LocalBuilder>();
        _blockCaptureIndices = new Dictionary<BoundSymbol, int>();
        _blockOutputLocal = null;
        _currentFunctionReturnType = null;
        _currentFunctionReturnRefinement = null;
        _currentThisType = null;
        _underscoreStack = new Stack<LocalBuilder>();
        _loopStack = new Stack<LoopFrame>();

        // 1. Bind flags (indices 0..flags.Count-1).
        for (var i = 0; i < flags.Count; i++)
        {
            var param = flags[i];
            if (!_staticFields.TryGetValue(param.Symbol, out var field)) continue;
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Stsfld, field);
        }

        // 2. Bind args (indices flags.Count..flags.Count+args.Count-1).
        for (var i = 0; i < args.Count; i++)
        {
            var param = args[i];
            if (!_staticFields.TryGetValue(param.Symbol, out var field)) continue;
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldc_I4, flags.Count + i);
            _il.Emit(OpCodes.Ldelem_Ref);
            _il.Emit(OpCodes.Stsfld, field);
        }

        // 3. Emit body statements.
        foreach (var stmt in bodyStatements)
            EmitStatement(stmt);

        _il.Emit(OpCodes.Ret);

        // Restore emitter state.
        _il = savedIl;
        _locals = savedLocals;
        _paramSlots = savedParams;
        _typedParamLocals = savedTypedParams;
        _blockCaptureIndices = savedBlockCaptureIndices;
        _blockOutputLocal = savedBlockOutputLocal;
        _currentFunctionReturnType = savedReturnType;
        _currentFunctionReturnRefinement = savedReturnRefinement;
        _currentThisType = savedThisType;
        _underscoreStack = savedUnderscoreStack;
        _loopStack = savedLoopStack;

        return method;
    }

    /// <summary>
    /// Pushes a <see cref="CompiledSubcommandNode"/> onto the IL
    /// evaluation stack via a call to
    /// <see cref="ToshHost.MakeSubcommandNode"/>.
    /// </summary>
    private void EmitSubcommandNodeExpression(
        string? name,
        SubcommandModifier modifiers,
        bool userDeclaredHelpFlag,
        IReadOnlyList<BoundParameter> flags,
        IReadOnlyList<BoundParameter> args,
        string[] childNames,
        LocalBuilder[] childLocals,
        MethodBuilder? bodyMethod)
    {
        // arg 0: name (string? or null)
        if (name is null)
            _il.Emit(OpCodes.Ldnull);
        else
            _il.Emit(OpCodes.Ldstr, name);

        // arg 1: modifiers (int)
        _il.Emit(OpCodes.Ldc_I4, (int)modifiers);

        // arg 2: userDeclaredHelpFlag (bool)
        _il.Emit(userDeclaredHelpFlag ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);

        // arg 3: flags (CompiledSubcommandParam[])
        EmitSubcommandParamArray(flags);

        // arg 4: args (CompiledSubcommandParam[])
        EmitSubcommandParamArray(args);

        // arg 5: childNames (string[])
        _il.Emit(OpCodes.Ldc_I4, childNames.Length);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (var i = 0; i < childNames.Length; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, childNames[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // arg 6: children (CompiledSubcommandNode[])
        _il.Emit(OpCodes.Ldc_I4, childLocals.Length);
        _il.Emit(OpCodes.Newarr, s_compiledSubcommandNodeType);
        for (var i = 0; i < childLocals.Length; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldloc, childLocals[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // arg 7: body (Action<object?[]>? or null)
        if (bodyMethod is null)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);                       // target = null (static method)
            _il.Emit(OpCodes.Ldftn, bodyMethod);
            _il.Emit(OpCodes.Newobj, s_actionOfObjArrayCtor);
        }

        _il.Emit(OpCodes.Call, s_hostMakeSubcommandNode);
    }

    /// <summary>
    /// Pushes a <c>CompiledSubcommandParam[]</c> onto the stack for
    /// <paramref name="params"/>, using
    /// <see cref="ToshHost.MakeSubcommandParam"/> per element.
    /// </summary>
    private void EmitSubcommandParamArray(IReadOnlyList<BoundParameter> @params)
    {
        _il.Emit(OpCodes.Ldc_I4, @params.Count);
        _il.Emit(OpCodes.Newarr, s_compiledSubcommandParamType);

        for (var i = 0; i < @params.Count; i++)
        {
            var p = @params[i];
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);

            // name
            _il.Emit(OpCodes.Ldstr, p.Name);
            // typeName
            if (p.TypeName is null)
                _il.Emit(OpCodes.Ldnull);
            else
                _il.Emit(OpCodes.Ldstr, p.TypeName);
            // isOptional
            _il.Emit(p.IsOptional ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // isRest
            _il.Emit(p.IsRest ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // isBool
            var isBool = IsBoolTypeName(p.TypeName);
            _il.Emit(isBool ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            // hasDefault + defaultValue
            if (p.Default is not null && TryGetLiteralDefaultValue(p.Default, out var defaultVal))
            {
                _il.Emit(OpCodes.Ldc_I4_1); // hasDefault = true
                EmitObjectLiteral(defaultVal);
            }
            else
            {
                _il.Emit(OpCodes.Ldc_I4_0); // hasDefault = false
                _il.Emit(OpCodes.Ldnull);   // defaultValue = null
            }

            _il.Emit(OpCodes.Call, s_hostMakeSubcommandParam);
            _il.Emit(OpCodes.Stelem_Ref);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="typeName"/> resolves
    /// to <c>bool</c> (handles nullable suffix and common aliases).
    /// </summary>
    private static bool IsBoolTypeName(string? typeName)
    {
        if (typeName is null) return false;
        var t = typeName.TrimEnd('?');
        return t is "bool" or "Boolean" or "System.Boolean";
    }

    /// <summary>
    /// Pushes a boxed object literal onto the stack for use as the
    /// default-value argument in compiled subcommand param descriptors.
    /// </summary>
    private void EmitObjectLiteral(object? value)
    {
        switch (value)
        {
            case null:
                _il.Emit(OpCodes.Ldnull);
                break;
            case bool b:
                _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Box, typeof(bool));
                break;
            case int i:
                _il.Emit(OpCodes.Ldc_I4, i);
                _il.Emit(OpCodes.Box, typeof(int));
                break;
            case long l:
                _il.Emit(OpCodes.Ldc_I8, l);
                _il.Emit(OpCodes.Box, typeof(long));
                break;
            case double d:
                _il.Emit(OpCodes.Ldc_R8, d);
                _il.Emit(OpCodes.Box, typeof(double));
                break;
            case string s:
                _il.Emit(OpCodes.Ldstr, s);
                break;
            default:
                // Fallback: convert to string representation.
                _il.Emit(OpCodes.Ldstr, value.ToString() ?? "");
                break;
        }
    }

    /// <summary>
    /// Emits a <see cref="global::Tosh.Runtime.ToshModuleAttribute"/>
    /// assembly attribute for <paramref name="module"/> and recurses
    /// into nested modules so each fully-qualified module path is
    /// recorded. <paramref name="parentPath"/> is the dotted prefix
    /// or <c>null</c> at the root.
    /// </summary>
    private void EmitToshModuleAttributes(BoundModuleDefinition module, string? parentPath)
    {
        var qualified = parentPath is null ? module.Name : $"{parentPath}.{module.Name}";
        var (start, length) = ExtendTypeDefinitionSpan(module.Span);
        var ctor = typeof(global::Tosh.Runtime.ToshModuleAttribute)
            .GetConstructor(new[] { typeof(string), typeof(int), typeof(int) })!;
        _ab.SetCustomAttribute(new CustomAttributeBuilder(
            ctor,
            new object[] { qualified, start, length }));

        foreach (var stmt in module.Body.Statements)
        {
            if (stmt is BoundModuleDefinition nested)
            {
                EmitToshModuleAttributes(nested, qualified);
            }
        }
    }

    /// <summary>
    /// Mangles a tosh identifier into a CLR-friendly identifier
    /// that downstream consumers (C#, F#, ILSpy, IDE tooling) can
    /// reference without escapes. Tosh allows hyphens in user-
    /// <summary>
    /// Walks <see cref="_unit"/>'s top-level statements and reports
    /// pairs of distinct tosh identifiers that mangle to the same
    /// CLR identifier within their respective namespace group
    /// (top-level types, top-level functions). Emits
    /// <c>tosh.compile.name_mangling_collision</c>-shaped diagnostics
    /// via <see cref="Diagnostics"/>.
    /// </summary>
    private void DetectNameManglingCollisions()
    {
        var typeBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var funcBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Bucket(Dictionary<string, List<string>> map, string original)
        {
            var mangled = MangleClrIdentifier(original);
            if (!map.TryGetValue(mangled, out var list))
            {
                list = new List<string>();
                map[mangled] = list;
            }
            // Skip exact duplicates — those are real "duplicate
            // definition" errors reported elsewhere; a collision
            // diagnostic on top would just be noise.
            if (!list.Contains(original, StringComparer.Ordinal))
            {
                list.Add(original);
            }
        }

        foreach (var statement in _unit.Root.Statements)
        {
            switch (statement)
            {
                case BoundClassDefinition c: Bucket(typeBuckets, c.Name); break;
                case BoundRecordDefinition r: Bucket(typeBuckets, r.Name); break;
                case BoundStructDefinition s: Bucket(typeBuckets, s.Name); break;
                case BoundEnumDefinition e: Bucket(typeBuckets, e.Name); break;
                case BoundUnionDefinition u: Bucket(typeBuckets, u.Name); break;
                case BoundInterfaceDefinition i: Bucket(typeBuckets, i.Name); break;
                case BoundModuleDefinition m: Bucket(typeBuckets, m.Name); break;
                case BoundFunctionDefinition f: Bucket(funcBuckets, f.Name); break;
            }
        }

        foreach (var (mangled, originals) in typeBuckets)
        {
            if (originals.Count >= 2)
            {
                Diagnostics.Add(
                    $"tosh.compile.name_mangling_collision: top-level types " +
                    $"[{string.Join(", ", originals.Select(o => $"'{o}'"))}] " +
                    $"all mangle to CLR identifier '{mangled}'");
            }
        }
        foreach (var (mangled, originals) in funcBuckets)
        {
            if (originals.Count >= 2)
            {
                Diagnostics.Add(
                    $"tosh.compile.name_mangling_collision: top-level functions " +
                    $"[{string.Join(", ", originals.Select(o => $"'{o}'"))}] " +
                    $"all mangle to CLR identifier '{mangled}'");
            }
        }
    }

    /// <summary>
    /// Translates a tosh identifier into a valid CLR identifier so
    /// it can be used in <c>DefineMethod</c> / <c>DefineType</c> /
    /// <c>DefineField</c>. Tosh allows hyphens and other CLR-illegal
    /// characters in user-defined names (e.g. <c>func to-json</c>);
    /// the CLR accepts them at the metadata level but every C-style
    /// language rejects them. This translates each non-identifier
    /// character to <c>_</c> and prepends <c>_</c> when the original
    /// starts with a digit. Tosh names that are already valid CLR
    /// identifiers pass through unchanged.
    /// </summary>
    internal static string MangleClrIdentifier(string toshName)
    {
        if (string.IsNullOrEmpty(toshName)) return "_";
        var needsMangling = false;
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            if (i == 0 && char.IsDigit(c)) { needsMangling = true; break; }
            if (!(char.IsLetterOrDigit(c) || c == '_')) { needsMangling = true; break; }
        }
        if (!needsMangling) return toshName;

        var sb = new System.Text.StringBuilder(toshName.Length + 1);
        if (char.IsDigit(toshName[0])) sb.Append('_');
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pattern-matches type-declaration statements that compiled
    /// tosh delegates to the engine via
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.RegisterTypeFromSource"/>.
    /// Returns the source span that should be re-evaluated.
    /// </summary>
    private static bool IsTypeDefinitionStatement(BoundStatement stmt, out TextSpan span)
    {
        switch (stmt)
        {
            case BoundClassDefinition c: span = c.Span; return true;
            case BoundRecordDefinition r: span = r.Span; return true;
            case BoundStructDefinition s: span = s.Span; return true;
            case BoundEnumDefinition e: span = e.Span; return true;
            case BoundUnionDefinition u: span = u.Span; return true;
            case BoundInterfaceDefinition i: span = i.Span; return true;
            case BoundTypeAliasStatement ta: span = ta.Span; return true;
            default: span = default; return false;
        }
    }

    /// <summary>
    /// Walks every <see cref="BoundFunctionDefinition"/> in the unit
    /// (top-level and nested) collecting captured symbols, then
    /// promotes those that refer to top-level variables to static
    /// fields on the program type. Top-level function names are
    /// also indexed so capture references that resolve to a peer
    /// user function can be ignored at IL time.
    /// </summary>
    private void PromoteCapturedSymbols()
    {
        // Index: top-level function names. The user-facing name
        // matches the capture symbol's Name field.
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundFunctionDefinition fn)
            {
                _topLevelFunctionNames.Add(fn.Name);
            }
        }

        // Set: top-level variable symbols. A capture is eligible
        // for static-field promotion only when its target symbol
        // matches one of these (or one of the top-level function
        // symbols, which we resolve through _userFunctions instead).
        var topLevelSymbols = new HashSet<BoundSymbol>();
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundVariableDeclaration decl)
            {
                topLevelSymbols.Add(decl.Symbol);
            }
        }

        // Collect every capture from every function definition in
        // the unit (recursively, since nested funcs may declare
        // their own).
        var seen = new HashSet<BoundSymbol>();
        var ordered = new List<BoundSymbol>();
        CollectCaptures(_unit.Root, seen, ordered);

        foreach (var sym in ordered)
        {
            if (!topLevelSymbols.Contains(sym)) continue;
            // Already promoted? (Defensive — `seen` should prevent
            // duplicates, but the same outer symbol can legitimately
            // appear once.)
            if (_staticFields.ContainsKey(sym)) continue;
            var field = _program.DefineField(
                $"_capture_{sym.Name}_{_staticFields.Count}",
                typeof(object),
                FieldAttributes.Private | FieldAttributes.Static);
            _staticFields[sym] = field;
        }

        static void CollectCaptures(BoundNode? node, HashSet<BoundSymbol> seen, List<BoundSymbol> ordered)
        {
            if (node is null) return;
            if (node is BoundFunctionDefinition fn)
            {
                foreach (var c in fn.Captures)
                {
                    if (seen.Add(c)) ordered.Add(c);
                }
            }
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    CollectCaptures(child, seen, ordered);
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn) CollectCaptures(bn, seen, ordered);
                    }
                }
            }
        }
    }

    private void DeclareUserFunction(BoundFunctionDefinition func)
    {
        // Captured outer variables are supported when every capture
        // either resolves to a top-level symbol promoted into a
        // static field (see PromoteCapturedSymbols) or names another
        // top-level function (those are dispatched through the
        // _userFunctions map, not via a value slot).
        foreach (var capture in func.Captures)
        {
            if (_staticFields.ContainsKey(capture)) continue;
            if (_topLevelFunctionNames.Contains(capture.Name)) continue;
            Diagnostics.Add(
                $"function '{func.Name}' captures '{capture.Name}' from a non-top-level scope (nested closures unsupported)");
            return;
        }
        if (_userFunctions.ContainsKey(func.Name))
        {
            Diagnostics.Add($"duplicate function definition: '{func.Name}'");
            return;
        }
        foreach (var p in func.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null)
            {
                Diagnostics.Add($"function '{func.Name}' uses unsupported parameter shape ('{p.Name}')");
                return;
            }
        }

        // Resolve declared CLR types per parameter and return slot.
        // Concrete BoundType.ClrType wins; everything else falls back
        // to <c>object</c> so the legacy dynamic dispatch shape is
        // preserved for unannotated and `: dynamic` slots.
        var paramClrTypes = new Type[func.Parameters.Count];
        var allParamsTyped = true;
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            var declared = func.Parameters[i].Symbol.DeclaredType;
            var clr = declared is { IsConcrete: true } ? declared.ClrType : null;
            if (clr is null || clr == typeof(object))
            {
                paramClrTypes[i] = typeof(object);
                allParamsTyped = false;
            }
            else
            {
                paramClrTypes[i] = clr;
            }
        }
        var returnBound = func.ReturnType;
        var returnClr = returnBound is { IsConcrete: true } ? returnBound.ClrType ?? typeof(object) : typeof(object);
        // A function is "fully typed" only when EVERY parameter
        // carries a concrete annotation AND the return is concrete
        // and non-object. Mixed shapes stay on the dynamic path —
        // matches what CheckCompileAnnotations enforces in compile
        // mode while keeping fully-untyped scripts (BoundUnitEmitter
        // is also exercised directly by tests with `func get() { …
        // }` style, no annotations) on their existing IL shape.
        var isTyped = allParamsTyped && returnClr != typeof(object) && func.Parameters.Count > 0
            || (func.Parameters.Count == 0 && returnClr != typeof(object));
        // Avoid name collisions with the auto-generated `Main`.
        if (string.Equals(func.Name, "Main", StringComparison.Ordinal)) isTyped = false;

        // For untyped user functions we emit a single
        // `Func_<name>(object,…) -> object` method that doubles as
        // both the internal call target and the pipeline dispatch
        // entry. Typed functions skip the shim entirely — their
        // typed primary `<name>(T1,…) -> TR` is reachable directly
        // from internal IL, and pipeline-stage dispatch coerces
        // args per parameter type via ToshHost.InvokeUserFunc.
        MethodBuilder primary;
        if (isTyped)
        {
            primary = _program.DefineMethod(
                MangleClrIdentifier(func.Name),
                MethodAttributes.Public | MethodAttributes.Static,
                returnClr,
                paramClrTypes);
            StampOriginalNameIfMangled(primary, func.Name);
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                primary.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name);
            }
        }
        else
        {
            var shimParamTypes = new Type[func.Parameters.Count];
            for (var i = 0; i < shimParamTypes.Length; i++) shimParamTypes[i] = typeof(object);
            primary = _program.DefineMethod(
                $"Func_{MangleClrIdentifier(func.Name)}",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(object),
                shimParamTypes);
            StampOriginalNameIfMangled(primary, func.Name);
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                primary.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name);
            }
        }

        _userFunctions[func.Name] = new UserFunction(
            primary,
            func,
            isTyped,
            paramClrTypes,
            isTyped ? returnClr : typeof(object));
    }

    /// <summary>
    /// Stack transition: <c>object → target</c>. For value targets
    /// uses Convert.ChangeType + Unbox_Any so boxed longs survive
    /// when the target is int / short / byte / etc. For reference
    /// targets emits a castclass (skipped when target is object).
    /// </summary>
    private static void CoerceObjectToTyped(ILGenerator il, Type target)
    {
        if (target == typeof(object)) return;
        if (target.IsValueType)
        {
            il.Emit(OpCodes.Ldtoken, target);
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) })!);
            il.Emit(OpCodes.Call, typeof(System.Convert).GetMethod(
                nameof(System.Convert.ChangeType),
                new[] { typeof(object), typeof(Type) })!);
            il.Emit(OpCodes.Unbox_Any, target);
            return;
        }
        il.Emit(OpCodes.Castclass, target);
    }

    private void EmitUserFunctionBody(BoundFunctionDefinition func)
    {
        if (!_userFunctions.TryGetValue(func.Name, out var entry) || entry.Definition != func)
        {
            // Declaration was rejected (closure / duplicate / bad params).
            return;
        }

        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedLocals = _typedParamLocals;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        try
        {
            _il = entry.Method.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            _typedParamLocals = new();
            _currentFunctionReturnType = entry.IsTyped ? entry.ReturnClrType : null;
            _currentFunctionReturnRefinement = entry.IsTyped ? func.ReturnType as RefinementType : null;
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                if (entry.IsTyped)
                {
                    // Box typed value-type params (and pass through
                    // ref-type params) into an object-typed local so
                    // the rest of the body emitter — which assumes
                    // parameter loads return `object` — keeps
                    // working without per-shape coercion.
                    var local = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Ldarg, i);
                    var clr = entry.ParamClrTypes[i];
                    if (clr.IsValueType) _il.Emit(OpCodes.Box, clr);
                    // Refinement-check enforcement on parameter
                    // entry: if the declared parameter type is a
                    // refinement, route the boxed value through
                    // ToshHost.CheckType. Throws a runtime
                    // diagnostic when the annotation is violated.
                    if (func.Parameters[i].Symbol.DeclaredType is RefinementType refParam)
                    {
                        _il.Emit(OpCodes.Ldstr, refParam.Name);
                        _il.Emit(OpCodes.Ldc_I4, func.Parameters[i].Span.Start);
                        _il.Emit(OpCodes.Ldc_I4, func.Parameters[i].Span.Length);
                        _il.Emit(OpCodes.Ldstr, $"parameter '{func.Parameters[i].Name}'");
                        _il.Emit(OpCodes.Call, s_hostCheckType);
                    }
                    _il.Emit(OpCodes.Stloc, local);
                    _typedParamLocals[func.Parameters[i].Symbol] = local;
                }
                else
                {
                    _paramSlots[func.Parameters[i].Symbol] = i;
                }
            }
            foreach (var stmt in func.Body.Statements)
            {
                EmitStatement(stmt);
            }
            // Fall-through return: typed funcs must produce a default
            // value of the declared return type; untyped funcs keep
            // the legacy `Ldnull/Ret` semantics.
            if (entry.IsTyped)
            {
                EmitDefaultValueForType(entry.ReturnClrType);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Ret);
        }
        finally
        {
            _il = savedIl;
            _locals = savedLocals;
            _paramSlots = savedParams;
            _typedParamLocals = savedTypedLocals;
            _currentFunctionReturnType = savedReturnType;
            _currentFunctionReturnRefinement = savedReturnRefinement;
        }
    }

    /// <summary>
    /// Pushes a default-of-T value matching <paramref name="t"/>:
    /// numeric zero, false, default char, or null for ref types.
    /// Used for typed-function fall-through returns.
    /// </summary>
    private void EmitDefaultValueForType(Type t)
    {
        if (!t.IsValueType)
        {
            _il.Emit(OpCodes.Ldnull);
            return;
        }
        if (t == typeof(bool) || t == typeof(int) || t == typeof(short) ||
            t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) ||
            t == typeof(ushort) || t == typeof(char))
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            return;
        }
        if (t == typeof(long) || t == typeof(ulong))
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Conv_I8);
            return;
        }
        if (t == typeof(float))
        {
            _il.Emit(OpCodes.Ldc_R4, 0f);
            return;
        }
        if (t == typeof(double))
        {
            _il.Emit(OpCodes.Ldc_R8, 0d);
            return;
        }
        // Generic value-type fallback: `default(T)` via initobj.
        var slot = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Ldloca, slot);
        _il.Emit(OpCodes.Initobj, t);
        _il.Emit(OpCodes.Ldloc, slot);
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
        if (_doc is not null)
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
        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: mappedFieldData,
            debugDirectoryBuilder: debugDir,
            entryPoint: MetadataTokens.MethodDefinitionHandle(_main.MetadataToken));

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        blob.WriteContentTo(output);
    }

    // ─── Statements ───────────────────────────────────────────────

    private void EmitStatement(BoundStatement statement)
    {
        // Statement-granularity sequence point: a debugger will
        // single-step from one tosh statement to the next, and stack
        // traces will name the line containing the active statement.
        // Pipeline / variable / control-flow statements all carry a
        // span — synthetic statements without a span are handled by
        // MarkSeqPoint's empty-span guard.
        MarkSeqPoint(statement.Span);
        switch (statement)
        {
            case BoundPipelineStatement pipelineStmt:
                EmitPipeline(pipelineStmt.Pipeline, asStatement: true);
                break;

            case BoundVariableDeclaration decl:
                EmitVariableDeclaration(decl);
                break;

            case BoundVariableAssignment assign:
                EmitVariableAssignment(assign);
                break;

            case BoundIfStatement ifStmt:
                EmitIfStatement(ifStmt);
                break;

            case BoundWhileStatement whileStmt:
                EmitWhileStatement(whileStmt);
                break;

            case BoundForStatement forStmt:
                EmitForStatement(forStmt);
                break;

            case BoundReturnStatement ret:
                EmitReturnStatement(ret);
                break;

            case BoundBreakStatement:
                if (_loopStack.Count == 0)
                {
                    Diagnostics.Add("'break' outside of a loop");
                    break;
                }
                _il.Emit(OpCodes.Leave, _loopStack.Peek().BreakLabel);
                break;

            case BoundContinueStatement:
                if (_loopStack.Count == 0)
                {
                    Diagnostics.Add("'continue' outside of a loop");
                    break;
                }
                _il.Emit(OpCodes.Leave, _loopStack.Peek().ContinueLabel);
                break;

            case BoundThrowStatement throwStmt:
                EmitThrowStatement(throwStmt);
                break;

            case BoundTryStatement tryStmt:
                EmitTryStatement(tryStmt);
                break;

            case BoundSwitchStatement switchStmt:
                EmitSwitchStatement(switchStmt);
                break;

            case BoundFunctionDefinition:
                // Nested function definitions are not yet supported.
                // Top-level ones are handled by Run() before reaching
                // this switch.
                Diagnostics.Add("nested function definitions are not supported");
                break;

            default:
                Diagnostics.Add($"unsupported statement: {statement.GetType().Name}");
                break;
        }
    }

    private void EmitVariableDeclaration(BoundVariableDeclaration decl)
    {
        // Captured top-level symbols live in a static field rather
        // than a method local so nested functions see by-reference
        // semantics.
        if (_staticFields.TryGetValue(decl.Symbol, out var captureField))
        {
            if (decl.Value is null)
            {
                _il.Emit(OpCodes.Ldnull);
            }
            else
            {
                var produced = EmitPipelineAsValue(decl.Value);
                if (produced is null)
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    BoxIfValueType(produced);
                }
            }
            _il.Emit(OpCodes.Stsfld, captureField);
            return;
        }

        if (decl.Value is null)
        {
            var slot = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, slot);
            _locals[decl.Symbol] = new LocalSlot(slot, typeof(object));
            return;
        }

        var producedType = EmitPipelineAsValue(decl.Value);
        if (producedType is null)
        {
            // Diagnostic was already recorded; still need a slot so
            // later refs don't crash. Default to object/null.
            producedType = typeof(object);
            _il.Emit(OpCodes.Ldnull);
        }
        // Refinement-check enforcement: when the declared symbol
        // type is a refinement, route the value through
        // ToshHost.CheckType so the IL throws a runtime diagnostic
        // on annotation failure, matching the interpreter's
        // semantics. The check leaves an `object` on the stack and
        // promotes the local's storage type accordingly.
        if (decl.Symbol.DeclaredType is RefinementType refDecl)
        {
            BoxIfValueType(producedType);
            _il.Emit(OpCodes.Ldstr, refDecl.Name);
            _il.Emit(OpCodes.Ldc_I4, decl.Span.Start);
            _il.Emit(OpCodes.Ldc_I4, decl.Span.Length);
            _il.Emit(OpCodes.Ldstr, $"var {decl.Symbol.Name}");
            _il.Emit(OpCodes.Call, s_hostCheckType);
            producedType = typeof(object);
        }
        var local = _il.DeclareLocal(producedType);
        _il.Emit(OpCodes.Stloc, local);
        _locals[decl.Symbol] = new LocalSlot(local, producedType);
    }

    /// <summary>
    /// Emits a reassignment <c>$x = ...</c>. Currently supports plain
    /// <c>=</c> on a previously-declared local whose stored type
    /// matches (or can be implicitly converted from) the new value,
    /// plus the compound forms <c>+= -= *= /= %=</c>. The compound
    /// forms are lowered to <c>$x = $x op rhs</c> at IL time, sharing
    /// the numeric-coercion path with <see cref="EmitNumericArith"/>.
    /// String <c>+=</c> falls into the string-concat branch.
    /// </summary>
    private void EmitVariableAssignment(BoundVariableAssignment assign)
    {
        if (assign.Symbol is not null && (_paramSlots.ContainsKey(assign.Symbol) || _typedParamLocals.ContainsKey(assign.Symbol)))
        {
            Diagnostics.Add($"cannot reassign parameter '{assign.Name}'");
            return;
        }
        if (assign.Symbol is not null && _staticFields.TryGetValue(assign.Symbol, out var captureField))
        {
            EmitCaptureFieldAssignment(captureField, assign);
            return;
        }
        if (assign.Symbol is null || !_locals.TryGetValue(assign.Symbol, out var slot))
        {
            Diagnostics.Add($"unresolved assignment target: {assign.Name}");
            return;
        }

        var op = assign.Operator;
        if (op == "=")
        {
            EmitPlainAssignmentInto(slot, assign);
            return;
        }

        // Compound assignment: load current value, emit RHS, combine,
        // store. Mirrors EmitBinaryOperator's coercion rules.
        var binaryOp = op switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => null,
        };
        if (binaryOp is null)
        {
            Diagnostics.Add($"unsupported assignment operator: '{op}'");
            return;
        }

        _il.Emit(OpCodes.Ldloc, slot.Local);
        var leftType = slot.Type;

        // String += rhs → string.Concat(left, rhs.ToString()).
        if (binaryOp == "+" && leftType == typeof(string))
        {
            var rhsStrType = EmitPipelineAsValue(assign.Value);
            if (rhsStrType is null) { _il.Emit(OpCodes.Pop); return; }
            ConvertToString(rhsStrType);
            _il.Emit(OpCodes.Call, typeof(string).GetMethod(
                nameof(string.Concat), new[] { typeof(string), typeof(string) })!);
            _il.Emit(OpCodes.Stloc, slot.Local);
            return;
        }

        var rhsType = EmitPipelineAsValue(assign.Value);
        if (rhsType is null) { _il.Emit(OpCodes.Pop); return; }
        var resultType = EmitNumericArith(binaryOp, leftType, rhsType);
        if (resultType is null) return;
        if (resultType != slot.Type)
        {
            ConvertNumeric(resultType, slot.Type);
        }
        _il.Emit(OpCodes.Stloc, slot.Local);
    }

    private void EmitPlainAssignmentInto(LocalSlot slot, BoundVariableAssignment assign)
    {
        var producedType = EmitPipelineAsValue(assign.Value);
        if (producedType is null) return;

        // Refinement enforcement on reassignment: when the target
        // symbol declared a refinement type, route the new value
        // through ToshHost.CheckType before storing. Promotes the
        // value to object, so we then re-coerce to the slot type
        // just like any other assignment shape.
        if (assign.Symbol is not null && assign.Symbol.DeclaredType is RefinementType refSym)
        {
            BoxIfValueType(producedType);
            _il.Emit(OpCodes.Ldstr, refSym.Name);
            _il.Emit(OpCodes.Ldc_I4, assign.Span.Start);
            _il.Emit(OpCodes.Ldc_I4, assign.Span.Length);
            _il.Emit(OpCodes.Ldstr, $"var {assign.Name}");
            _il.Emit(OpCodes.Call, s_hostCheckType);
            producedType = typeof(object);
        }

        if (producedType != slot.Type)
        {
            if (IsNumericType(producedType) && IsNumericType(slot.Type))
            {
                ConvertNumeric(producedType, slot.Type);
            }
            else if (slot.Type == typeof(object))
            {
                BoxIfValueType(producedType);
            }
            else
            {
                Diagnostics.Add(
                    $"assignment type mismatch for '{assign.Name}': " +
                    $"slot is {slot.Type.Name}, value is {producedType.Name}");
                _il.Emit(OpCodes.Pop);
                return;
            }
        }

        _il.Emit(OpCodes.Stloc, slot.Local);
    }

    /// <summary>
    /// Compound + plain assignment to a captured top-level symbol
    /// stored in a static field. The field is always typed
    /// <c>object</c>, so we coerce the right-hand side through the
    /// usual numeric / string / box paths and end with <c>Stsfld</c>.
    /// </summary>
    private void EmitCaptureFieldAssignment(FieldBuilder field, BoundVariableAssignment assign)
    {
        var op = assign.Operator;
        if (op == "=")
        {
            var produced = EmitPipelineAsValue(assign.Value);
            if (produced is null) return;
            BoxIfValueType(produced);
            // Refinement enforcement on captured-field reassignment.
            if (assign.Symbol is not null && assign.Symbol.DeclaredType is RefinementType refSym)
            {
                _il.Emit(OpCodes.Ldstr, refSym.Name);
                _il.Emit(OpCodes.Ldc_I4, assign.Span.Start);
                _il.Emit(OpCodes.Ldc_I4, assign.Span.Length);
                _il.Emit(OpCodes.Ldstr, $"var {assign.Name}");
                _il.Emit(OpCodes.Call, s_hostCheckType);
            }
            _il.Emit(OpCodes.Stsfld, field);
            return;
        }

        var binaryOp = op switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => null,
        };
        if (binaryOp is null)
        {
            Diagnostics.Add($"unsupported assignment operator: '{op}'");
            return;
        }

        // Load current field value, run the op via the same numeric
        // dispatcher used by `+`/`-`/etc., then store back. Both
        // operands are object (boxed) so EmitNumericArith unboxes
        // through Convert.* — same path as a regular `$x + y`.
        _il.Emit(OpCodes.Ldsfld, field);
        var rhsType = EmitPipelineAsValue(assign.Value);
        if (rhsType is null) { _il.Emit(OpCodes.Pop); return; }
        BoxIfValueType(rhsType);
        var resultType = EmitNumericArith(binaryOp, typeof(object), typeof(object));
        if (resultType is null) return;
        BoxIfValueType(resultType);
        _il.Emit(OpCodes.Stsfld, field);
    }

    /// <summary>
    /// Emits an <c>if cond { … } else { … }</c>. The condition must
    /// evaluate to <see cref="bool"/>; nested non-bool conditions are
    /// reported as a diagnostic and the block is skipped.
    /// </summary>
    private void EmitIfStatement(BoundIfStatement ifStmt)
    {
        var condType = EmitExpression(ifStmt.Condition);
        if (condType is null) return;
        if (condType != typeof(bool))
        {
            Diagnostics.Add($"if condition must be bool, got {condType.Name}");
            _il.Emit(OpCodes.Pop);
            return;
        }

        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Brfalse, elseLabel);
        EmitBlock(ifStmt.ThenBlock);
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        if (ifStmt.ElseBlock is not null)
        {
            EmitBlock(ifStmt.ElseBlock);
        }
        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits a <c>while cond { … }</c> or <c>until cond { … }</c>
    /// loop. The two forms differ only in which branch opcode tests
    /// the condition (<c>brfalse</c> vs <c>brtrue</c>).
    /// </summary>
    private void EmitWhileStatement(BoundWhileStatement whileStmt)
    {
        var topLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.MarkLabel(topLabel);
        var condType = EmitExpression(whileStmt.Condition);
        if (condType is null) return;
        if (condType != typeof(bool))
        {
            Diagnostics.Add($"while condition must be bool, got {condType.Name}");
            _il.Emit(OpCodes.Pop);
            return;
        }

        // until inverts the test: keep looping while condition is true.
        _il.Emit(whileStmt.IsUntil ? OpCodes.Brtrue : OpCodes.Brfalse, endLabel);
        _loopStack.Push(new LoopFrame(ContinueLabel: topLabel, BreakLabel: endLabel));
        try
        {
            EmitBlock(whileStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }
        _il.Emit(OpCodes.Br, topLabel);
        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits a <c>for var in source { … }</c> loop. v1 only handles
    /// integer ranges with no explicit step:
    ///   for $i in start..end { … }
    /// where Start and End are evaluable to integers (or object that
    /// can be coerced via <see cref="Convert.ToInt32(object)"/>).
    /// Ranges are inclusive on both ends, matching
    /// <c>ToshRange.Enumerate</c>. Other source shapes (array
    /// literals, command pipelines, lazy sequences) record an
    /// unsupported diagnostic so the caller can fall back.
    /// </summary>
    private void EmitForStatement(BoundForStatement forStmt)
    {
        // Range fast path: `for i in (1..10)` → int counter loop.
        if (forStmt.Source.Stages.Count == 1 &&
            forStmt.Source.Stages[0] is BoundExpressionStage stage &&
            stage.Value is BoundRange range &&
            range.Step is null &&
            range.End is not null)
        {
            EmitForRangeStatement(forStmt, range);
            return;
        }

        // Generic fallback: evaluate the source as an object,
        // coerce via ToshHost.ToEnumerable, walk via IEnumerator.
        EmitForEachStatement(forStmt);
    }

    private void EmitForRangeStatement(BoundForStatement forStmt, BoundRange range)
    {
        var startType = EmitExpression(range.Start);
        if (startType is null) return;
        ConvertNumeric(startType, typeof(int));
        var loopVarLocal = _il.DeclareLocal(typeof(int));
        _il.Emit(OpCodes.Stloc, loopVarLocal);
        _locals[forStmt.LoopVariable] = new LocalSlot(loopVarLocal, typeof(int));

        var endType = EmitExpression(range.End!);
        if (endType is null) return;
        ConvertNumeric(endType, typeof(int));
        var endLocal = _il.DeclareLocal(typeof(int));
        _il.Emit(OpCodes.Stloc, endLocal);

        var topLabelF = _il.DefineLabel();
        var endLabelF = _il.DefineLabel();
        var contLabelF = _il.DefineLabel();
        _il.MarkLabel(topLabelF);

        // Exit when loopVar > end (inclusive upper bound).
        _il.Emit(OpCodes.Ldloc, loopVarLocal);
        _il.Emit(OpCodes.Ldloc, endLocal);
        _il.Emit(OpCodes.Cgt);
        _il.Emit(OpCodes.Brtrue, endLabelF);

        _loopStack.Push(new LoopFrame(ContinueLabel: contLabelF, BreakLabel: endLabelF));
        try
        {
            EmitBlock(forStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }

        // continue lands here, before the increment.
        _il.MarkLabel(contLabelF);
        // loopVar++
        _il.Emit(OpCodes.Ldloc, loopVarLocal);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Stloc, loopVarLocal);
        _il.Emit(OpCodes.Br, topLabelF);
        _il.MarkLabel(endLabelF);
    }

    /// <summary>
    /// Generic <c>for x in expr</c>: evaluates the source as an
    /// object, calls <see cref="global::Tosh.Compiler.Runtime.ToshHost.ToEnumerable"/>
    /// to coerce it into <c>IEnumerable&lt;object?&gt;</c>, then
    /// walks via <c>GetEnumerator</c>/<c>MoveNext</c>/<c>Current</c>
    /// inside a try/finally that disposes the enumerator.
    /// </summary>
    private void EmitForEachStatement(BoundForStatement forStmt)
    {
        // Evaluate the source pipeline as a value.
        var srcType = EmitPipelineAsValue(forStmt.Source);
        if (srcType is null) return;
        BoxIfValueType(srcType);
        _il.Emit(OpCodes.Call, s_hostToEnumerable);

        // Get an IEnumerator<object?> from the IEnumerable<object?>.
        _il.Emit(OpCodes.Callvirt, s_enumerableGetEnumerator);
        var enumeratorLocal = _il.DeclareLocal(typeof(IEnumerator<object?>));
        _il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop variable is object-typed in the generic case.
        var loopVarLocal = _il.DeclareLocal(typeof(object));
        _locals[forStmt.LoopVariable] = new LocalSlot(loopVarLocal, typeof(object));

        var afterLoopLabel = _il.DefineLabel();
        _il.BeginExceptionBlock();
        var topLabelF = _il.DefineLabel();
        var endLabelF = _il.DefineLabel();
        _il.MarkLabel(topLabelF);

        // if (!enumerator.MoveNext()) goto end
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_enumeratorMoveNext);
        _il.Emit(OpCodes.Brfalse, endLabelF);

        // loopVar = enumerator.Current
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_enumeratorOfObjectGetCurrent);
        _il.Emit(OpCodes.Stloc, loopVarLocal);

        // break exits the foreach (running finally to dispose);
        // continue heads back to the MoveNext check at topLabelF.
        _loopStack.Push(new LoopFrame(ContinueLabel: topLabelF, BreakLabel: afterLoopLabel));
        try
        {
            EmitBlock(forStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }
        _il.Emit(OpCodes.Br, topLabelF);

        _il.MarkLabel(endLabelF);
        _il.Emit(OpCodes.Leave, afterLoopLabel);

        _il.BeginFinallyBlock();
        // enumerator?.Dispose();
        var skipDispose = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Brfalse_S, skipDispose);
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_disposableDispose);
        _il.MarkLabel(skipDispose);
        _il.EndExceptionBlock();
        _il.MarkLabel(afterLoopLabel);
    }

    private void EmitReturnStatement(BoundReturnStatement ret)
    {
        var typedReturn = _currentFunctionReturnType;
        if (ret.Value is null)
        {
            if (typedReturn is not null)
            {
                EmitDefaultValueForType(typedReturn);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Ret);
            return;
        }
        var t = EmitPipelineAsValue(ret.Value);
        if (t is null)
        {
            if (typedReturn is not null)
            {
                EmitDefaultValueForType(typedReturn);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Ret);
            return;
        }
        if (typedReturn is not null)
        {
            // Refinement enforcement on return value: route the
            // (boxed) result through ToshHost.CheckType. Throws
            // tosh.runtime.annotation_conversion_failed when the
            // returned value violates the declared refinement.
            // Performed before numeric coercion so the host sees
            // the raw runtime value the user produced.
            if (_currentFunctionReturnRefinement is RefinementType refRet)
            {
                BoxIfValueType(t);
                _il.Emit(OpCodes.Ldstr, refRet.Name);
                _il.Emit(OpCodes.Ldc_I4, ret.Span.Start);
                _il.Emit(OpCodes.Ldc_I4, ret.Span.Length);
                _il.Emit(OpCodes.Ldstr, "return value");
                _il.Emit(OpCodes.Call, s_hostCheckType);
                t = typeof(object);
            }
            // Coerce expression CLR type → declared return type.
            // Numeric returns: stay unboxed, use ConvertNumeric for
            // primitive widening (e.g. arithmetic widens to long but
            // declared return is `int`). Other shapes: box to object
            // and round-trip through Convert.ChangeType / castclass.
            if (typedReturn.IsValueType && IsNumericType(typedReturn) && IsNumericOrObject(t))
            {
                ConvertNumeric(t, typedReturn);
            }
            else if (typedReturn != t)
            {
                if (t.IsValueType) _il.Emit(OpCodes.Box, t);
                CoerceObjectToTyped(_il, typedReturn);
            }
        }
        else
        {
            BoxIfValueType(t);
        }
        _il.Emit(OpCodes.Ret);
    }

    private void EmitBlock(BoundBlock block)
    {
        foreach (var statement in block.Statements)
        {
            EmitStatement(statement);
        }
    }

    /// <summary>
    /// <c>throw expr</c>: evaluate the value pipeline, box it, and
    /// hand it to <see cref="global::Tosh.Compiler.Runtime.ToshHost.ThrowValue"/>
    /// which raises a <see cref="global::Tosh.Runtime.ThrowSignalException"/>.
    /// A bare <c>throw</c> with no value re-throws the message-only
    /// default, matching the interpreter.
    /// </summary>
    private void EmitThrowStatement(BoundThrowStatement throwStmt)
    {
        if (throwStmt.Value is null)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            var t = EmitPipelineAsValue(throwStmt.Value);
            if (t is null)
            {
                _il.Emit(OpCodes.Ldnull);
            }
            else
            {
                BoxIfValueType(t);
            }
        }
        _il.Emit(OpCodes.Call, s_hostThrowValue);
    }

    /// <summary>
    /// <c>try { … } [catch [(name)] { … }] [finally { … }]</c>. The
    /// catch arm filters on <see cref="global::Tosh.Runtime.ThrowSignalException"/>
    /// so user code can't accidentally swallow CLR-level errors that
    /// the interpreter wouldn't have caught either. The bound
    /// <see cref="BoundCatchClause.Variable"/> (when present) is
    /// bound to the unwrapped <see cref="global::Tosh.Runtime.ThrowSignalException.Value"/>.
    /// </summary>
    private void EmitTryStatement(BoundTryStatement tryStmt)
    {
        _il.BeginExceptionBlock();
        EmitBlock(tryStmt.TryBlock);

        if (tryStmt.Catch is { } catchClause)
        {
            _il.BeginCatchBlock(typeof(global::Tosh.Runtime.ThrowSignalException));
            // The exception is on the eval stack. If the catch
            // declared a variable, unwrap to .Value and stash; else
            // pop.
            if (catchClause.Variable is { } sym)
            {
                _il.Emit(OpCodes.Call, s_hostThrownValueOf);
                var slot = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, slot);
                _locals[sym] = new LocalSlot(slot, typeof(object));
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }
            EmitBlock(catchClause.Body);
        }

        if (tryStmt.Finally is { } finallyBlock)
        {
            _il.BeginFinallyBlock();
            EmitBlock(finallyBlock);
        }

        _il.EndExceptionBlock();
    }

    // ─── match / switch ──────────────────────────────────────────

    /// <summary>
    /// Lowers <c>match $x { 1 =&gt; …; default =&gt; … }</c> to a
    /// chain of pattern tests + guards. The match value is
    /// evaluated once into a fresh local; each arm's pattern is
    /// dispatched by bound-IR shape:
    /// <list type="bullet">
    /// <item><see cref="BoundComparisonPattern"/> →
    /// <c>OperatorEvaluator.Matches(value, op, operand, false)</c>.</item>
    /// <item><see cref="BoundRange"/> →
    /// <c>value &gt;= start &amp;&amp; value &lt;= end</c> (open-ended
    /// upper bound matches anything ≥ start).</item>
    /// <item>Anything else → <c>OperatorEvaluator.AreEqual(value, pattern)</c>.</item>
    /// </list>
    /// Guards run after a successful pattern test with the match
    /// value bound to <c>_</c>. Match arms are required to be
    /// expression-shaped (single-pipeline body) to participate in
    /// expression context; richer block bodies fall back to a
    /// diagnostic for now.
    /// </summary>
    private Type? EmitMatchExpression(BoundMatchExpression match)
    {
        var valueType = EmitExpression(match.Value);
        if (valueType is null) return null;
        BoxIfValueType(valueType);
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        var resultLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, resultLocal);

        var endLabel = _il.DefineLabel();

        _underscoreStack.Push(valueLocal);
        try
        {
            foreach (var arm in match.Arms)
            {
                var nextArmLabel = _il.DefineLabel();

                if (!arm.IsWildcard)
                {
                    if (!EmitPatternTest(arm.Pattern!, valueLocal))
                        return null;
                    _il.Emit(OpCodes.Brfalse, nextArmLabel);
                }

                if (arm.Guard is not null)
                {
                    var guardType = EmitExpression(arm.Guard);
                    if (guardType is null) return null;
                    BoxIfValueType(guardType);
                    _il.Emit(OpCodes.Call, s_opToBoolean);
                    _il.Emit(OpCodes.Brfalse, nextArmLabel);
                }

                if (!EmitMatchArmBodyAsValue(arm, resultLocal))
                    return null;
                _il.Emit(OpCodes.Br, endLabel);

                _il.MarkLabel(nextArmLabel);
            }
        }
        finally
        {
            _underscoreStack.Pop();
        }

        _il.MarkLabel(endLabel);
        _il.Emit(OpCodes.Ldloc, resultLocal);
        return typeof(object);
    }

    /// <summary>
    /// Lowers <c>switch ($x) { case … { }; default { } }</c> to the
    /// same pattern-test chain as <see cref="EmitMatchExpression"/>,
    /// but each case body executes for side effects only — no
    /// result is materialized. The <c>default</c> block runs when
    /// no case matches.
    /// </summary>
    private void EmitSwitchStatement(BoundSwitchStatement switchStmt)
    {
        var valueType = EmitExpression(switchStmt.Value);
        if (valueType is null) return;
        BoxIfValueType(valueType);
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        var endLabel = _il.DefineLabel();

        _underscoreStack.Push(valueLocal);
        try
        {
            foreach (var c in switchStmt.Cases)
            {
                var nextCaseLabel = _il.DefineLabel();

                if (!EmitPatternTest(c.Pattern, valueLocal)) return;
                _il.Emit(OpCodes.Brfalse, nextCaseLabel);

                if (c.Guard is not null)
                {
                    var guardType = EmitExpression(c.Guard);
                    if (guardType is null) return;
                    BoxIfValueType(guardType);
                    _il.Emit(OpCodes.Call, s_opToBoolean);
                    _il.Emit(OpCodes.Brfalse, nextCaseLabel);
                }

                EmitBlock(c.Body);
                _il.Emit(OpCodes.Br, endLabel);

                _il.MarkLabel(nextCaseLabel);
            }

            if (switchStmt.Default is { } def)
            {
                EmitBlock(def);
            }
        }
        finally
        {
            _underscoreStack.Pop();
        }

        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Pushes a <see cref="bool"/> onto the eval stack indicating
    /// whether <paramref name="pattern"/> matches the value held in
    /// <paramref name="valueLocal"/>. Returns false (with a
    /// diagnostic recorded) for unsupported pattern shapes.
    /// </summary>
    private bool EmitPatternTest(BoundExpression pattern, LocalBuilder valueLocal)
    {
        switch (pattern)
        {
            case BoundComparisonPattern cmp:
                _il.Emit(OpCodes.Ldloc, valueLocal);
                _il.Emit(OpCodes.Ldstr, cmp.Operator);
                {
                    var t = EmitExpression(cmp.Operand);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Call, s_opMatches);
                return true;

            case BoundRange range:
                // value >= start
                _il.Emit(OpCodes.Ldloc, valueLocal);
                _il.Emit(OpCodes.Ldstr, ">=");
                {
                    var t = EmitExpression(range.Start);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Call, s_opMatches);

                if (range.End is not null)
                {
                    // && value <= end
                    var falseLabel = _il.DefineLabel();
                    var doneLabel = _il.DefineLabel();
                    _il.Emit(OpCodes.Brfalse, falseLabel);

                    _il.Emit(OpCodes.Ldloc, valueLocal);
                    _il.Emit(OpCodes.Ldstr, "<=");
                    {
                        var t = EmitExpression(range.End);
                        if (t is null) return false;
                        BoxIfValueType(t);
                    }
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Call, s_opMatches);
                    _il.Emit(OpCodes.Br, doneLabel);

                    _il.MarkLabel(falseLabel);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.MarkLabel(doneLabel);
                }
                return true;

            default:
                _il.Emit(OpCodes.Ldloc, valueLocal);
                {
                    var t = EmitExpression(pattern);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Call, s_opAreEqual);
                return true;
        }
    }

    /// <summary>
    /// Emits a match-arm body in expression context: the body must
    /// be a <see cref="BoundBlock"/> wrapping a single
    /// <see cref="BoundPipelineStatement"/>; that pipeline's value
    /// becomes the arm's result and is stored into
    /// <paramref name="resultLocal"/>. Multi-statement arm bodies
    /// are not yet supported in value context.
    /// </summary>
    private bool EmitMatchArmBodyAsValue(BoundMatchArm arm, LocalBuilder resultLocal)
    {
        if (arm.Body.Statements.Count == 1
            && arm.Body.Statements[0] is BoundPipelineStatement pipeStmt)
        {
            var t = EmitPipelineAsValue(pipeStmt.Pipeline);
            if (t is null) return false;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Stloc, resultLocal);
            return true;
        }
        Diagnostics.Add(
            "match arm: only single-pipeline expression bodies are "
            + "supported in value context");
        return false;
    }

    // ─── Pipelines ────────────────────────────────────────────────

    private Type? EmitPipeline(BoundPipeline pipeline, bool asStatement)
    {
        if (pipeline.Stages.Count == 0)
        {
            Diagnostics.Add("empty pipeline");
            return null;
        }
        if (pipeline.Stages.Count >= 2)
        {
            return EmitMultiStagePipeline(pipeline, asStatement);
        }

        var stage = pipeline.Stages[0];
        switch (stage)
        {
            case BoundExpressionStage exprStage:
                if (asStatement)
                {
                    var t = EmitExpression(exprStage.Value);
                    if (t is not null) _il.Emit(OpCodes.Pop);
                    return null;
                }
                return EmitExpression(exprStage.Value);

            case BoundCommandCall call when _userFunctions.ContainsKey(call.Name):
                return EmitUserFunctionCall(call, asStatement);

            case BoundCommandCall call when asStatement:
                EmitCommandCallStatement(call);
                return null;

            case BoundCommandCall call:
                return EmitHostInvokeValue(call);

            default:
                Diagnostics.Add($"unsupported pipeline stage: {stage.GetType().Name}");
                return null;
        }
    }

    private Type? EmitPipelineAsValue(BoundPipeline pipeline) => EmitPipeline(pipeline, asStatement: false);

    /// <summary>
    /// Emits IL for a 2+ stage pipeline. Each stage is dispatched
    /// through <see cref="global::Tosh.Compiler.Runtime.ToshHost.RunStage"/>
    /// which receives the previous stage's
    /// <see cref="IAsyncEnumerable{T}"/> as input. The accumulator
    /// is held in a single local of type <c>IAsyncEnumerable&lt;object?&gt;</c>
    /// so each stage's IL is short and uniform. The terminal call
    /// is either <c>DrainStatement</c> (statement context) or
    /// <c>DrainValue</c> (value context, returns
    /// <see cref="List{T}"/>).
    ///
    /// v1 limitations:
    ///   • User-defined functions cannot be pipeline stages.
    ///   • Splat / named arguments still surface as diagnostics.
    /// </summary>
    private Type? EmitMultiStagePipeline(BoundPipeline pipeline, bool asStatement)
    {
        // Reusable accumulator local: each stage replaces it.
        var accLocal = _il.DeclareLocal(typeof(IAsyncEnumerable<object?>));

        // Stage 0: either a command call (Phase 1) or an arbitrary
        // expression seeding the pipeline (Phase 3).
        switch (pipeline.Stages[0])
        {
            case BoundCommandCall first when _userFunctions.TryGetValue(first.Name, out var firstUserEntry):
                if (!EmitUserFuncPipelineStage(first, firstUserEntry, isFirstStage: true, accLocal))
                    return null;
                break;

            case BoundCommandCall first:
                // ResolveCommand(name) → RunStage(cmd, EmptyInput(), args0)
                _il.Emit(OpCodes.Ldstr, first.Name);
                _il.Emit(OpCodes.Call, s_hostResolveCommand);
                _il.Emit(OpCodes.Call, s_hostEmptyInput);
                if (!EmitStageArgsArray(first)) return null;
                RequireTier(2, "builtin command dispatch (pipeline stage)");
                _il.Emit(OpCodes.Call, s_hostRunStage);
                break;

            case BoundExpressionStage exprStage:
                // SeedFromValue(<expr>) — boxes the value (if needed)
                // and turns it into IAsyncEnumerable<object?>.
                var exprType = EmitExpression(exprStage.Value);
                if (exprType is null) return null;
                BoxIfValueType(exprType);
                _il.Emit(OpCodes.Call, s_hostSeedFromValue);
                break;

            default:
                Diagnostics.Add(
                    $"unsupported first pipeline stage: {pipeline.Stages[0].GetType().Name}");
                return null;
        }
        _il.Emit(OpCodes.Stloc, accLocal);

        // Stages 1..N-1: chain through RunStage(cmd, acc, args).
        for (var i = 1; i < pipeline.Stages.Count; i++)
        {
            if (pipeline.Stages[i] is not BoundCommandCall stage)
            {
                Diagnostics.Add(
                    $"non-command pipeline stage at position {i}: {pipeline.Stages[i].GetType().Name}");
                return null;
            }
            if (_userFunctions.TryGetValue(stage.Name, out var userEntry))
            {
                if (!EmitUserFuncPipelineStage(stage, userEntry, isFirstStage: false, accLocal))
                    return null;
                _il.Emit(OpCodes.Stloc, accLocal);
                continue;
            }

            _il.Emit(OpCodes.Ldstr, stage.Name);
            _il.Emit(OpCodes.Call, s_hostResolveCommand);
            _il.Emit(OpCodes.Ldloc, accLocal);
            if (!EmitStageArgsArray(stage)) return null;
            RequireTier(2, "builtin command dispatch (multi-stage pipeline)");
            _il.Emit(OpCodes.Call, s_hostRunStage);
            _il.Emit(OpCodes.Stloc, accLocal);
        }

        // Drain.
        _il.Emit(OpCodes.Ldloc, accLocal);
        if (asStatement)
        {
            _il.Emit(OpCodes.Call, s_hostDrainStatement);
            return null;
        }
        _il.Emit(OpCodes.Call, s_hostDrainValue);
        return s_listOfObject;
    }

    /// <summary>
    /// Pushes an <c>object[]</c> of evaluated, boxed arguments for
    /// a single pipeline stage. Named arguments are wrapped in a
    /// <see cref="global::Tosh.Language.NamedArgument"/> instance
    /// (matching what the interpreter passes commands). Splat
    /// arguments expand at runtime via
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.SpreadArgs"/>;
    /// when any splat is present we build the array via
    /// <c>List&lt;object?&gt;.ToArray()</c> instead of a
    /// fixed-length allocation.
    /// </summary>
    private bool EmitStageArgsArray(BoundCommandCall call)
        => EmitArgsArray(call);

    /// <summary>
    /// Emits a user function as a pipeline stage. Pushes onto the
    /// stack the <see cref="IAsyncEnumerable{T}"/> produced by
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.RunUserFuncStage"/>
    /// — the caller is responsible for storing it back into the
    /// pipeline accumulator. Validates arity at compile time:
    /// the user function must take either exactly the call's
    /// argument count (ignores input) or exactly one more
    /// (takes one input element per call as the leading
    /// parameter).
    /// </summary>
    private bool EmitUserFuncPipelineStage(
        BoundCommandCall stage,
        UserFunction entry,
        bool isFirstStage,
        LocalBuilder accLocal)
    {
        var paramCount = entry.Definition.Parameters.Count;
        var argCount = stage.Arguments.Count;
        if (paramCount != argCount && paramCount != argCount + 1)
        {
            Diagnostics.Add(
                $"user function '{stage.Name}' as a pipeline stage expects "
                + $"{argCount} or {argCount + 1} parameters, got {paramCount}");
            return false;
        }
        foreach (var a in stage.Arguments)
        {
            if (a.IsSplat || a.Name is not null)
            {
                Diagnostics.Add(
                    $"user function '{stage.Name}' as a pipeline stage: "
                    + "splat/named arguments not yet supported");
                return false;
            }
        }

        // ldtoken methodBuilder + Call MethodBase.GetMethodFromHandle
        // → MethodInfo. PersistedAssemblyBuilder resolves the token
        // lazily after the assembly is loaded. Pipeline-stage
        // dispatch targets the user function's canonical method
        // (typed primary for typed funcs, dynamic Func_<name>
        // for untyped). Per-parameter arg coercion happens in
        // ToshHost.InvokeUserFunc.
        _il.Emit(OpCodes.Ldtoken, entry.Method);
        _il.Emit(OpCodes.Call, s_methodBaseGetFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Ldc_I4, paramCount);
        if (isFirstStage)
        {
            _il.Emit(OpCodes.Call, s_hostEmptyInput);
        }
        else
        {
            _il.Emit(OpCodes.Ldloc, accLocal);
        }
        if (!EmitArgsArray(stage)) return false;
        RequireTier(2, "user function dispatch via host (pipeline stage)");
        _il.Emit(OpCodes.Call, s_hostRunUserFuncStage);
        return true;
    }

    /// <summary>
    /// Pushes a freshly-built <see cref="ShellBlock"/> onto the eval
    /// stack: <c>ToshHost.MakeBlock(span.Start, span.Length, captures)</c>.
    /// Captures are materialized as a <c>Dictionary&lt;string,object?&gt;</c>
    /// snapshot of the named locals/params the binder identified.
    /// </summary>
    private void EmitMakeBlock(BoundBlockExpression block)
    {
        if (CanCompileBlockBody(block))
        {
            // Build the subset of captures that must be passed at runtime
            // (static-field captures remain accessible directly in the body).
            var runtimeCaptures = new List<BoundSymbol>(block.Captures.Count);
            foreach (var c in block.Captures)
            {
                if (!_staticFields.ContainsKey(c)) runtimeCaptures.Add(c);
            }
            var captureIndices = new Dictionary<BoundSymbol, int>(runtimeCaptures.Count);
            for (var i = 0; i < runtimeCaptures.Count; i++)
                captureIndices[runtimeCaptures[i]] = i;

            var blockMethod = EmitBlockBodyMethod(block, runtimeCaptures, captureIndices);
            if (blockMethod is not null)
            {
                // new Func<object?,object[],List<object?>>(null, ldftn __block_N)
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, blockMethod);
                _il.Emit(OpCodes.Newobj, s_funcBlockBodyCtor);

                // captureValues = new object[runtimeCaptures.Count] { ... }
                _il.Emit(OpCodes.Ldc_I4, runtimeCaptures.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (var i = 0; i < runtimeCaptures.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    var cap = runtimeCaptures[i];
                    if (_typedParamLocals.TryGetValue(cap, out var typedLocal))
                        _il.Emit(OpCodes.Ldloc, typedLocal);
                    else if (_paramSlots.TryGetValue(cap, out var pIdx))
                        _il.Emit(OpCodes.Ldarg, pIdx);
                    else if (_staticFields.TryGetValue(cap, out var sf))
                        _il.Emit(OpCodes.Ldsfld, sf);
                    else if (_locals.TryGetValue(cap, out var s))
                    {
                        _il.Emit(OpCodes.Ldloc, s.Local);
                        BoxIfValueType(s.Type);
                    }
                    else
                    {
                        Diagnostics.Add($"block capture '{cap.Name}' has no IL slot");
                        _il.Emit(OpCodes.Ldnull);
                    }
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, s_hostMakeCompiledBlock);
                return;
            }
        }

        // Fallback: source-replay.
        EmitMakeBlockFallback(block);
    }

    private static bool CanCompileBlockBody(BoundBlockExpression block)
    {
        if (block.Body.Statements.Count == 0) return true;
        if (block.Body.Statements.Count != 1) return false;
        if (block.Body.Statements[0] is not BoundPipelineStatement ps) return false;
        if (ps.Pipeline.Stages.Count != 1) return false;
        return ps.Pipeline.Stages[0] is BoundExpressionStage or BoundCommandCall;
    }

    private MethodBuilder? EmitBlockBodyMethod(
        BoundBlockExpression block,
        List<BoundSymbol> runtimeCaptures,
        Dictionary<BoundSymbol, int> captureIndices)
    {
        var methodName = $"__block_{block.Body.Span.Start}";
        var blockMethod = _program.DefineMethod(
            methodName,
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(List<object>),
            new[] { typeof(object), typeof(object[]) });

        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedParams = _typedParamLocals;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        var savedThisType = _currentThisType;
        var savedUnderscoreStack = _underscoreStack;
        var savedLoopStack = _loopStack;
        var savedBlockOutput = _blockOutputLocal;
        var savedBlockCaptures = _blockCaptureIndices;
        try
        {
            _il = blockMethod.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            _typedParamLocals = new();
            _currentFunctionReturnType = null;
            _currentFunctionReturnRefinement = null;
            _currentThisType = null;
            _underscoreStack = new();
            _loopStack = new();
            _blockCaptureIndices = captureIndices;

            var resultsLocal = _il.DeclareLocal(typeof(List<object>));
            _blockOutputLocal = resultsLocal;
            _il.Emit(OpCodes.Newobj, s_listCtor);
            _il.Emit(OpCodes.Stloc, resultsLocal);

            if (block.Body.Statements.Count == 1
                && block.Body.Statements[0] is BoundPipelineStatement ps)
            {
                if (!EmitBlockBodyPipelineStatement(ps))
                    return null;
            }

            _il.Emit(OpCodes.Ldloc, resultsLocal);
            _il.Emit(OpCodes.Ret);
            return blockMethod;
        }
        catch
        {
            return null;
        }
        finally
        {
            _il = savedIl;
            _locals = savedLocals;
            _paramSlots = savedParams;
            _typedParamLocals = savedTypedParams;
            _currentFunctionReturnType = savedReturnType;
            _currentFunctionReturnRefinement = savedReturnRefinement;
            _currentThisType = savedThisType;
            _underscoreStack = savedUnderscoreStack;
            _loopStack = savedLoopStack;
            _blockOutputLocal = savedBlockOutput;
            _blockCaptureIndices = savedBlockCaptures;
        }
    }

    private bool EmitBlockBodyPipelineStatement(BoundPipelineStatement ps)
    {
        var stage = ps.Pipeline.Stages[0];
        switch (stage)
        {
            case BoundExpressionStage exprStage:
                {
                    var t = EmitExpression(exprStage.Value);
                    if (t is null) return false;
                    BoxIfValueType(t);
                    var tmp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, tmp);
                    var skip = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Brfalse_S, skip);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Callvirt, s_listAdd);
                    _il.MarkLabel(skip);
                    return true;
                }

            case BoundCommandCall call when _userFunctions.ContainsKey(call.Name):
                {
                    var t = EmitUserFunctionCall(call, asStatement: false);
                    if (t is null) return false;
                    BoxIfValueType(t);
                    var tmp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, tmp);
                    var skip = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Brfalse_S, skip);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Callvirt, s_listAdd);
                    _il.MarkLabel(skip);
                    return true;
                }

            case BoundCommandCall call:
                {
                    if (!EmitHostArgs(call)) return false;
                    RequireTier(2, "command invocation (block collect)");
                    _il.Emit(OpCodes.Call, s_hostInvokeCollect);
                    var items = _il.DeclareLocal(typeof(object[]));
                    _il.Emit(OpCodes.Stloc, items);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, items);
                    _il.Emit(OpCodes.Callvirt, s_listAddRange);
                    return true;
                }

            default:
                Diagnostics.Add($"unsupported block body stage: {stage.GetType().Name}");
                return false;
        }
    }

    private void EmitMakeBlockFallback(BoundBlockExpression block)
    {
        RequireTier(3, "block argument (re-evaluates source at runtime)");
        _il.Emit(OpCodes.Ldc_I4, block.Body.Span.Start);
        _il.Emit(OpCodes.Ldc_I4, block.Body.Span.Length);
        if (block.Captures.Count == 0)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            _il.Emit(OpCodes.Newobj, s_dictCtor);
            foreach (var capture in block.Captures)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldstr, capture.Name);
                if (_typedParamLocals.TryGetValue(capture, out var typedParamLocal))
                    _il.Emit(OpCodes.Ldloc, typedParamLocal);
                else if (_paramSlots.TryGetValue(capture, out var paramIndex))
                    _il.Emit(OpCodes.Ldarg, paramIndex);
                else if (_locals.TryGetValue(capture, out var slot))
                {
                    _il.Emit(OpCodes.Ldloc, slot.Local);
                    BoxIfValueType(slot.Type);
                }
                else
                {
                    Diagnostics.Add($"block capture '{capture.Name}' has no IL slot");
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Callvirt, s_dictSetItem);
            }
        }
        _il.Emit(OpCodes.Call, s_hostMakeBlock);
    }

    /// <summary>
    /// Emits a call to a user-defined function. Untyped callees take
    /// <c>object</c> for every parameter and return <c>object</c>;
    /// args are evaluated and boxed in declaration order. Fully
    /// typed callees use their declared CLR signature directly —
    /// args are coerced to the typed param shape (numeric widening
    /// for primitives, Convert.ChangeType + Unbox_Any for other
    /// value types, castclass for ref types) and the return is
    /// produced in its declared CLR type. Statement context pops
    /// the unused return value.
    /// </summary>
    private Type? EmitUserFunctionCall(BoundCommandCall call, bool asStatement)
    {
        var entry = _userFunctions[call.Name];
        var expected = entry.Definition.Parameters.Count;
        if (call.Arguments.Count != expected)
        {
            Diagnostics.Add(
                $"function '{call.Name}' expects {expected} argument(s), got {call.Arguments.Count}");
            return null;
        }
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.IsSplat || arg.Name is not null)
            {
                Diagnostics.Add(
                    $"function '{call.Name}': splat/named arguments not yet supported");
                return null;
            }
            var argType = EmitExpression(arg.Value);
            if (argType is null) return null;
            if (entry.IsTyped)
            {
                var target = entry.ParamClrTypes[i];
                if (target == typeof(object))
                {
                    BoxIfValueType(argType);
                }
                else if (target.IsValueType && IsNumericType(target) && IsNumericOrObject(argType))
                {
                    ConvertNumeric(argType, target);
                }
                else if (target != argType)
                {
                    if (argType.IsValueType) _il.Emit(OpCodes.Box, argType);
                    CoerceObjectToTyped(_il, target);
                }
            }
            else
            {
                BoxIfValueType(argType);
            }
        }
        _il.Emit(OpCodes.Call, entry.Method);
        var resultType = entry.IsTyped ? entry.ReturnClrType : typeof(object);
        if (asStatement)
        {
            _il.Emit(OpCodes.Pop);
            return null;
        }
        return resultType;
    }

    private void EmitCommandCallStatement(BoundCommandCall call)
    {
        if (!string.Equals(call.Name, "echo", StringComparison.Ordinal))
        {
            EmitHostInvokeStatement(call);
            return;
        }

        // Echo's inlined Console.WriteLine fast path can't unfold a
        // splat at compile time (the runtime length isn't known).
        // Build the argument array via EmitArgsArray (which handles
        // the splat expansion) and route through EchoArgs so the
        // output matches the inlined "join with space" formatting,
        // not the registered echo command's table layout. Named
        // args still fall back to the host bridge.
        var hasSplat = false;
        foreach (var a in call.Arguments)
        {
            if (a.Name is not null)
            {
                EmitHostInvokeStatement(call);
                return;
            }
            if (a.IsSplat) hasSplat = true;
        }
        if (hasSplat)
        {
            if (!EmitArgsArray(call)) return;
            _il.Emit(OpCodes.Call, s_hostEchoArgs);
            return;
        }

        if (call.Arguments.Count == 0)
        {
            _il.Emit(OpCodes.Ldstr, string.Empty);
            _il.Emit(OpCodes.Call, s_writeLineString);
            return;
        }

        if (call.Arguments.Count == 1)
        {
            var argType = EmitExpression(call.Arguments[0].Value);
            if (argType is null) return;
            BoxIfValueType(argType);
            _il.Emit(OpCodes.Call, s_writeLineObject);
            return;
        }

        // Multi-arg: build a string[] and call String.Join(" ", arr).
        _il.Emit(OpCodes.Ldstr, " ");
        _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var argType = EmitExpression(call.Arguments[i].Value);
            if (argType is null)
            {
                _il.Emit(OpCodes.Ldstr, "?");
            }
            else
            {
                ConvertToString(argType);
            }
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, s_stringJoin);
        _il.Emit(OpCodes.Call, s_writeLineString);
    }

    /// <summary>
    /// Emits a statement-context dispatch through the runtime host
    /// shim. Pushes <c>name</c> and an <c>object[]</c> of evaluated
    /// arguments, calls <c>ToshHost.InvokeStatement</c>, and pops the
    /// returned "last yielded value". Splat / named args are not yet
    /// supported and emit a diagnostic.
    /// </summary>
    private void EmitHostInvokeStatement(BoundCommandCall call)
    {
        if (!EmitHostArgs(call)) return;
        RequireTier(2, "command invocation (statement)");
        _il.Emit(OpCodes.Call, s_hostInvokeStatement);
        _il.Emit(OpCodes.Pop);
    }

    /// <summary>
    /// Emits a value-context dispatch through the runtime host shim.
    /// Returns <see cref="object"/> (the unwrapped single value, the
    /// list when multiple were yielded, or null).
    /// </summary>
    private Type? EmitHostInvokeValue(BoundCommandCall call)
    {
        if (!EmitHostArgs(call)) return null;
        RequireTier(2, "command invocation (value)");
        _il.Emit(OpCodes.Call, s_hostInvokeValue);
        return typeof(object);
    }

    /// <summary>
    /// Pushes <c>name</c> and an <c>object[]</c> of boxed argument
    /// values onto the eval stack. Returns false (with a diagnostic
    /// recorded) when an argument shape is unsupported, leaving the
    /// stack in an undefined state — callers must abort emission.
    /// </summary>
    private bool EmitHostArgs(BoundCommandCall call)
    {
        _il.Emit(OpCodes.Ldstr, call.Name);
        return EmitArgsArray(call);
    }

    /// <summary>
    /// Builds an <c>object[]</c> from <paramref name="call"/>'s
    /// arguments and leaves it on the eval stack. Picks one of two
    /// emission strategies:
    /// <list type="bullet">
    /// <item><b>Fast path</b> (no splat): <c>newarr object[N]</c>
    /// + <c>stelem.ref</c> in slot order.</item>
    /// <item><b>Splat path</b>: build a <see cref="List{T}"/>,
    /// add positional / named entries, expand splats via the host
    /// shim, then call <c>ToArray()</c>.</item>
    /// </list>
    /// Named entries are emitted as
    /// <c>new NamedArgument(name, value)</c> so commands see the
    /// same shape they get from the interpreter.
    /// </summary>
    private bool EmitArgsArray(BoundCommandCall call)
    {
        var hasSplat = false;
        foreach (var arg in call.Arguments)
        {
            if (arg.IsSplat) { hasSplat = true; break; }
        }

        if (!hasSplat)
        {
            _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
            _il.Emit(OpCodes.Newarr, typeof(object));
            for (var i = 0; i < call.Arguments.Count; i++)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, i);
                if (!EmitOneArgValue(call, call.Arguments[i])) return false;
                _il.Emit(OpCodes.Stelem_Ref);
            }
            return true;
        }

        // Splat path: List<object?> + ToArray(), so the array
        // length isn't known until runtime.
        _il.Emit(OpCodes.Newobj, s_listCtor);
        foreach (var arg in call.Arguments)
        {
            if (arg.IsSplat)
            {
                if (arg.Name is not null)
                {
                    Diagnostics.Add(
                        $"command '{call.Name}': named splat arguments are not allowed");
                    return false;
                }
                if (arg.Value is BoundBlockExpression)
                {
                    Diagnostics.Add(
                        $"command '{call.Name}': cannot splat a block expression");
                    return false;
                }
                _il.Emit(OpCodes.Dup);
                var t = EmitExpression(arg.Value);
                if (t is null) return false;
                BoxIfValueType(t);
                _il.Emit(OpCodes.Call, s_hostSpreadArgs);
                continue;
            }

            _il.Emit(OpCodes.Dup);
            if (!EmitOneArgValue(call, arg)) return false;
            _il.Emit(OpCodes.Callvirt, s_listAdd);
        }
        _il.Emit(OpCodes.Callvirt, s_listToArray);
        return true;
    }

    /// <summary>
    /// Emits the value for a single (non-splat) argument and leaves
    /// it on the eval stack as <see cref="object"/>. Block
    /// expressions are materialized via
    /// <see cref="EmitMakeBlock"/>; named arguments are wrapped in
    /// a fresh <see cref="global::Tosh.Language.NamedArgument"/>;
    /// everything else is the boxed expression value.
    /// </summary>
    private bool EmitOneArgValue(BoundCommandCall call, BoundArgument arg)
    {
        if (arg.Value is BoundBlockExpression block)
        {
            if (arg.Name is not null)
            {
                Diagnostics.Add(
                    $"command '{call.Name}': named block arguments not yet supported");
                return false;
            }
            EmitMakeBlock(block);
            return true;
        }
        if (arg.Name is not null)
        {
            _il.Emit(OpCodes.Ldstr, arg.Name);
            var nt = EmitExpression(arg.Value);
            if (nt is null) return false;
            BoxIfValueType(nt);
            _il.Emit(OpCodes.Newobj, s_namedArgumentCtor);
            return true;
        }
        var t = EmitExpression(arg.Value);
        if (t is null) return false;
        BoxIfValueType(t);
        return true;
    }

    // ─── Expressions ──────────────────────────────────────────────

    private Type? EmitExpression(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteral literal:
                return EmitLiteral(literal);

            case BoundVariableReference varRef:
                return EmitVariableReference(varRef);

            case BoundBinaryOperator binOp:
                return EmitBinaryOperator(binOp);

            case BoundUnaryOperator unOp:
                return EmitUnaryOperator(unOp);

            case BoundSubexpression sub:
                // (expr) — unwrap to the inner pipeline.
                return EmitPipelineAsValue(sub.Pipeline);

            case BoundInterpolatedString interp:
                return EmitInterpolatedString(interp);

            case BoundMemberAccess member:
                return EmitMemberAccess(member);

            case BoundStaticMemberAccess staticMember:
                _il.Emit(OpCodes.Ldstr, staticMember.Path);
                RequireTier(2, "qualified-name resolution (Foo.bar)");
                _il.Emit(OpCodes.Call, s_hostResolveQualifiedAccess);
                return typeof(object);

            case BoundStaticMethodCall staticCall:
                return EmitStaticMethodCall(staticCall);

            case BoundIndexAccess index:
                return EmitIndexAccess(index);

            case BoundArrayLiteral arr:
                return EmitArrayLiteral(arr);

            case BoundRecordLiteral rec:
                return EmitRecordLiteral(rec);

            case BoundDictLiteral dict:
                return EmitDictLiteral(dict);

            case BoundMatchExpression match:
                return EmitMatchExpression(match);

            case BoundNewObject newObj:
                return EmitNewObject(newObj);

            case BoundMethodCall methodCall:
                return EmitMethodCall(methodCall);

            case BoundRange range:
                return EmitRange(range);

            default:
                Diagnostics.Add($"unsupported expression: {expression.GetType().Name}");
                return null;
        }
    }

    private Type? EmitLiteral(BoundLiteral literal)
    {
        switch (literal.Value)
        {
            case null:
                _il.Emit(OpCodes.Ldnull);
                return typeof(object);

            case string s:
                _il.Emit(OpCodes.Ldstr, s);
                return typeof(string);

            case bool b:
                _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                return typeof(bool);

            case int i:
                _il.Emit(OpCodes.Ldc_I4, i);
                return typeof(int);

            case long l:
                _il.Emit(OpCodes.Ldc_I8, l);
                return typeof(long);

            case double d:
                _il.Emit(OpCodes.Ldc_R8, d);
                return typeof(double);

            default:
                Diagnostics.Add($"unsupported literal type: {literal.Value.GetType().Name}");
                return null;
        }
    }

    private Type? EmitVariableReference(BoundVariableReference varRef)
    {
        if (varRef.Symbol is not null && _typedParamLocals.TryGetValue(varRef.Symbol, out var typedParamLocal))
        {
            // Typed user-function param materialized into an
            // object-typed local at method entry. Loads as object so
            // the rest of the body emitter — which assumes parameter
            // refs produce object — keeps working unchanged.
            _il.Emit(OpCodes.Ldloc, typedParamLocal);
            return typeof(object);
        }
        // Block-capture: the symbol was snapshotted into the captureValues
        // array (arg 1) at block-construction time. Load by index.
        if (varRef.Symbol is not null && _blockCaptureIndices.TryGetValue(varRef.Symbol, out var captureIdx))
        {
            _il.Emit(OpCodes.Ldarg_1);           // object[] _captureValues
            _il.Emit(OpCodes.Ldc_I4, captureIdx);
            _il.Emit(OpCodes.Ldelem_Ref);
            return typeof(object);
        }
        if (varRef.Symbol is not null && _paramSlots.TryGetValue(varRef.Symbol, out var paramIndex))
        {
            _il.Emit(OpCodes.Ldarg, paramIndex);
            return typeof(object);
        }
        if (varRef.Symbol is not null && _staticFields.TryGetValue(varRef.Symbol, out var captureField))
        {
            _il.Emit(OpCodes.Ldsfld, captureField);
            return typeof(object);
        }
        if (varRef.Symbol is null)
        {
            // Symbol-less references reach the emitter for special
            // names like the match-arm scrutinee placeholder `_`.
            if (string.Equals(varRef.Name, "_", StringComparison.Ordinal)
                && _underscoreStack.Count > 0)
            {
                _il.Emit(OpCodes.Ldloc, _underscoreStack.Peek());
                return typeof(object);
            }
            // Inside a compiled block-body method, `_` (no dollar) with no
            // outer-scope symbol is the pipeline item passed as arg 0.
            if (string.Equals(varRef.Name, "_", StringComparison.Ordinal)
                && _blockOutputLocal is not null)
            {
                _il.Emit(OpCodes.Ldarg_0);       // object? _item
                return typeof(object);
            }
            // `$this` inside a class-method body lowers to slot 0
            // typed as the shell. Member access / method calls then
            // pick up the shell's static type and lower to direct
            // ldfld / callvirt via the existing dispatch paths.
            if (string.Equals(varRef.Name, "this", StringComparison.Ordinal)
                && _currentThisType is not null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                return _currentThisType;
            }
            Diagnostics.Add($"unresolved variable: {varRef.Name}");
            return null;
        }
        if (!_locals.TryGetValue(varRef.Symbol, out var slot))
        {
            Diagnostics.Add($"unresolved variable: {varRef.Name}");
            return null;
        }
        _il.Emit(OpCodes.Ldloc, slot.Local);
        return slot.Type;
    }

    /// <summary>
    /// Emits an interpolated string by building a <c>string[]</c> of
    /// each part's stringified value and calling
    /// <c>string.Concat(string[])</c>. Each part is either literal
    /// text or an expression hole whose value is converted to string
    /// via boxing + <c>object.ToString</c>.
    /// </summary>
    private Type? EmitInterpolatedString(BoundInterpolatedString interp)
    {
        var partCount = interp.Parts.Count;
        if (partCount == 0)
        {
            _il.Emit(OpCodes.Ldstr, string.Empty);
            return typeof(string);
        }

        if (partCount == 1 && interp.Parts[0] is BoundInterpolatedLiteral onlyLit)
        {
            _il.Emit(OpCodes.Ldstr, onlyLit.Text);
            return typeof(string);
        }

        _il.Emit(OpCodes.Ldc_I4, partCount);
        _il.Emit(OpCodes.Newarr, typeof(string));

        for (var i = 0; i < partCount; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);

            switch (interp.Parts[i])
            {
                case BoundInterpolatedLiteral lit:
                    _il.Emit(OpCodes.Ldstr, lit.Text);
                    break;

                case BoundInterpolatedExpression hole:
                    if (hole.Expression is null)
                    {
                        Diagnostics.Add($"interpolation hole has no bound expression: {hole.SourceText}");
                        _il.Emit(OpCodes.Ldstr, string.Empty);
                    }
                    else
                    {
                        var holeType = EmitExpression(hole.Expression);
                        if (holeType is null)
                        {
                            _il.Emit(OpCodes.Ldstr, string.Empty);
                        }
                        else
                        {
                            ConvertToString(holeType);
                        }
                    }
                    break;

                default:
                    Diagnostics.Add($"unsupported interpolated part: {interp.Parts[i].GetType().Name}");
                    _il.Emit(OpCodes.Ldstr, string.Empty);
                    break;
            }

            _il.Emit(OpCodes.Stelem_Ref);
        }

        var concatArray = typeof(string).GetMethod(
            nameof(string.Concat),
            new[] { typeof(string[]) })!;
        _il.Emit(OpCodes.Call, concatArray);
        return typeof(string);
    }

    /// <summary>
    /// Emits IL for <c>$target.path</c> / <c>$target?.path</c>. The
    /// dotted path is preserved verbatim; the runtime accessor
    /// walks each segment dynamically (matching the interpreter's
    /// behaviour). Always produces an <see cref="object"/> on the
    /// stack — refinement via cast happens at the use site.
    /// </summary>
    /// <summary>
    /// Emits a <c>new TypeName(args…)</c> expression by delegating
    /// to <see cref="global::Tosh.Compiler.Runtime.ToshHost.NewObject"/>.
    /// Named arguments are not yet supported in this position.
    /// </summary>
    private Type? EmitNewObject(BoundNewObject newObj)
    {
        // Direct lowering when a CLR shell exists for this type and
        // the call site arity matches the shell's primary ctor —
        // emits `newobj <ctor>` instead of routing through
        // ToshHost.NewObject. Result lands on the stack as the
        // typed shell type so member access / method calls on the
        // resulting local can take the typed paths too.
        if (_clrTypeShells.TryGetValue(newObj.TypeName, out var shell)
            && shell.SupportsDirectNewObj
            && shell.CtorParamTypes.Length == newObj.Arguments.Count
            && newObj.Arguments.All(a => a.Name is null && !a.IsSplat))
        {
            for (int i = 0; i < newObj.Arguments.Count; i++)
            {
                var argType = EmitExpression(newObj.Arguments[i].Value);
                if (argType is null) return null;
                BoxIfValueType(argType);
            }
            _il.Emit(OpCodes.Newobj, shell.Ctor);
            return shell.Type;
        }

        _il.Emit(OpCodes.Ldstr, newObj.TypeName);
        // Build object?[] of arg values.
        _il.Emit(OpCodes.Ldc_I4, newObj.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < newObj.Arguments.Count; i++)
        {
            var arg = newObj.Arguments[i];
            if (arg.Name is not null)
            {
                Diagnostics.Add(
                    $"named arguments to `new {newObj.TypeName}` are not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpression(arg.Value);
            if (t is null) return null;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Call, s_hostNewObject);
        return typeof(object);
    }

    /// <summary>
    /// Emits an instance method call <c>$target.Method(args)</c>.
    /// Routes through <see cref="global::Tosh.Compiler.Runtime.ToshHost.InvokeMember"/>
    /// so tosh-defined types and CLR types use the same dispatch
    /// surface. Named arguments aren't supported in this position.
    /// </summary>
    /// <summary>
    /// Emits IL for a dotted static method call like
    /// <c>Lib.greet()</c>. The host bridge resolves the path
    /// against modules, classes, and CLR types and dispatches the
    /// invocation.
    /// </summary>
    private Type? EmitStaticMethodCall(BoundStaticMethodCall call)
    {
        _il.Emit(OpCodes.Ldstr, call.Path);
        _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.Name is not null)
            {
                Diagnostics.Add(
                    $"named arguments to static method '{call.Path}' are not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var at = EmitExpression(arg.Value);
            if (at is null) return null;
            BoxIfValueType(at);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        RequireTier(2, "qualified-method invocation (Foo.bar(...))");
        _il.Emit(OpCodes.Call, s_hostInvokeQualifiedMethod);
        return typeof(object);
    }

    private Type? EmitMethodCall(BoundMethodCall call)
    {
        var t = EmitExpression(call.Target);
        if (t is null) return null;
        BoxIfValueType(t);

        // Fast path: target's static type is a CLR shell with the
        // method declared on it. We bypass ToshHost.InvokeMember and
        // emit a direct callvirt against the shell's MethodBuilder.
        // Rejected when:
        //   - any argument is named (the trampoline doesn't model
        //     keyword args; let the host bridge do the runtime
        //     fallback);
        //   - the call site uses null-safe access (preserve the
        //     host's null check);
        //   - argument count doesn't match the trampoline arity.
        if (!call.NullSafe
            && _clrShellsByType.TryGetValue(t, out var shell)
            && shell.Methods.TryGetValue(call.MethodName, out var mb)
            && mb.GetParameters().Length == call.Arguments.Count)
        {
            var allPositional = true;
            foreach (var arg in call.Arguments)
            {
                if (arg.Name is not null) { allPositional = false; break; }
            }
            if (allPositional)
            {
                // Push args; box value types, leave reference types
                // as-is. Method is `object`-typed so no coercion.
                foreach (var arg in call.Arguments)
                {
                    var at = EmitExpression(arg.Value);
                    if (at is null) return null;
                    BoxIfValueType(at);
                }
                _il.Emit(OpCodes.Callvirt, mb);
                return typeof(object);
            }
        }

        _il.Emit(OpCodes.Ldstr, call.MethodName);
        _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.Name is not null)
            {
                Diagnostics.Add(
                    $"named arguments to method '{call.MethodName}' are not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var at = EmitExpression(arg.Value);
            if (at is null) return null;
            BoxIfValueType(at);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(call.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        RequireTier(2, "dynamic member access");
        _il.Emit(OpCodes.Call, s_hostInvokeMember);
        return typeof(object);
    }

    private Type? EmitMemberAccess(BoundMemberAccess member)
    {
        // Direct ldfld when target produces a known CLR shell type
        // and the member path is a single segment naming a public
        // field on that shell. Multi-segment paths (e.g. "a.b.c")
        // and missing fields fall back to the dynamic
        // ToshHost.GetMember path. Null-safe access also stays on
        // the dynamic path so the host's null check is preserved.
        if (!member.NullSafe && !member.MemberPath.Contains('.'))
        {
            // Peek at target's static type via a no-side-effect
            // emission of the target expression. We need the type
            // BEFORE emitting, so we emit, check, and either commit
            // (ldfld) or wrap into the host call. The target was
            // already pushed onto the stack here.
            var t = EmitExpression(member.Target);
            if (t is null) return null;
            if (_clrShellsByType.TryGetValue(t, out var shell)
                && shell.Fields.TryGetValue(member.MemberPath, out var field))
            {
                _il.Emit(OpCodes.Ldfld, field);
                return typeof(object);
            }
            // Fall through to host dispatch with target still on stack.
            BoxIfValueType(t);
            _il.Emit(OpCodes.Ldstr, member.MemberPath);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Call, s_hostGetMember);
            return typeof(object);
        }

        var t2 = EmitExpression(member.Target);
        if (t2 is null) return null;
        BoxIfValueType(t2);
        _il.Emit(OpCodes.Ldstr, member.MemberPath);
        _il.Emit(member.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Call, s_hostGetMember);
        return typeof(object);
    }

    /// <summary>
    /// Emits IL for <c>$target[index]</c>. The host shim uses the
    /// runtime's <c>ShellIndexingUtilities.GetIndexedValue</c> so
    /// behaviour matches the interpreter for lists, dicts, strings,
    /// and CLR indexers. <see cref="IndexLookupKind"/> beyond the
    /// default isn't yet plumbed through.
    /// </summary>
    private Type? EmitIndexAccess(BoundIndexAccess index)
    {
        if (index.LookupKind != global::Tosh.Runtime.IndexLookupKind.Default)
        {
            Diagnostics.Add(
                $"index lookup kind '{index.LookupKind}' not yet supported");
            return null;
        }
        var tt = EmitExpression(index.Target);
        if (tt is null) return null;
        BoxIfValueType(tt);
        var ti = EmitExpression(index.Index);
        if (ti is null) return null;
        BoxIfValueType(ti);
        _il.Emit(OpCodes.Call, s_hostGetIndex);
        return typeof(object);
    }

    /// <summary>
    /// Emits a range literal as a <see cref="global::Tosh.Runtime.ToshRange"/>
    /// instance. Each bound is converted to <c>int</c> via
    /// <see cref="Convert.ToInt32(object)"/> so non-int sources
    /// (e.g. doubles or strings) follow the same coercion path the
    /// interpreter uses. Missing <c>Step</c> / <c>End</c> push
    /// default(<c>int?</c>).
    /// </summary>
    private Type? EmitRange(BoundRange range)
    {
        var startT = EmitExpression(range.Start);
        if (startT is null) return null;
        if (startT != typeof(int))
        {
            BoxIfValueType(startT);
            _il.Emit(OpCodes.Call, s_convertToInt32);
        }

        EmitNullableInt(range.Step);
        EmitNullableInt(range.End);

        _il.Emit(OpCodes.Newobj, s_toshRangeCtor);
        return typeof(global::Tosh.Runtime.ToshRange);
    }

    private void EmitNullableInt(BoundExpression? expr)
    {
        if (expr is null)
        {
            var loc = _il.DeclareLocal(typeof(int?));
            _il.Emit(OpCodes.Ldloca, loc);
            _il.Emit(OpCodes.Initobj, typeof(int?));
            _il.Emit(OpCodes.Ldloc, loc);
            return;
        }

        var t = EmitExpression(expr);
        if (t is null)
        {
            // Diagnostic already added; emit a default so IL stays balanced.
            var loc = _il.DeclareLocal(typeof(int?));
            _il.Emit(OpCodes.Ldloca, loc);
            _il.Emit(OpCodes.Initobj, typeof(int?));
            _il.Emit(OpCodes.Ldloc, loc);
            return;
        }
        if (t != typeof(int))
        {
            BoxIfValueType(t);
            _il.Emit(OpCodes.Call, s_convertToInt32);
        }
        _il.Emit(OpCodes.Newobj, s_nullableInt32Ctor);
    }

    /// <summary>
    /// Emits a list literal as <c>new List&lt;object?&gt;()</c>
    /// followed by <c>Add</c> calls for each item. Spread elements
    /// (<c>...$xs</c>) reuse <see cref="global::Tosh.Compiler.Runtime.ToshHost.SpreadArgs"/>:
    /// the source value is enumerated and each element pushed into
    /// the same backing list.
    /// </summary>
    private Type? EmitArrayLiteral(BoundArrayLiteral arr)
    {
        _il.Emit(OpCodes.Newobj, s_listCtor);
        foreach (var item in arr.Items)
        {
            _il.Emit(OpCodes.Dup);
            var t = EmitExpression(item.Value);
            if (t is null) return null;
            BoxIfValueType(t);
            if (item.IsSpread)
            {
                // Stack: list, list, value -> SpreadArgs(list, value)
                _il.Emit(OpCodes.Call, s_hostSpreadArgs);
            }
            else
            {
                _il.Emit(OpCodes.Callvirt, s_listAdd);
            }
        }
        return s_listOfObject;
    }

    /// <summary>
    /// Emits a record literal (<c>{ name: "x", age: 1 }</c>) as
    /// <c>new Dictionary&lt;string, object?&gt;()</c> with one
    /// indexer-set per field. Computed-name and spread entries
    /// aren't yet supported.
    /// </summary>
    private Type? EmitRecordLiteral(BoundRecordLiteral rec)
    {
        _il.Emit(OpCodes.Newobj, s_dictCtor);
        foreach (var entry in rec.Fields)
        {
            if (entry is not BoundRecordField field)
            {
                Diagnostics.Add(
                    $"record literal: '{entry.GetType().Name}' entries not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldstr, field.Name);
            var vt = EmitExpression(field.Value);
            if (vt is null) return null;
            BoxIfValueType(vt);
            _il.Emit(OpCodes.Callvirt, s_dictSetItem);
        }
        return s_dictOfStringObject;
    }

    /// <summary>
    /// Emits a dict literal (<c>{ "k" =&gt; v, ... }</c>) as
    /// <c>new Dictionary&lt;object, object?&gt;()</c> populated via
    /// the indexer setter. Keys are evaluated as expressions and
    /// boxed.
    /// </summary>
    private Type? EmitDictLiteral(BoundDictLiteral dict)
    {
        _il.Emit(OpCodes.Newobj, s_dictObjCtor);
        foreach (var entry in dict.Entries)
        {
            _il.Emit(OpCodes.Dup);
            var kt = EmitExpression(entry.Key);
            if (kt is null) return null;
            BoxIfValueType(kt);
            var vt = EmitExpression(entry.Value);
            if (vt is null) return null;
            BoxIfValueType(vt);
            _il.Emit(OpCodes.Callvirt, s_dictObjSetItem);
        }
        return s_dictOfObjectObject;
    }

    private Type? EmitBinaryOperator(BoundBinaryOperator binOp)
    {
        // String concat: "a" + b → string.Concat(a, b.ToString()).
        if (binOp.Operator == "+")
        {
            var leftType = EmitExpression(binOp.Left);
            if (leftType is null) return null;
            if (leftType == typeof(string))
            {
                var rightType = EmitExpression(binOp.Right);
                if (rightType is null) return null;
                ConvertToString(rightType);
                _il.Emit(OpCodes.Call, typeof(string).GetMethod(
                    nameof(string.Concat),
                    new[] { typeof(string), typeof(string) })!);
                return typeof(string);
            }

            // Numeric path.
            var rightTypeNum = EmitExpression(binOp.Right);
            if (rightTypeNum is null) return null;
            return EmitNumericArith("+", leftType, rightTypeNum);
        }

        var l = EmitExpression(binOp.Left);
        if (l is null) return null;
        var r = EmitExpression(binOp.Right);
        if (r is null) return null;

        switch (binOp.Operator)
        {
            case "-":
            case "*":
            case "/":
            case "%":
                return EmitNumericArith(binOp.Operator, l, r);

            case "==":
                EmitEquality(l, r, invert: false);
                return typeof(bool);
            case "!=":
                EmitEquality(l, r, invert: true);
                return typeof(bool);
            case "<":
                EmitComparison(l, r, OpCodes.Clt);
                return typeof(bool);
            case ">":
                EmitComparison(l, r, OpCodes.Cgt);
                return typeof(bool);
            case "<=":
                // !(l > r)
                EmitComparison(l, r, OpCodes.Cgt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);
            case ">=":
                // !(l < r)
                EmitComparison(l, r, OpCodes.Clt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);

            default:
                Diagnostics.Add($"unsupported binary operator: '{binOp.Operator}'");
                return null;
        }
    }

    /// <summary>
    /// Emits arithmetic between two numeric operands already on the
    /// stack (left below, right on top). Coerces both to the smallest
    /// common numeric type and emits the matching IL opcode.
    /// </summary>
    private Type? EmitNumericArith(string op, Type left, Type right)
    {
        var common = CommonNumericType(left, right);
        if (common is null)
        {
            Diagnostics.Add($"non-numeric operands to '{op}': {left.Name} and {right.Name}");
            return null;
        }

        // Right is on top; convert if needed.
        if (right != common) ConvertNumeric(right, common);

        // Left is below right; need to reorder to convert it.
        if (left != common)
        {
            var temp = _il.DeclareLocal(common);
            _il.Emit(OpCodes.Stloc, temp);
            ConvertNumeric(left, common);
            _il.Emit(OpCodes.Ldloc, temp);
        }

        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); break;
            case "-": _il.Emit(OpCodes.Sub); break;
            case "*": _il.Emit(OpCodes.Mul); break;
            case "/": _il.Emit(OpCodes.Div); break;
            case "%": _il.Emit(OpCodes.Rem); break;
            default:
                Diagnostics.Add($"unsupported numeric op: '{op}'");
                return null;
        }
        return common;
    }

    private void EmitEquality(Type left, Type right, bool invert)
    {
        if (IsNumericType(left) && IsNumericType(right))
        {
            var common = CommonNumericType(left, right)!;
            if (right != common) ConvertNumeric(right, common);
            if (left != common)
            {
                var temp = _il.DeclareLocal(common);
                _il.Emit(OpCodes.Stloc, temp);
                ConvertNumeric(left, common);
                _il.Emit(OpCodes.Ldloc, temp);
            }
            _il.Emit(OpCodes.Ceq);
        }
        else
        {
            // Box value types and call object.Equals(a, b).
            if (right.IsValueType) _il.Emit(OpCodes.Box, right);
            var rTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, rTemp);
            if (left.IsValueType) _il.Emit(OpCodes.Box, left);
            _il.Emit(OpCodes.Ldloc, rTemp);
            _il.Emit(OpCodes.Call, s_objectEquals);
        }
        if (invert)
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Ceq);
        }
    }

    private void EmitComparison(Type left, Type right, OpCode op)
    {
        var common = CommonNumericType(left, right);
        if (common is null)
        {
            Diagnostics.Add($"non-numeric operands to comparison: {left.Name} and {right.Name}");
            return;
        }
        if (right != common) ConvertNumeric(right, common);
        if (left != common)
        {
            var temp = _il.DeclareLocal(common);
            _il.Emit(OpCodes.Stloc, temp);
            ConvertNumeric(left, common);
            _il.Emit(OpCodes.Ldloc, temp);
        }
        _il.Emit(op);
    }

    private Type? EmitUnaryOperator(BoundUnaryOperator unOp)
    {
        var operandType = EmitExpression(unOp.Operand);
        if (operandType is null) return null;

        switch (unOp.Operator)
        {
            case "-":
                if (operandType == typeof(object))
                {
                    // Coerce to long; users wanting double semantics can
                    // multiply by a double literal first.
                    _il.Emit(OpCodes.Call, s_convertToInt64);
                    operandType = typeof(long);
                }
                if (!IsNumericType(operandType))
                {
                    Diagnostics.Add($"unary '-' on non-numeric: {operandType.Name}");
                    return null;
                }
                _il.Emit(OpCodes.Neg);
                return operandType;

            case "!":
                if (operandType != typeof(bool))
                {
                    Diagnostics.Add($"unary '!' on non-bool: {operandType.Name}");
                    return null;
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);

            default:
                Diagnostics.Add($"unsupported unary operator: '{unOp.Operator}'");
                return null;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(double);

    /// <summary>
    /// Like <see cref="IsNumericType"/> but also accepts
    /// <see cref="object"/>. Object-typed slots show up whenever a
    /// value comes from a function parameter or a function-call
    /// result — v1 emits all of those as <c>object</c> for uniform
    /// dispatch. We handle them in numeric contexts by coercing at
    /// runtime via <see cref="Convert.ToInt32(object)"/> /
    /// <see cref="Convert.ToInt64(object)"/> / <see
    /// cref="Convert.ToDouble(object)"/>.
    /// </summary>
    private static bool IsNumericOrObject(Type t) =>
        IsNumericType(t) || t == typeof(object);

    private static Type? CommonNumericType(Type left, Type right)
    {
        if (!IsNumericOrObject(left) || !IsNumericOrObject(right)) return null;
        if (left == typeof(double) || right == typeof(double)) return typeof(double);
        if (left == typeof(long) || right == typeof(long)) return typeof(long);
        if (left == typeof(int) && right == typeof(int)) return typeof(int);
        // At least one operand is object — default to long, the
        // widest integer type that round-trips through Convert.ToInt64.
        return typeof(long);
    }

    private void ConvertNumeric(Type from, Type to)
    {
        if (from == to) return;
        if (from == typeof(object))
        {
            if (to == typeof(int)) _il.Emit(OpCodes.Call, s_convertToInt32);
            else if (to == typeof(long)) _il.Emit(OpCodes.Call, s_convertToInt64);
            else if (to == typeof(double)) _il.Emit(OpCodes.Call, s_convertToDouble);
            return;
        }
        if (to == typeof(double)) _il.Emit(OpCodes.Conv_R8);
        else if (to == typeof(long)) _il.Emit(OpCodes.Conv_I8);
        else if (to == typeof(int)) _il.Emit(OpCodes.Conv_I4);
    }

    private void BoxIfValueType(Type t)
    {
        if (t.IsValueType) _il.Emit(OpCodes.Box, t);
    }

    private void ConvertToString(Type t)
    {
        if (t == typeof(string)) return;
        if (t.IsValueType) _il.Emit(OpCodes.Box, t);
        _il.Emit(OpCodes.Callvirt, s_objectToString);
    }

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
        Type[] ParamClrTypes,
        Type ReturnClrType);

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
    /// One entry on <c>_loopStack</c>. <see cref="ContinueLabel"/> is
    /// where <c>continue</c> branches (typically the loop's
    /// test/increment); <see cref="BreakLabel"/> is the loop's exit.
    /// We always emit <c>leave</c> (not <c>br</c>) so the same
    /// branches work whether or not the loop body is wrapped in a
    /// protected (try) region.
    /// </summary>
    private readonly record struct LoopFrame(Label ContinueLabel, Label BreakLabel);
}
