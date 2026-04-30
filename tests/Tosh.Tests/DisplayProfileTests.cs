using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class DisplayProfileTests
{
    [Fact]
    public void Registry_allows_only_one_profile_per_type()
    {
        var registry = new DisplayProfileRegistry();
        registry.Register(DisplayProfile.For<DateTime>());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(DisplayProfile.For<DateTime>()));

        Assert.Contains("DateTime", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_profile_can_use_conditional_value_cases()
    {
        var registry = new DisplayProfileRegistry();
        registry.Register(
            DisplayProfile
                .For<ConditionalDemo>()
                .AddValueCase(
                    DisplaySurface.Root | DisplaySurface.Nested,
                    context => ((ConditionalDemo)context.Value).Highlight,
                    _ => "highlighted")
                .AddValueCase(
                    DisplaySurface.Root | DisplaySurface.Nested,
                    context => ((ConditionalDemo)context.Value).Name));

        var formatter = new ObjectFormatter(registry);

        Assert.Equal("highlighted", formatter.Format(new ConditionalDemo("alpha", Highlight: true)));
        Assert.Equal("beta", formatter.Format(new ConditionalDemo("beta", Highlight: false)));
    }

    [Fact]
    public void Display_engine_uses_profile_table_schema_for_registered_types()
    {
        var registry = new DisplayProfileRegistry();
        registry.Register(
            DisplayProfile
                .For<ConditionalDemo>()
                .AddValueCase(
                    DisplaySurface.Root | DisplaySurface.Nested,
                    context => ((ConditionalDemo)context.Value).Name)
                .AddTableCase(
                    _ =>
                    [
                        new DisplayTableColumn("Name", row => ((ConditionalDemo)row).Name, MinWidth: 4, CanHide: false),
                        new DisplayTableColumn("State", row => ((ConditionalDemo)row).Highlight ? "hot" : "plain", MinWidth: 5),
                    ]));

        var display = new DisplayEngine(new ObjectFormatter(registry));
        var text = display.RenderMany(
        [
            new ConditionalDemo("alpha", Highlight: true),
            new ConditionalDemo("beta", Highlight: false),
        ]);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("State", text, StringComparison.Ordinal);
        Assert.Contains("hot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Highlight", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_tables_render_profile_backed_value_types()
    {
        var preferences = new DisplayPreferences
        {
            NowProvider = () => new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
        };
        var display = new DisplayEngine(new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences)));

        var text = display.RenderMany(
        [
            new SizedDemo("alpha", StorageSize.FromBytes(1536)),
            new SizedDemo("beta", StorageSize.FromBytes(2_000_000)),
        ]);

        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("1.5 kB", text, StringComparison.Ordinal);
        Assert.Contains("2 MB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_uses_record_value_surface_for_nested_profile_overrides()
    {
        var registry = DisplayProfileRegistry.CreateDefault();
        registry.Register(
            DisplayProfile
                .For<RecordValueDemo>()
                .AddValueCase(DisplaySurface.TableCell, _ => "compact")
                .AddValueCase(DisplaySurface.RecordValue, _ => "detail line 1\ndetail line 2"));

        IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();
        record["Demo"] = new RecordValueDemo("alpha");

        var display = new DisplayEngine(new ObjectFormatter(registry));
        var text = display.RenderMany([record]);

        Assert.Contains("detail line 1", text, StringComparison.Ordinal);
        Assert.Contains("detail line 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("compact", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_applies_user_defined_table_column_overrides()
    {
        IDictionary<string, object?> first = new System.Dynamic.ExpandoObject();
        first["Name"] = "alpha";
        first["Value"] = 1;
        first["Kind"] = "demo";

        IDictionary<string, object?> second = new System.Dynamic.ExpandoObject();
        second["Name"] = "beta";
        second["Value"] = 2;
        second["Kind"] = "demo";

        var preferences = new DisplayPreferences();
        preferences.Profiles.GetOrCreate("table").SetTableColumns(["Kind", "Name"]);

        var display = new DisplayEngine(new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences)))
        {
            Preferences = preferences,
        };

        var text = display.RenderMany([first, second]);

        Assert.Contains("Kind", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Value", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Kind", StringComparison.Ordinal) < text.IndexOf("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_color_renders_with_three_row_swatch_and_all_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var color = System.Drawing.Color.CornflowerBlue;
        var rendered = display.Render(color);
        var plain = StyledText.StripAnsi(rendered);

        Assert.Contains("Sample", plain, StringComparison.Ordinal);
        Assert.Contains("Name", plain, StringComparison.Ordinal);
        Assert.Contains("Hex", plain, StringComparison.Ordinal);
        Assert.Contains("CornflowerBlue", plain, StringComparison.Ordinal);
        Assert.Contains("#6495ED", plain, StringComparison.Ordinal);
        Assert.Contains("IsKnown", plain, StringComparison.Ordinal);
        Assert.Contains("IsNamed", plain, StringComparison.Ordinal);
        Assert.Contains("IsSystem", plain, StringComparison.Ordinal);

        // Should have multiline swatch (3 lines of block chars)
        Assert.Contains("███████", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Color_collection_renders_with_sample_last()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            System.Drawing.Color.Red,
            System.Drawing.Color.Green,
            System.Drawing.Color.Blue,
        };
        var rendered = display.RenderMany(values);
        var plain = StyledText.StripAnsi(rendered);

        Assert.Contains("Name", plain, StringComparison.Ordinal);
        Assert.Contains("Hex", plain, StringComparison.Ordinal);
        Assert.Contains("Sample", plain, StringComparison.Ordinal);
        Assert.Contains("Red", plain, StringComparison.Ordinal);
        Assert.Contains("Green", plain, StringComparison.Ordinal);
        Assert.Contains("Blue", plain, StringComparison.Ordinal);

        // Sample should be after Hex in the header line
        var headerLine = plain.Split('\n').First(l => l.Contains("Name", StringComparison.Ordinal) && l.Contains("Sample", StringComparison.Ordinal));
        Assert.True(
            headerLine.IndexOf("Sample", StringComparison.Ordinal) >
            headerLine.IndexOf("Hex", StringComparison.Ordinal));

    }

    private sealed record ConditionalDemo(string Name, bool Highlight);

    private sealed record SizedDemo(string Name, StorageSize Size);

    private sealed record RecordValueDemo(string Name);
}
