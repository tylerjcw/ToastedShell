using Tosh.Cli.Tui;
using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class ConfigBrowserScreenTests
{
    [Fact]
    public void Config_browser_auto_discovers_top_level_config_sections()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, null));

        var labels = screen.BuildSidebarLabels();

        Assert.Contains(labels, label => label.Contains("Theme", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Display", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Repl", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Prompt", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("History", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Startup", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_filters_nodes_by_query_and_keeps_ancestor_sections_visible()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("box style", null));

        var labels = screen.BuildSidebarLabels();

        Assert.Contains(labels, label => label.Contains("Theme", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Tables", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Box Style", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_builds_detail_lines_with_current_and_default_metadata()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Tables.BoxStyle = ToshTableBoxStyle.Double;
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("box style", null));

        Assert.True(screen.SelectSidebarEntryContaining("Box Style"));

        var lines = screen.BuildDetailLines(90);

        Assert.Contains(lines, line => line.Contains("$tosh.Config.Theme.Tables.BoxStyle", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Type: ToshTableBoxStyle", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Status: customized", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Current Value", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Double", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Default Value", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Rounded", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_renders_boxed_titled_layout()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("box style", null));

        var frame = screen.Render(new TuiSize(90, 24));
        var rendered = StyledText.StripAnsi(frame.Content);

        Assert.Contains("Config Browser", rendered, StringComparison.Ordinal);
        Assert.Contains("Configuration", rendered, StringComparison.Ordinal);
        Assert.Contains("Box Style", rendered, StringComparison.Ordinal);
        Assert.Contains("╭", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_browser_can_toggle_boolean_values_and_apply_staged_changes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("ghost text", null));

        Assert.True(screen.SelectSidebarEntryContaining("Ghost Text Enabled"));

        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));

        var stagedLines = screen.BuildDetailLines(90);
        Assert.Contains(stagedLines, line => line.Contains("Staged Value", StringComparison.Ordinal));
        Assert.Contains(stagedLines, line => line.Contains("false", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.False(runtime.Config.Repl.GhostTextEnabled);
        var appliedLines = screen.BuildDetailLines(90);
        Assert.DoesNotContain(appliedLines, line => line.Contains("Staged Value", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_can_stage_and_apply_enum_changes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("box style", null));

        Assert.True(screen.SelectSidebarEntryContaining("Box Style"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var stagedLines = screen.BuildDetailLines(90);
        Assert.Contains(stagedLines, line => line.Contains("Staged Value", StringComparison.Ordinal));
        Assert.Contains(stagedLines, line => line.Contains("Square", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal(ToshTableBoxStyle.Square, runtime.Config.Theme.Tables.BoxStyle);
    }

    [Fact]
    public void Config_browser_can_stage_numeric_text_input_and_apply()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("completion max visible", null));

        Assert.True(screen.SelectSidebarEntryContaining("Completion Max Visible"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('9', ConsoleKey.D9, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal(9, runtime.Config.Repl.CompletionMaxVisible);
    }

    [Fact]
    public void Config_browser_can_stage_apply_and_save_prompt_layout_changes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(tempDirectory.FullName);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "config.tosh"), "# manual config\n");
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt.HeaderRightLayout"));

            screen.HandleKey(new ConsoleKeyInfo('t', ConsoleKey.T, shift: false, alt: false, control: false));

            foreach (var _ in runtime.Config.Prompt.HeaderRightLayout)
            {
                screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
            }

            foreach (var character in "Time, UserHost, Jobs, Duration")
            {
                screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
            }

            screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

            var stagedLines = screen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
            Assert.Contains(stagedLines, line => line.Contains("Staged Value", StringComparison.Ordinal));
            Assert.Contains(stagedLines, line => line.Contains("Time, UserHost, Jobs, Duration", StringComparison.Ordinal));
            Assert.Contains(stagedLines, line => line.Contains("Header Right: Time, UserHost, Jobs, Duration", StringComparison.Ordinal));

            screen.HandleKey(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));

            Assert.Equal("Time, UserHost, Jobs, Duration", runtime.Config.Prompt.HeaderRightLayout);
            Assert.True(runtime.Config.Prompt.TimeEnabled);

            var configText = File.ReadAllText(Path.Combine(tempDirectory.FullName, "config.tosh"));
            Assert.Contains("# manual config", configText, StringComparison.Ordinal);
            Assert.Contains("# >>> tosh config browse >>>", configText, StringComparison.Ordinal);
            Assert.Contains("$tosh.Config.Prompt.HeaderRightLayout = \"Time, UserHost, Jobs, Duration\"", configText, StringComparison.Ordinal);
            Assert.Contains("$tosh.Config.Prompt.TimeEnabled = true", configText, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_can_use_structured_style_editor_to_toggle_flags()
    {
        var runtime = ToshRuntime.CreateDefault();
        var originalBold = runtime.Config.Theme.Tui.SearchLabel.Bold;
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Theme.Tui.SearchLabel"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        MoveGroupEditorSelectionUntil(screen, line => line.Contains("> ", StringComparison.Ordinal) && line.Contains("Bold:", StringComparison.Ordinal));
        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal(!originalBold, runtime.Config.Theme.Tui.SearchLabel.Bold);
    }

    [Fact]
    public void Config_browser_can_use_structured_style_editor_to_edit_text_fields()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Theme.Tui.SearchLabel"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('t', ConsoleKey.T, shift: false, alt: false, control: false));

        foreach (var _ in runtime.Config.Theme.Tui.SearchLabel.Foreground ?? string.Empty)
        {
            screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
        }

        foreach (var character in "bright-magenta")
        {
            screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal("bright-magenta", runtime.Config.Theme.Tui.SearchLabel.Foreground);
    }

    [Fact]
    public void Config_browser_can_use_generic_group_editor_for_prompt_settings()
    {
        var runtime = ToshRuntime.CreateDefault();
        var originalTimeEnabled = runtime.Config.Prompt.TimeEnabled;
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));

        var lines = screen.BuildDetailLines(120).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(lines, line => line.Contains("Prompt Editor", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Time Enabled", StringComparison.Ordinal));

        MoveGroupEditorSelectionUntil(screen, line => line.Contains("> ", StringComparison.Ordinal) && line.Contains("Time Enabled:", StringComparison.Ordinal));
        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal(!originalTimeEnabled, runtime.Config.Prompt.TimeEnabled);
    }

    [Fact]
    public void Config_browser_can_use_structured_prompt_layout_editor_to_add_and_reorder_modules()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt.HeaderRightLayout"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false));

        var layoutEditorLines = screen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(layoutEditorLines, line => line.Contains("Current Layout: Time, UserHost, Jobs, Duration", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal("Time, UserHost, Jobs, Duration", runtime.Config.Prompt.HeaderRightLayout);
        Assert.True(runtime.Config.Prompt.TimeEnabled);
    }

    [Fact]
    public void Config_browser_can_use_color_picker_editor_for_color_fields()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt.NameColor"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        MoveDetailSelectionUntil(screen, line => line.Contains("> ( ) bright-magenta", StringComparison.Ordinal));

        var previewLines = screen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(previewLines, line => line.Contains("Preview: Sample Text 123", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.Equal("bright-magenta", runtime.Config.Prompt.NameColor);
    }

    [Fact]
    public void Config_browser_can_use_path_editor_with_resolved_path_preview()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(tempDirectory.FullName);
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Startup.ConfigFilePath"));

            screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));

            foreach (var _ in runtime.Config.Startup.ConfigFilePath)
            {
                screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
            }

            foreach (var character in "custom-config.tosh")
            {
                screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
            }

            var lines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();
            Assert.Contains(lines, line => line.Contains($"Resolved Path: {Path.Combine(tempDirectory.FullName, "custom-config.tosh")}", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains($"Base Directory: {tempDirectory.FullName}", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Exists: missing", StringComparison.Ordinal));

            screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
            screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

            Assert.Equal("custom-config.tosh", runtime.Config.Startup.ConfigFilePath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_can_browse_for_paths_with_filesystem_picker()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var pickedPath = Path.Combine(tempDirectory.FullName, "custom-config.tosh");
            File.WriteAllText(pickedPath, "# sample");

            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(tempDirectory.FullName);
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Startup.ConfigFilePath"));

            screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
            screen.HandleKey(new ConsoleKeyInfo('b', ConsoleKey.B, shift: false, alt: false, control: false));
            MoveDetailSelectionUntil(screen, line => line.Contains("> [-] custom-config.tosh", StringComparison.Ordinal));
            screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
            screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

            Assert.Equal("custom-config.tosh", runtime.Config.Startup.ConfigFilePath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_shows_active_child_editor_inside_prompt_group_editor()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        MoveGroupEditorSelectionUntil(screen, line => line.Contains("> ", StringComparison.Ordinal) && line.Contains("Header Right Layout:", StringComparison.Ordinal));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var lines = screen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(lines, line => line.Contains("Editing Header Right Layout", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Current Layout: UserHost, Jobs, Duration", StringComparison.Ordinal));
    }

    private static void MoveGroupEditorSelectionUntil(ConfigBrowserScreen screen, Func<string, bool> predicate, int maxMoves = 32)
    {
        for (var move = 0; move < maxMoves; move++)
        {
            var lines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();

            if (lines.Any(predicate))
            {
                return;
            }

            screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        }

        Assert.Fail("Could not move group editor selection to the requested field.");
    }

    private static void MoveDetailSelectionUntil(ConfigBrowserScreen screen, Func<string, bool> predicate, int maxMoves = 32)
    {
        for (var move = 0; move < maxMoves; move++)
        {
            var lines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();

            if (lines.Any(predicate))
            {
                return;
            }

            screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: false, alt: false, control: false));
        }

        Assert.Fail("Could not move editor selection to the requested line.");
    }

    [Fact]
    public void Config_browser_can_revert_staged_changes_for_selected_node()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("ghost text", null));

        Assert.True(screen.SelectSidebarEntryContaining("Ghost Text Enabled"));

        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        Assert.Contains(screen.BuildSidebarLabels(), label => label.Contains("Ghost Text Enabled *", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('r', ConsoleKey.R, shift: false, alt: false, control: false));

        Assert.True(runtime.Config.Repl.GhostTextEnabled);
        Assert.DoesNotContain(screen.BuildSidebarLabels(), label => label.Contains("Ghost Text Enabled *", StringComparison.Ordinal));
        Assert.DoesNotContain(screen.BuildDetailLines(90), line => line.Contains("Staged Value", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_requires_confirmation_before_quitting_with_dirty_changes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("ghost text", null));

        Assert.True(screen.SelectSidebarEntryContaining("Ghost Text Enabled"));
        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));

        var firstQuit = screen.HandleKey(new ConsoleKeyInfo('q', ConsoleKey.Q, shift: false, alt: false, control: false));
        Assert.Equal(TuiScreenResult.Continue, firstQuit);
        Assert.Contains(screen.BuildDetailLines(90), line => line.Contains("Discard them and quit", StringComparison.Ordinal));

        var cancelledQuit = screen.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, shift: false, alt: false, control: false));
        Assert.Equal(TuiScreenResult.Continue, cancelledQuit);

        var confirmedQuit = screen.HandleKey(new ConsoleKeyInfo('q', ConsoleKey.Q, shift: false, alt: false, control: false));
        Assert.Equal(TuiScreenResult.Continue, confirmedQuit);
        var exit = screen.HandleKey(new ConsoleKeyInfo('y', ConsoleKey.Y, shift: false, alt: false, control: false));
        Assert.Equal(TuiScreenResult.Exit, exit);
    }

    [Fact]
    public void Config_browser_surfaces_validation_for_invalid_color_values()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("directory color", null));

        Assert.True(screen.SelectSidebarEntryContaining("Directory Color"));

        screen.HandleKey(new ConsoleKeyInfo('t', ConsoleKey.T, shift: false, alt: false, control: false));

        foreach (var character in "blue")
        {
            screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
        }

        foreach (var character in "not-a-color")
        {
            screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var lines = screen.BuildDetailLines(100);
        Assert.Contains(lines, line => line.Contains("Validation", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("not a supported named or hex color", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Config_browser_shows_style_preview_for_text_style_groups()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("search label", null));

        Assert.True(screen.SelectSidebarEntryContaining("Search Label"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Style Preview", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Foreground:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Sample Text 123", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_shows_prompt_preview_for_prompt_nodes()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Prompt.TimeEnabled = true;
        runtime.Config.Prompt.GitEnabled = false;
        runtime.Config.Prompt.NameText = "toast";
        runtime.Config.Prompt.HeaderLeftLayout = "Time, Directory";
        runtime.Config.Prompt.HeaderRightLayout = "UserHost, Jobs, Duration";
        runtime.Config.Prompt.PromptLeftLayout = "HistoryId, ExitCode, Name, Indicator";
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest("prompt", null));

        Assert.True(screen.SelectSidebarEntryContaining("Prompt"));

        var lines = screen.BuildDetailLines(140)
            .Select(StyledText.StripAnsi)
            .ToArray();

        Assert.Contains(lines, line => line.Contains("Prompt Preview", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Layout: two-line prompt", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Header Left: Time, Directory", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Header Right: UserHost, Jobs, Duration", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Prompt Left: HistoryId, ExitCode, Name, Indicator", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Sample Success Preview", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Sample Failure Preview", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("!432", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("toast", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("✘ 7", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("jobs:2", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_shows_visual_theme_previews_for_theme_sections()
    {
        var runtime = ToshRuntime.CreateDefault();

        var tableScreen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Theme.Tables"));
        var tableLines = tableScreen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(tableLines, line => line.Contains("Theme Preview", StringComparison.Ordinal));
        Assert.Contains(tableLines, line => line.Contains("Sample Table", StringComparison.Ordinal));
        Assert.Contains(tableLines, line => line.Contains("alpha", StringComparison.Ordinal));

        var syntaxScreen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Theme.Syntax"));
        var syntaxLines = syntaxScreen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(syntaxLines, line => line.Contains("Sample Command", StringComparison.Ordinal));
        Assert.Contains(syntaxLines, line => line.Contains("summarize", StringComparison.Ordinal));

        var tuiScreen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Theme.Tui"));
        var tuiLines = tuiScreen.BuildDetailLines(140).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(tuiLines, line => line.Contains("Preview Pane", StringComparison.Ordinal));
        Assert.Contains(tuiLines, line => line.Contains("Section Heading", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_renders_collection_shaped_values_as_real_collection_views()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.DisplayPreferences.Profiles.GetOrCreate("System.String").SetTableColumns(["Length", "Chars"]);
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Display.Profiles.Types"));

        var lines = screen.BuildDetailLines(140)
            .Select(StyledText.StripAnsi)
            .ToArray();

        Assert.Contains(lines, line => line.Contains("Collection View", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Item Count: 1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("System.String", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Length", StringComparison.Ordinal));
    }

    [Fact]
    public void Config_browser_can_edit_display_profile_collections()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Display.Profiles.Types"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, shift: false, alt: false, control: false));

        foreach (var character in "System.String = Length, Chars")
        {
            screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var stagedLines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();
        Assert.Contains(stagedLines, line => line.Contains("System.String", StringComparison.Ordinal));
        Assert.Contains(stagedLines, line => line.Contains("Length", StringComparison.Ordinal));
        Assert.Contains(stagedLines, line => line.Contains("Chars", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('a', ConsoleKey.A, shift: false, alt: false, control: false));

        Assert.True(runtime.DisplayPreferences.Profiles.TryGet("System.String", out var profile));
        Assert.Equal(["Length", "Chars"], profile.TableColumns.ToArray());
    }

    [Fact]
    public void Config_browser_can_save_display_profile_collections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(tempDirectory.FullName);
            File.WriteAllText(Path.Combine(tempDirectory.FullName, "config.tosh"), "# manual config\n");
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Display.Profiles.Types"));

            screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
            screen.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, shift: false, alt: false, control: false));

            foreach (var character in "System.String = Length, Chars")
            {
                screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
            }

            screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
            screen.HandleKey(new ConsoleKeyInfo('s', ConsoleKey.S, shift: false, alt: false, control: false));

            Assert.True(runtime.DisplayPreferences.Profiles.TryGet("System.String", out var profile));
            Assert.Equal(["Length", "Chars"], profile.TableColumns.ToArray());

            var configText = File.ReadAllText(Path.Combine(tempDirectory.FullName, "config.tosh"));
            Assert.Contains("# manual config", configText, StringComparison.Ordinal);
            Assert.Contains("# >>> tosh config browse >>>", configText, StringComparison.Ordinal);
            Assert.Contains("view columns \"System.String\" \"Length\" \"Chars\"", configText, StringComparison.Ordinal);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_shows_staged_diff_for_group_nodes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt"));

        screen.HandleKey(new ConsoleKeyInfo('e', ConsoleKey.E, shift: false, alt: false, control: false));
        MoveGroupEditorSelectionUntil(screen, line => line.Contains("> ", StringComparison.Ordinal) && line.Contains("Time Enabled:", StringComparison.Ordinal));
        screen.HandleKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift: false, alt: false, control: false));

        var lines = screen.BuildDetailLines(140)
            .Select(StyledText.StripAnsi)
            .ToArray();

        Assert.Contains(lines, line => line.Contains("Staged Diff", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("TimeEnabled", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("staged", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Config_browser_can_reload_startup_configuration_from_the_startup_section()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var configDirectory = tempDirectory.FullName;
            var autoloadDirectory = Path.Combine(configDirectory, "autoload");
            Directory.CreateDirectory(autoloadDirectory);

            File.WriteAllText(Path.Combine(configDirectory, "config.tosh"), "$tosh.Config.Prompt.NameText = \"toast\"\n$tosh.Config.Theme.Tables.BoxStyle = \"Double\"");
            File.WriteAllText(Path.Combine(configDirectory, "profile.tosh"), "func ll => ls -la");
            File.WriteAllText(Path.Combine(autoloadDirectory, "20-theme.tosh"), "$tosh.Config.Repl.ContinuationPrompt = \"..> \"");

            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(configDirectory);
            runtime.Config.Prompt.NameText = "stale";
            runtime.Config.Repl.GhostTextEnabled = false;
            _ = new ToshEngine(runtime);
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Startup"));

            screen.HandleKey(new ConsoleKeyInfo('l', ConsoleKey.L, shift: false, alt: false, control: false));

            Assert.Equal("toast", runtime.Config.Prompt.NameText);
            Assert.Equal("..> ", runtime.Config.Repl.ContinuationPrompt);
            Assert.True(runtime.Config.Repl.GhostTextEnabled);
            Assert.Equal(ToshTableBoxStyle.Double, runtime.Config.Theme.Tables.BoxStyle);

            var lines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();
            Assert.Contains(lines, line => line.Contains("Startup Actions", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Reloaded 3 startup files", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_can_initialize_startup_layout_from_the_startup_section()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var runtime = ToshRuntime.CreateDefault();
            runtime.Config.Startup.ApplyRootDirectory(tempDirectory.FullName);
            var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Startup"));

            screen.HandleKey(new ConsoleKeyInfo('i', ConsoleKey.I, shift: false, alt: false, control: false));

            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, "config.tosh")));
            Assert.True(File.Exists(Path.Combine(tempDirectory.FullName, "profile.tosh")));
            Assert.True(Directory.Exists(Path.Combine(tempDirectory.FullName, "autoload")));

            var lines = screen.BuildDetailLines(160).Select(StyledText.StripAnsi).ToArray();
            Assert.Contains(lines, line => line.Contains("Initialized 3 startup paths", StringComparison.Ordinal));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_browser_validates_unknown_prompt_layout_modules()
    {
        var runtime = ToshRuntime.CreateDefault();
        var screen = new ConfigBrowserScreen(runtime, new ConfigBrowseRequest(null, "Prompt.HeaderLeftLayout"));

        screen.HandleKey(new ConsoleKeyInfo('t', ConsoleKey.T, shift: false, alt: false, control: false));

        foreach (var character in "Directory, Git")
        {
            screen.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift: false, alt: false, control: false));
        }

        foreach (var character in "Directory, Banana")
        {
            screen.HandleKey(new ConsoleKeyInfo(character, ConsoleKey.A, shift: false, alt: false, control: false));
        }

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var lines = screen.BuildDetailLines(140);
        Assert.Contains(lines, line => line.Contains("Prompt module \"Banana\" is not recognized", StringComparison.Ordinal));
    }
}
