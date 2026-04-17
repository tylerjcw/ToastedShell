using Tosh.Cli.Tui;
using Tosh.Tui.Requests;

namespace Tosh.Tests;

public sealed class TuiCoreTests
{
    [Fact]
    public void Tui_split_layout_can_split_columns_with_a_gap()
    {
        var bounds = new TuiRect(0, 0, 80, 24);
        var (left, right) = TuiSplitLayout.SplitColumns(bounds, firstWidth: 24, gap: 1);

        Assert.Equal(new TuiRect(0, 0, 24, 24), left);
        Assert.Equal(new TuiRect(25, 0, 55, 24), right);
    }

    [Fact]
    public void Tui_split_layout_can_split_rows_with_a_gap()
    {
        var bounds = new TuiRect(0, 0, 80, 24);
        var (top, bottom) = TuiSplitLayout.SplitRows(bounds, firstHeight: 3, gap: 1);

        Assert.Equal(new TuiRect(0, 0, 80, 3), top);
        Assert.Equal(new TuiRect(0, 4, 80, 20), bottom);
    }

    [Fact]
    public void Tui_scroll_state_clamps_and_keeps_items_visible()
    {
        var scroll = new TuiScrollState();
        scroll.SetDimensions(itemCount: 100, pageSize: 10);

        scroll.EnsureVisible(0);
        Assert.Equal((0, 10), scroll.GetVisibleRange());

        scroll.EnsureVisible(15);
        Assert.Equal(6, scroll.Offset);

        scroll.End();
        Assert.Equal(90, scroll.Offset);

        scroll.PageUp();
        Assert.Equal(80, scroll.Offset);
    }

    [Fact]
    public void Tui_list_state_tracks_selection_and_scroll_together()
    {
        var state = new TuiListState<string>();
        state.SetItems(Enumerable.Range(1, 20).Select(index => $"item-{index}").ToArray(), pageSize: 5);

        Assert.True(state.MoveNext());
        Assert.Equal(1, state.SelectedIndex);
        Assert.Equal(0, state.Scroll.Offset);

        Assert.True(state.PageDown());
        Assert.Equal(6, state.SelectedIndex);
        Assert.Equal(5, state.Scroll.Offset);

        Assert.True(state.End());
        Assert.Equal(19, state.SelectedIndex);
        Assert.Equal(15, state.Scroll.Offset);
        Assert.True(state.TryGetSelected(out var selected));
        Assert.Equal("item-20", selected);
    }

