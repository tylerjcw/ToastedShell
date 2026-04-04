using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

public sealed class TuiScreenTests
{
    [Fact]
    public void TuiScreen_builder_sets_title()
    {
        var screen = new TuiScreen().Title("My Screen");

        Assert.Equal("My Screen", screen.ScreenTitle);
    }

    [Fact]
    public void TuiScreen_builder_sets_layout()
    {
        var screen = new TuiScreen()
            .SetLayout(TuiLayout.SplitHorizontal)
            .SetRatio("30:70")
            .SetGap(2);

        Assert.Equal(TuiLayout.SplitHorizontal, screen.LayoutConfig.Layout);
        Assert.Equal("30:70", screen.LayoutConfig.Ratio);
        Assert.Equal(2, screen.LayoutConfig.Gap);
    }

    [Fact]
    public void TuiScreen_builder_adds_widgets()
    {
        var list = new TuiListWidgetConfig("sidebar", ["a", "b", "c"]) { DisplayProperty = "Name" };
        var text = new TuiTextWidgetConfig("detail") { Content = "hello" };

        var screen = new TuiScreen()
            .Title("Test")
            .AddWidget(list)
            .AddWidget(text);

        Assert.Equal(2, screen.Widgets.Count);
        Assert.Equal("sidebar", screen.Widgets[0].Id);
        Assert.Equal("detail", screen.Widgets[1].Id);
    }

    [Fact]
    public void TuiScreen_GetWidget_finds_by_id()
    {
        var list = new TuiListWidgetConfig("my-list", ["x"]);
        var screen = new TuiScreen().AddWidget(list);

        Assert.Same(list, screen.GetWidget("my-list"));
        Assert.Same(list, screen.GetWidget("MY-LIST")); // case-insensitive
        Assert.Null(screen.GetWidget("nonexistent"));
    }

    [Fact]
    public void TuiScreen_config_constructor_parses_dictionary()
    {
        var config = new Dictionary<string, object?>
        {
            ["Title"] = "From Config",
            ["Layout"] = "SplitHorizontal",
            ["Ratio"] = "40:60",
            ["Gap"] = 3,
        };

        var screen = new TuiScreen(config);

        Assert.Equal("From Config", screen.ScreenTitle);
        Assert.Equal(TuiLayout.SplitHorizontal, screen.LayoutConfig.Layout);
        Assert.Equal("40:60", screen.LayoutConfig.Ratio);
        Assert.Equal(3, screen.LayoutConfig.Gap);
    }

    [Fact]
    public void TuiLayoutConfig_ParseRatio_handles_valid_and_invalid()
    {
        var config = new TuiLayoutConfig { Ratio = "30:70" };
        var (first, second) = config.ParseRatio();
        Assert.Equal(30, first);
        Assert.Equal(70, second);

        config.Ratio = null;
        (first, second) = config.ParseRatio();
        Assert.Equal(50, first);
        Assert.Equal(50, second);

        config.Ratio = "invalid";
        (first, second) = config.ParseRatio();
        Assert.Equal(50, first);
        Assert.Equal(50, second);
    }

    [Fact]
    public void TuiSize_clamps_negative_values()
    {
        var size = new TuiSize(-5, -10);
        Assert.Equal(0, size.Width);
        Assert.Equal(0, size.Height);
    }

    [Fact]
    public void TuiRect_computes_derived_properties()
    {
        var rect = new TuiRect(10, 20, 30, 40);
        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
        Assert.False(rect.IsEmpty);
    }

    [Fact]
    public void TuiRect_empty_when_zero_dimension()
    {
        Assert.True(new TuiRect(0, 0, 0, 10).IsEmpty);
        Assert.True(new TuiRect(0, 0, 10, 0).IsEmpty);
    }

    [Fact]
    public void TuiScreenOutcome_defaults()
    {
        var outcome = new TuiScreenOutcome();
        Assert.Empty(outcome.Selected);
        Assert.False(outcome.Cancelled);
        Assert.Empty(outcome.Values);
    }

