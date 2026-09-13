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

    [Fact]
    public async Task A_run_rejects_a_flag_it_does_not_take()
    {
        var engine = CreateEngine();

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("tui run {| Text = \"x\" |} --cli"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.tui.unknown_flag");
    }

    [Fact]
    public async Task A_run_with_no_tree_says_what_one_looks_like()
    {
        var engine = CreateEngine();

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("tui run"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.tui.run.no_screen");
    }

    [Fact]
    public async Task Tui_run_yields_a_tree_request()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui run {| Text = \"hello\" |} --title \"Test\"");

        var request = Assert.IsType<TuiTreeRunRequest>(Assert.Single(results));

        Assert.Equal("Test", request.Title);
        Assert.False(request.ReturnOutcome);
    }

    [Fact]
    public async Task Tui_run_with_result_flag()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui run {| Text = \"hello\" |} --result");

        Assert.True(Assert.IsType<TuiTreeRunRequest>(Assert.Single(results)).ReturnOutcome);
    }

    [Fact]
    public async Task A_tree_reaches_run_through_the_pipeline_as_well()
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("{| Text = \"hello\" |} | tui run");

        Assert.IsType<TuiTreeRunRequest>(Assert.Single(results));
    }

    [Fact]
    public async Task A_binding_that_produces_several_values_answers_with_all_of_them()
    {
        // A function returning a collection yields its elements one at a time, and keeping
        // only the last kept only the last row — so every binding that carries more than
        // one thing showed its final element and nothing else: Lines, List, Table, Spark
        // and Bars all did.
        var produced = await Invoke(new Yielding("alpha", "beta", "gamma"));

        Assert.Equal(["alpha", "beta", "gamma"], Assert.IsAssignableFrom<IEnumerable<object?>>(produced));
    }

    [Fact]
    public async Task A_binding_that_produces_one_value_answers_with_the_value()
    {
        // Not a one-element collection: a caption is a caption.
        Assert.Equal("ready", await Invoke(new Yielding("ready")));
    }

    [Fact]
    public async Task A_binding_that_produces_nothing_answers_with_nothing()
    {
        Assert.Null(await Invoke(new Yielding()));
    }

    /// <summary>Runs one callable through the invoker `tui run` hands its bindings.</summary>
    private static async Task<object?> Invoke(IShellCallable source)
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync("tui run {| Text = \"x\" |}");
        var request = Assert.IsType<TuiTreeRunRequest>(Assert.Single(results));

        return request.Invoke!(source, null);
    }

    /// <summary>A callable that produces a known number of values, as a pipeline does.</summary>
    private sealed class Yielding(params object?[] values) : IShellCallable
    {
        public string CallableName => "yielding";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            foreach (var value in values)
            {
                await Task.Yield();
                yield return value;
            }
        }
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
