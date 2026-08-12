using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Parsing;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Tests;

/// <summary>
/// TS-P3-07 — focused contracts for the first-class quantity stabilization.
/// Kept in one selection so it can be run without invoking the memory-heavy
/// full solution suite.
/// </summary>
public sealed class UnitSystemStabilizationTests
{
    [Theory]
    [InlineData("km/hr", 1.0, 1000.0 / 3600.0)]
    [InlineData("deg/s", 90.0, Math.PI / 2.0)]
    [InlineData("hr/s", 1.0, 3600.0)]
    public void Compound_literals_retain_their_display_to_base_factor(
        string unit,
        double magnitude,
        double expectedBaseValue)
    {
        var quantity = Quantity.FromLiteral(magnitude, unit);

        Assert.Equal(expectedBaseValue, quantity.BaseValue, 12);
    }

    [Fact]
    public void Explicit_conversion_accepts_simple_and_compound_targets()
    {
        var distance = Quantity.FromLiteral(2, "mi").To("ft");
        var duration = Quantity.FromLiteral(2, "hr").To("s");
        var speed = Quantity.FromLiteral(10, "m/s").To("mph");

        Assert.Equal(10_560, distance.Magnitude, 10);
        Assert.Equal(7_200, duration.Magnitude, 10);
        Assert.Equal(22.369362920544, speed.Magnitude, 10);

        var fromBase = UnitRegistry.Instance.CreateTypedFromBase(
            1_000,
            UnitExpression.Of(UnitDimension.Length),
            "km");
        Assert.Equal(1, fromBase.Magnitude, 12);
    }

    [Fact]
    public async Task As_with_a_backtick_target_is_the_language_conversion_form()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var result = Assert.Single(await engine.ExecuteToListAsync("2`mi as `ft"));
        var distance = Assert.IsAssignableFrom<Quantity>(result);

