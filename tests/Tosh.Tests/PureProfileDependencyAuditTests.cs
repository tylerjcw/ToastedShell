using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-25. The pure profile's promise is that the artifact stands alone: no
/// interpreter, no compiler host, nothing from <c>Tosh.Compiler.Runtime</c>.
/// <see cref="BoundUnitEmitter"/> enforces that promise through
/// <c>RequireTier</c>, which reasons about the *shapes* in the source — it
/// cannot see what the emitter itself unconditionally writes into every
/// artifact. So a program made entirely of tier-1 shapes compiles clean and
/// still carries host calls.
/// </summary>
/// <remarks>
/// The audit reads the emitted metadata rather than trusting the emit result,
/// which is the "independently of RequireTier" half of the acceptance. It began
/// as a characterization of the defect — the assertions recorded what was
/// emitted and named the item — and was inverted in the commit that made
/// bootstrap and the recursion guard profile-aware.
/// </remarks>
public sealed class PureProfileDependencyAuditTests
{
    /// <summary>
    /// Assemblies a pure artifact must not reference. <c>Tosh.Runtime</c> is
    /// deliberately absent: the acceptance allows a pure artifact to use a
    /// stable runtime primitive, and the recursion guard is expected to move
    /// there rather than disappear.
    /// </summary>
    private static readonly string[] ForbiddenAssemblies =
    [
        "Tosh.Compiler.Runtime",
        "Tosh.Compiler.IR",
        "Tosh.Language",
    ];

    /// <summary>A program made only of tier-1 shapes: no builtins, no dynamic dispatch.</summary>
    private const string PureSource =
        """
        func add(a: int, b: int) -> int {
            return $a + $b
        }
        """;

    [Fact]
    public void Tier_clean_pure_emit_reports_no_diagnostics()
    {
        // Establishes the premise: RequireTier is satisfied, so anything the
        // audit finds is invisible to the profile's own gate.
        var (result, _) = Emit(PureSource, CompileProfile.Pure);

        Assert.True(
            result.IsClean,
            $"expected a tier-clean emit, got: {string.Join("; ", result.UnsupportedShapes)}");
    }

    [Fact]
    public void Pure_artifact_references_no_forbidden_assembly()
    {
        var (_, image) = Emit(PureSource, CompileProfile.Pure);
        var references = ReadAssemblyReferences(image);

        var violations = ForbiddenAssemblies
            .Where(forbidden => references.Contains(forbidden))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Pure_artifact_calls_no_host_member()
    {
        // The three the emitter used to write unconditionally: Initialize and
        // RegisterCompiledAssembly from Main, EnterExecutionFrame from every
        // function, method, lambda, and block.
        var (_, image) = Emit(PureSource, CompileProfile.Pure);
        var members = ReadMemberReferences(image);

        Assert.DoesNotContain("ToshHost.Initialize", members);
        Assert.DoesNotContain("ToshHost.RegisterCompiledAssembly", members);
        Assert.DoesNotContain("ToshHost.EnterExecutionFrame", members);
    }

    [Fact]
    public void Pure_artifact_still_guards_recursion_through_the_runtime_primitive()
    {
        // Dropping the host must not drop the guard. ToshHost.EnterExecutionFrame
        // only supplied the session's configured limit before delegating here, so
        // a pure artifact guards at the documented default instead.
        var (_, image) = Emit(PureSource, CompileProfile.Pure);
        var members = ReadMemberReferences(image);

        Assert.Contains("ToshExecutionDepthGuard.Enter", members);
    }

    [Fact]
    public void Audit_distinguishes_the_profiles_it_is_meant_to_distinguish()
    {
        // Negative control for the audit itself. If ReadAssemblyReferences
        // returned the same set regardless of input, every assertion above
        // would pass vacuously. The permissive profile emits strictly more
        // host surface than the pure one, so the two must not be identical
        // once pure is actually pure — and today's equality is itself the
        // finding, recorded here so the fix has something to change.
        var (_, pureImage) = Emit(PureSource, CompileProfile.Pure);
        var (_, permissiveImage) = Emit(PureSource, CompileProfile.Permissive);

        var pure = ReadAssemblyReferences(pureImage);
        var permissive = ReadAssemblyReferences(permissiveImage);

        Assert.DoesNotContain("Tosh.Compiler.Runtime", pure);
        Assert.Contains("Tosh.Compiler.Runtime", permissive);
        Assert.False(
            pure.SetEquals(permissive),
            "pure and permissive must not emit the same references — that equality "
            + "was the original finding");
    }

    private static (EmitResult Result, byte[] Image) Emit(string source, CompileProfile profile)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var parse = engine.Parse(source, "<pure-profile-audit>");
        Assert.Empty(parse.Diagnostics);

        var unit = Lowerer.Lower(parse, runtime.Commands);
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(
            unit,
            $"ToshPureAudit_{Guid.NewGuid():N}",
            stream,
            profile);

        return (result, stream.ToArray());
    }

    private static HashSet<string> ReadAssemblyReferences(byte[] image)
    {
        using var peReader = new PEReader(ImmutableArray.Create(image));
        var metadata = peReader.GetMetadataReader();

        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns <c>DeclaringType.Member</c> for every member reference in the
    /// image, which is what catches a call the assembly-reference list alone
    /// would not attribute to a specific host entry point.
    /// </summary>
    private static HashSet<string> ReadMemberReferences(byte[] image)
    {
        using var peReader = new PEReader(ImmutableArray.Create(image));
        var metadata = peReader.GetMetadataReader();
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in metadata.MemberReferences)
        {
            var reference = metadata.GetMemberReference(handle);
            var name = metadata.GetString(reference.Name);

            var declaring = reference.Parent.Kind == HandleKind.TypeReference
                ? metadata.GetString(
                    metadata.GetTypeReference((TypeReferenceHandle)reference.Parent).Name)
                : "<unknown>";

            members.Add($"{declaring}.{name}");
        }

        return members;
    }
}