    [Fact]
    public void Tui_application_processes_pending_key_bursts_before_the_next_redraw()
    {
        var host = new FakeTuiHost(
            [
                new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false),
                new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false),
                new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false),
            ]);
        var screen = new CountingScreen();

        var result = TuiApplication.ProcessInputBatch(
            host,
            screen,
            TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false)));

        Assert.Equal(TuiScreenResult.Continue, result);
        Assert.Equal(4, screen.HandledKeys.Count);
        Assert.All(screen.HandledKeys, key => Assert.Equal(ConsoleKey.DownArrow, key.Key));
    }

    [Fact]
    public void Tui_confirmation_dialog_state_tracks_selection_and_confirms()
    {
        var dialog = new TuiConfirmationDialogState();
        dialog.Open("Discard Changes?", "Discard staged changes and quit?", "Discard", "Stay");

        var firstLines = dialog.BuildEntries(80);
        Assert.Contains(firstLines, line => line.Contains("> [Discard]", StringComparison.Ordinal));

        var toggle = dialog.HandleKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: false, alt: false, control: false));
        Assert.Equal(TuiConfirmationDialogResultKind.None, toggle.Kind);

        var secondLines = dialog.BuildEntries(80);
        Assert.Contains(secondLines, line => line.Contains("> [Stay]", StringComparison.Ordinal));

        var confirm = dialog.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.Equal(TuiConfirmationDialogResultKind.Cancelled, confirm.Kind);
    }

    [Fact]
    public void Tui_file_picker_can_navigate_and_select_a_file()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "nested"));
            var filePath = Path.Combine(tempDirectory.FullName, "picked.txt");
            File.WriteAllText(filePath, "hello");

            var picker = new TuiFilePickerState();
            picker.Open(tempDirectory.FullName, TuiFilePickerSelectionMode.File, null, pageSize: 8);

            var lines = picker.BuildEntries(120, 16);
            Assert.Contains(lines, line => line.Contains("picked.txt", StringComparison.Ordinal));

            while (!picker.BuildEntries(120, 16).Any(line => line.Contains("> [-] picked.txt", StringComparison.Ordinal)))
            {
                picker.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false), pageSize: 8);
            }

            var result = picker.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false), pageSize: 8);
            Assert.Equal(TuiFilePickerResultKind.Selected, result.Kind);
            Assert.Equal(filePath, result.Path);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Tui_form_layout_aligns_and_wraps_label_value_rows()
    {
        var entries = TuiFormLayout.BuildEntries(
            [
                new TuiFormRow("Type", "System.String", IsSelected: true),
                new TuiFormRow("Columns", "Length, Chars, HashCode, SomethingLong", TuiFormRowKind.Meta),
            ],
            width: 36,
            labelWidth: 10);

        Assert.Contains(entries, entry => entry.Text.Contains("> Type", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Text.Contains("System.String", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Text.Contains("Columns", StringComparison.Ordinal));
        Assert.True(entries.Count(entry => entry.Text.Contains("Chars", StringComparison.Ordinal) || entry.Text.Contains("HashCode", StringComparison.Ordinal)) >= 1);
    }

    [Fact]
    public void Tui_collection_editor_state_can_edit_and_submit_selected_items()
    {
        var state = new TuiCollectionEditorState<TestCollectionItem>();
        state.Open(
            [
                new TestCollectionItem("alpha", "Alpha", "one"),
                new TestCollectionItem("beta", "Beta", "two"),
            ],
            pageSize: 5,
            keySelector: item => item.Key,
            editValueSelector: item => item.EditValue);

        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        var startEdit = state.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));

        Assert.Equal(TuiCollectionEditorActionKind.None, startEdit.Kind);
        Assert.Equal(TuiCollectionEditorInputMode.EditItem, state.InputMode);
        Assert.Equal("beta", state.EditingItemKey);
        Assert.Contains("two", state.RenderInputWithCursor(), StringComparison.Ordinal);

        var submit = state.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.SubmitInput, submit.Kind);
        Assert.Equal("beta", submit.Key);
        Assert.Equal("two", submit.Text);
        Assert.Equal(TuiCollectionEditorInputMode.EditItem, submit.InputMode);
    }

    [Fact]
    public void Tui_collection_editor_state_can_add_remove_and_request_actions()
    {
        var state = new TuiCollectionEditorState<TestCollectionItem>();
        state.Open(
            [
                new TestCollectionItem("alpha", "Alpha", "one"),
            ],
            pageSize: 5,
            keySelector: item => item.Key,
            editValueSelector: item => item.EditValue);

        state.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorInputMode.AddItem, state.InputMode);

        foreach (var character in "gamma")
        {
            state.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        var submit = state.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.SubmitInput, submit.Kind);
        Assert.Null(submit.Key);
        Assert.Equal("gamma", submit.Text);
        Assert.Equal(TuiCollectionEditorInputMode.AddItem, submit.InputMode);

        state.CompleteInput("alpha");

        var remove = state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.RemoveItem, remove.Kind);
        Assert.Equal("alpha", remove.Key);

        var apply = state.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.Apply, apply.Kind);

        var save = state.HandleKey(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.Save, save.Kind);

        var close = state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        Assert.Equal(TuiCollectionEditorActionKind.Close, close.Kind);
    }

    [Fact]
    public void Tui_group_editor_state_tracks_selection_and_emits_actions()
    {
        var state = new TuiGroupEditorState<TestGroupItem>();
        state.Open(
            [
                new TestGroupItem("alpha", "Alpha"),
                new TestGroupItem("beta", "Beta"),
            ],
            pageSize: 5,
            keySelector: item => item.Key);

        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        Assert.Equal("beta", state.SelectedKey);

        var toggle = state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        Assert.Equal(TuiGroupEditorActionKind.ToggleSelected, toggle.Kind);
        Assert.Equal("beta", toggle.Key);

        var edit = state.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        Assert.Equal(TuiGroupEditorActionKind.EditSelected, edit.Kind);
        Assert.Equal("beta", edit.Key);

        var raw = state.HandleKey(new ConsoleKeyInfo('t', ConsoleKey.T, shift: false, alt: false, control: false));
        Assert.Equal(TuiGroupEditorActionKind.RawEditSelected, raw.Kind);
        Assert.Equal("beta", raw.Key);

        var close = state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        Assert.Equal(TuiGroupEditorActionKind.Close, close.Kind);
    }

    [Fact]
    public void Tui_ordered_toggle_editor_state_can_toggle_reorder_and_commit()
    {
        var state = new TuiOrderedToggleEditorState<TestToggleItem>();
        state.Open(
            [
                new TestToggleItem("time", Included: false),
                new TestToggleItem("user", Included: true),
                new TestToggleItem("jobs", Included: true),
            ],
            pageSize: 4,
            keySelector: item => item.Key,
            includedSelector: item => item.Included,
            includedUpdater: (item, included) => item with { Included = included },
            preferredKey: "time",
            minimumIncludedCount: 1);

        var toggle = state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        Assert.Equal(TuiOrderedToggleEditorActionKind.Toggled, toggle.Kind);
        Assert.Equal("time", toggle.Key);
        Assert.True(state.Items[0].Included);

        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        var reorder = state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false));
        Assert.Equal(TuiOrderedToggleEditorActionKind.Reordered, reorder.Kind);
        Assert.Equal("user", state.Items[2].Key);

        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, shift: false, alt: false, control: false));
        state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        var reject = state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        Assert.Equal(TuiOrderedToggleEditorActionKind.ToggleRejected, reject.Kind);

        var commit = state.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.Equal(TuiOrderedToggleEditorActionKind.Commit, commit.Kind);
        Assert.Equal("user", commit.Key);
    }

    [Fact]
    public void Tui_option_picker_state_tracks_selection_and_commit()
    {
        var state = new TuiOptionPickerState<string>();
        state.Open(["alpha", "beta", "gamma"], pageSize: 2, keySelector: item => item, preferredKey: "beta");

        Assert.Equal(1, state.SelectedIndex);
        Assert.Equal("beta", state.SelectedKey);

        state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        Assert.Equal("gamma", state.SelectedKey);

        var commit = state.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.Equal(TuiOptionPickerActionKind.Commit, commit.Kind);
        Assert.Equal("gamma", commit.Key);

        var cancel = state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        Assert.Equal(TuiOptionPickerActionKind.Cancel, cancel.Kind);
    }

    [Fact]
    public void Tui_path_editor_state_handles_text_and_picker_flows()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var filePath = Path.Combine(tempDirectory.FullName, "picked.txt");
            File.WriteAllText(filePath, "hello");

            var state = new TuiPathEditorState();
            state.Open("config.tosh");

            foreach (var character in ".bak")
            {
                var changed = state.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false), pageSize: 8);
                Assert.Equal(TuiPathEditorActionKind.TextChanged, changed.Kind);
            }

            Assert.Contains("config.tosh.bak", state.RenderInputWithCursor(), StringComparison.Ordinal);

            var browse = state.HandleKey(new ConsoleKeyInfo('b', ConsoleKey.B, shift: false, alt: false, control: false), pageSize: 8);
            Assert.Equal(TuiPathEditorActionKind.BrowseRequested, browse.Kind);

            state.OpenPicker(tempDirectory.FullName, TuiFilePickerSelectionMode.File, null, pageSize: 8);

            while (!state.BuildPickerEntries(120, 16).Any(line => line.Contains("> [-] picked.txt", StringComparison.Ordinal)))
            {
                state.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false), pageSize: 8);
            }

            var picked = state.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false), pageSize: 8);
            Assert.Equal(TuiPathEditorActionKind.PickedPath, picked.Kind);
            Assert.Equal(filePath, picked.Path);
            Assert.False(state.IsBrowsing);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    // ── Mouse event construction and dispatch ──

    [Fact]
    public void Tui_input_event_from_mouse_reports_kind_correctly()
    {
        var mouse = new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 10, 5, false, false, false);
        var input = TuiInputEvent.FromMouse(mouse);

        Assert.True(input.IsMouse);
        Assert.False(input.IsKey);
        Assert.Equal(TuiMouseAction.Press, input.Mouse.Action);
        Assert.Equal(TuiMouseButton.Left, input.Mouse.Button);
        Assert.Equal(10, input.Mouse.Column);
        Assert.Equal(5, input.Mouse.Row);
    }

    [Fact]
    public void Tui_mouse_event_hits_rect_and_computes_local_coordinates()
    {
        var rect = new TuiRect(10, 5, 20, 10);
        var inside = new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 15, 8, false, false, false);
        var outside = new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 5, 3, false, false, false);
        var edge = new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 29, 14, false, false, false);
        var pastEdge = new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 30, 15, false, false, false);

        Assert.True(inside.HitsRect(rect));
        Assert.False(outside.HitsRect(rect));
        Assert.True(edge.HitsRect(rect));
        Assert.False(pastEdge.HitsRect(rect));

        var local = inside.ToLocal(rect);
        Assert.NotNull(local);
        Assert.Equal((5, 3), local.Value);

        Assert.Null(outside.ToLocal(rect));
    }

    [Fact]
    public void Tui_rect_contains_checks_boundaries()
    {
        var rect = new TuiRect(0, 0, 80, 24);

        Assert.True(rect.Contains(0, 0));
        Assert.True(rect.Contains(79, 23));
        Assert.False(rect.Contains(80, 0));
        Assert.False(rect.Contains(0, 24));
        Assert.False(rect.Contains(-1, 0));
    }

    [Theory]
    [InlineData("\x1b[<0;10;5M", 0, 0, 9, 4)]    // Press, Left
    [InlineData("\x1b[<0;1;1m", 1, 0, 0, 0)]      // Release, Left
    [InlineData("\x1b[<2;20;10M", 0, 2, 19, 9)]   // Press, Right
    [InlineData("\x1b[<64;5;8M", 3, 3, 4, 7)]     // Scroll, ScrollUp
    [InlineData("\x1b[<65;5;8M", 3, 4, 4, 7)]     // Scroll, ScrollDown
    [InlineData("\x1b[<32;3;3M", 2, 0, 2, 2)]     // Drag, Left (32 = drag flag)
    public void Tui_input_reader_parses_sgr_mouse_sequences(
        string sequence, int expectedAction, int expectedButton,
        int expectedColumn, int expectedRow)
    {
        Assert.True(TuiInputReader.TryParseSgrMouse(sequence, out var mouse));
        Assert.Equal((TuiMouseAction)expectedAction, mouse.Action);
        Assert.Equal((TuiMouseButton)expectedButton, mouse.Button);
        Assert.Equal(expectedColumn, mouse.Column);
        Assert.Equal(expectedRow, mouse.Row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("\x1b[<0;1M")]
    [InlineData("\x1b[<0;1;1X")]
    public void Tui_input_reader_rejects_invalid_sgr_sequences(string sequence)
    {
        Assert.False(TuiInputReader.TryParseSgrMouse(sequence, out _));
    }

    [Fact]
    public void Tui_input_reader_parses_modifier_flags()
    {
        // Shift = 4, Alt = 8, Control = 16 — left press with all modifiers: 0 + 4 + 8 + 16 = 28
        Assert.True(TuiInputReader.TryParseSgrMouse("\x1b[<28;1;1M", out var mouse));
        Assert.True(mouse.Shift);
        Assert.True(mouse.Alt);
        Assert.True(mouse.Control);
        Assert.Equal(TuiMouseAction.Press, mouse.Action);
        Assert.Equal(TuiMouseButton.Left, mouse.Button);
    }

    // ── ProcessInputBatch with mouse events ──

    [Fact]
    public void Tui_process_input_batch_dispatches_mouse_events()
    {
        var host = new FakeTuiHost([]);
        var screen = new CountingScreen();

        var scroll = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 0, 0, false, false, false));

        var result = TuiApplication.ProcessInputBatch(host, screen, scroll);

        Assert.Equal(TuiScreenResult.Continue, result);
        Assert.Single(screen.HandledInputs);
        Assert.True(screen.HandledInputs[0].IsMouse);
    }

    // ── TuiPickScreen mouse interaction ──

    [Fact]
    public void Tui_pick_screen_scroll_wheel_moves_selection()
    {
        var request = new TuiPickRequest(
            Items: ["alpha", "beta", "gamma", "delta"],
            Prompt: "Pick one");
        var screen = new TuiPickScreen(request);

        // Initial render to initialize list state
        screen.Render(new TuiSize(80, 24));

        // Scroll down
        var scrollDown = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 10, 5, false, false, false));
        screen.HandleInput(scrollDown);

        // Re-render and check second item is highlighted
        var frame = screen.Render(new TuiSize(80, 24));
        Assert.Contains("> beta", frame.Content, StringComparison.Ordinal);

        // Scroll up moves back
        var scrollUp = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollUp, 10, 5, false, false, false));
        screen.HandleInput(scrollUp);

        frame = screen.Render(new TuiSize(80, 24));
        Assert.Contains("> alpha", frame.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Tui_pick_screen_click_selects_item_at_row()
    {
        var request = new TuiPickRequest(
            Items: ["alpha", "beta", "gamma", "delta"],
            Prompt: "Pick one");
        var screen = new TuiPickScreen(request);

        // Render to initialize _headerLines (title + separator = 2 header lines)
        screen.Render(new TuiSize(80, 24));

        // Click on row for "gamma" (header=2, so row 2=alpha, 3=beta, 4=gamma)
        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 5, 4, false, false, false));
        screen.HandleInput(click);

        var frame = screen.Render(new TuiSize(80, 24));
        Assert.Contains("> gamma", frame.Content, StringComparison.Ordinal);
    }

    // ── TuiFilePickerScreen mouse interaction ──

    [Fact]
    public void Tui_file_picker_screen_scroll_wheel_navigates_entries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "aaa.txt"), "");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "bbb.txt"), "");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "ccc.txt"), "");

            var request = new TuiFilePickRequest(InitialPath: tempDirectory.FullName);
            var screen = new TuiFilePickerScreen(request);

            // Initial render — first entry is selected (may be a special entry like Self or Parent)
            var frame0 = screen.Render(new TuiSize(120, 24));

            // Scroll down several times to get past special entries into file entries
            var scrollDown = TuiInputEvent.FromMouse(
                new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 10, 10, false, false, false));

            for (var i = 0; i < 5; i++)
                screen.HandleInput(scrollDown);

            var frame1 = screen.Render(new TuiSize(120, 24));

            // The rendered content must change after scrolling (selection moved)
            Assert.NotEqual(frame0.Content, frame1.Content);

            // Scroll up should also work
            var scrollUp = TuiInputEvent.FromMouse(
                new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollUp, 10, 10, false, false, false));
            screen.HandleInput(scrollUp);

            var frame2 = screen.Render(new TuiSize(120, 24));
            Assert.NotEqual(frame1.Content, frame2.Content);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    // ── TuiConfirmScreen mouse interaction ──

    [Fact]
    public void Tui_confirm_screen_click_on_confirm_button_exits()
    {
        var request = new TuiConfirmRequest(
            Message: "Are you sure?",
            ConfirmLabel: "Yes",
            CancelLabel: "No",
            DefaultConfirm: true);
        var screen = new TuiConfirmScreen(request);

        // Render to compute button positions
        var frame = screen.Render(new TuiSize(80, 24));

        // Find the button row and confirm button position in the rendered output
        var lines = frame.Content.Split('\n');
        var buttonRow = -1;
        var confirmCol = -1;

        for (var row = 0; row < lines.Length; row++)
        {
            var idx = lines[row].IndexOf("[Yes]", StringComparison.Ordinal);

            if (idx >= 0)
            {
                buttonRow = row;
                confirmCol = idx + 1; // Click inside the button
                break;
            }
        }

        Assert.True(buttonRow >= 0, "Could not find button row");

        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, confirmCol, buttonRow, false, false, false));
        var result = screen.HandleInput(click);

        Assert.Equal(TuiScreenResult.Exit, result);
        Assert.NotNull(screen.Outcome);
        Assert.False(screen.Outcome.Cancelled);
    }

    [Fact]
    public void Tui_confirm_screen_click_on_cancel_button_cancels()
    {
        var request = new TuiConfirmRequest(
            Message: "Are you sure?",
            ConfirmLabel: "Yes",
            CancelLabel: "No",
            DefaultConfirm: true);
        var screen = new TuiConfirmScreen(request);

        var frame = screen.Render(new TuiSize(80, 24));
        var lines = frame.Content.Split('\n');
        var buttonRow = -1;
        var cancelCol = -1;

        for (var row = 0; row < lines.Length; row++)
        {
            var idx = lines[row].IndexOf("[No]", StringComparison.Ordinal);

            if (idx >= 0)
            {
                buttonRow = row;
                cancelCol = idx + 1;
                break;
            }
        }

        Assert.True(buttonRow >= 0, "Could not find cancel button");

        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, cancelCol, buttonRow, false, false, false));
        var result = screen.HandleInput(click);

        Assert.Equal(TuiScreenResult.Exit, result);
        Assert.NotNull(screen.Outcome);
        Assert.True(screen.Outcome.Cancelled);
    }

    // ── TuiInputScreen mouse interaction ──

    [Fact]
    public void Tui_input_screen_click_positions_cursor()
    {
        var request = new TuiInputRequest(Prompt: "Enter name:", DefaultValue: "hello world");
        var screen = new TuiInputScreen(request);

        // Render to set _inputRow
        var frame = screen.Render(new TuiSize(80, 24));

        // Find the row with the input text
        var lines = frame.Content.Split('\n');
        var inputRow = -1;

        for (var row = 0; row < lines.Length; row++)
        {
            if (lines[row].Contains("hello world", StringComparison.Ordinal))
            {
                inputRow = row;
                break;
            }
        }

        Assert.True(inputRow >= 0, "Could not find input row");

        // Click at column 5 to position cursor there
        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 5, inputRow, false, false, false));
        screen.HandleInput(click);

        // Now type a character — it should insert at position 5
        screen.HandleInput(TuiInputEvent.FromKey(
            new ConsoleKeyInfo('X', ConsoleKey.X, false, false, false)));

        frame = screen.Render(new TuiSize(80, 24));
        // RenderWithCursor() inserts a cursor marker, so just check the characters are in the right order
        Assert.Contains("helloX", frame.Content, StringComparison.Ordinal);
        Assert.Contains(" world", frame.Content, StringComparison.Ordinal);
    }

    // ── TuiTextInputState.SetCursorIndex ──

    [Fact]
    public void Tui_text_input_state_set_cursor_index_positions_correctly()
    {
        var input = new TuiTextInputState();
        input.SetText("abcdef");

        // Cursor starts at end (6)
        input.SetCursorIndex(3);

        // Type a character at position 3
        input.HandleKey(new ConsoleKeyInfo('X', ConsoleKey.X, false, false, false));
        Assert.Equal("abcXdef", input.Text);
    }

    // ── TuiFilePickerState mouse adapter methods ──

    [Fact]
    public void Tui_file_picker_state_mouse_adapters_navigate_and_select()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "aaa.txt"), "");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "bbb.txt"), "");
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "ccc.txt"), "");

            var picker = new TuiFilePickerState();
            picker.Open(tempDirectory.FullName, TuiFilePickerSelectionMode.File, null, pageSize: 10);

            // Initial — first entry is "[..] Parent directory"
            var entries = picker.BuildEntries(120, 16);
            Assert.Contains(entries, line => line.Contains("> [..] Parent", StringComparison.Ordinal));

            // MoveNext via mouse adapter — moves to aaa.txt
            picker.MoveNext();
            entries = picker.BuildEntries(120, 16);
            Assert.Contains(entries, line => line.Contains("> [-] aaa.txt", StringComparison.Ordinal));

            // MovePrevious via mouse adapter — back to parent dir
            picker.MovePrevious();
            entries = picker.BuildEntries(120, 16);
            Assert.Contains(entries, line => line.Contains("> [..] Parent", StringComparison.Ordinal));

            // SelectIndex via mouse adapter — select fourth entry (parent=0, aaa=1, bbb=2, ccc=3)
            picker.SelectIndex(3);
            entries = picker.BuildEntries(120, 16);
            Assert.Contains(entries, line => line.Contains("> [-] ccc.txt", StringComparison.Ordinal));

            // Scroll state is accessible
            Assert.Equal(0, picker.Scroll.Offset);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    // ── Mouse events are ignored when irrelevant ──

    [Fact]
    public void Tui_pick_screen_ignores_right_click()
    {
        var request = new TuiPickRequest(Items: ["alpha", "beta"], Prompt: "Pick");
        var screen = new TuiPickScreen(request);
        screen.Render(new TuiSize(80, 24));

        var rightClick = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Right, 5, 3, false, false, false));
        var result = screen.HandleInput(rightClick);

        Assert.Equal(TuiScreenResult.Continue, result);

        // Selection should still be on first item
        var frame = screen.Render(new TuiSize(80, 24));
        Assert.Contains("> alpha", frame.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Tui_pick_screen_ignores_click_outside_list()
    {
        var request = new TuiPickRequest(Items: ["alpha"], Prompt: "Pick");
        var screen = new TuiPickScreen(request);
        screen.Render(new TuiSize(80, 24));

        // Click on header row (row 0) — not in list area
        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 5, 0, false, false, false));
        var result = screen.HandleInput(click);

        Assert.Equal(TuiScreenResult.Continue, result);
    }

    [Fact]
    public void Tui_confirm_screen_ignores_click_outside_buttons()
    {
        var request = new TuiConfirmRequest(Message: "Sure?");
        var screen = new TuiConfirmScreen(request);
        screen.Render(new TuiSize(80, 24));

        // Click at row 0 column 0 — nowhere near buttons
        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 0, 0, false, false, false));
        var result = screen.HandleInput(click);

        Assert.Equal(TuiScreenResult.Continue, result);
        Assert.Null(screen.Outcome);
    }

    [Fact]
    public void Tui_input_screen_ignores_click_off_input_row()
    {
        var request = new TuiInputRequest(Prompt: "Enter:", DefaultValue: "test");
        var screen = new TuiInputScreen(request);
        screen.Render(new TuiSize(80, 24));

        // Click far above the input line
        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 0, 0, false, false, false));
        var result = screen.HandleInput(click);

        Assert.Equal(TuiScreenResult.Continue, result);

        // Typing should still append at end (cursor didn't move)
        screen.HandleInput(TuiInputEvent.FromKey(
            new ConsoleKeyInfo('Z', ConsoleKey.Z, false, false, false)));
        var frame = screen.Render(new TuiSize(80, 24));
        Assert.Contains("testZ", frame.Content, StringComparison.Ordinal);
    }

    private sealed record TestCollectionItem(string Key, string Label, string EditValue);
    private sealed record TestGroupItem(string Key, string Label);
    private sealed record TestToggleItem(string Key, bool Included);

    private sealed class FakeTuiHost : ITuiHost
    {
        private readonly Queue<ConsoleKeyInfo> pendingKeys;

        public FakeTuiHost(IEnumerable<ConsoleKeyInfo> pendingKeys)
        {
            this.pendingKeys = new Queue<ConsoleKeyInfo>(pendingKeys);
        }

        public bool IsInteractive => true;

        public TuiSize? TryGetSize() => new(80, 24);

        public ConsoleKeyInfo ReadKey(bool intercept = true) => throw new NotSupportedException();

        public bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true)
        {
            if (pendingKeys.Count == 0)
            {
                key = default;
                return false;
            }

            key = pendingKeys.Dequeue();
            return true;
        }

        public TuiInputEvent ReadInput() => throw new NotSupportedException();

        public bool TryReadPendingInput(out TuiInputEvent inputEvent)
        {
            if (pendingKeys.Count == 0)
            {
                inputEvent = default;
                return false;
            }

            inputEvent = TuiInputEvent.FromKey(pendingKeys.Dequeue());
            return true;
        }

        public void Write(string text)
        {
        }
    }

    private sealed class CountingScreen : ITuiScreen
    {
        public List<ConsoleKeyInfo> HandledKeys { get; } = [];

        public List<TuiInputEvent> HandledInputs { get; } = [];

        public TuiFrame Render(TuiSize size) => new(string.Empty);

        public TuiScreenResult HandleInput(TuiInputEvent input)
        {
            HandledInputs.Add(input);

            if (input.IsKey)
                HandledKeys.Add(input.Key);

            return TuiScreenResult.Continue;
        }

        public TuiScreenResult HandleKey(ConsoleKeyInfo key)
        {
            HandledKeys.Add(key);
            return TuiScreenResult.Continue;
        }
    }
}
