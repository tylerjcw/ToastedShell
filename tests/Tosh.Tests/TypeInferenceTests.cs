using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for the light type-inference pass that runs as part of
/// lowering. The contract is "best-effort": when the inferrer can't
/// prove a concrete type the node stays dynamic (no false positives).
/// </summary>
public sealed class TypeInferenceTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public TypeInferenceTests(ToshRuntimeFixture fixture)
    {
        _runtime = fixture.Runtime;
    }

    private BoundUnit Lower(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<inference-test>");
        return Lowerer.Lower(parse, _runtime.Commands);
    }

    private static BoundExpression FirstArg(BoundUnit unit)
    {
        var pipeline = (BoundPipelineStatement)unit.Root.Statements[0];
        var call = (BoundCommandCall)pipeline.Pipeline.Stages[0];
        return call.Arguments[0].Value;
    }

    [Fact]
    public void Integer_literal_has_concrete_int_type()
    {
        var unit = Lower("echo 42");
        var literal = Assert.IsType<BoundLiteral>(FirstArg(unit));
        Assert.True(literal.Type.IsConcrete);
        Assert.Equal(typeof(int), literal.Type.ClrType);
    }

    [Fact]
    public void Var_declaration_propagates_value_type_to_references()
    {
        var unit = Lower("var x = 42\necho $x");

        var decl = (BoundVariableDeclaration)unit.Root.Statements[0];
        Assert.Equal(typeof(int), decl.Symbol.DeclaredType.ClrType);

        var pipeline = (BoundPipelineStatement)unit.Root.Statements[1];
        var call = (BoundCommandCall)pipeline.Pipeline.Stages[0];
        var varRef = (BoundVariableReference)call.Arguments[0].Value;
        Assert.Equal(typeof(int), varRef.Type.ClrType);
        Assert.Same(decl.Symbol, varRef.Symbol);
    }

    [Fact]
    public void Var_declaration_without_initializer_stays_dynamic()
    {
        var unit = Lower("var x");
        var decl = (BoundVariableDeclaration)unit.Root.Statements[0];
        Assert.True(decl.Symbol.DeclaredType.IsDynamic);
    }

    [Fact]
    public void Range_of_two_int_endpoints_has_ienumerable_int_type()
    {
        // Direct construction via the inferrer keeps this test
        // independent of how the parser shapes "1..10" inside a pipe.
        var intT = BoundType.FromClr(typeof(int));
        var inferred = TypeInferrer.InferRange(intT, step: null, intT);

        Assert.True(inferred.IsConcrete);
        Assert.Equal(typeof(IEnumerable<int>), inferred.ClrType);
    }

    [Fact]
    public void Range_with_dynamic_endpoint_falls_back_to_dynamic()
    {
        var intT = BoundType.FromClr(typeof(int));
        var inferred = TypeInferrer.InferRange(intT, step: null, BoundType.Dynamic);
        Assert.True(inferred.IsDynamic);
    }

    [Theory]
    [InlineData("+", typeof(int), typeof(int), typeof(int))]
    [InlineData("+", typeof(int), typeof(double), typeof(double))]
    [InlineData("*", typeof(int), typeof(long), typeof(long))]
    [InlineData("/", typeof(double), typeof(int), typeof(double))]
    [InlineData("==", typeof(int), typeof(int), typeof(bool))]
    [InlineData("<", typeof(int), typeof(double), typeof(bool))]
    [InlineData("&&", typeof(bool), typeof(bool), typeof(bool))]
    public void Binary_operators_promote_correctly(string op, Type leftClr, Type rightClr, Type expected)
    {
        var left = BoundType.FromClr(leftClr);
        var right = BoundType.FromClr(rightClr);
        var result = TypeInferrer.InferBinary(left, op, right);
        Assert.Equal(expected, result.ClrType);
    }

    [Fact]
    public void Binary_operator_with_unknown_operand_stays_dynamic()
    {
        var result = TypeInferrer.InferBinary(BoundType.Dynamic, "+", BoundType.FromClr(typeof(int)));
        Assert.True(result.IsDynamic);
    }

    [Fact]
    public void Unary_minus_preserves_numeric_type()
    {
        Assert.Equal(typeof(int), TypeInferrer.InferUnary("-", BoundType.FromClr(typeof(int))).ClrType);
        Assert.Equal(typeof(double), TypeInferrer.InferUnary("-", BoundType.FromClr(typeof(double))).ClrType);
    }

    [Fact]
    public void Unary_not_yields_bool()
    {
        Assert.Equal(typeof(bool), TypeInferrer.InferUnary("!", BoundType.FromClr(typeof(bool))).ClrType);
        Assert.Equal(typeof(bool), TypeInferrer.InferUnary("not", BoundType.FromClr(typeof(int))).ClrType);
    }
}
