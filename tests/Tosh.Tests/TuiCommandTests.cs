using Tosh.Runtime;
using Tosh.Language;
using Tosh.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Tests;

public sealed class TuiCommandTests
{
    // tui commands are [ShellOnly]; mark the test engine as interactive so they
    // bypass the bind-time shell-only check.
    private static ToshEngine CreateEngine() => new(ToshRuntime.CreateDefault()) { IsInteractiveSession = true };

    [Fact]
    public async Task Tui_pick_yields_TuiPickRequest()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui pick a b c");

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.Equal(3, request.Items.Count);
        Assert.False(request.MultiSelect);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_pick_with_multi_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui pick a b c --multi");

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.True(request.MultiSelect);
    }

    [Fact]
    public async Task Tui_pick_with_prompt_and_display()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui pick a b prompt \"Choose:\" display Name");

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.Equal("Choose:", request.Prompt);
        Assert.Equal("Name", request.DisplayProperty);
    }

    [Fact]
    public async Task Tui_pick_with_result_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui pick a b --result");

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.True(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_pick_from_pipeline()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("echo x y z | tui pick");

        var request = Assert.IsType<TuiPickRequest>(Assert.Single(results));
        Assert.Equal(3, request.Items.Count);
    }

    [Fact]
    public async Task Tui_confirm_yields_TuiConfirmRequest()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui confirm \"Delete files?\"");

        var request = Assert.IsType<TuiConfirmRequest>(Assert.Single(results));
        Assert.Equal("Delete files?", request.Message);
        Assert.True(request.DefaultConfirm);
    }

    [Fact]
    public async Task Tui_confirm_with_result_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui confirm \"Proceed?\" --result");

        var request = Assert.IsType<TuiConfirmRequest>(Assert.Single(results));
        Assert.True(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_input_yields_TuiInputRequest()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui input \"Enter name:\"");

        var request = Assert.IsType<TuiInputRequest>(Assert.Single(results));
        Assert.Equal("Enter name:", request.Prompt);
        Assert.False(request.Multiline);
    }

    [Fact]
    public async Task Tui_input_with_multiline_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui input \"Enter text:\" --multiline");

        var request = Assert.IsType<TuiInputRequest>(Assert.Single(results));
        Assert.True(request.Multiline);
    }

    [Fact]
    public async Task Tui_file_yields_TuiFilePickRequest()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui file");

        var request = Assert.IsType<TuiFilePickRequest>(Assert.Single(results));
        Assert.False(request.DirectoryOnly);
    }

    [Fact]
    public async Task Tui_file_with_directory_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui file --directory");

        var request = Assert.IsType<TuiFilePickRequest>(Assert.Single(results));
        Assert.True(request.DirectoryOnly);
    }

    [Fact]
    public async Task Tui_file_with_filter()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui file filter \"*.json\"");

        var request = Assert.IsType<TuiFilePickRequest>(Assert.Single(results));
        Assert.Equal("*.json", request.Filter);
    }

    [Fact]
    public async Task Tui_screen_yields_TuiScreen()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui screen title \"My App\"");

        var screen = Assert.IsType<TuiScreen>(Assert.Single(results));
        Assert.Equal("My App", screen.ScreenTitle);
    }

    [Fact]
    public async Task Tui_run_yields_TuiRunRequest()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui screen title \"Test\" | tui run");

        var request = Assert.IsType<TuiRunRequest>(Assert.Single(results));
        Assert.Equal("Test", request.Screen.ScreenTitle);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_run_with_result_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui screen title \"Test\" | tui run --result");

        var request = Assert.IsType<TuiRunRequest>(Assert.Single(results));
        Assert.True(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_layout_sets_orientation()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui screen | tui layout split-horizontal ratio \"30:70\"");

        var screen = Assert.IsType<TuiScreen>(Assert.Single(results));
        Assert.Equal(TuiLayout.SplitHorizontal, screen.LayoutConfig.Layout);
        Assert.Equal("30:70", screen.LayoutConfig.Ratio);
    }

    [Fact]
    public async Task Tui_without_subcommand_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui"));
    }

    [Fact]
    public async Task Tui_unknown_subcommand_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui bogus"));
    }

    [Fact]
    public async Task Tui_pick_no_items_throws()
    {
        var engine = CreateEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(() => engine.ExecuteToListAsync("tui pick"));
    }
}
