using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Tosh.Runtime;
using Tosh.Language.Bridge;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Native interop: binding a shared library into the language, marshalling across the
/// boundary, and turning a native failure into a diagnostic.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`, the file-split phase of the
/// Tōast/TōSh separation. Every member here moved **verbatim** — this file is a
/// relocation, not a rewrite, and nothing was fixed on the way past.
///
/// Native interop went first because it is the most self-contained concern in the
/// engine: thirteen members that talk to `NativeFunctionCommand`, the marshaller and
/// the CLR type system, and to almost nothing else. It was not, however, contiguous —
/// the members were spread across three regions of the original file, which is why
/// this is a redistribution rather than a cut.
/// </summary>
public sealed partial class ToshEngine
{

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
            var isVariadic = false;

            foreach (var parameter in function.Parameters)
            {
                // `...` — the C variadic tail. It names no type, so it is recognised
                // here rather than sent to the type resolver, which used to reject it
                // with "Native interop does not currently support '...'" (`TS-P3-24`).
                if (parameter.TypeName is "..." || parameter.Name is "...")
                {
                    if (!ReferenceEquals(parameter, function.Parameters[^1]))
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.native_variadic_not_last",
                            Title: "'...' must be the last parameter.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: parameter.Span,
                            Label: "move '...' to the end of the parameter list",
                            Help: "C reads the variadic tail after every fixed parameter, so nothing can follow it."));
                    }

                    isVariadic = true;
                    continue;
                }

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

                var parameterType = ResolveNativeInteropParameterType(parameter.TypeName, parameter.PassingMode, sourceName, sourceText, parameter.Span, $"parameter '{parameter.Name}'");

                // A `raw callback` name resolves to an emitted delegate type;
                // the declaration is carried alongside so the thunk can convert
                // arguments using the names the author actually wrote.
                ToshNativeCallbackDefinition? callback = null;

                if (typeof(Delegate).IsAssignableFrom(parameterType) &&
                    parameter.TypeName is { } declaredTypeName &&
                    TryGetNamedType(declaredTypeName.Trim(), out var namedType))
                {
                    callback = namedType as ToshNativeCallbackDefinition;
                }

                parameters.Add(new NativeFunctionParameterDefinition(
                    parameter.Name,
                    parameter.TypeName ?? string.Empty,
                    parameterType,
                    parameter.PassingMode,
                    IsCStringPointer: parameter.PassingMode != NativeParameterPassingMode.In &&
                                      NativeTypeLexicon.IsCStringName(parameter.TypeName),
                    Callback: callback));
            }
            // A binding with a by-ref `cstring` takes ownership of its input strings
            // too: the pointer a callee hands back commonly points into one of them,
            // and the default marshaller's temporary is gone by the time it could be
            // decoded (`TS-P3-26`).
            if (parameters.Any(static parameter => parameter.IsCStringPointer))
            {
                for (var index = 0; index < parameters.Count; index++)
                {
                    if (parameters[index] is { IsCStringPointer: false, ClrType: var type } input &&
                        type == typeof(string) &&
                        input.PassingMode == NativeParameterPassingMode.In)
                    {
                        parameters[index] = input with { ClrType = typeof(IntPtr), OwnsCStringInput = true };
                    }
                }
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
                successPredicate,
                isVariadic,
                warn: (code, title, help) => WriteWarning(
                    code, title, help, category: ToshDiagnosticCategory.Runtime));
        }
    }

    private void EnsureNativeModuleAvailable(
        string sourceName,
        string nativeTarget,
        string moduleName,
        DeclarationModifier modifier)
    {
        RequireTarget requirement;

        try
        {
            requirement = ResolveNativeRequirement(nativeTarget, GetExecutionDirectory(sourceName));
        }
        catch (FileNotFoundException notFound)
        {
            // `TS-P3-25`. This reached the reader as the generic `tosh.runtime.error`,
            // so the commonest binding mistake of all had no code to match on.
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.native.library_not_found",
                Title: notFound.Message,
                SourceName: sourceName,
                SourceText: string.Empty,
                Span: default,
                Label: $"'{nativeTarget}' could not be loaded",
                Help: "check the path, or give a bare name so the dynamic loader searches for it."));
        }

        if (!_requiredNativeLibraries.TryGetValue(requirement.CacheKey, out var shared))
        {
            nint handle;

            try
            {
                handle = NativeLibrary.Load(requirement.ResolvedPath);
            }
            catch (DllNotFoundException notFound)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.native.library_not_found",
                    Title: $"Native library '{nativeTarget}' was not found.",
                    SourceName: sourceName,
                    SourceText: string.Empty,
                    Span: default,
                    Label: notFound.Message,
                    Help: "check the name and that the library is installed and on the loader's search path."));
            }
            shared = new NativeLibraryBinding(
                requirement.ResolvedPath,
                requirement.CacheKey,
                handle,
                new ModuleExportTable());
            _requiredNativeLibraries[requirement.CacheKey] = shared;
        }

        // `TS-P2-90`. The *handle* is shared per library — loading the same `.so`
        // twice would be waste, and the handle is what the cache is for — but the
        // export table must not be. Handing every module the cached table gave two
        // modules binding the same library one surface between them: `module A`
        // declaring `abs` could call `labs` through its own alias because `module B`
        // had declared it, so a library built from several modules over one `.so`
        // had no encapsulation at all.
        //
        // Class-body binds were already isolated, routing through
        // `SetNativeMember` instead, so this brings module-level binds in line
        // rather than inventing a rule.
        var binding = shared with { Exports = new ModuleExportTable() };

        var module = new ToshModuleObject(this, moduleName, binding.Exports)
        {
            NativeLibraryBinding = binding,
        };
        DeclareModule(moduleName, module, modifier);
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
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this,
            ShellRuntime: ShellRuntime);

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

            // By reference, a `cstring` is a `char**`: the slot holds a pointer the
            // callee replaces, not characters it writes over. The parameter carries
            // `IsCStringPointer` so the pointer is decoded after the call
            // (`TS-P3-26`).
            return isByRef ? typeof(IntPtr) : typeof(string);
        }

        // The scalar table is the single source of truth for the native interop
        // surface, so consult it before the CLR resolver rather than after.
        // Without this, `int`, `uint`, `ptr` and `byte` — which are most of what
        // a bind block declares — each took the full resolution path, scanning
        // every import. A 28-function SDL block resolves roughly 110 parameter
        // types, and type resolution was measured at ~518 ms of the ~574 ms
        // spent loading a profile's modules.
        if (NativeTypeLexicon.TryResolveScalar(normalized, out var scalar) && scalar != typeof(void))
        {
            if (NativeTypeLexicon.ValidateByRef(normalized, isByRef, sourceName, sourceText, span) is { } scalarRejection)
            {
                throw ToshDiagnosticException.Create(scalarRejection);
            }

            return scalar;
        }

        var resolved = ResolveTypeName(normalized);

        // A `raw callback` resolves to an emitted delegate type. It needs no
        // layout support — the marshaller turns a delegate into a function
        // pointer by itself — so it is admitted here rather than in
        // NativeInteropUtilities, which answers a question about memory layout
        // that `size-of` and `alloc` also ask.
        if (resolved is not null && typeof(Delegate).IsAssignableFrom(resolved))
        {
            if (isByRef)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_callback_by_reference",
                    Title: "A callback cannot be passed by reference.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"{owner} declares '{typeName}' as out/ref",
                    Help: "a callback already marshals as a pointer; pass it by value."));
            }

            return resolved;
        }

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

        // `<type> count` — the same contract read at a stated width.
        //
        // `TS-P2-124`. Bare `count` assumes `ssize_t`, and bound to a function that
        // really returns `int32_t` the -1 failure arrived as 4294967295: the low
        // half read as unsigned, the high half never written by the callee. That is
        // positive, so the `>= 0` check passed, no `NativeError` was raised, and the
        // caller got a huge count instead of an error. The width is knowable only at
        // the declaration site, so it is stated there.
        if (normalized.EndsWith(" count", StringComparison.OrdinalIgnoreCase))
        {
            var widthName = normalized[..^" count".Length].Trim();
            var width = ResolveNativeInteropReturnType(widthName, sourceName, sourceText, span);

            if (width.ClrType is null || !IsIntegerReturnWidth(width.ClrType))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_count_width_not_integer",
                    Title: $"'{widthName} count' needs an integer width.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"'{widthName}' is not an integer type",
                    Help: "write the width the C function returns — `int count`, `long count`, or bare `count` for ssize_t."));
            }

            return new NativeFunctionReturnDefinition(
                normalized, width.ClrType, NativeFunctionReturnKind.Default, NativeErrorConvention.Count);
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
}
