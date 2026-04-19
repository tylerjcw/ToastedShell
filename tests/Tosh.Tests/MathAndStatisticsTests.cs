using System.Numerics;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class MathAndStatisticsTests
{
    // --- Math static type: constants ---

    [Fact]
    public async Task Math_PI_returns_pi()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo Math.PI");
        Assert.Single(results);
        Assert.Equal(Math.PI, results[0]);
    }

    [Fact]
    public async Task Math_E_returns_eulers_number()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo Math.E");
        Assert.Single(results);
        Assert.Equal(Math.E, results[0]);
    }

    [Fact]
    public async Task Math_Tau_returns_tau()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo Math.Tau");
        Assert.Single(results);
        Assert.Equal(Math.Tau, results[0]);
    }

    // --- Math static type: functions ---

    [Fact]
    public async Task Math_sqrt_returns_square_root()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.sqrt(144))");
        Assert.Single(results);
        Assert.Equal(12.0, results[0]);
    }

    [Fact]
    public async Task Math_abs_returns_absolute_value()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.abs(-42))");
        Assert.Single(results);
        Assert.Equal(42, results[0]);
    }

    [Fact]
    public async Task Math_pow_raises_to_power()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.pow(2, 10))");
        Assert.Single(results);
        Assert.Equal(1024.0, results[0]);
    }

    [Fact]
    public async Task Math_sin_returns_sine()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.sin(0))");
        Assert.Single(results);
        Assert.Equal(0.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_cos_returns_cosine()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.cos(0))");
        Assert.Single(results);
        Assert.Equal(1.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_log_returns_natural_log()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.log(Math.E))");
        Assert.Single(results);
        Assert.Equal(1.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_log_with_base_returns_log_base_n()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.log(8, 2))");
        Assert.Single(results);
        Assert.Equal(3.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_ceil_rounds_up()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.ceil(4.1))");
        Assert.Single(results);
        Assert.Equal(5.0, results[0]);
    }

    [Fact]
    public async Task Math_floor_rounds_down()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.floor(4.9))");
        Assert.Single(results);
        Assert.Equal(4.0, results[0]);
    }

    [Fact]
    public async Task Math_round_with_digits()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.round(3.14159, 2))");
        Assert.Single(results);
        Assert.Equal(3.14, results[0]);
    }

    [Fact]
    public async Task Math_factorial_returns_big_integer()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.factorial(20))");
        Assert.Single(results);
        Assert.Equal(new BigInteger(2432902008176640000), results[0]);
    }

    [Fact]
    public async Task Math_gcd_returns_greatest_common_divisor()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.gcd(48, 18))");
        Assert.Single(results);
        Assert.Equal(new BigInteger(6), results[0]);
    }

    [Fact]
    public async Task Math_lcm_returns_least_common_multiple()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.lcm(12, 8))");
        Assert.Single(results);
        Assert.Equal(new BigInteger(24), results[0]);
    }

    [Fact]
    public async Task Math_is_prime_returns_boolean()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync(
            """
            echo (Math.is-prime(7))
            echo (Math.is-prime(4))
            """);
        Assert.Collection(results,
            item => Assert.Equal(true, item),
            item => Assert.Equal(false, item));
    }

    [Fact]
    public async Task Math_choose_returns_binomial_coefficient()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.choose(10, 3))");
        Assert.Single(results);
        Assert.Equal(new BigInteger(120), results[0]);
    }

    [Fact]
    public async Task Math_hypot_returns_hypotenuse()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.hypot(3, 4))");
        Assert.Single(results);
        Assert.Equal(5.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_to_radians_converts_degrees()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.to-radians(180))");
        Assert.Single(results);
        Assert.Equal(Math.PI, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_to_degrees_converts_radians()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.to-degrees(Math.PI))");
        Assert.Single(results);
        Assert.Equal(180.0, Assert.IsType<double>(results[0]!), 10);
    }

    [Fact]
    public async Task Math_clamp_constrains_value()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo (Math.clamp(15, 0, 10))");
        Assert.Single(results);
        Assert.Equal(10.0, results[0]);
    }

    // --- Statistical pipeline commands ---

    [Fact]
    public async Task Median_of_odd_count_returns_middle_value()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 3 5 7 9 | median");
        Assert.Single(results);
        Assert.Equal(5.0, results[0]);
    }

    [Fact]
    public async Task Median_of_even_count_returns_average_of_middle_two()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 4 | median");
        Assert.Single(results);
        Assert.Equal(2.5, results[0]);
    }

    [Fact]
    public async Task Stdev_returns_population_standard_deviation()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 2 4 4 4 5 5 7 9 | stdev");
        Assert.Single(results);
        Assert.Equal(2.0, Assert.IsType<double>(results[0]!), 5);
    }

    [Fact]
    public async Task Stddev_alias_works()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 2 4 4 4 5 5 7 9 | stddev");
        Assert.Single(results);
        Assert.Equal(2.0, Assert.IsType<double>(results[0]!), 5);
    }

    [Fact]
    public async Task Variance_returns_population_variance()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 2 4 4 4 5 5 7 9 | variance");
        Assert.Single(results);
        Assert.Equal(4.0, Assert.IsType<double>(results[0]!), 5);
    }

    [Fact]
    public async Task Percentile_95_returns_correct_value()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 4 5 6 7 8 9 10 | percentile 95");
        Assert.Single(results);
        var value = Assert.IsType<double>(results[0]!);
        Assert.True(value >= 9.0 && value <= 10.0, $"Expected 95th percentile near 9.55, got {value}");
    }

    [Fact]
    public async Task Percentile_50_equals_median()
    {
        var engine = new ToshEngine();
        var medianResults = await engine.ExecuteToListAsync("echo 1 3 5 7 9 | median");
        var percentileResults = await engine.ExecuteToListAsync("echo 1 3 5 7 9 | percentile 50");
        Assert.Equal(medianResults[0], percentileResults[0]);
    }

    [Fact]
    public async Task Describe_returns_nine_stat_rows()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 10 20 30 40 50 | describe");
        Assert.Equal(9, results.Count);
    }

    [Fact]
    public async Task Empty_pipeline_returns_nothing_for_stats()
    {
        var engine = new ToshEngine();

        var medianResults = await engine.ExecuteToListAsync("echo | median");
        Assert.Empty(medianResults);

        var stdevResults = await engine.ExecuteToListAsync("echo | stdev");
        Assert.Empty(stdevResults);

        var varianceResults = await engine.ExecuteToListAsync("echo | variance");
        Assert.Empty(varianceResults);
    }
}