        Assert.Equal("ft", distance.UnitSymbol);
        Assert.Equal(10_560, distance.Magnitude, 10);
    }

    [Fact]
    public async Task Quantity_interpolation_uses_scalar_display_without_losing_structured_members()
    {
        var power = Quantity.FromLiteral(483.06, "MW");
        var formatter = new ObjectFormatter();
        var record = new Dictionary<string, object?> { ["power"] = power };

        Assert.Equal("483.06 MW", formatter.Format(power));
        Assert.Equal("{| power = 483.06 MW |}", formatter.Format(record));

        var displayedRecord = StyledText.StripAnsi(new DisplayEngine(formatter).Render(record));
        Assert.Contains("483.06 MW", displayedRecord, StringComparison.Ordinal);
        Assert.DoesNotContain("base-value", displayedRecord, StringComparison.Ordinal);

        Assert.True(power.TryGetMember("base-value", out var baseValue));
        Assert.Equal(483_060_000.0, Assert.IsType<double>(baseValue), 6);

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            var power = 483.06`MW
            echo $"{$power}"
            echo $power.ToString()
            echo $power.ToString("F1")
            """);

        Assert.Equal(
            ["483.06 MW", "483.06 MW", "483.1 MW"],
            results.Select(value => value?.ToString()).ToArray());
    }

    [Theory]
    [InlineData("90°", typeof(AngleQuantity), 90.0, Math.PI / 2.0)]
    [InlineData("20°C", typeof(TemperatureQuantity), 20.0, 293.15)]
    [InlineData("68°F", typeof(TemperatureQuantity), 68.0, 293.15)]
    [InlineData("540°R", typeof(TemperatureQuantity), 540.0, 300.0)]
    public void Degree_adjacency_lexes_as_a_typed_quantity(
        string source,
        Type expectedType,
        double expectedMagnitude,
        double expectedBaseValue)
    {
        var token = new ToshLexer(source).Lex()[0];
        var quantity = Assert.IsAssignableFrom<Quantity>(token.Value);

        Assert.Equal(SyntaxTokenKind.UnitLiteral, token.Kind);
        Assert.Equal(expectedType, quantity.GetType());
        Assert.Equal(expectedMagnitude, quantity.Magnitude, 10);
        Assert.Equal(expectedBaseValue, quantity.BaseValue, 10);
    }

    [Fact]
    public void Degree_adjacency_can_lead_a_linear_compound_unit()
    {
        var token = new ToshLexer("90°/s").Lex()[0];
        var angularVelocity = Assert.IsType<AngularVelocityQuantity>(token.Value);

        Assert.Equal(Math.PI / 2.0, angularVelocity.BaseValue, 12);
    }

    [Theory]
    [InlineData(".5`m", 0.5)]
    [InlineData("5.`m", 5.0)]
    [InlineData(".5°", 0.5)]
    [InlineData("5.°", 5.0)]
    public void Leading_or_trailing_decimal_points_are_consistent_quantity_magnitudes(
        string source,
        double expectedMagnitude)
    {
        var token = Assert.Single(new ToshLexer(source).Lex().Where(
            candidate => candidate.Kind != SyntaxTokenKind.EndOfFile));
        var quantity = Assert.IsAssignableFrom<Quantity>(token.Value);

        Assert.Equal(SyntaxTokenKind.UnitLiteral, token.Kind);
        Assert.Equal(expectedMagnitude, quantity.Magnitude, 12);
    }

    [Fact]
    public void Degree_shorthand_does_not_capture_kelvin_or_electrical_symbols()
    {
        var kelvin = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer("20°K").Lex());
        Assert.Equal("tosh.parser.invalid_unit_literal", kelvin.Diagnostic.Code);

        Assert.IsType<TemperatureQuantity>(Quantity.FromLiteral(20, "K"));
        Assert.IsType<ChargeQuantity>(Quantity.FromLiteral(1, "C"));
        Assert.IsType<CapacitanceQuantity>(Quantity.FromLiteral(1, "F"));

        var adjacentText = new ToshLexer("5km").Lex()[0];
        Assert.Equal(SyntaxTokenKind.Bareword, adjacentText.Kind);
    }

    [Theory]
    [InlineData("90º")]
    [InlineData("90˚")]
    [InlineData("90∘")]
    public void Degree_lookalikes_are_not_silently_normalized(string source)
    {
        var token = new ToshLexer(source).Lex()[0];

        Assert.Equal(SyntaxTokenKind.Bareword, token.Kind);
        Assert.Equal(source, token.Text);
    }

    [Theory]
    [InlineData("1__0`m")]
    [InlineData("1_`m")]
    [InlineData("1_e2`m")]
    [InlineData("1e_2`m")]
    [InlineData("_1`m")]
    public void Quantity_magnitudes_cannot_bypass_separator_validation(string source)
    {
        var exception = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer(source).Lex());

        Assert.Equal("tosh.parser.invalid_numeric_separator", exception.Diagnostic.Code);
    }

    [Fact]
    public void An_explicit_unknown_unit_is_a_unit_diagnostic()
    {
        var exception = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer("1`bogus").Lex());

        Assert.Equal("tosh.parser.invalid_unit_literal", exception.Diagnostic.Code);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("m*1")]
    [InlineData("m/1")]
    [InlineData("1^2")]
    public void Numeric_one_is_only_valid_as_a_reciprocal_numerator(string unit)
    {
        Assert.False(UnitExpressionParser.TryParseConversion(unit, out _, out _, out _));
        Assert.True(UnitExpressionParser.TryParseConversion("1/s", out _, out _, out _));
    }

    [Fact]
    public void Derived_arithmetic_uses_base_values_and_a_canonical_unit()
    {
        var power = Quantity.FromLiteral(40, "MW");
        var duration = Quantity.FromLiteral(5, "s");

        var energy = power * duration;

        Assert.IsType<EnergyQuantity>(energy);
        Assert.Equal("J", energy.UnitSymbol);
        Assert.Equal(200_000_000, energy.Magnitude, 6);
        Assert.Equal(200_000_000, energy.BaseValue, 6);
    }

    [Fact]
    public void Canonical_symbols_round_trip_multiple_denominator_dimensions()
    {
        var result = Quantity.FromLiteral(1, "m") /
            (Quantity.FromLiteral(1, "kg") * Quantity.FromLiteral(1, "s"));

        Assert.Equal("m/kg/s", result.UnitSymbol);
        Assert.True(UnitExpressionParser.TryParseConversion(
            result.UnitSymbol,
            out var conversion,
            out var reparsed,
            out _));
        Assert.Equal(result.Dimension, reparsed);
        Assert.Equal(1.0, conversion.ToBaseFactor, 12);
    }

    [Fact]
    public void Dimension_exponents_cannot_be_mutated_through_the_public_view()
    {
        var dimension = UnitExpression.Of(UnitDimension.Length);
        var exposed = Assert.IsAssignableFrom<IDictionary<UnitDimension, int>>(dimension.Exponents);

        Assert.Throws<NotSupportedException>(() => exposed[UnitDimension.Time] = 1);
        Assert.Equal(0, dimension.GetExponent(UnitDimension.Time));
    }

    [Theory]
    [InlineData("Nm", typeof(TorqueQuantity))]
    [InlineData("F", typeof(CapacitanceQuantity))]
    [InlineData("H", typeof(InductanceQuantity))]
    [InlineData("mol", typeof(SubstanceQuantity))]
    [InlineData("cd", typeof(LuminosityQuantity))]
    [InlineData("rpm", typeof(AngularVelocityQuantity))]
    public void A_named_unit_preserves_its_semantic_category(string unit, Type expectedType)
    {
        var quantity = Quantity.FromLiteral(1, unit);

        Assert.Equal(expectedType, quantity.GetType());
    }

    [Fact]
    public void A_dimensionless_language_quotient_is_a_number()
    {
        var ratio = OperatorEvaluator.EvaluateBinary(
            Quantity.FromLiteral(40, "MW"),
            "/",
            Quantity.FromLiteral(2.5, "MW"));

        Assert.Equal(16.0, Assert.IsType<double>(ratio), 12);
    }

    [Fact]
    public void Language_arithmetic_scalarizes_cancelled_dimensions_and_supports_unary_quantity_values()
    {
        var product = OperatorEvaluator.EvaluateBinary(
            Quantity.FromLiteral(2, "m"),
            "*",
            Quantity.FromLiteral(0.5, "1/m"));
        var negated = OperatorEvaluator.EvaluateUnary("-", Quantity.FromLiteral(2, "km"));
        var positive = OperatorEvaluator.EvaluateUnary("+", Quantity.FromLiteral(2, "km"));

        Assert.Equal(1.0, Assert.IsType<double>(product), 12);
        Assert.Equal(-2.0, Assert.IsType<LengthQuantity>(negated).Magnitude, 12);
        Assert.Equal(2.0, Assert.IsType<LengthQuantity>(positive).Magnitude, 12);
    }

    [Fact]
    public async Task Friendly_quantity_annotations_convert_function_argument_strings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var result = Assert.Single(await engine.ExecuteToListAsync(
            """
            func in-feet(distance: length) -> length {
                return ($distance as `ft)
            }
            in-feet "2mi"
            """));
        var distance = Assert.IsType<LengthQuantity>(result);

        Assert.Equal("ft", distance.UnitSymbol);
        Assert.Equal(10_560, distance.Magnitude, 10);
    }

    [Fact]
    public async Task Quantity_aliases_share_is_and_as_type_resolution()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            "var d = 2`mi\necho ($d is quantity)\necho ($d as length)");

        Assert.True(Assert.IsType<bool>(results[0]));
        Assert.IsType<LengthQuantity>(results[1]);
    }

    [Fact]
    public async Task Named_quantities_satisfy_inherited_arithmetic_traits()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var result = Assert.Single(await engine.ExecuteToListAsync("echo (2`m is Add)"));

        Assert.True(Assert.IsType<bool>(result));
    }

    [Fact]
    public async Task Sleep_accepts_a_zero_duration_quantity_without_delaying()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        Assert.Empty(await engine.ExecuteToListAsync("sleep 0`ms"));
    }

    [Fact]
    public void Quantity_and_legacy_shell_value_bridges_are_lossless_when_representable()
    {
        Assert.True(TypeConversion.TryConvert("2hr", typeof(DurationQuantity), out var durationValue));
        Assert.True(TypeConversion.TryConvert(durationValue, typeof(TimeSpan), out var spanValue));
        Assert.Equal(TimeSpan.FromHours(2), Assert.IsType<TimeSpan>(spanValue));
        Assert.True(TypeConversion.TryConvert(durationValue, typeof(TemporalAmount), out var amountValue));
        var amount = Assert.IsType<TemporalAmount>(amountValue);
        Assert.True(amount.TryAsTimeSpan(out var fixedSpan));
        Assert.Equal(TimeSpan.FromHours(2), fixedSpan);

        var storage = StorageSize.FromBytes(1_000);
        Assert.True(TypeConversion.TryConvert(storage, typeof(DataSizeQuantity), out var dataValue));
        var data = Assert.IsType<DataSizeQuantity>(dataValue);
        Assert.Equal(8_000, data.BaseValue, 10);
        Assert.True(TypeConversion.TryConvert(data, typeof(StorageSize), out var roundTrip));
        Assert.Equal(storage, Assert.IsType<StorageSize>(roundTrip));

        var fractionalByte = Quantity.FromLiteral(1, "bit");
        Assert.False(TypeConversion.TryConvert(fractionalByte, typeof(StorageSize), out _));
    }

    [Theory]
    [InlineData(9_007_199_254_740_992L, true)]
    [InlineData(9_007_199_254_740_993L, false)]
    [InlineData(9_007_199_254_740_994L, true)]
    [InlineData(long.MinValue, true)]
    [InlineData(long.MaxValue, false)]
    public void Storage_bridge_accepts_exact_binary64_integers_only(long bytes, bool expected)
    {
        var converted = TypeConversion.TryConvert(
            StorageSize.FromBytes(bytes),
            typeof(DataSizeQuantity),
            out _);

        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    public void Numeric_timespan_try_conversion_never_throws(double seconds)
    {
        Assert.False(TypeConversion.TryConvert(seconds, typeof(TimeSpan), out _));
    }

    [Theory]
    [InlineData("K/s")]
    [InlineData("°R/s")]
    [InlineData("degC*m")]
    public void Absolute_temperature_scales_cannot_be_compound_units(string unit)
    {
        Assert.False(UnitExpressionParser.TryParseConversion(unit, out _, out _, out _));
    }

    [Theory]
    [InlineData("mK")]
    [InlineData("mrad")]
    [InlineData("zm")]
    [InlineData("qm")]
    public void Prefixable_si_units_accept_registered_prefix_families(string unit)
    {
        Assert.NotNull(UnitRegistry.Instance.TryResolve(unit));
    }

    [Theory]
    [InlineData("kkg")]
    [InlineData("mmin")]
    [InlineData("kft")]
    [InlineData("kKiB")]
    public void Non_prefixable_units_reject_synthetic_prefixes(string unit)
    {
        Assert.Null(UnitRegistry.Instance.TryResolve(unit));
    }

    [Fact]
    public void Quantity_aggregations_preserve_the_first_display_unit()
    {
        object?[] values =
        [
            Quantity.FromLiteral(1, "km"),
            Quantity.FromLiteral(500, "m"),
        ];

        var sum = Assert.IsType<LengthQuantity>(AggregationUtilities.Sum(values));
        var average = Assert.IsType<LengthQuantity>(AggregationUtilities.Average(values));

        Assert.Equal("km", sum.UnitSymbol);
        Assert.Equal(1.5, sum.Magnitude, 12);
        Assert.Equal("km", average.UnitSymbol);
        Assert.Equal(0.75, average.Magnitude, 12);
    }

    [Fact]
    public void Compile_time_type_names_share_the_runtime_quantity_aliases()
    {
        var resolver = new TypeNameResolver();

        Assert.Equal(typeof(Quantity), resolver.Resolve("quantity").ClrType);
        Assert.Equal(typeof(LengthQuantity), resolver.Resolve("length").ClrType);
        Assert.Equal(typeof(DurationQuantity), resolver.Resolve("timequantity").ClrType);
        Assert.Equal(typeof(TemporalAmount), resolver.Resolve("duration").ClrType);
    }

    [Fact]
    public async Task Reactor_example_calculates_the_reference_uranium_block_with_quantities()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../examples/reactor-block.tosh"));
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await AsyncEnumerableExtensions.ToListAsync(
            engine.ExecuteScriptFileAsync(
                path,
                ["calc", "Nuclear", "2", "2", "300", "25000"]),
            default);
        var block = Assert.Single(results);

        var output = Assert.IsAssignableFrom<Quantity>(
            runtime.ObjectAccessor.GetValue(block, "OutputPower"));
        var burnTime = Assert.IsAssignableFrom<Quantity>(
            runtime.ObjectAccessor.GetValue(block, "BurnTime"));

        Assert.Equal(480, output.To("MW").Magnitude, 10);
        Assert.Equal(200, burnTime.To("s").Magnitude, 10);
        // These are computed by division and ceiling, so they arrive as `double` — comparing
        // against an `int` literal failed on the boxed type while reporting "Expected: 48,
        // Actual: 48".
        Assert.Equal(48, Convert.ToInt32(runtime.ObjectAccessor.GetValue(block, "Exchangers")));
        Assert.Equal(83, Convert.ToInt32(runtime.ObjectAccessor.GetValue(block, "Turbines")));
        Assert.Equal(60, Convert.ToInt32(runtime.ObjectAccessor.GetValue(block, "Tanks")));
        Assert.Equal(1.2, Assert.IsType<double>(
            runtime.ObjectAccessor.GetValue(block, "FuelPerMin")), 10);
    }

    [Fact]
    public void Ambiguous_absolute_temperature_arithmetic_is_rejected_for_now()
    {
        var left = Quantity.FromLiteral(20, "°C");
        var right = Quantity.FromLiteral(10, "°C");

        var exception = Assert.Throws<InvalidOperationException>(() => left - right);
        Assert.Contains("temperature-difference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Absolute_temperature_validation_precedes_divide_by_zero()
    {
        var point = Quantity.FromLiteral(0, "K");

        var exception = Assert.Throws<InvalidOperationException>(() => point / 0.0);
        Assert.Contains("temperature-difference", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dimension_reciprocal_uses_checked_exponent_arithmetic()
    {
        var expression = UnitExpression.Of(UnitDimension.Length, int.MinValue);

        Assert.Throws<OverflowException>(() => expression.Reciprocal());
        Assert.Throws<OverflowException>(() => expression.ToCanonicalUnitSymbol());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unit_definitions_reject_non_invertible_conversion_factors(double factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitConversion(factor));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UnitDefinition(
            "bad",
            "bad unit",
            "Test",
            UnitExpression.Of(UnitDimension.Length),
            factor));
    }

    [Fact]
    public void User_registration_cannot_replace_a_builtin_symbol()
    {
        var replacement = new UnitDefinition(
            "m",
            "mutable meter",
            "Length",
            UnitExpression.Of(UnitDimension.Length),
            2,
            isUserDefined: true);

        Assert.Throws<InvalidOperationException>(() => UnitRegistry.Instance.RegisterUnit(replacement));
        Assert.Equal(1.0, UnitRegistry.Instance.TryResolve("m")!.ToBaseFactor, 12);
    }
}
