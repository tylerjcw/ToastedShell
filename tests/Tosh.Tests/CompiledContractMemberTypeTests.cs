using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The compile-time path applies the trait/interface member contract that the interpreter
/// applies when a class is declared — <c>TOAST-0020</c>.
/// </summary>
public sealed class CompiledContractMemberTypeTests
{
    [Theory]
    [InlineData(
        "trait CctReturn { func render() -> string }\n"
        + "class CctBadReturn uses CctReturn { func render() -> int => 42 }",
        "CctReturn.render",
        "return type")]
    [InlineData(
        "trait CctParameter { func take(value: string) }\n"
        + "class CctBadParameter uses CctParameter { func take(value: int) { } }",
        "CctParameter.take",
        "parameter 'value'")]
    [InlineData(
        "trait CctProperty { prop Name: string }\n"
        + "class CctBadProperty uses CctProperty { prop Name: int = 1 }",
        "CctProperty.Name",
        "property 'Name'")]
    [InlineData(
        "interface CctInterfaceReturn { func make() -> string }\n"
        + "class CctBadInterfaceReturn implements CctInterfaceReturn { func make() -> int => 1 }",
        "CctInterfaceReturn.make",
        "return type")]
    [InlineData(
        "interface CctInterfaceParameter { func take(value: string) }\n"
        + "class CctBadInterfaceParameter implements CctInterfaceParameter { func take(value: int) { } }",
        "CctInterfaceParameter.take",
        "parameter 'value'")]
    [InlineData(
        "export module CctContracts { export trait CctQualified { func value() -> string } }\n"
        + "class CctBadQualified uses CctContracts.CctQualified { func value() -> int => 1 }",
        "CctContracts.CctQualified.value",
        "return type")]
    public void Compiler_refuses_the_same_contract_mismatches(
        string source,
        string member,
        string mismatch)
    {
        var diagnostic = Assert.Single(ContractDiagnostics(source));

        Assert.Equal("tosh.runtime.contract_member_type_mismatch", diagnostic.Code);
        Assert.Contains(member, diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains(mismatch, diagnostic.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "trait CctExact { func render(value: string) -> string }\n"
        + "class CctExactImpl uses CctExact { func render(value: string) -> string => value }")]
    [InlineData(
        "class CctBase { }\nclass CctLeaf extends CctBase { }\n"
        + "trait CctCovariant { func make() -> CctBase }\n"
        + "class CctCovariantImpl uses CctCovariant { func make() -> CctLeaf => new CctLeaf() }")]
    [InlineData(
        "trait CctAlias { func number() -> int }\n"
        + "class CctAliasImpl uses CctAlias { func number() -> Int32 => 1 }")]
    [InlineData(
        "trait CctSilentContract { func value() -> string }\n"
        + "class CctSilentImplementation uses CctSilentContract { func value() => 1 }")]
    [InlineData(
        "trait CctSilentTrait { func value() }\n"
        + "class CctTypedImplementation uses CctSilentTrait { func value() -> int => 1 }")]
    [InlineData(
        "trait CctExactProperty { prop Name: string }\n"
        + "class CctExactPropertyImpl uses CctExactProperty { prop Name: string = \"x\" }")]
    [InlineData(
        "class CctInterfaceBase { }\nclass CctInterfaceLeaf extends CctInterfaceBase { }\n"
        + "interface CctInterfaceFactory { func make() -> CctInterfaceBase }\n"
        + "class CctInterfaceFactoryImpl implements CctInterfaceFactory { func make() -> CctInterfaceLeaf => new CctInterfaceLeaf() }")]
    public void Compiler_accepts_conforming_or_unconstrained_members(string source)
        => Assert.Empty(ContractDiagnostics(source));

    [Fact]
    public async Task Compiler_and_interpreter_produce_the_same_diagnostic()
    {
        const string Source =
            "trait CctParity { func render() -> string }\n"
            + "class CctParityBad uses CctParity { func render() -> int => 42 }";

        var compiled = TypeChecker.PromoteSeverity(
            Assert.Single(ContractDiagnostics(Source)),
            ToshDiagnosticSeverity.Error);

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(Source, "<compiled-contract>", CancellationToken.None));
        var interpreted = Assert.Single(exception.Diagnostics);

        Assert.Equal(interpreted, compiled);
    }

    private static IReadOnlyList<ToshDiagnostic> ContractDiagnostics(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var parse = engine.Parse(source, "<compiled-contract>");
        Assert.Empty(parse.Diagnostics);

        var unit = Lowerer.Lower(parse, runtime.Commands);
        return TypeChecker.Check(unit)
            .Where(diagnostic => diagnostic.Code == "tosh.runtime.contract_member_type_mismatch")
            .ToArray();
    }
}
