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

    [Fact]
    public async Task Cas_live_variables_and_math_expressions()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // Variable creation and live binary operators (+, -, *, /, ^, ==)
        var res1 = await engine.ExecuteToListAsync("var x = sym x; echo (2 * $x + 4)");
        Assert.Single(res1);
        Assert.Equal("2*x + 4", res1[0]?.ToString());

        // Reverse non-commutative operators: 10 - $x canonicalizes as polynomial sum
        var resRevSub = await engine.ExecuteToListAsync("var x = sym x; echo (10 - $x)");
        Assert.Single(resRevSub);
        Assert.Equal("-1*x + 10", resRevSub[0]?.ToString());

        // Reverse non-commutative operators: 20 / $x canonicalizes as 20*x^-1
        var resRevDiv = await engine.ExecuteToListAsync("var x = sym x; echo (20 / $x)");
        Assert.Single(resRevDiv);
        Assert.Equal("20*x^-1", resRevDiv[0]?.ToString());

        // Unary negation: -$x canonicalizes as -1*x
        var resNeg = await engine.ExecuteToListAsync("var x = sym x; echo (-$x)");
        Assert.Single(resNeg);
        Assert.Equal("-1*x", resNeg[0]?.ToString());

        // Live solve with == operator
        var resSolve1 = await engine.ExecuteToListAsync("var x = sym x; solve (2 * $x + 4 == 6)");
        Assert.Single(resSolve1);
        Assert.Equal("1", resSolve1[0]?.ToString());

        // Live solve with auto-detected variable name 'y'
        var resSolveY = await engine.ExecuteToListAsync("var y = sym y; solve ($y ** 2 - 16 == 0)");
        Assert.Equal(2, resSolveY.Count);
        Assert.Contains(resSolveY, s => s?.ToString() == "4");
        Assert.Contains(resSolveY, s => s?.ToString() == "-4");

        // Live diff
        var resDiff = await engine.ExecuteToListAsync("var x = sym x; diff ($x ** 3 + 2 * $x)");
        Assert.Single(resDiff);
        Assert.Equal("2 + 3*x^2", resDiff[0]?.ToString());

        // Live expand
        var resExpand = await engine.ExecuteToListAsync("var x = sym x; expand (($x + 1) * ($x + 2))");
        Assert.Single(resExpand);
        var expStr = resExpand[0]?.ToString();
        Assert.NotNull(expStr);
        Assert.Contains("x^2", expStr);
        Assert.Contains("3*x", expStr);
        Assert.Contains("2", expStr);

        // Live simplify
        var resSimp = await engine.ExecuteToListAsync("var x = sym x; simplify (5 * $x - 2 * $x + 7)");
        Assert.Single(resSimp);
        Assert.Equal("3*x + 7", resSimp[0]?.ToString());

        // Standard Math functions: Math.sin($x)
        var resMathSin = await engine.ExecuteToListAsync("var x = sym x; diff (Math.sin($x))");
        Assert.Single(resMathSin);
        Assert.Equal("cos(x)", resMathSin[0]?.ToString());

        // Standard Math functions: Math.sqrt($x)
        var resMathSqrt = await engine.ExecuteToListAsync("var x = sym x; diff (Math.sqrt($x))");
        Assert.Single(resMathSqrt);
        Assert.Equal("(2*sqrt(x))^-1", resMathSqrt[0]?.ToString());

        // Plotting a live symbolic expression
        var resPlot = await engine.ExecuteToListAsync("var x = sym x; plot ($x ** 2) --min -5 --max 5 --samples 11");
        Assert.Single(resPlot);
        Assert.IsAssignableFrom<Tosh.Stdlib.Plotting.Figure>(resPlot[0]);
    }

    [Fact]
    public void SymNumber_properties_and_approximation()
    {
        var num = new SymNumber(new BigRational(-93, 2));
        Assert.Equal(-1, num.Sign);
        Assert.Equal(-93, num.Numerator);
        Assert.Equal(2, num.Denominator);
        Assert.Equal(-46.5, num.Approximate);

        var intNum = new SymNumber(new BigRational(42));
        Assert.Equal(1, intNum.Sign);
        Assert.Equal(42, intNum.Numerator);
        Assert.Equal(1, intNum.Denominator);
        Assert.Equal(42.0, intNum.Approximate);
    }

    [Fact]
    public async Task Equation_algebra_and_eval_pipeline()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        // TI-Nspire CX II style equation algebra
        var resEqSub = await engine.ExecuteToListAsync("var x = sym x; var y = 2 * $x + 4 == 8; echo ($y - 4)");
        Assert.Single(resEqSub);
        Assert.Equal("2*x = 4", resEqSub[0]?.ToString());

        var resEqDiv = await engine.ExecuteToListAsync("var x = sym x; var y = 2 * $x + 4 == 8; echo (($y - 4) / 2)");
        Assert.Single(resEqDiv);
        Assert.Equal("x = 2", resEqDiv[0]?.ToString());

        // Piping solved expression to eval: solve (2 * $x + 4 == 97) | eval => 46.5
        var resSolveEval = await engine.ExecuteToListAsync("var x = sym x; solve (2 * $x + 4 == 97) | eval");
        Assert.Single(resSolveEval);
        Assert.Equal(46.5, resSolveEval[0]);
        Assert.IsType<double>(resSolveEval[0]);

        // Integer solution returns int
        var resSolveEvalInt = await engine.ExecuteToListAsync("var x = sym x; solve (2 * $x + 4 == 8) | eval");
        Assert.Single(resSolveEvalInt);
        Assert.Equal(2, resSolveEvalInt[0]);
        Assert.IsType<int>(resSolveEvalInt[0]);

        // Piping isolated equation x = 93/2 to eval extracts RHS
        var resEqEval = await engine.ExecuteToListAsync("var x = sym x; var y = 2 * $x + 4 == 97; ($y - 4) / 2 | eval");
        Assert.Single(resEqEval);
        Assert.Equal(46.5, resEqEval[0]);

        // CAS trig approximation via eval
        var resSinEval = await engine.ExecuteToListAsync("sym \"sin(pi/2)\" | eval");
        Assert.Single(resSinEval);
        Assert.Equal(1, resSinEval[0]);

        // String pipeline eval
        var resStringEval = await engine.ExecuteToListAsync("\"1 + 2\" | eval");
        Assert.Single(resStringEval);
        Assert.Equal(3, resStringEval[0]);

        // True division vs floor division
        var resDiv = await engine.ExecuteToListAsync("echo (5 / 2)");
        Assert.Single(resDiv);
        Assert.Equal("2.5", resDiv[0]?.ToString());

        var resFloorDiv = await engine.ExecuteToListAsync("echo (5 // 2)");
        Assert.Single(resFloorDiv);
        Assert.Equal("2", resFloorDiv[0]?.ToString());
    }
}

