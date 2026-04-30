using Tosh.Runtime;
using Tosh.Language;
using Tosh.Tui.Requests;

namespace Tosh.Tests;

public sealed class TuiInlineTests
{
    private static ToshEngine CreateEngine(IInlinePromptProvider? provider = null)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.InlinePrompts = provider;
        // tui commands are [ShellOnly]; mark interactive so the bind-time guard
        // doesn't reject these tests.
        return new ToshEngine(runtime) { IsInteractiveSession = true };
    }

    // ── --cli flag requires provider ──────────────────────────

    [Fact]
    public async Task Pick_cli_without_provider_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui pick a b c --cli"));
    }

    [Fact]
    public async Task Confirm_cli_without_provider_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui confirm \"ok?\" --cli"));
    }

    [Fact]
    public async Task Input_cli_without_provider_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui input \"name:\" --cli"));
    }

    [Fact]
    public async Task Filter_without_provider_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui filter a b c"));
    }

    [Fact]
    public async Task Help_cli_without_provider_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("help --cli"));
    }

    // ── --cli flag with mock provider ─────────────────────────

    [Fact]
    public async Task Pick_cli_returns_selected_items()
    {
        var mock = new MockInlineProvider(pickResult: ["b"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui pick a b c --cli");

        Assert.Single(results);
        Assert.Equal("b", results[0]);
    }

    [Fact]
    public async Task Pick_cli_multi_returns_multiple()
    {
        var mock = new MockInlineProvider(pickResult: ["a", "c"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui pick a b c --cli --multi");

        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0]);
        Assert.Equal("c", results[1]);
    }

    [Fact]
    public async Task Pick_cli_cancelled_returns_empty()
    {
        var mock = new MockInlineProvider(pickResult: null);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui pick a b c --cli");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Pick_cli_passes_prompt()
    {
        var mock = new MockInlineProvider(pickResult: ["x"]);
        var engine = CreateEngine(mock);
        await engine.ExecuteToListAsync("tui pick x y --cli prompt \"Choose:\"");

        Assert.Equal("Choose:", mock.LastPickPrompt);
    }

    [Fact]
    public async Task Pick_cli_passes_display_property()
    {
        var mock = new MockInlineProvider(pickResult: ["x"]);
        var engine = CreateEngine(mock);
        await engine.ExecuteToListAsync("tui pick x y --cli display Name");

        Assert.Equal("Name", mock.LastPickDisplayProperty);
    }

    [Fact]
    public async Task Pick_cli_passes_page_size()
    {
        var mock = new MockInlineProvider(pickResult: ["x"]);
        var engine = CreateEngine(mock);
        await engine.ExecuteToListAsync("tui pick x y z --cli page-size 5");

        Assert.Equal(5, mock.LastPickPageSize);
    }

    [Fact]
    public async Task Confirm_cli_returns_true()
    {
        var mock = new MockInlineProvider(confirmResult: true);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui confirm \"Delete?\" --cli");

        Assert.True((bool)Assert.Single(results)!);
    }

    [Fact]
    public async Task Confirm_cli_returns_false()
    {
        var mock = new MockInlineProvider(confirmResult: false);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui confirm \"Delete?\" --cli");

        Assert.False((bool)Assert.Single(results)!);
    }

    [Fact]
    public async Task Confirm_cli_cancelled_returns_false()
    {
        var mock = new MockInlineProvider(confirmResult: null);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui confirm \"ok?\" --cli");

        Assert.False((bool)Assert.Single(results)!);
    }

    [Fact]
    public async Task Input_cli_returns_text()
    {
        var mock = new MockInlineProvider(inputResult: "hello world");
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui input \"Name:\" --cli");

        Assert.Equal("hello world", Assert.Single(results));
    }

    [Fact]
    public async Task Input_cli_cancelled_returns_empty()
    {
        var mock = new MockInlineProvider(inputResult: null);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui input \"Name:\" --cli");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Input_cli_passes_password_flag()
    {
        var mock = new MockInlineProvider(inputResult: "secret");
        var engine = CreateEngine(mock);
        await engine.ExecuteToListAsync("tui input \"Password:\" --cli --password");

        Assert.True(mock.LastInputPassword);
    }

    // ── tui filter ────────────────────────────────────────────

    [Fact]
    public async Task Filter_returns_selected_items()
    {
        var mock = new MockInlineProvider(filterResult: ["b"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui filter a b c");

        Assert.Single(results);
        Assert.Equal("b", results[0]);
    }

    [Fact]
    public async Task Filter_multi_returns_multiple()
    {
        var mock = new MockInlineProvider(filterResult: ["a", "c"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui filter a b c --multi");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Filter_cancelled_returns_empty()
    {
        var mock = new MockInlineProvider(filterResult: null);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui filter a b c");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Filter_passes_prompt()
    {
        var mock = new MockInlineProvider(filterResult: ["x"]);
        var engine = CreateEngine(mock);
        await engine.ExecuteToListAsync("tui filter x y prompt \"Search:\"");

        Assert.Equal("Search:", mock.LastFilterPrompt);
    }

    [Fact]
    public async Task Filter_no_items_throws()
    {
        var mock = new MockInlineProvider(filterResult: ["x"]);
        var engine = CreateEngine(mock);
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui filter"));
    }

    [Fact]
    public async Task Filter_from_pipeline()
    {
        var mock = new MockInlineProvider(filterResult: ["y"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("echo x y z | tui filter");

        Assert.Single(results);
        Assert.Equal("y", results[0]);
    }

    // ── Without --cli, commands still yield requests ──────────

    [Fact]
    public async Task Pick_without_cli_yields_request()
    {
        var mock = new MockInlineProvider(pickResult: ["a"]);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui pick a b c");

        Assert.IsType<TuiPickRequest>(Assert.Single(results));
    }

    [Fact]
    public async Task Confirm_without_cli_yields_request()
    {
        var mock = new MockInlineProvider(confirmResult: true);
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui confirm \"ok?\"");

        Assert.IsType<TuiConfirmRequest>(Assert.Single(results));
    }

    [Fact]
    public async Task Input_without_cli_yields_request()
    {
        var mock = new MockInlineProvider(inputResult: "text");
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("tui input \"Name:\"");

        Assert.IsType<TuiInputRequest>(Assert.Single(results));
    }

    [Fact]
    public async Task Inspect_with_inline_provider_uses_interactive_path()
    {
        var mock = new MockInlineProvider();
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | inspect");

        Assert.Empty(results);
        Assert.IsType<System.Text.StringBuilder>(mock.LastInspectValue);
        Assert.False(mock.LastInspectIncludeAllMembers);
    }

    [Fact]
    public async Task Inspect_with_flat_flag_returns_legacy_object_inspection()
    {
        var mock = new MockInlineProvider();
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | inspect --flat");

        var inspection = Assert.IsType<ObjectInspection>(Assert.Single(results));
        Assert.Equal("System.Text.StringBuilder", inspection.TypeName);
        Assert.Null(mock.LastInspectValue);
    }

    [Fact]
    public async Task Inspect_passes_all_members_flag_to_inline_provider()
    {
        var mock = new MockInlineProvider();
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("new System.Text.StringBuilder hello | inspect -a");

        Assert.Empty(results);
        Assert.True(mock.LastInspectIncludeAllMembers);
    }

    [Fact]
    public async Task Help_cli_with_inline_provider_uses_interactive_path()
    {
        var mock = new MockInlineProvider();
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("help --cli regex");

        Assert.Empty(results);
        Assert.Equal("regex", mock.LastHelpInitialQuery);
        Assert.Equal("Regex", mock.LastHelpInitialTopicName);
    }

    [Fact]
    public async Task Help_browse_cli_with_inline_provider_uses_interactive_path()
    {
        var mock = new MockInlineProvider();
        var engine = CreateEngine(mock);
        var results = await engine.ExecuteToListAsync("help browse --cli func");

        Assert.Empty(results);
        Assert.Equal("func", mock.LastHelpInitialQuery);
        Assert.Equal("func", mock.LastHelpInitialTopicName);
    }

    // ── Mock ──────────────────────────────────────────────────

    private sealed class MockInlineProvider : IInlinePromptProvider
    {
        private readonly IReadOnlyList<object?>? _pickResult;
        private readonly bool? _confirmResult;
        private readonly string? _inputResult;
        private readonly IReadOnlyList<object?>? _filterResult;

        public string? LastPickPrompt { get; private set; }
        public string? LastPickDisplayProperty { get; private set; }
        public int LastPickPageSize { get; private set; }
        public bool LastInputPassword { get; private set; }
        public string? LastFilterPrompt { get; private set; }
        public object? LastInspectValue { get; private set; }
        public bool LastInspectIncludeAllMembers { get; private set; }
        public string? LastInspectSourceExpression { get; private set; }
        public string? LastHelpInitialQuery { get; private set; }
        public string? LastHelpInitialTopicName { get; private set; }

        public MockInlineProvider(
            IReadOnlyList<object?>? pickResult = null,
            bool? confirmResult = null,
            string? inputResult = null,
            IReadOnlyList<object?>? filterResult = null)
        {
            _pickResult = pickResult;
            _confirmResult = confirmResult;
            _inputResult = inputResult;
            _filterResult = filterResult;
        }

        public void Inspect(object? value, bool includeAllMembers = false, string? sourceExpression = null)
        {
            LastInspectValue = value;
            LastInspectIncludeAllMembers = includeAllMembers;
            LastInspectSourceExpression = sourceExpression;
        }

        public void BrowseHelp(string? initialQuery = null, string? initialTopicName = null)
        {
            LastHelpInitialQuery = initialQuery;
            LastHelpInitialTopicName = initialTopicName;
        }

        public IReadOnlyList<object?>? Pick(IReadOnlyList<object?> items, string? prompt, string? displayProperty, bool multiSelect, int pageSize)
        {
            LastPickPrompt = prompt;
            LastPickDisplayProperty = displayProperty;
            LastPickPageSize = pageSize;
            return _pickResult;
        }

        public bool? Confirm(string message, bool defaultValue) => _confirmResult;

        public string? Input(string? prompt, string? defaultValue, bool password)
        {
            LastInputPassword = password;
            return _inputResult;
        }

        public IReadOnlyList<object?>? Filter(IReadOnlyList<object?> items, string? prompt, string? displayProperty, bool multiSelect, int pageSize)
        {
            LastFilterPrompt = prompt;
            return _filterResult;
        }
    }
}
