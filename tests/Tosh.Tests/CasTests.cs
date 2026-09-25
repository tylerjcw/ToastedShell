using System.Numerics;
using Tosh.Language;
using Tosh.Runtime;
using Tosh.Stdlib.Cas;
using Xunit;

namespace Tosh.Tests;

public sealed class CasTests
{
    [Fact]
    public void BigRational_basic_arithmetic_and_reduction()
    {
        var r1 = new BigRational(2, 4);
        Assert.Equal(1, r1.Numerator);
        Assert.Equal(2, r1.Denominator);

        var r2 = new BigRational(1, 3);
        var sum = r1 + r2; // 1/2 + 1/3 = 5/6
        Assert.Equal(5, sum.Numerator);
        Assert.Equal(6, sum.Denominator);

        var prod = r1 * r2; // 1/6
        Assert.Equal(1, prod.Numerator);
        Assert.Equal(6, prod.Denominator);

        var quot = r1 / r2; // 3/2
        Assert.Equal(3, quot.Numerator);
        Assert.Equal(2, quot.Denominator);

        var pow = r1.Pow(3); // 1/8
        Assert.Equal(1, pow.Numerator);
        Assert.Equal(8, pow.Denominator);
    }

    [Fact]
    public void SymParser_parses_expressions_and_implicit_multiplication()
    {
        var e1 = SymParser.Parse("x^2 + 2*x + 1");
        Assert.IsType<SymAdd>(e1);

        var e2 = SymParser.Parse("2x + 3");
        Assert.IsType<SymAdd>(e2);

        var e3 = SymParser.Parse("sin(x)");
        Assert.IsType<SymFunction>(e3);
        Assert.Equal("sin", ((SymFunction)e3).Name);

        var e4 = SymParser.Parse("pi + e");
        Assert.IsType<SymAdd>(e4);
    }

    [Fact]
    public void Simplifier_combines_like_terms_and_folds_constants()
    {
        var expr = SymParser.Parse("2*x + 3*x");
        var simplified = Simplifier.Simplify(expr);
        Assert.Equal("5*x", simplified.ToString());

        var zeroExpr = SymParser.Parse("x - x");
        Assert.Equal("0", Simplifier.Simplify(zeroExpr).ToString());

        var funcExpr = SymParser.Parse("sin(0)");
        Assert.Equal("0", Simplifier.Simplify(funcExpr).ToString());

        var cosExpr = SymParser.Parse("cos(0)");
        Assert.Equal("1", Simplifier.Simplify(cosExpr).ToString());
    }

    [Fact]
    public void Differentiate_computes_exact_derivatives()
    {
        // d(x^3)/dx = 3*x^2
        var e1 = SymParser.Parse("x^3");
        var d1 = Simplifier.Simplify(e1.Differentiate("x"));
        Assert.Equal("3*x^2", d1.ToString());

        // d(sin(x))/dx = cos(x)
        var e2 = SymParser.Parse("sin(x)");
        var d2 = Simplifier.Simplify(e2.Differentiate("x"));
        Assert.Equal("cos(x)", d2.ToString());

        // d(x^2 + 5*x + 7)/dx = 2*x + 5
        var e3 = SymParser.Parse("x^2 + 5*x + 7");
        var d3 = Simplifier.Simplify(e3.Differentiate("x"));
        Assert.Equal("2*x + 5", d3.ToString());
    }

    [Fact]
    public void Expander_expands_polynomials()
    {
        // (x + 1)^2 = x^2 + 2*x + 1
        var e1 = SymParser.Parse("(x + 1)^2");
        var expanded1 = Expander.Expand(e1);
        var res1 = expanded1.ToString();
        Assert.Contains("x^2", res1);
        Assert.Contains("2*x", res1);
        Assert.Contains("1", res1);

        // (x - 1)*(x + 1) = x^2 - 1
        var e2 = SymParser.Parse("(x - 1)*(x + 1)");
        var expanded2 = Expander.Expand(e2);
        var res2 = expanded2.ToString();
        Assert.Contains("x^2", res2);
        Assert.Contains("-1", res2);
    }

    [Fact]
    public async Task Cas_commands_execute_in_engine()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // sym command
        var symRes = await engine.ExecuteToListAsync("sym \"x^2 + 3*x\"");
        Assert.Single(symRes);
        Assert.IsAssignableFrom<SymExpr>(symRes[0]);

        // diff command
        var diffRes = await engine.ExecuteToListAsync("diff \"x^3 + 4*x\"");
        Assert.Single(diffRes);
        Assert.Equal("3*x^2 + 4", diffRes[0]?.ToString());

        // pipeline diff
        var pipeDiffRes = await engine.ExecuteToListAsync("sym \"x^2\" | diff");
        Assert.Single(pipeDiffRes);
        Assert.Equal("2*x", pipeDiffRes[0]?.ToString());

        // simplify command
        var simpRes = await engine.ExecuteToListAsync("simplify \"3*x + 4*x - 2*x\"");
        Assert.Single(simpRes);
        Assert.Equal("5*x", simpRes[0]?.ToString());

        // expand command
        var expRes = await engine.ExecuteToListAsync("expand \"(x + 2)*(x + 3)\"");
        Assert.Single(expRes);
        var expStr = expRes[0]?.ToString();
        Assert.NotNull(expStr);
        Assert.Contains("x^2", expStr);
        Assert.Contains("5*x", expStr);
        Assert.Contains("6", expStr);
        // solve command: linear
        var solveLin = await engine.ExecuteToListAsync("solve \"2*x + 4 = 6\"");
        Assert.Single(solveLin);
        Assert.Equal("1", solveLin[0]?.ToString());

        // solve command: quadratic
        var solveQuad = await engine.ExecuteToListAsync("solve \"x^2 - 4 = 0\"");
        Assert.Equal(2, solveQuad.Count);
        Assert.Contains(solveQuad, s => s?.ToString() == "2");
        Assert.Contains(solveQuad, s => s?.ToString() == "-2");
    }

    [Fact]
    public void Solver_solves_linear_and_quadratic_equations()
    {
        // 2*x + 4 = 6 => x = 1
        var sol1 = Solver.Solve("2*x + 4 = 6", "x");
        Assert.Single(sol1);
        Assert.Equal("1", sol1[0].ToString());

        // x^2 - 5*x + 6 = 0 => x = 3, 2
        var sol2 = Solver.Solve("x^2 - 5*x + 6 = 0", "x");
        Assert.Equal(2, sol2.Count);
        Assert.Contains(sol2, s => s.ToString() == "3");
        Assert.Contains(sol2, s => s.ToString() == "2");

        // 3*y + 9 = 0 => y = -3
        var sol3 = Solver.Solve("3*y + 9 = 0", "y");
        Assert.Single(sol3);
        Assert.Equal("-3", sol3[0].ToString());
    }
}