    [Fact]
    public void TuiPickRequest_defaults()
    {
        var request = new TuiPickRequest(["a", "b"]);
        Assert.Null(request.DisplayProperty);
        Assert.Null(request.Prompt);
        Assert.False(request.MultiSelect);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public void TuiConfirmRequest_defaults()
    {
        var request = new TuiConfirmRequest("Are you sure?");
        Assert.Equal("Yes", request.ConfirmLabel);
        Assert.Equal("No", request.CancelLabel);
        Assert.True(request.DefaultConfirm);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public void TuiInputRequest_defaults()
    {
        var request = new TuiInputRequest();
        Assert.Null(request.Prompt);
        Assert.Null(request.DefaultValue);
        Assert.False(request.Multiline);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public void TuiFilePickRequest_defaults()
    {
        var request = new TuiFilePickRequest();
        Assert.Null(request.InitialPath);
        Assert.Null(request.Filter);
        Assert.False(request.DirectoryOnly);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public void TuiRunRequest_wraps_screen()
    {
        var screen = new TuiScreen().Title("Test");
        var request = new TuiRunRequest(screen);
        Assert.Same(screen, request.Screen);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public void Widget_configs_preserve_properties()
    {
        var list = new TuiListWidgetConfig("list1", ["x", "y"])
        {
            MultiSelect = true,
            Searchable = true,
            DisplayProperty = "Name",
            Prompt = "Pick:",
        };

        Assert.Equal(TuiWidgetKind.List, list.Kind);
        Assert.Equal("list1", list.Id);
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.MultiSelect);
        Assert.True(list.Searchable);
        Assert.Equal("Name", list.DisplayProperty);
        Assert.Equal("Pick:", list.Prompt);

        var text = new TuiTextWidgetConfig("text1") { Content = "hello", WordWrap = false };
        Assert.Equal(TuiWidgetKind.Text, text.Kind);
        Assert.Equal("hello", text.Content);
        Assert.False(text.WordWrap);

        var input = new TuiTextInputConfig("input1") { Prompt = "Name:", DefaultValue = "test", Multiline = true };
        Assert.Equal(TuiWidgetKind.TextInput, input.Kind);
        Assert.Equal("Name:", input.Prompt);

        var fp = new TuiFilePickerConfig("fp1") { InitialPath = "/tmp", Filter = "*.cs", DirectoryOnly = true };
        Assert.Equal(TuiWidgetKind.FilePicker, fp.Kind);
        Assert.True(fp.DirectoryOnly);

        var op = new TuiOptionPickerConfig("op1", ["a", "b"]) { DisplayProperty = "Value", Prompt = "Choose:" };
        Assert.Equal(TuiWidgetKind.OptionPicker, op.Kind);
        Assert.Equal(2, op.Options.Count);

        var confirm = new TuiConfirmationConfig("c1", "Delete?") { ConfirmLabel = "Delete", CancelLabel = "Keep", DefaultConfirm = false };
        Assert.Equal(TuiWidgetKind.Confirmation, confirm.Kind);
        Assert.Equal("Delete?", confirm.Message);
        Assert.False(confirm.DefaultConfirm);
    }

    [Fact]
    public void TuiWidgetBinding_properties()
    {
        var binding = new TuiWidgetBinding("sidebar", "selected");
        Assert.Equal("sidebar", binding.SourceWidgetId);
        Assert.Equal("selected", binding.Property);
    }

    [Fact]
    public void TuiTextWidgetConfig_binding_overrides_content()
    {
        var widget = new TuiTextWidgetConfig("detail")
        {
            Content = "static",
            Binding = new TuiWidgetBinding("list1", "selected"),
        };

        Assert.NotNull(widget.Binding);
        Assert.Equal("list1", widget.Binding.SourceWidgetId);
    }
}
