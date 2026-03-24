using Tosh.Core;

namespace Tosh.Tests;

public sealed class DisplayEngineTests
{
    [Fact]
    public void Display_engine_renders_history_with_explicit_shell_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new CommandHistoryEntry(1, "help", new DateTimeOffset(2026, 3, 23, 9, 15, 0, TimeSpan.Zero)),
            new CommandHistoryEntry(2, "ls -la", new DateTimeOffset(2026, 3, 23, 9, 16, 0, TimeSpan.Zero)),
        };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("╰", text, StringComparison.Ordinal);
        Assert.Contains("│ # ", text, StringComparison.Ordinal);
        Assert.Contains("Index", text, StringComparison.Ordinal);
        Assert.Contains("Text", text, StringComparison.Ordinal);
        Assert.Contains("When", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Timestamp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_drops_low_priority_columns_when_terminal_is_narrow()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePathA = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        var filePathB = System.IO.Path.Combine(tempDirectory.Path, "beta.txt");
        File.WriteAllText(filePathA, "alpha");
        File.WriteAllText(filePathB, "beta");

        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            FileSystemEntry.From(new FileInfo(filePathA), preferLongDisplay: true),
            FileSystemEntry.From(new FileInfo(filePathB), preferLongDisplay: true),
        };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 34));

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Type", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Modified", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_structured_values_as_record_tables()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePath = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        File.WriteAllText(filePath, "alpha");

        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            FileSystemEntry.From(new FileInfo(filePath), preferLongDisplay: true),
        };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("│ Name", text, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("│ # ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("├", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_scalar_lists_as_indexed_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[] { "alpha", "beta" };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("│ 0 │ alpha │", text, StringComparison.Ordinal);
        Assert.Contains("│ 1 │ beta  │", text, StringComparison.Ordinal);
        Assert.DoesNotContain("├", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_projected_objects_with_dynamic_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new ProjectedObject(
            [
                new ProjectedField("Name", "Name", "alpha"),
                new ProjectedField("Size", "Size", StorageSize.FromBytes(1024)),
            ]),
            new ProjectedObject(
            [
                new ProjectedField("Name", "Name", "beta"),
                new ProjectedField("Size", "Size", StorageSize.FromBytes(2048)),
            ]),
        };

        var text = display.RenderMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains("2 kB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_groupings_with_key_and_count_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new GroupingInfo("file", [1, 2]),
            new GroupingInfo("dir", [3]),
        };

        var text = display.RenderMany(values);

        Assert.Contains("Key", text, StringComparison.Ordinal);
        Assert.Contains("Count", text, StringComparison.Ordinal);
        Assert.Contains("file", text, StringComparison.Ordinal);
        Assert.Contains("dir", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_enumerable_values_as_their_contents()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[] { new[] { "alpha", "beta" } };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains("beta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Object[]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_external_text_lines_as_plain_text()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new ShellTextLine("alpha"),
            new ShellTextLine("beta"),
            new ShellTextLine(string.Empty),
            new ShellTextLine("gamma"),
        };

        var text = display.RenderMany(values);

        Assert.Equal($"alpha{Environment.NewLine}beta{Environment.NewLine}{Environment.NewLine}gamma", text);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-display-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
