using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class StorageSizeLiteralSemanticsTests
{
    [Theory]
    [InlineData("10b", 10L)]
    [InlineData("10kb", 10_000L)]
    [InlineData("2.5mb", 2_500_000L)]
    [InlineData("1gb", 1_000_000_000L)]
    [InlineData("1tb", 1_000_000_000_000L)]
    [InlineData("1pb", 1_000_000_000_000_000L)]
    [InlineData("1KiB", 1_024L)]
    [InlineData("1mib", 1_048_576L)]
    [InlineData("1gib", 1_073_741_824L)]
    [InlineData("1tib", 1_099_511_627_776L)]
    [InlineData("1pib", 1_125_899_906_842_624L)]
    [InlineData("1e3b", 1_000L)]
    [InlineData("-1kb", -1_000L)]
    public void Every_documented_storage_suffix_parses_to_a_typed_literal(
        string text,
        long expectedBytes)
    {
        Assert.True(StorageSize.TryParseLiteral(text, out var parsed));
        Assert.Equal(StorageSize.FromBytes(expectedBytes), parsed);

        Assert.True(IntrinsicLiteralParser.TryParseExpressionLiteral(text, out var intrinsic));
        Assert.Equal(parsed, Assert.IsType<StorageSize>(intrinsic));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10 kb")]
    [InlineData("10xb")]
    [InlineData("kb")]
    [InlineData("1.2.3kb")]
    [InlineData("10000pb")]
    [InlineData("1e28pb")]
    public void Storage_literals_require_one_valid_suffix_token(string text)
    {
        Assert.False(StorageSize.TryParseLiteral(text, out _));
    }

    [Fact]
    public async Task Storage_literals_are_typed_in_declarations_and_collections()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            var small = 10kb
            var sizes = [1kb, 2kib]
            echo $small | type-of
            echo $small.Bytes
            echo $sizes[0].Bytes $sizes[1].Bytes
            """);

        Assert.Equal(typeof(StorageSize), results[0]);
        Assert.Equal(10_000L, results[1]);
        Assert.Equal(1_000L, results[2]);
        Assert.Equal(2_048L, results[3]);
    }

    [Fact]
    public async Task Specification_storage_comparison_and_arithmetic_examples_are_typed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            var small = 10kb
            var large = 2.5mb
            echo ($large > $small)
            echo (10kb > 5kb)
            var total = (10kb + 10kb)
            echo $total.Bytes
            """);

        Assert.Equal(new object?[] { true, true, 20_000L }, results);
    }

    [Fact]
    public async Task Raw_command_arguments_remain_strings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var result = Assert.Single(await engine.ExecuteToListAsync("echo 10kb | type-of"));

        Assert.Equal(typeof(string), result);
    }

    [Fact]
    public async Task Invalid_storage_suffixes_do_not_fall_back_to_expression_strings()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var size = 10xb"));

        Assert.Equal("tosh.runtime.unknown_command", Assert.Single(exception.Diagnostics).Code);
    }
}
