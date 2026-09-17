using Tosh.Cli;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Tui;
using Tosh.Tui.Declarative;
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

    /// <summary>
    /// The two shapes a real function delivers a collection in, and the one answer a
    /// binding gets from either (<c>TUI-0027</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A collection written as an expression is a sequence, and the engine delivers one two
    /// ways: a bare variable reference is expanded at the producer, so the callable yields
    /// its <em>elements</em>; a literal is yielded once, marked spreadable, and expanded by
    /// whatever consumes it. Downstream honours the marking, so at script level the two are
    /// indistinguishable — <c>F | collect</c> counts three either way.
    /// </para>
    /// <para>
    /// A host reading the raw stream does not see the marking, and the difference cost real
    /// time: it was read as the test engine and the shell disagreeing, which they do not.
    /// A binding takes everything a callable produces, so both shapes land on the same list.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("func Items() => [\"alpha\", \"beta\", \"gamma\"]")]
    [InlineData("var rows = [\"alpha\", \"beta\", \"gamma\"]\nfunc Items() => $rows")]
    public async Task A_collection_reaches_a_binding_whole_however_the_function_spells_it(string declaration)
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync($"{declaration}\ntui run {{| Lines = &Items |}}");
        var request = Assert.IsType<TuiTreeRunRequest>(Assert.Single(results));

        var widget = Assert.IsType<TuiLines>(TuiTreeBuilder.Build(
            request.Node, registry: null, invoke: request.Invoke, out _));

        TuiBindings.Apply(widget, request.Invoke!, values: null);

        Assert.Equal(["alpha", "beta", "gamma"], widget.Lines.Select(line => line.Text));
    }

    /// <summary>Runs one callable through the invoker `tui run` hands its bindings.</summary>
    private static Task<object?> Invoke(IShellCallable source) => Invoke(source, options: "");

    /// <summary>The same, for a `tui run` written with options on it.</summary>
    private static async Task<object?> Invoke(IShellCallable source, string options)
    {
        var engine = CreateEngine();
        var results = await engine.ExecuteToListAsync($"tui run {{| Text = \"x\" |}} {options}");
        var request = Assert.IsType<TuiTreeRunRequest>(Assert.Single(results));

        return request.Invoke!(source, null);
    }

    /// <summary>
    /// A handler that never returns is stopped rather than keeping the render loop
    /// (<c>TUI-0008</c>).
    /// </summary>
    /// <remarks>
    /// Bindings, key handlers and feed handlers all reach a script through this one
    /// invoker, and all three run on the thread that answers keys — so one that loops
    /// forever is a terminal that has to be killed from somewhere else.
    /// </remarks>
    [Fact]
    public async Task A_handler_that_never_returns_is_stopped_and_says_so()
    {
        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => Invoke(new Blocking(), "--budget 100ms"));

        // Not the cancellation it was implemented as: a screen reports what it catches by
        // type and message, and "A task was canceled" names neither the handler nor why.
        Assert.Contains("100ms", failure.Message);
        Assert.Contains("--budget off", failure.Message);
    }

    /// <summary>The budget is what reaches the handler, and `off` really is off.</summary>
    /// <remarks>
    /// Asserted on the token rather than on a stopwatch, because a handler that outlives a
    /// generous budget and one that was never given a budget look identical from outside —
    /// the first draft of this test asserted that a 250ms call survived <c>--budget off</c>
    /// and would have passed just as well if <c>off</c> did nothing at all.
    /// </remarks>
    [Theory]
    [InlineData("--budget 100ms", "cancelled")]
    [InlineData("--budget off", "not cancelled")]
    [InlineData("--budget none", "not cancelled")]
    [InlineData("--budget 0", "not cancelled")]
    public async Task The_budget_is_the_deadline_the_handler_is_given(string options, string expected)
        => Assert.Equal(expected, await Invoke(new Watching(), options));

    /// <summary>An unreadable budget keeps the guard rather than removing it.</summary>
    /// <remarks>
    /// The two mistakes are not equal. A typo that silently keeps a five-second guard is
    /// found when a handler hangs; one that silently removes it is found when a terminal
    /// does.
    /// <para>
    /// What this can show is that a typo does not become <c>off</c> by another name: the
    /// handler below is well inside the default and is not stopped. Telling the default
    /// apart from <c>off</c> would mean a handler that outlives five seconds, which is not
    /// worth five seconds of every test run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_budget_nobody_can_read_falls_back_to_the_default()
        => Assert.Equal("not cancelled", await Invoke(new Watching(), "--budget soonish"));

    /// <summary>A callable that reports whether its own deadline came due.</summary>
    private sealed class Watching : IShellCallable
    {
        public string CallableName => "watching";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            yield return await Observe(context.CancellationToken);
        }

        // Caught here rather than let out, so the answer is a value the invoker passes
        // through: a cancellation thrown out of here is translated into a timeout, which
        // is the other test.
        private static async Task<string> Observe(CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token);
                return "not cancelled";
            }
            catch (OperationCanceledException)
            {
                return "cancelled";
            }
        }
    }

    /// <summary>A callable that waits for a cancellation it is meant to be stopped by.</summary>
    /// <remarks>
    /// Bounded rather than infinite. <c>Timeout.Infinite</c> is what this handler is
    /// standing in for, but it makes the test hang forever if the deadline is ever lost —
    /// and a suite that stops is harder to read than one that fails. Ten seconds is never
    /// waited on a passing run: the budget is a tenth of a second.
    /// </remarks>
    private sealed class Blocking : IShellCallable
    {
        public string CallableName => "blocking";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
            yield return "never reached";
        }
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
