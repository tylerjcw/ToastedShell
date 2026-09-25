using System.Globalization;
using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Stdlib.Cas;
using Xunit;

namespace Tosh.Tests;

public sealed class ScientificUnitsTests
{
    [Fact]
    public void RationalExponent_arithmetic_normalization_and_formatting()
    {
        // Normalization and GCD reduction
        var half = new RationalExponent(2, 4);
        Assert.Equal(1, half.Numerator);
        Assert.Equal(2, half.Denominator);
        Assert.True(half == new RationalExponent(1, 2));

        // Negative denominator normalized
        var negHalf = new RationalExponent(1, -2);
        Assert.Equal(-1, negHalf.Numerator);
        Assert.Equal(2, negHalf.Denominator);

        // Arithmetic
        var third = new RationalExponent(1, 3);
        var sixth = new RationalExponent(1, 6);
        Assert.Equal(new RationalExponent(1, 2), third + sixth);

        var threeFourths = new RationalExponent(3, 4);
        var oneFourth = new RationalExponent(1, 4);
        Assert.Equal(new RationalExponent(1, 2), threeFourths - oneFourth);
        Assert.Equal(new RationalExponent(1, 2), new RationalExponent(2, 3) * new RationalExponent(3, 4));
        Assert.Equal(new RationalExponent(2, 1), new RationalExponent(1, 2) / new RationalExponent(1, 4));

        // Unary minus
        Assert.Equal(new RationalExponent(-1, 2), -half);

        // Comparisons
        Assert.True(third < half);
        Assert.True(half > third);
        Assert.True(half >= new RationalExponent(2, 4));

        // Formatting
        Assert.Equal("1/2", half.ToString());
        Assert.Equal("3", new RationalExponent(3, 1).ToString());
    }

    [Fact]
    public void UnitExpression_rational_exponents_and_roots()
    {
        var meter = UnitExpression.Of(UnitDimension.Length, 1);
        var sqrtMeter = meter.Power(new RationalExponent(1, 2));

        Assert.Equal(new RationalExponent(1, 2), sqrtMeter.GetRationalExponent(UnitDimension.Length));
        Assert.Equal("m^(1/2)", sqrtMeter.ToCanonicalUnitSymbol());

        var hertz = UnitExpression.Of(UnitDimension.Time, -1);
        var sqrtHz = hertz.Root(2);

        Assert.Equal(new RationalExponent(-1, 2), sqrtHz.GetRationalExponent(UnitDimension.Time));
        Assert.Equal("1/s^(1/2)", sqrtHz.ToCanonicalUnitSymbol());

        // Reciprocal
        var recip = sqrtHz.Reciprocal();
        Assert.Equal(new RationalExponent(1, 2), recip.GetRationalExponent(UnitDimension.Time));
        Assert.Equal("s^(1/2)", recip.ToCanonicalUnitSymbol());
    }

    [Theory]
    [InlineData("nV/sqrt(Hz)")]
    [InlineData("m^(1/2)")]
    [InlineData("s^(-1/2)")]
    [InlineData("kg*m^2/s^3")]
    public void UnitExpressionParser_parses_fractional_and_sqrt_expressions(string unitText)
    {
        var success = UnitExpressionParser.TryParseConversion(
            unitText,
            out var conversion,
            out var dimension,
            out var normalizedSymbol);

        Assert.True(success, $"Failed to parse unit expression '{unitText}'.");
        Assert.False(dimension.IsDimensionless);
    }

    [Fact]
    public void Lexer_reads_complex_fractional_unit_literals()
    {
        var tokens = new ToshLexer("15`nV/sqrt(Hz)").Lex();
        var token = Assert.Single(tokens, t => t.Kind != SyntaxTokenKind.EndOfFile);

        Assert.Equal(SyntaxTokenKind.UnitLiteral, token.Kind);
        var q = Assert.IsAssignableFrom<Quantity>(token.Value);
        Assert.Equal(15.0, q.Magnitude);
        Assert.Equal("nV/sqrt(Hz)", q.UnitSymbol);
    }

