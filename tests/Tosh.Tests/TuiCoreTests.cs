using Tosh.Cli.Tui;

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
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));

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

        public void Write(string text)
        {
        }
    }

    private sealed class CountingScreen : ITuiScreen
    {
        public List<ConsoleKeyInfo> HandledKeys { get; } = [];

        public TuiFrame Render(TuiSize size) => new(string.Empty);

        public TuiScreenResult HandleKey(ConsoleKeyInfo key)
        {
            HandledKeys.Add(key);
            return TuiScreenResult.Continue;
        }
    }
}
