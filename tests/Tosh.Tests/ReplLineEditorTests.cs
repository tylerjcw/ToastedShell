using Tosh.Cli;

namespace Tosh.Tests;

public sealed class ReplLineEditorTests
{
    [Fact]
    public void Line_editor_buffer_supports_insertion_cursor_movement_and_deletion()
    {
        var buffer = new LineEditorBuffer("helo");

        Assert.True(buffer.MoveLeft());
        Assert.True(buffer.Insert('l'));
        Assert.Equal("hello", buffer.Text);
        Assert.Equal(4, buffer.CursorIndex);

        Assert.True(buffer.MoveHome());
        Assert.True(buffer.MoveRight());
        Assert.True(buffer.Delete());
        Assert.Equal("hllo", buffer.Text);

        Assert.True(buffer.MoveEnd());
        Assert.True(buffer.Backspace());
        Assert.Equal("hll", buffer.Text);
        Assert.Equal(3, buffer.CursorIndex);
    }

    [Fact]
    public void Repl_command_line_insertion_sink_prefills_the_next_prompt_buffer()
    {
        var sink = new ReplCommandLineInsertionSink();

        Assert.True(sink.TryInsertText("$person."));
        Assert.True(sink.TryInsertText("Name"));
        Assert.True(sink.TryConsume(out var pending));

        Assert.Equal("$person.Name", pending.Text);
        Assert.Equal("$person.Name".Length, pending.CursorIndex);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public void Repl_command_line_insertion_sink_inserts_into_active_buffer_at_cursor()
    {
        var sink = new ReplCommandLineInsertionSink();
        var buffer = new LineEditorBuffer("filter");
        buffer.SetCursor(3);

        sink.ActivateBuffer(buffer);
        Assert.True(sink.TryInsertText("X"));
        sink.DeactivateBuffer(buffer);

        Assert.Equal("filXter", buffer.Text);
        Assert.Equal(4, buffer.CursorIndex);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public void Repl_command_line_insertion_sink_can_replace_active_buffer_range()
    {
        var sink = new ReplCommandLineInsertionSink();
        var buffer = new LineEditorBuffer("5");
        buffer.SetCursor(1);

        sink.ActivateBuffer(buffer);
        sink.SetPendingReplacement(0, 1);
        Assert.True(sink.TryInsertText("(5).GetType()"));
        sink.DeactivateBuffer(buffer);

        Assert.Equal("(5).GetType()", buffer.Text);
        Assert.Equal("(5).GetType()".Length, buffer.CursorIndex);
        Assert.False(sink.TryConsume(out _));
    }

    [Fact]
    public void Line_editor_buffer_handles_home_end_and_clear()
    {
        var buffer = new LineEditorBuffer("toasted");

        Assert.True(buffer.MoveHome());
        Assert.Equal(0, buffer.CursorIndex);

        Assert.True(buffer.MoveEnd());
        Assert.Equal(7, buffer.CursorIndex);

        Assert.True(buffer.Clear());
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Equal(0, buffer.CursorIndex);
    }

    [Fact]
    public void History_navigation_moves_backward_and_restores_pending_input()
    {
        var history = new LineEditorHistory(new[] { "help", "ls -la", "history" });

        Assert.True(history.TryPrevious("par", out var previous1));
        Assert.Equal("history", previous1);

        Assert.True(history.TryPrevious(previous1, out var previous2));
        Assert.Equal("ls -la", previous2);

        Assert.True(history.TryNext(out var next1));
        Assert.Equal("history", next1);

        Assert.True(history.TryNext(out var next2));
        Assert.Equal("par", next2);

        Assert.False(history.TryNext(out var next3));
        Assert.Equal("par", next3);
    }

    [Fact]
    public void History_navigation_stops_at_oldest_entry()
    {
        var history = new LineEditorHistory(new[] { "one", "two" });

        Assert.True(history.TryPrevious(string.Empty, out var first));
        Assert.Equal("two", first);

        Assert.True(history.TryPrevious(first, out var second));
        Assert.Equal("one", second);

        Assert.True(history.TryPrevious(second, out var stillOldest));
        Assert.Equal("one", stillOldest);
    }

    [Fact]
    public void Repl_history_expander_supports_unambiguous_designators()
    {
        var history = new[]
        {
            new Tosh.Core.CommandHistoryEntry(12, "echo alpha", DateTimeOffset.Parse("2026-03-27T00:00:00Z")),
            new Tosh.Core.CommandHistoryEntry(13, "echo beta gamma", DateTimeOffset.Parse("2026-03-27T00:01:00Z")),
        };

        Assert.Equal("echo beta gamma", ReplHistoryExpander.Expand("!!", history).Text);
        Assert.Equal("echo alpha", ReplHistoryExpander.Expand("!12", history).Text);
        Assert.Equal("echo alpha", ReplHistoryExpander.Expand("!-2", history).Text);
        Assert.Equal("echo beta gamma", ReplHistoryExpander.Expand("!echo", history).Text);
        Assert.Equal("echo echo beta gamma", ReplHistoryExpander.Expand("echo !!", history).Text);
        Assert.Equal("echo gamma", ReplHistoryExpander.Expand("echo !$", history).Text);
        Assert.Equal("echo beta", ReplHistoryExpander.Expand("echo !^", history).Text);
        Assert.Equal("echo beta gamma", ReplHistoryExpander.Expand("echo !*", history).Text);
        Assert.Equal("echo gamma", ReplHistoryExpander.Expand("echo !!:$", history).Text);
        Assert.Equal("echo beta gamma # !!", ReplHistoryExpander.Expand("echo beta gamma # !!", history).Text);
        Assert.Equal("value!!", ReplHistoryExpander.Expand("value!!", history).Text);
    }

    [Fact]
    public void Repl_history_expander_supports_quick_substitution()
    {
        var history = new[]
        {
            new Tosh.Core.CommandHistoryEntry(12, "echo beta", DateTimeOffset.Parse("2026-03-27T00:01:00Z")),
        };

        Assert.Equal("echo gamma", ReplHistoryExpander.Expand("^beta^gamma^", history).Text);
    }

    [Fact]
    public void Reverse_history_search_can_match_cycle_and_cancel()
    {
        var buffer = new LineEditorBuffer("draft");
        var state = new LineEditorHistorySearchState(["echo alpha", "git status", "echo beta"], buffer.Text, buffer.CursorIndex);

        state.Activate(buffer);
        Assert.Equal("echo beta", buffer.Text);
        Assert.Equal(string.Empty, state.Query);

        Assert.True(state.TryCyclePrevious(buffer));
        Assert.Equal("git status", buffer.Text);

        Assert.True(state.Append(buffer, 'e'));
        Assert.Equal("echo beta", buffer.Text);
        Assert.Equal("e", state.Query);

        state.Cancel(buffer);
        Assert.Equal("draft", buffer.Text);
    }

    [Fact]
    public void Reverse_history_search_restores_original_text_when_query_fails()
    {
        var buffer = new LineEditorBuffer("draft");
        var state = new LineEditorHistorySearchState(["echo alpha", "git status"], buffer.Text, buffer.CursorIndex);

        state.Activate(buffer);
        Assert.True(state.Append(buffer, 'z'));

        Assert.True(state.Failed);
        Assert.Equal("draft", buffer.Text);
    }

    [Fact]
    public void Line_editor_buffer_can_replace_ranges_and_restore_cursor()
    {
        var buffer = new LineEditorBuffer("hello world");

        Assert.True(buffer.ReplaceRange(6, 5, "ToSh"));
        Assert.Equal("hello ToSh", buffer.Text);
        Assert.Equal(10, buffer.CursorIndex);

        buffer.SetCursor(5);
        Assert.Equal(5, buffer.CursorIndex);
    }

    [Fact]
    public void Line_editor_buffer_supports_word_wise_horizontal_movement()
    {
        var buffer = new LineEditorBuffer("hello brave new world");

        Assert.True(buffer.MoveWordLeft());
        Assert.Equal("hello brave new ", buffer.Text[..buffer.CursorIndex]);

        Assert.True(buffer.MoveWordLeft());
        Assert.Equal("hello brave ", buffer.Text[..buffer.CursorIndex]);

        Assert.True(buffer.MoveWordRight());
        Assert.Equal("hello brave new", buffer.Text[..buffer.CursorIndex]);
    }

    [Fact]
    public void Wrapped_vertical_movement_uses_visual_rows_instead_of_history_rows()
    {
        var buffer = new LineEditorBuffer("abcdefghijkl");
        buffer.SetCursor(10);

        Assert.True(ReplLineEditor.TryMoveWrappedVertical(buffer, "prompt> ", "....> ", consoleWidth: 10, direction: -1, preferredColumn: null, out var columnAfterUp));
        Assert.Equal(0, buffer.CursorIndex);
        Assert.Equal(8, columnAfterUp);

        Assert.True(ReplLineEditor.TryMoveWrappedVertical(buffer, "prompt> ", "....> ", consoleWidth: 10, direction: 1, preferredColumn: columnAfterUp, out var columnAfterDown));
        Assert.Equal(10, buffer.CursorIndex);
        Assert.Equal(8, columnAfterDown);
    }

    [Fact]
    public void Wrapped_vertical_movement_respects_buffer_bounds()
    {
        var buffer = new LineEditorBuffer("abcdefghijk");
        buffer.SetCursor(1);

        Assert.False(ReplLineEditor.TryMoveWrappedVertical(buffer, "prompt> ", "....> ", consoleWidth: 10, direction: -1, preferredColumn: null, out _));

        buffer.SetCursor(buffer.Text.Length);
        Assert.False(ReplLineEditor.TryMoveWrappedVertical(buffer, "prompt> ", "....> ", consoleWidth: 10, direction: 1, preferredColumn: null, out _));
    }

    [Fact]
    public void Multiline_layout_tracks_cursor_positions_across_real_newlines()
    {
        var layout = ReplLineEditor.BuildInputLayout("prompt> ", "....> ", "alpha\nbeta", "alpha\nbeta", consoleWidth: 12);

        Assert.Equal(new ReplLineEditor.VisualPosition(1, 8), layout.CursorPositions[0]);
        Assert.Equal(new ReplLineEditor.VisualPosition(3, 6), layout.CursorPositions[6]);
        Assert.Equal(new ReplLineEditor.VisualPosition(3, 10), layout.CursorPositions[10]);
    }

    [Fact]
    public void Completion_application_can_append_dot_for_member_chaining()
    {
        var buffer = new LineEditorBuffer("$person.Na");

        ReplLineEditor.ApplyCompletionSuggestion(buffer, "$person.Na", replacementStart: 8, replacementLength: 2, suggestion: "Name", suffix: ".");

        Assert.Equal("$person.Name.", buffer.Text);
        Assert.Equal("$person.Name.".Length, buffer.CursorIndex);
    }

    [Fact]
    public void Completion_application_can_append_call_paren_for_method_chaining()
    {
        var buffer = new LineEditorBuffer("$person.Des");

        ReplLineEditor.ApplyCompletionSuggestion(buffer, "$person.Des", replacementStart: 8, replacementLength: 3, suggestion: "Describe", suffix: "(");

        Assert.Equal("$person.Describe(", buffer.Text);
        Assert.Equal("$person.Describe(".Length, buffer.CursorIndex);
    }

    [Theory]
    [InlineData("OP", ConsoleKey.F1)]
    [InlineData("OQ", ConsoleKey.F2)]
    [InlineData("[11~", ConsoleKey.F1)]
    [InlineData("[12~", ConsoleKey.F2)]
    [InlineData("[[A", ConsoleKey.F1)]
    [InlineData("[[B", ConsoleKey.F2)]
    public void Escape_sequence_translation_recognizes_common_function_key_sequences(string sequence, ConsoleKey expectedKey)
    {
        Assert.True(ReplLineEditor.TryTranslateEscapeSequence(sequence, out var translated));
        Assert.Equal(expectedKey, translated.Key);
    }

    [Theory]
    [InlineData("h", ConsoleKey.H, ConsoleModifiers.Alt)]
    [InlineData("i", ConsoleKey.I, ConsoleModifiers.Alt)]
    public void Escape_sequence_translation_recognizes_alt_shortcut_fallbacks(string sequence, ConsoleKey expectedKey, ConsoleModifiers expectedModifiers)
    {
        Assert.True(ReplLineEditor.TryTranslateEscapeSequence(sequence, out var translated));
        Assert.Equal(expectedKey, translated.Key);
        Assert.Equal(expectedModifiers, translated.Modifiers);
    }

    [Fact]
    public void Shift_enter_uses_continuation_indent_when_expression_is_incomplete()
    {
        var insertedText = ReplLineEditor.BuildInsertedNewLineText("if ($ok) {", "if ($ok) {".Length, new ReplContinuationState(true, "    "));

        Assert.Equal("\n    ", insertedText);
    }

    [Fact]
    public void Shift_enter_preserves_current_line_indent_when_buffer_is_already_multiline()
    {
        var source = "if ($ok) {\n    echo \"hi\"";
        var insertedText = ReplLineEditor.BuildInsertedNewLineText(source, source.Length, new ReplContinuationState(false, string.Empty));

        Assert.Equal("\n    ", insertedText);
    }

    [Fact]
    public void Prompt_renderer_uses_header_right_layout_when_width_allows_it()
    {
        var runtime = Tosh.Core.ToshRuntime.CreateDefault();
        runtime.Config.Prompt.TimeEnabled = true;
        runtime.Config.Prompt.GitEnabled = false;
        runtime.Config.Prompt.HeaderLeftLayout = "Time, Directory";
        runtime.Config.Prompt.HeaderRightLayout = "UserHost, Jobs, Duration";
        runtime.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator";

        var lines = ToshPromptRenderer.BuildPreviewLines(runtime, 7, width: 120)
            .Select(Tosh.Core.StyledText.StripAnsi)
            .ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Contains("@", lines[0], StringComparison.Ordinal);
        Assert.Contains("jobs:2", lines[0], StringComparison.Ordinal);
        Assert.Contains("1.4s", lines[0], StringComparison.Ordinal);
        Assert.Contains("!432", lines[1], StringComparison.Ordinal);
        Assert.Contains("✘ 7", lines[1], StringComparison.Ordinal);
        Assert.Contains("tosh", lines[1], StringComparison.Ordinal);
        Assert.True(Tosh.Core.StyledText.GetVisibleLength(lines[0]) < 120);
    }
}
