using Tosh.Cli.Tui;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class HelpBrowserScreenTests(ToshRuntimeFixture fixture) : IClassFixture<ToshRuntimeFixture>
{
    [Fact]
    public void Help_browser_filters_topics_by_query()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("regex", null));

        var topics = screen.FilterTopics();

        Assert.Contains(topics, topic => topic.Name == "grep");
        Assert.Contains(topics, topic => topic.Name == "match");
    }

    [Fact]
    public void Help_browser_builds_detail_lines_for_selected_topic()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("grep", "grep"));

        var lines = screen.BuildDetailLines(60);

        Assert.Contains(lines, line => line.Contains("grep", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("Usage", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Arguments", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Options", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Pipeline Input", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Output", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Examples", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_renders_boxed_titled_layout()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("grep", "grep"));

        var frame = screen.Render(new TuiSize(80, 20));
        var rendered = StyledText.StripAnsi(frame.Content);

        Assert.Contains("╭", rendered, StringComparison.Ordinal);
        Assert.Contains("Help Browser", rendered, StringComparison.Ordinal);
        Assert.Contains("ToastedShell", rendered, StringComparison.Ordinal);
        Assert.Contains("grep [BuiltIn]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_browser_uses_configured_tui_box_style()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Tui.BoxStyle = ToshTableBoxStyle.Double;
        var screen = new HelpBrowserScreen(runtime, new HelpBrowseRequest("grep", "grep"));

        var frame = screen.Render(new TuiSize(80, 20));
        var rendered = StyledText.StripAnsi(frame.Content);

        Assert.Contains("╔", rendered, StringComparison.Ordinal);
        Assert.Contains("║", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_browser_can_quit_with_q_even_when_search_has_focus()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("grep", "grep"));

        var result = screen.HandleKey(new ConsoleKeyInfo('q', ConsoleKey.Q, shift: false, alt: false, control: false));

        Assert.Equal(TuiScreenResult.Exit, result);
    }

    [Fact]
    public void Help_browser_can_open_related_topics_and_navigate_back_and_forward()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest(null, "grep"));

        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('1', ConsoleKey.D1, shift: false, alt: false, control: false));
        Assert.Equal("match", screen.CurrentTopicName);

        screen.HandleKey(new ConsoleKeyInfo('[', ConsoleKey.Oem4, shift: false, alt: false, control: false));
        Assert.Equal("grep", screen.CurrentTopicName);

        screen.HandleKey(new ConsoleKeyInfo(']', ConsoleKey.Oem6, shift: false, alt: false, control: false));
        Assert.Equal("match", screen.CurrentTopicName);
    }

    [Fact]
    public void Help_browser_can_switch_top_level_groups()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("func", "func"));

        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.F3, shift: false, alt: false, control: false));

        var rendered = StyledText.StripAnsi(screen.Render(new TuiSize(80, 20)).Content);
        Assert.Contains("ToastScript", rendered, StringComparison.Ordinal);
        Assert.Contains("func", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_browser_picks_a_sensible_initial_group_from_the_starting_query()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("func", null));

        var rendered = StyledText.StripAnsi(screen.Render(new TuiSize(80, 20)).Content);
        Assert.Contains("ToastScript", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_browser_defaults_to_an_all_view_when_there_is_no_query()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest(null, null));

        var labels = screen.BuildSidebarLabels();

        Assert.Contains(labels, label => label.Contains("ToastedShell /", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("ToastScript /", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_can_collapse_a_sidebar_subgroup()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("grep", null));

        var before = screen.BuildSidebarLabels();
        Assert.Contains(before, label => label.Contains("grep", StringComparison.OrdinalIgnoreCase));

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var after = screen.BuildSidebarLabels();
        Assert.DoesNotContain(after, label => label.Contains("  grep", StringComparison.Ordinal));
        Assert.Contains(after, label => label.StartsWith("▸ ", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_can_browse_the_unified_clr_namespace_tree_and_reach_types()
    {
        var screen = OpenClrBrowser();
        Assert.True(screen.SelectSidebarEntryContaining("System"));

        var labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("▸ System [", StringComparison.Ordinal));

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("Collections [", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("String", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Help_browser_renders_clr_namespaces_as_a_nested_tree_with_collapsed_root_branches()
    {
        var screen = OpenClrBrowser();

        var labels = screen.BuildSidebarLabels();

        Assert.Contains(labels, label => label.Contains("▸ System [", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, label => label.Contains("Collections [", StringComparison.Ordinal));

        Assert.True(screen.SelectSidebarEntryContaining("System"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("▾ System [", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Collections [", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_uses_enter_to_toggle_namespace_branches_and_right_arrow_to_open_detail()
    {
        var screen = OpenClrBrowser();
        Assert.True(screen.SelectSidebarEntryContaining("System"));

        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var expandedLabels = screen.BuildSidebarLabels();
        Assert.Contains(expandedLabels, label => label.Contains("Collections [", StringComparison.Ordinal));
        Assert.Contains(expandedLabels, label => label.Contains("String", StringComparison.OrdinalIgnoreCase));

        Assert.True(screen.SelectSidebarEntryContaining("System"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var collapsedLabels = screen.BuildSidebarLabels();
        Assert.DoesNotContain(collapsedLabels, label => label.Contains("Collections [", StringComparison.Ordinal));
        Assert.DoesNotContain(collapsedLabels, label => label.Contains("String", StringComparison.OrdinalIgnoreCase));

        Assert.True(screen.SelectSidebarEntryContaining("System"));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: false));

        var reopenedLabels = screen.BuildSidebarLabels();
        Assert.Contains(reopenedLabels, label => label.Contains("Collections [", StringComparison.Ordinal));

        var detailLines = screen.BuildDetailLines(100);
        Assert.Contains(detailLines, line => line.Contains("Path: CLR / .NET / System", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_shows_namespace_counts_and_breadcrumbs_in_namespace_detail_pages()
    {
        var screen = OpenClrBrowser();
        Assert.True(screen.SelectSidebarEntryContaining("System"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Path: CLR / .NET / System", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Assemblies Contributing:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Direct Types:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Subtree Types:", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_shows_richer_clr_type_sections()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("System.String", "System.String"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Identity", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Constructors", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Properties & Fields", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Methods", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Shell Helpers", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_can_drill_into_a_clr_type_scope_and_browse_members()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.String"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("Navigation", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("View Options", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Constructors", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Properties & Fields", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Methods", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_can_toggle_declared_only_inside_a_clr_type_scope()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.Exception"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        Assert.True(screen.SelectSidebarEntryContaining("Declared Only: off"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("Declared Only: on", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_groups_clr_method_overloads_inside_type_scope()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.String"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var labels = screen.BuildSidebarLabels();
        Assert.Contains(labels, label => label.Contains("×", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_can_insert_the_selected_topic_into_the_next_prompt()
    {
        var runtime = ToshRuntime.CreateDefault();
        var sink = new RecordingInsertionSink();
        runtime.CommandLineInsertion = sink;
        var screen = new HelpBrowserScreen(runtime, new HelpBrowseRequest(null, "grep"));

        var result = screen.HandleKey(new ConsoleKeyInfo('i', ConsoleKey.I, shift: false, alt: false, control: false));

        Assert.Equal(TuiScreenResult.Exit, result);
        Assert.Equal("grep", Assert.Single(sink.Inserted));
    }

    [Fact]
    public void Help_browser_can_insert_selected_clr_members_into_the_next_prompt()
    {
        var runtime = ToshRuntime.CreateDefault();
        var sink = new RecordingInsertionSink();
        runtime.CommandLineInsertion = sink;
        var screen = new HelpBrowserScreen(runtime, new HelpBrowseRequest(null, null));

        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.F4, shift: false, alt: false, control: false));
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.String"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var labels = screen.BuildSidebarLabels();
        var memberSectionIndex = Array.FindIndex(labels.ToArray(), label => label.Contains("Properties & Fields", StringComparison.Ordinal));
        Assert.True(memberSectionIndex >= 0);

        var memberLabel = labels
            .Skip(memberSectionIndex + 1)
            .First(label => label.StartsWith("  ", StringComparison.Ordinal) && label.Contains(" : ", StringComparison.Ordinal));
        var expectedMemberName = memberLabel.TrimStart()[..memberLabel.TrimStart().IndexOf(" : ", StringComparison.Ordinal)];

        Assert.True(screen.SelectSidebarEntryContaining(memberLabel.Trim()));

        var result = screen.HandleKey(new ConsoleKeyInfo('i', ConsoleKey.I, shift: false, alt: false, control: false));

        Assert.Equal(TuiScreenResult.Exit, result);
        Assert.Equal(expectedMemberName, Assert.Single(sink.Inserted));
    }

    [Fact]
    public void Help_browser_can_follow_base_type_navigation_inside_a_clr_type_scope()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.String"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        Assert.True(screen.SelectSidebarEntryContaining("base: System.Object"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        Assert.Equal("System.Object", screen.CurrentTopicName);
    }

    [Fact]
    public void Help_browser_shows_richer_clr_namespace_sections()
    {
        var screen = OpenClrBrowser();
        Assert.True(screen.SelectSidebarEntryContaining("System"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Assemblies Contributing:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Sample Types", StringComparison.Ordinal));
    }

    [Fact]
    public void Type_catalog_includes_the_current_loaded_runtime_assemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => assembly.FullName ?? assembly.GetName().Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = TypeCatalog.GetAssemblies()
            .Select(assembly => assembly.FullName ?? assembly.GetName().Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(loaded.IsSubsetOf(catalog));
    }

    [Fact]
    public void Help_browser_shows_generic_parameter_sections_for_generic_clr_types()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System", "System.Collections", "System.Collections.Generic");
        Assert.True(screen.SelectSidebarEntryContaining("System.Collections.Generic.List<T>"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.True(screen.SelectSidebarEntryContaining("Navigation"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Generic Parameters", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("T", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_shows_value_type_default_construction_and_factory_methods_for_color()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest("System.Drawing.Color", "System.Drawing.Color"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("System.Drawing.Color()", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Factory Methods", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("FromArgb", StringComparison.Ordinal));
    }

    [Fact]
    public void Help_browser_builds_focused_method_overload_detail_pages_inside_type_scope()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.String"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        Assert.True(screen.SelectSidebarEntryContaining("×"));

        var lines = screen.BuildDetailLines(100);

        Assert.Contains(lines, line => line.Contains("Method Overloads", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Overloads:", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("declared on", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Help_browser_marks_inherited_clr_methods_in_the_sidebar()
    {
        var screen = OpenClrBrowser();
        ExpandClrPath(screen, "System");
        Assert.True(screen.SelectSidebarEntryContaining("System.Exception"));
        screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));

        var labels = screen.BuildSidebarLabels();

        Assert.Contains(labels, label => label.Contains("[from System.Object]", StringComparison.Ordinal));
    }

    private HelpBrowserScreen OpenClrBrowser()
    {
        var screen = new HelpBrowserScreen(fixture.Runtime, new HelpBrowseRequest(null, null));
        screen.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.F4, shift: false, alt: false, control: false));
        return screen;
    }

    private static void ExpandClrPath(HelpBrowserScreen screen, params string[] namespacePath)
    {
        foreach (var namespaceName in namespacePath)
        {
            Assert.True(screen.SelectSidebarEntryContaining(namespaceName));
            screen.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        }
    }

    private sealed class RecordingInsertionSink : ICommandLineInsertionSink
    {
        public List<string> Inserted { get; } = [];

        public bool TryInsertText(string text)
        {
            Inserted.Add(text);
            return true;
        }
    }
}