    [Fact]
    public void Quantity_power_and_sqrt_operations()
    {
        var length = Quantity.FromLiteral(5, "m");
        var area = length.Power(2);

        Assert.Equal(25.0, area.Magnitude);
        Assert.Equal(2, area.Dimension.Exponents[UnitDimension.Length]);

        var volume = Quantity.FromLiteral(8, "m^3");
        var cubeRoot = volume.Power(new RationalExponent(1, 3));
        Assert.Equal(2.0, cubeRoot.Magnitude, 6);
        Assert.Equal(1, cubeRoot.Dimension.Exponents[UnitDimension.Length]);

        var hz = Quantity.FromLiteral(100, "Hz");
        var sqrtHz = hz.Sqrt();
        Assert.Equal(10.0, sqrtHz.Magnitude, 6);
        Assert.Equal(new RationalExponent(-1, 2), sqrtHz.Dimension.GetRationalExponent(UnitDimension.Time));
    }

    [Fact]
    public void SemanticKind_separates_energy_and_torque_on_identical_dimensions()
    {
        var joules = Quantity.FromLiteral(10, "J");
        var newtonMeters = Quantity.FromLiteral(5, "Nm");

        Assert.Equal(joules.Dimension, newtonMeters.Dimension);
        Assert.Equal("Energy", joules.SemanticKind);
        Assert.Equal("Torque", newtonMeters.SemanticKind);

        // Same semantic kind adds/subtracts cleanly
        var jSum = joules + Quantity.FromLiteral(5, "J");
        Assert.Equal(15.0, jSum.Magnitude);

        var nmSum = newtonMeters + Quantity.FromLiteral(2, "Nm");
        Assert.Equal(7.0, nmSum.Magnitude);

        // Colliding semantic kinds fail on addition and subtraction
        var addEx = Assert.Throws<InvalidOperationException>(() => joules + newtonMeters);
        Assert.Contains("semantic kind", addEx.Message, StringComparison.OrdinalIgnoreCase);

        var subEx = Assert.Throws<InvalidOperationException>(() => joules - newtonMeters);
        Assert.Contains("semantic kind", subEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuantityArray_vectorized_simd_arithmetic()
    {
        var a = new QuantityArray([1.0, 2.0, 3.0, 4.0, 5.0], "m");
        var b = new QuantityArray([10.0, 20.0, 30.0, 40.0, 50.0], "m");

        // Element-wise addition
        var sum = a + b;
        Assert.Equal(5, sum.Count);
        Assert.Equal("m", sum.UnitSymbol);
        Assert.Equal([11.0, 22.0, 33.0, 44.0, 55.0], sum.Magnitudes.ToArray());

        // Element-wise subtraction
        var diff = b - a;
        Assert.Equal([9.0, 18.0, 27.0, 36.0, 45.0], diff.Magnitudes.ToArray());

        // Scalar multiplication & division
        var scaled = a * 3.0;
        Assert.Equal([3.0, 6.0, 9.0, 12.0, 15.0], scaled.Magnitudes.ToArray());

        var halved = b / 2.0;
        Assert.Equal([5.0, 10.0, 15.0, 20.0, 25.0], halved.Magnitudes.ToArray());

        // Scalar quantity addition
        var shifted = a + Quantity.FromLiteral(10, "m");
        Assert.Equal([11.0, 12.0, 13.0, 14.0, 15.0], shifted.Magnitudes.ToArray());

        // Reductions
        var total = a.Sum();
        Assert.Equal(15.0, total.Magnitude);
        Assert.Equal("m", total.UnitSymbol);

        var avg = a.Mean();
        Assert.Equal(3.0, avg.Magnitude);

        var min = a.Min();
        Assert.Equal(1.0, min.Magnitude);

        var max = a.Max();
        Assert.Equal(5.0, max.Magnitude);

        // Indexing & Slicing
        Assert.Equal(1.0, a[0].Magnitude);
        Assert.Equal(5.0, a[4].Magnitude);

        var slice = a.Slice(1, 3);
        Assert.Equal(3, slice.Count);
        Assert.Equal([2.0, 3.0, 4.0], slice.Magnitudes.ToArray());
        Assert.Equal("m", slice.UnitSymbol);

        // Conversion
        var inKm = a.To("km");
        Assert.Equal("km", inKm.UnitSymbol);
        Assert.Equal(0.001, inKm[0].Magnitude, 6);
    }

    [Fact]
    public void QuantityArray_rejects_mismatched_dimensions_and_semantic_kinds()
    {
        var meters = new QuantityArray([1.0, 2.0], "m");
        var seconds = new QuantityArray([1.0, 2.0], "s");

        var dimEx = Assert.Throws<InvalidOperationException>(() => meters + seconds);
        Assert.Contains("dimension", dimEx.Message, StringComparison.OrdinalIgnoreCase);

        var energy = new QuantityArray([10.0, 20.0], "J");
        var torque = new QuantityArray([5.0, 15.0], "Nm");

        var kindEx = Assert.Throws<InvalidOperationException>(() => energy + torque);
        Assert.Contains("semantic kind", kindEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cas_symbolic_quantities_and_differentiation()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // Trajectory with physical units: 4.9 m/s^2 * t^2
        var posRes = await engine.ExecuteToListAsync("var t = sym t; var pos = 4.9`m/s^2 * ($t ** 2); echo $pos");
        Assert.Single(posRes);
        var posStr = posRes[0]?.ToString();
        Assert.NotNull(posStr);
        Assert.Contains("4.9 m/s^2", posStr);
        Assert.Contains("t^2", posStr);

        // Velocity: diff position t => (9.8 m/s^2)*t
        var velRes = await engine.ExecuteToListAsync("var t = sym t; var pos = 4.9`m/s^2 * ($t ** 2); var v = diff $pos $t; echo $v");
        Assert.Single(velRes);
        var velStr = velRes[0]?.ToString();
        Assert.NotNull(velStr);
        Assert.Contains("9.8 m/s^2", velStr);
        Assert.Contains("t", velStr);

        // Acceleration: diff velocity t => 9.8 m/s^2
        var accRes = await engine.ExecuteToListAsync("var t = sym t; var pos = 4.9`m/s^2 * ($t ** 2); var v = diff $pos $t; var a = diff $v $t; echo $a");
        Assert.Single(accRes);
        var accStr = accRes[0]?.ToString();
        Assert.NotNull(accStr);
        Assert.Contains("9.8 m/s^2", accStr);
    }

    [Fact]
    public async Task Cas_evaluate_symbolic_quantity_pipeline()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // Evaluating symbolic velocity at t = 2 => 19.6 m/s^2
        var evalRes = await engine.ExecuteToListAsync("var t = sym t; var pos = 4.9`m/s^2 * ($t ** 2); var v = diff $pos $t; $v.Substitute(\"t\", 2) | eval");
        Assert.Single(evalRes);
        var q = Assert.IsAssignableFrom<Quantity>(evalRes[0]);
        Assert.Equal(19.6, q.Magnitude, 5);
        Assert.Equal("m/s^2", q.UnitSymbol);
    }

    [Fact]
    public async Task Cas_unit_cancellation_and_like_terms()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // Unit cancellation: (10 m * x) / (2 m) => 5*x
        var cancelRes = await engine.ExecuteToListAsync("var x = sym x; var f = (10`m * $x) / (2`m); simplify $f");
        Assert.Single(cancelRes);
        Assert.Equal("5*x", cancelRes[0]?.ToString());

        // Like terms combining: 10 m * x + 20 m * x => (30 m)*x
        var likeRes = await engine.ExecuteToListAsync("var x = sym x; var f = 10`m * $x + 20`m * $x; simplify $f");
        Assert.Single(likeRes);
        Assert.Equal("(30 m)*x", likeRes[0]?.ToString());
    }

    [Fact]
    public async Task Cas_solves_equations_with_units()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // solve (2 * $x == 10 m) | eval => 5 m
        var solveRes = await engine.ExecuteToListAsync("var x = sym x; solve (2 * $x == 10`m) | eval");
        Assert.Single(solveRes);
        var q = Assert.IsAssignableFrom<Quantity>(solveRes[0]);
        Assert.Equal(5.0, q.Magnitude);
        Assert.Equal("m", q.UnitSymbol);
    }

    [Fact]
    public void Cas_sym_parses_backticked_unit_expressions()
    {
        var expr = SymParser.Parse("4.9`m/s^2 * t^2");
        Assert.IsType<SymMul>(expr);
        var mul = (SymMul)expr;
        Assert.Equal(2, mul.Factors.Count);
        var sq = Assert.IsType<SymQuantity>(mul.Factors[0]);
        Assert.Equal(4.9, sq.Magnitude.ToDouble(), 5);
        Assert.Equal("m/s^2", sq.Symbol);
    }
}
