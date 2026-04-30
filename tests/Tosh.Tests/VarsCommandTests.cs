using Tosh.Runtime;
using Tosh.Stdlib;
using Tosh.Stdlib.Sys;
using Tosh.Language;
using Tosh.Tui.Requests;

namespace Tosh.Tests;

public sealed class VarsCommandTests
{
    [Fact]
    public async Task Vars_browse_returns_picker_request_when_inline_prompt_is_unavailable()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InlinePrompts = null;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            var sample = 5
            vars browse
            """);

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.NotEmpty(request.Items);
        Assert.Contains(request.Items, item => item is ShellVariableBrowseEntry entry && entry.Expression == "$tosh");
        Assert.Contains(request.Items, item => item is ShellVariableBrowseEntry entry && entry.Expression == "$sample");
    }

    [Fact]
    public async Task Vars_browse_with_inline_provider_uses_pick_and_inspects_selected_value()
    {
        var runtime = ToshRuntime.CreateDefault();
        var inline = new VarsInlineProvider();
        runtime.InlinePrompts = inline;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync(
            """
            var sample = 5
            vars browse
            """);

        var selected = Assert.IsType<ShellVariableBrowseEntry>(Assert.Single(results));
        Assert.Equal("$sample", selected.Expression);
        Assert.Equal(1, inline.PickCalls);
        Assert.Equal(0, inline.FilterCalls);
        Assert.Equal(1, inline.InspectCalls);
        Assert.Equal("$sample", inline.LastInspectedExpression);
        Assert.Equal(5, Assert.IsType<int>(inline.LastInspectedValue));
    }

    private sealed class VarsInlineProvider : IInlinePromptProvider
    {
        public int PickCalls { get; private set; }
        public int FilterCalls { get; private set; }
        public int InspectCalls { get; private set; }
        public object? LastInspectedValue { get; private set; }
        public string? LastInspectedExpression { get; private set; }

        public void Inspect(object? value, bool includeAllMembers = false, string? sourceExpression = null)
        {
            InspectCalls++;
            LastInspectedValue = value;
            LastInspectedExpression = sourceExpression;
        }

        public void BrowseHelp(string? initialQuery = null, string? initialTopicName = null)
        {
        }

        public IReadOnlyList<object?>? Pick(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10)
        {
            PickCalls++;
            var sample = items
                .OfType<ShellVariableBrowseEntry>()
                .First(entry => string.Equals(entry.Expression, "$sample", StringComparison.Ordinal));
            return [sample];
        }

        public bool? Confirm(string message, bool defaultValue = true)
            => true;

        public string? Input(string? prompt = null, string? defaultValue = null, bool password = false)
            => defaultValue;

        public IReadOnlyList<object?>? Filter(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10)
        {
            FilterCalls++;
            return null;
        }
    }
}
