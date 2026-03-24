using Tosh.Core;

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

    private sealed record ConditionalDemo(string Name, bool Highlight);

    private sealed record SizedDemo(string Name, StorageSize Size);
}
