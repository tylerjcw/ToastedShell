using Tosh.Cli;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Tui;
using Tosh.Tui.Requests;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

public sealed class TuiCommandTests
{
    // tui commands are [ShellOnly]; mark the test engine as interactive so they
    // bypass the bind-time shell-only check.
    private static ToshEngine CreateEngine() => new(ToshRuntime.CreateDefault().Language) { IsInteractiveSession = true };

    [Fact]
    public void Tui_request_probe_ignores_ordinary_output()
    {
        Assert.False(TuiRequestProbe.IsTuiRequestBatch(["not a TUI request"]));
    }

    [Fact]
    public void Tui_request_probe_recognizes_tui_requests_without_dispatching()
    {
        Assert.True(TuiRequestProbe.IsTuiRequestBatch([new TuiPickRequest(["alpha", "beta"])]));
    }

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

    // ── widgets that the renderer supported but no command could add ──────────
    //
    // TuiCustomScreen has dispatched ConfirmationWidgetHost and FilePickerWidgetHost
    // since it was written; two of the six widget kinds were simply unreachable.

    [Fact]
    public async Task Add_confirm_puts_a_confirmation_on_the_screen()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            "tui screen title \"Deploy\" | tui add-confirm \"Deploy now?\" --id go --default no");

        var screen = Assert.IsType<TuiScreen>(Assert.Single(results));
        var widget = Assert.IsType<TuiConfirmationConfig>(Assert.Single(screen.Widgets));

        Assert.Equal("go", widget.Id);
        Assert.Equal("Deploy now?", widget.Message);
        Assert.False(widget.DefaultConfirm);
    }

    [Fact]
    public async Task Add_confirm_requires_a_message()
    {
        var engine = CreateEngine();

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("tui screen | tui add-confirm"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.tui.add_confirm.missing_message");
    }

    [Fact]
    public async Task Add_file_puts_a_file_picker_on_the_screen()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync(
            "tui screen | tui add-file --id pick --path /tmp --filter \"*.tosh\" --directory");

        var screen = Assert.IsType<TuiScreen>(Assert.Single(results));
        var widget = Assert.IsType<TuiFilePickerConfig>(Assert.Single(screen.Widgets));

        Assert.Equal("pick", widget.Id);
        Assert.Equal("/tmp", widget.InitialPath);
        Assert.Equal("*.tosh", widget.Filter);
        Assert.True(widget.DirectoryOnly);
    }

    [Fact]
    public async Task A_screen_builder_rejects_a_flag_it_does_not_take()
    {
        var engine = CreateEngine();

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("tui screen title \"x\" | tui run --cli"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.tui.unknown_flag");
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
