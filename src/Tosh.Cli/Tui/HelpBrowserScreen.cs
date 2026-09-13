using System.Reflection;
using System.Text;
using Tosh.Runtime;
using Tosh.Tui.Requests;
using Tosh.Tui;

namespace Tosh.Cli.Tui;

internal sealed partial class HelpBrowserScreen : ITuiScreen
{
    private const int SearchBoxHeight = 4;
    private readonly ToshRuntime _runtime;
    private readonly IReadOnlyList<HelpSummary> _allTopics;
    private readonly TuiListState<HelpBrowserListEntry> _sidebar = new();
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private readonly HashSet<string> _collapsedSections = new(StringComparer.OrdinalIgnoreCase);
    private string _query;
    private string? _currentTopicName;
    private HelpBrowserGroup _activeGroup;
    private HelpBrowserFocus _focus;
    private bool _shouldExit;
    private string? _filteredTopicsCacheQuery;
    private IReadOnlyList<HelpSummary>? _filteredTopicsCache;
    private string? _sidebarEntriesCacheKey;
    private IReadOnlyList<HelpBrowserListEntry>? _sidebarEntriesCache;
    private string? _resolvedTopicCacheName;
    private HelpTopic? _resolvedTopicCache;
    private string? _detailEntriesCacheKey;
    private IReadOnlyList<HelpDetailEntry>? _detailEntriesCache;
    private readonly TuiShortcuts _shortcuts;

    public HelpBrowserScreen(ToshRuntime runtime, HelpBrowseRequest request)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(request);
        _allTopics = HelpCatalog.BuildSummaries(runtime);
        _query = request.InitialQuery ?? string.Empty;
        _activeGroup = HelpBrowserGroup.All;

        if (string.IsNullOrWhiteSpace(request.InitialTopicName) &&
            !string.IsNullOrWhiteSpace(_query) &&
            TryDetermineBestGroupForQuery(_query, out var initialGroup))
        {
            _activeGroup = initialGroup;
        }

        _focus = _query.Length > 0 ? HelpBrowserFocus.Search : HelpBrowserFocus.List;

        // The browser's own keys, asked about only once the focused pane has declined.
        // Matched on the character for `/`, `[` and `]`, because the `ConsoleKey` a
        // terminal reports for punctuation is not the one the Oem names suggest: .NET
        // hands back `Divide` for `/` and `None` for both brackets, so the three cases
        // written against `Oem2`, `Oem4` and `Oem6` could never fire outside a test that
        // built its own key info. Search, back and forward were dead keys.
        // Registered in the order the footer should read them, because the footer is
        // generated from this table rather than written out again beside it.
        _shortcuts = new TuiShortcuts()
            .On('/', "/", "search", () => _focus = HelpBrowserFocus.Search)
            .On('[', "[", "back", () => NavigateBack())
            .On(']', "]", "forward", () => NavigateForward())
            .On(ConsoleKey.Q, "q", "quit", Quit)
            .On(ConsoleKey.Escape, "Esc", string.Empty, Quit);

        BuildTree();
        ApplyFilter(pageSize: 10);

        if (!string.IsNullOrWhiteSpace(request.InitialTopicName))
        {
            var topic = HelpCatalog.ResolveTopic(runtime, request.InitialTopicName!);
            if (topic is not null)
            {
                _activeGroup = DetermineGroup(topic);
                if (_activeGroup != HelpBrowserGroup.Clr)
                {
                    _clrAssemblyScope = null;
                    _clrNamespaceScope = null;
                    _clrTypeScope = null;
                }
            }

            SelectTopicByName(request.InitialTopicName!);
            _currentTopicName = request.InitialTopicName;
        }
        else if (_sidebar.TryGetSelected(out var selected) && selected.TopicName is not null)
        {
            _currentTopicName = selected.TopicName;
        }
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
        {
            return HandleKey(input.Key);
        }

        var mouse = input.Mouse;

        // Asked of the arrangement rather than of rectangles saved during the last render.
        // A pane that has been laid out knows where it is, and a browser that keeps its own
        // copy of that has two answers to one question.
        if (_headerFrame.Bounds.Contains(mouse.Column, mouse.Row))
        {
            if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
            {
                _focus = HelpBrowserFocus.Search;
            }

            return TuiScreenResult.Continue;
        }

        if (_listFrame.Bounds.Contains(mouse.Column, mouse.Row))
        {
            // The wheel moves the choice rather than the view: the point of the sidebar is
            // to land on a topic, and the list keeps whatever it lands on in sight itself.
            if (mouse.Action == TuiMouseAction.Scroll)
            {
                if (mouse.Button == TuiMouseButton.ScrollUp)
                {
                    MoveSidebarPrevious();
                }
                else
                {
                    MoveSidebarNext();
                }

                return TuiScreenResult.Continue;
            }

            if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
            {
                _focus = HelpBrowserFocus.List;

                // The list converts the click through its own offset and tells the model.
                _sidebarList.OnInput(input);
            }

            return TuiScreenResult.Continue;
        }

        if (_detailFrame.Bounds.Contains(mouse.Column, mouse.Row))
        {
            if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
            {
                _focus = HelpBrowserFocus.Detail;
            }

            _detailLines.OnInput(input);
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        if (_shouldExit)
        {
            return TuiScreenResult.Exit;
        }

        // Whatever holds the keyboard answers first. A search box consumes `q` as a
        // letter, so the browser's own keys never see it — which is the whole of
        // `TOSH-0011`, and is now a property of the order rather than of two `if`s
        // somebody has to keep in the right sequence.
        if (_focus == HelpBrowserFocus.Search && HandleSearchKey(key))
        {
            return _shouldExit ? TuiScreenResult.Exit : TuiScreenResult.Continue;
        }

        if (_shortcuts.TryHandle(key, out var shortcut))
        {
            return shortcut;
        }

        switch (key.Key)
        {
            case ConsoleKey.F1:
                SelectGroup(HelpBrowserGroup.All);
                return TuiScreenResult.Continue;
            case ConsoleKey.F2:
                SelectGroup(HelpBrowserGroup.ToastedShell);
                return TuiScreenResult.Continue;
            case ConsoleKey.F3:
                SelectGroup(HelpBrowserGroup.ToastScript);
                return TuiScreenResult.Continue;
            case ConsoleKey.F4:
                SelectGroup(HelpBrowserGroup.Clr);
                return TuiScreenResult.Continue;
            case ConsoleKey.Tab:
                CycleFocus(reverse: key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                return TuiScreenResult.Continue;
            case ConsoleKey.LeftArrow:
                if (_focus == HelpBrowserFocus.List && NavigateClrUp())
                {
                    return TuiScreenResult.Continue;
                }

                _focus = HelpBrowserFocus.List;
                return TuiScreenResult.Continue;
            case ConsoleKey.RightArrow:
                if (_focus == HelpBrowserFocus.List)
                {
                    if (ActivateSelectedEntry(preferOpen: true))
                    {
                        _focus = HelpBrowserFocus.Detail;
                    }

                    return TuiScreenResult.Continue;
                }

                _focus = HelpBrowserFocus.Detail;
                return TuiScreenResult.Continue;
            case ConsoleKey.Backspace:
                if (_focus == HelpBrowserFocus.List && NavigateClrUp())
                {
                    return TuiScreenResult.Continue;
                }

                break;
            case ConsoleKey.Insert or ConsoleKey.I:
                if (_focus != HelpBrowserFocus.Search && TryInsertCurrentSelection())
                {
                    _shouldExit = true;
                    return TuiScreenResult.Exit;
                }

                break;
        }

        return _focus switch
        {
            HelpBrowserFocus.List => HandleListKey(key),
            HelpBrowserFocus.Detail => HandleDetailKey(key),
            _ => TuiScreenResult.Continue,
        };
    }

    internal IReadOnlyList<HelpSummary> FilterTopics()
    {
        if (string.Equals(_filteredTopicsCacheQuery, _query, StringComparison.Ordinal) &&
            _filteredTopicsCache is not null)
        {
            return _filteredTopicsCache;
        }

        IReadOnlyList<HelpSummary> results = string.IsNullOrWhiteSpace(_query)
            ? _allTopics
            : _allTopics
                .Where(summary =>
                    summary.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    summary.Category.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    summary.Description.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    summary.Usage.Contains(_query, StringComparison.OrdinalIgnoreCase) ||
                    summary.Aliases.Any(alias => alias.Contains(_query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

        _filteredTopicsCacheQuery = _query;
        _filteredTopicsCache = results;
        return results;
    }

    internal IReadOnlyList<string> BuildDetailLines(int width)
    {
        return BuildDetailEntries(width)
            .Select(entry => entry.Text)
            .ToArray();
    }

    internal IReadOnlyList<string> BuildSidebarLabels()
    {
        return BuildSidebarEntries()
            .Select(entry => entry.Label)
            .ToArray();
    }

    internal string? CurrentTopicName => _currentTopicName;

    internal string? GetSelectedInsertionText()
    {
        if (_sidebar.TryGetSelected(out var selected))
        {
            return selected.Kind switch
            {
                HelpBrowserListEntryKind.Topic => selected.TopicName,
                HelpBrowserListEntryKind.ClrType => selected.Value,
                HelpBrowserListEntryKind.ClrAssembly => selected.Value,
                HelpBrowserListEntryKind.ClrNamespace => selected.Value,
                HelpBrowserListEntryKind.ClrMethod => BuildMethodInsertionText(selected.Value),
                HelpBrowserListEntryKind.ClrMember => ExtractClrMemberInsertionText(selected.Value),
                HelpBrowserListEntryKind.ClrConstructor => BuildConstructorInsertionText(selected.Value),
                _ => ResolveCurrentTopic()?.Name,
            };
        }

        return ResolveCurrentTopic()?.Name;
    }

    internal bool SelectSidebarEntryContaining(string text)
    {
        var index = BuildSidebarEntries()
            .Select((entry, entryIndex) => new { entry, entryIndex })
            .FirstOrDefault(item =>
                item.entry.Label.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.entry.RawLabel.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (item.entry.Value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            ?.entryIndex ?? -1;

        if (index < 0)
        {
            return false;
        }

        SyncSidebar(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
        return _sidebar.SelectIndex(index);
    }

    internal bool SelectSidebarEntryBySectionKey(string sectionKey)
    {
        var index = BuildSidebarEntries()
            .Select((entry, entryIndex) => new { entry, entryIndex })
            .FirstOrDefault(item => string.Equals(item.entry.SectionKey, sectionKey, StringComparison.OrdinalIgnoreCase))
            ?.entryIndex ?? -1;

        if (index < 0)
        {
            return false;
        }

        SyncSidebar(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
        return _sidebar.SelectIndex(index);
    }

    internal IReadOnlyList<HelpDetailEntry> BuildDetailEntries(int width)
    {
        var key = BuildDetailCacheKey(width);
        if (string.Equals(_detailEntriesCacheKey, key, StringComparison.Ordinal) &&
            _detailEntriesCache is not null)
        {
            return _detailEntriesCache;
        }

        IReadOnlyList<HelpDetailEntry> lines;
        var topic = ResolveCurrentTopic();

        if (_sidebar.TryGetSelected(out var selected) && ShouldPreferContextDetail(selected))
        {
            lines = BuildContextDetailEntries(selected, width);
        }
        else if (topic is not null)
        {
            lines = BuildTopicDetailEntries(topic, width);
        }
        else if (_sidebar.TryGetSelected(out selected))
        {
            lines = BuildContextDetailEntries(selected, width);
        }
        else
        {
            lines = [new HelpDetailEntry("No help topics matched the current query.", HelpDetailEntryKind.Meta)];
        }

        _detailEntriesCacheKey = key;
        _detailEntriesCache = lines;
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildTopicDetailEntries(HelpTopic topic, int width)
    {
        if (string.Equals(topic.Category, "CLR", StringComparison.OrdinalIgnoreCase) &&
            TryResolveClrType(topic, out var clrType))
        {
            return BuildClrTypeDetailEntries(topic, clrType, width);
        }

        var lines = new List<HelpDetailEntry>
        {
            new($"{topic.Kind} • {topic.Category}", HelpDetailEntryKind.Meta),
        };

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Description, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Usage", HelpDetailEntryKind.SectionHeading));
        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Usage, width, "  ", "    ")
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Example)));

        if (topic.Arguments?.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Arguments", HelpDetailEntryKind.SectionHeading));

            foreach (var argument in topic.Arguments)
            {
                var header = argument.Required ? argument.Name : $"{argument.Name} (optional)";
                if (!string.IsNullOrWhiteSpace(argument.TypeName))
                {
                    header += $" : {argument.TypeName}";
                }

                lines.Add(new($"  {header}", HelpDetailEntryKind.Meta));
                lines.AddRange(TextDocumentFormatter.WrapParagraph(argument.Description, width, "    ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }

        if (topic.Options?.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Options", HelpDetailEntryKind.SectionHeading));

            foreach (var option in topic.Options)
            {
                lines.Add(new($"  {option.Syntax}", HelpDetailEntryKind.Meta));
                lines.AddRange(TextDocumentFormatter.WrapParagraph(option.Description, width, "    ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }

        if (topic.PipelineInput is not null)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Pipeline Input", HelpDetailEntryKind.SectionHeading));
            lines.Add(new($"  object: {TuiRenderHelpers.FormatBoolean(topic.PipelineInput.Object)}  scalar: {TuiRenderHelpers.FormatBoolean(topic.PipelineInput.Scalar)}  path-like: {TuiRenderHelpers.FormatBoolean(topic.PipelineInput.PathLike)}  collection: {TuiRenderHelpers.FormatBoolean(topic.PipelineInput.Collection)}", HelpDetailEntryKind.Meta));

            if (!string.IsNullOrWhiteSpace(topic.PipelineInput.Notes))
            {
                lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.PipelineInput.Notes!, width, "    ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }

        if (!string.IsNullOrWhiteSpace(topic.Output))
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Output", HelpDetailEntryKind.SectionHeading));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Output, width, "  ", "    ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        if (topic.Aliases.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Aliases", HelpDetailEntryKind.SectionHeading));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(string.Join(", ", topic.Aliases), width, "  ", "  ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        var examples = topic.ExampleItems?.Count > 0
            ? topic.ExampleItems
            : topic.Examples.Select(example => new HelpExample(example)).ToArray();

        if (examples.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Examples", HelpDetailEntryKind.SectionHeading));

            foreach (var example in examples)
            {
                if (!string.IsNullOrWhiteSpace(example.Title))
                {
                    lines.Add(new($"  {example.Title}", HelpDetailEntryKind.Meta));
                }

                lines.AddRange(TextDocumentFormatter.WrapParagraph(example.Code, width, "    ", "      ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Example)));

                if (!string.IsNullOrWhiteSpace(example.Description))
                {
                    lines.AddRange(TextDocumentFormatter.WrapParagraph(example.Description!, width, "      ", "      ")
                        .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(topic.Notes))
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Notes", HelpDetailEntryKind.SectionHeading));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Notes!, width, "  ", "    ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        if (topic.Related.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Related", HelpDetailEntryKind.SectionHeading));

            for (var index = 0; index < topic.Related.Count; index += 1)
            {
                lines.Add(new($"  [{index + 1}] {topic.Related[index]}", HelpDetailEntryKind.RelatedTopic, index + 1));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildContextDetailEntries(HelpBrowserListEntry selected, int width)
    {
        return selected.Kind switch
        {
            HelpBrowserListEntryKind.SectionHeader when _clrTypeScope is not null => BuildClrTypeScopeDetailEntries(width),
            HelpBrowserListEntryKind.SectionHeader => BuildSectionDetailEntries(selected, width),
            HelpBrowserListEntryKind.ClrAssembly => BuildClrAssemblyDetailEntries(selected.Value!, width),
            HelpBrowserListEntryKind.ClrNamespace => BuildClrNamespaceDetailEntries(selected.Value!, width),
            HelpBrowserListEntryKind.Up when _clrTypeScope is not null => BuildClrTypeScopeDetailEntries(width),
            HelpBrowserListEntryKind.Up => BuildClrScopeDetailEntries(width),
            HelpBrowserListEntryKind.ClrFilterToggle => BuildClrFilterDetailEntries(width),
            HelpBrowserListEntryKind.ClrConstructor => BuildClrConstructorDetailEntries(selected.Value!, width),
            HelpBrowserListEntryKind.ClrMember => BuildClrMemberDetailEntries(selected.Value!, width),
            HelpBrowserListEntryKind.ClrMethod => BuildClrMethodDetailEntries(selected.Value!, width),
            _ => [new HelpDetailEntry("Select a topic to view its full help page.", HelpDetailEntryKind.Meta)],
        };
    }

    private IReadOnlyList<HelpDetailEntry> BuildSectionDetailEntries(HelpBrowserListEntry selected, int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new("Section", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };
        var action = selected.IsCollapsed ? "collapsed" : "expanded";
        lines.AddRange(TextDocumentFormatter.WrapParagraph($"{selected.RawLabel} is currently {action}. Press Enter to toggle it.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private static bool ShouldPreferContextDetail(HelpBrowserListEntry selected)
    {
        return selected.Kind is
            HelpBrowserListEntryKind.SectionHeader or
            HelpBrowserListEntryKind.ClrAssembly or
            HelpBrowserListEntryKind.ClrNamespace or
            HelpBrowserListEntryKind.Up or
            HelpBrowserListEntryKind.ClrFilterToggle or
            HelpBrowserListEntryKind.ClrConstructor or
            HelpBrowserListEntryKind.ClrMember or
            HelpBrowserListEntryKind.ClrMethod;
    }

    private TuiScreenResult HandleListKey(ConsoleKeyInfo key)
    {
        _ = key.Key switch
        {
            ConsoleKey.UpArrow => MoveSidebarPrevious(),
            ConsoleKey.DownArrow => MoveSidebarNext(),
            ConsoleKey.PageUp => MoveSidebarPage(-1),
            ConsoleKey.PageDown => MoveSidebarPage(1),
            ConsoleKey.Home => MoveSidebarHome(),
            ConsoleKey.End => MoveSidebarEnd(),
            ConsoleKey.Enter => ActivateSelectedEntry(preferOpen: false),
            _ => false,
        };

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleDetailKey(ConsoleKeyInfo key)
    {
        if (char.IsDigit(key.KeyChar))
        {
            var relatedIndex = (int)char.GetNumericValue(key.KeyChar);
            if (relatedIndex >= 1 && OpenRelatedTopic(relatedIndex))
            {
                return TuiScreenResult.Continue;
            }
        }

        // The pane keeps its own offset, so scrolling is asking it to move rather than
        // driving a scroll state alongside it and hoping the two agree (`TUI-0007`).
        _detailLines.OnInput(TuiInputEvent.FromKey(key));

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult Quit()
    {
        _shouldExit = true;
        return TuiScreenResult.Exit;
    }

    private bool HandleSearchKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = HelpBrowserFocus.List;
                return true;
            case ConsoleKey.Enter:
                _focus = HelpBrowserFocus.List;
                return true;
            case ConsoleKey.Backspace:
                if (_query.Length > 0)
                {
                    _query = _query[..^1];
                    _collapsedSections.Clear();
                    _expandedClrNamespaces.Clear();
                    InvalidateDerivedCaches();
                    ApplyFilter(pageSize: _sidebar.Scroll.PageSize);
                }

                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _query += key.KeyChar;
            _collapsedSections.Clear();
            _expandedClrNamespaces.Clear();
            InvalidateDerivedCaches();
            ApplyFilter(pageSize: _sidebar.Scroll.PageSize);
            return true;
        }

        return false;
    }

    private void ApplyFilter(int pageSize)
    {
        SyncSidebar(Math.Max(1, pageSize));
        EnsureSidebarSelection();
        _detailLines.Offset = 0;
    }

    private void SyncSidebar(int pageSize)
    {
        _sidebar.SetItems(BuildSidebarEntries(), Math.Max(1, pageSize));
    }

    private void SelectTopicByName(string topicName)
    {
        var entries = BuildSidebarEntries();
        var matchIndex = entries
            .Select((entry, index) => new { entry, index })
            .FirstOrDefault(item => string.Equals(item.entry.TopicName, topicName, StringComparison.OrdinalIgnoreCase))
            ?.index ?? -1;

        if (matchIndex < 0)
        {
            ExpandAllSections();
            entries = BuildSidebarEntries();
            matchIndex = entries
                .Select((entry, index) => new { entry, index })
                .FirstOrDefault(item => string.Equals(item.entry.TopicName, topicName, StringComparison.OrdinalIgnoreCase))
                ?.index ?? -1;
        }

        if (matchIndex < 0)
        {
            return;
        }

        SyncSidebar(Math.Max(1, _sidebar.Scroll.PageSize));
        _sidebar.SelectIndex(matchIndex);
    }

    private IReadOnlyList<HelpBrowserListEntry> BuildSidebarEntries()
    {
        var key = BuildSidebarCacheKey();
        if (string.Equals(_sidebarEntriesCacheKey, key, StringComparison.Ordinal) &&
            _sidebarEntriesCache is not null)
        {
            return _sidebarEntriesCache;
        }

        IReadOnlyList<HelpBrowserListEntry> entries = _activeGroup == HelpBrowserGroup.Clr
            ? BuildClrSidebarEntriesCore()
            : BuildGroupedHelpSidebarEntriesCore();

        _sidebarEntriesCacheKey = key;
        _sidebarEntriesCache = entries;
        return entries;
    }

    private IReadOnlyList<HelpBrowserListEntry> BuildGroupedHelpSidebarEntriesCore()
    {
        var summaries = FilterTopics()
            .Where(summary => _activeGroup == HelpBrowserGroup.All || DetermineGroup(summary) == _activeGroup)
            .ToArray();

        var entries = new List<HelpBrowserListEntry>();
        var sections = summaries
            .GroupBy(summary => BuildSectionDescriptor(summary))
            .OrderBy(group => group.Key.GroupOrder)
            .ThenBy(group => group.Key.SectionOrder)
            .ThenBy(group => group.Key.Label, StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            var collapsed = _collapsedSections.Contains(section.Key.Key);
            entries.Add(HelpBrowserListEntry.SectionHeader(section.Key.Label, section.Key.Key, collapsed));

            if (collapsed)
            {
                continue;
            }

            foreach (var summary in section.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(HelpBrowserListEntry.Topic(summary));
            }
        }

        return entries;
    }

    private SectionDescriptor BuildSectionDescriptor(HelpSummary summary)
    {
        var group = DetermineGroup(summary);
        var subgroup = DetermineSubgroup(summary);

        return _activeGroup == HelpBrowserGroup.All
            ? new SectionDescriptor(
                Key: $"{group}:{subgroup}",
                Label: $"{GetGroupTitle(group)} / {subgroup}",
                GroupOrder: GetGroupOrder(group),
                SectionOrder: GetSubgroupOrder(subgroup))
            : new SectionDescriptor(
                Key: subgroup,
                Label: subgroup,
                GroupOrder: 0,
                SectionOrder: GetSubgroupOrder(subgroup));
    }

    private void CycleFocus(bool reverse)
    {
        _focus = (reverse, _focus) switch
        {
            (false, HelpBrowserFocus.Search) => HelpBrowserFocus.List,
            (false, HelpBrowserFocus.List) => HelpBrowserFocus.Detail,
            (false, HelpBrowserFocus.Detail) => HelpBrowserFocus.Search,
            (true, HelpBrowserFocus.Search) => HelpBrowserFocus.Detail,
            (true, HelpBrowserFocus.Detail) => HelpBrowserFocus.List,
            _ => HelpBrowserFocus.Search,
        };
    }

    private void SelectGroup(HelpBrowserGroup group)
    {
        if (_activeGroup == group)
        {
            return;
        }

        _activeGroup = group;
        _expandedClrNamespaces.Clear();
        if (group != HelpBrowserGroup.Clr)
        {
            _clrAssemblyScope = null;
            _clrNamespaceScope = null;
            _clrTypeScope = null;
        }
        InvalidateDerivedCaches();
        ApplyFilter(pageSize: _sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);

        var current = ResolveCurrentTopic();
        if (current is not null && group != HelpBrowserGroup.All && DetermineGroup(current) != group)
        {
            _currentTopicName = null;
            InvalidateDetailCache();
        }
    }

    private bool MoveSidebarPrevious()
    {
        return _sidebar.MovePrevious();
    }

    private bool MoveSidebarNext()
    {
        return _sidebar.MoveNext();
    }

    private bool MoveSidebarPage(int direction)
    {
        return direction < 0 ? _sidebar.PageUp() : _sidebar.PageDown();
    }

    private bool MoveSidebarHome()
    {
        return _sidebar.Home();
    }

    private bool MoveSidebarEnd()
    {
        return _sidebar.End();
    }

    private void EnsureSidebarSelection()
    {
        if (_sidebar.Items.Count == 0)
        {
            return;
        }

        _sidebar.SelectIndex(Math.Clamp(_sidebar.SelectedIndex, 0, _sidebar.Items.Count - 1));
    }

    private bool ActivateSelectedEntry(bool preferOpen)
    {
        if (!_sidebar.TryGetSelected(out var selected))
        {
            return false;
        }

        switch (selected.Kind)
        {
            case HelpBrowserListEntryKind.SectionHeader:
                ToggleSection(selected.SectionKey!);
                return false;
            case HelpBrowserListEntryKind.Up:
                NavigateClrUp();
                return false;
            case HelpBrowserListEntryKind.ClrAssembly:
                _clrAssemblyScope = selected.Value;
                _clrNamespaceScope = null;
                _clrTypeScope = null;
                _currentTopicName = null;
                InvalidateDerivedCaches();
                ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
                return false;
            case HelpBrowserListEntryKind.ClrNamespace:
                if (!preferOpen && ClrNamespaceHasTreeChildren(selected.Value!))
                {
                    ToggleClrNamespaceExpansion(selected.Value!);
                    return false;
                }

                if (preferOpen)
                {
                    ExpandClrNamespace(selected.Value!);
                    return true;
                }

                return false;
            case HelpBrowserListEntryKind.ClrType:
                if (selected.Value is not null && ResolveClrTypeByDisplayName(selected.Value) is { } linkedType)
                {
                    OpenClrTypeScope(linkedType);
                }
                return false;
            case HelpBrowserListEntryKind.Topic:
                _clrTypeScope = null;
                return OpenTopic(selected.TopicName!, pushHistory: true);
            case HelpBrowserListEntryKind.ClrFilterToggle:
                _clrDeclaredOnly = !_clrDeclaredOnly;
                InvalidateDerivedCaches();
                ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
                return false;
            case HelpBrowserListEntryKind.ClrConstructor:
            case HelpBrowserListEntryKind.ClrMember:
            case HelpBrowserListEntryKind.ClrMethod:
                _focus = HelpBrowserFocus.Detail;
                InvalidateDetailCache();
                return false;
            default:
                return false;
        }
    }

    private void ToggleSection(string sectionKey)
    {
        if (_collapsedSections.Contains(sectionKey))
        {
            _collapsedSections.Remove(sectionKey);
        }
        else
        {
            _collapsedSections.Add(sectionKey);
        }

        InvalidateSidebarCache();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
        SelectSidebarEntryBySectionKey(sectionKey);
    }

    private static Type UnwrapByRef(Type type) => type.IsByRef ? type.GetElementType() ?? type : type;

    private static HelpBrowserGroup DetermineGroup(HelpSummary summary)
    {
        if (summary.Kind == HelpSubjectKind.Language ||
            summary.Category is "Language" or "Control Flow" or "Interop" or "Shell Types")
        {
            return HelpBrowserGroup.ToastScript;
        }

        if (summary.Category == "CLR")
        {
            return HelpBrowserGroup.Clr;
        }

        return HelpBrowserGroup.ToastedShell;
    }

    private static HelpBrowserGroup DetermineGroup(HelpTopic topic)
    {
        if (topic.Kind == HelpSubjectKind.Language ||
            topic.Category is "Language" or "Control Flow" or "Interop" or "Shell Types")
        {
            return HelpBrowserGroup.ToastScript;
        }

        if (topic.Category == "CLR")
        {
            return HelpBrowserGroup.Clr;
        }

        return HelpBrowserGroup.ToastedShell;
    }

    private static string DetermineSubgroup(HelpSummary summary)
    {
        return DetermineGroup(summary) switch
        {
            HelpBrowserGroup.ToastScript when summary.Category == "Shell Types" => "Types",
            HelpBrowserGroup.ToastScript => summary.Category,
            HelpBrowserGroup.Clr => summary.Category == "CLR" ? "Commands" : summary.Category,
            _ => summary.Category,
        };
    }

    private static int GetSubgroupOrder(string subgroup)
    {
        return subgroup switch
        {
            "Shell" => 0,
            "Filesystem" => 1,
            "Text" => 2,
            "Process" => 3,
            "Data" => 4,
            "System" => 5,
            "Prompt" => 6,
            "Pipeline" => 7,
            "Language" => 0,
            "Control Flow" => 1,
            "Types" => 2,
            "Interop" => 3,
            "Commands" => 0,
            "Assemblies" => 1,
            _ => 100,
        };
    }

    private static int GetGroupOrder(HelpBrowserGroup group)
    {
        return group switch
        {
            HelpBrowserGroup.ToastedShell => 0,
            HelpBrowserGroup.ToastScript => 1,
            HelpBrowserGroup.Clr => 2,
            _ => 0,
        };
    }

    private static string GetGroupTitle(HelpBrowserGroup group)
    {
        return group switch
        {
            HelpBrowserGroup.All => "All",
            HelpBrowserGroup.ToastScript => "ToastScript",
            HelpBrowserGroup.Clr => "CLR / .NET",
            _ => "ToastedShell",
        };
    }

    private static string BuildGroupLabel(HelpBrowserGroup group) =>
        group switch
        {
            HelpBrowserGroup.All => "F1 All",
            HelpBrowserGroup.ToastedShell => "F2 ToastedShell",
            HelpBrowserGroup.ToastScript => "F3 ToastScript",
            HelpBrowserGroup.Clr => "F4 CLR/.NET",
            _ => "F1 All",
        };

    private bool TryDetermineBestGroupForQuery(string query, out HelpBrowserGroup group)
    {
        var best = HelpCatalog.Search(_runtime, query, maxResults: 1).FirstOrDefault();
        if (best is not null)
        {
            var topic = HelpCatalog.ResolveTopic(_runtime, best.Name);
            if (topic is not null)
            {
                group = DetermineGroup(topic);
                return true;
            }
        }

        group = HelpBrowserGroup.All;
        return false;
    }

    private bool TryInsertCurrentSelection()
    {
        var sink = _runtime.CommandLineInsertion;
        var text = GetSelectedInsertionText();
        return sink is not null &&
               !string.IsNullOrWhiteSpace(text) &&
               sink.TryInsertText(text);
    }

    private bool OpenRelatedTopic(int relatedIndex)
    {
        var topic = ResolveCurrentTopic();

        if (topic is null || relatedIndex < 1 || relatedIndex > topic.Related.Count)
        {
            return false;
        }

        return OpenTopic(topic.Related[relatedIndex - 1], pushHistory: true);
    }

    private bool OpenTopic(string topicName, bool pushHistory)
    {
        var resolved = HelpCatalog.ResolveTopic(_runtime, topicName);

        if (resolved is null)
        {
            return false;
        }

        if (pushHistory &&
            !string.IsNullOrWhiteSpace(_currentTopicName) &&
            !string.Equals(_currentTopicName, resolved.Name, StringComparison.OrdinalIgnoreCase))
        {
            _backHistory.Push(_currentTopicName!);
            _forwardHistory.Clear();
        }

        _currentTopicName = resolved.Name;
        _activeGroup = DetermineGroup(resolved);
        if (_activeGroup != HelpBrowserGroup.Clr)
        {
            _clrAssemblyScope = null;
            _clrNamespaceScope = null;
            _clrTypeScope = null;
        }

        SelectTopicByName(resolved.Name);
        _detailLines.Offset = 0;
        InvalidateDetailCache();
        return true;
    }

    private bool NavigateBack()
    {
        if (_backHistory.Count == 0 || string.IsNullOrWhiteSpace(_currentTopicName))
        {
            return false;
        }

        _forwardHistory.Push(_currentTopicName!);
        _currentTopicName = _backHistory.Pop();
        var topic = HelpCatalog.ResolveTopic(_runtime, _currentTopicName!);
        if (topic is not null)
        {
            _activeGroup = DetermineGroup(topic);
        }

        SelectTopicByName(_currentTopicName);
        _detailLines.Offset = 0;
        _focus = HelpBrowserFocus.Detail;
        InvalidateDetailCache();
        return true;
    }

    private bool NavigateForward()
    {
        if (_forwardHistory.Count == 0 || string.IsNullOrWhiteSpace(_currentTopicName))
        {
            return false;
        }

        _backHistory.Push(_currentTopicName!);
        _currentTopicName = _forwardHistory.Pop();
        var topic = HelpCatalog.ResolveTopic(_runtime, _currentTopicName!);
        if (topic is not null)
        {
            _activeGroup = DetermineGroup(topic);
        }

        SelectTopicByName(_currentTopicName);
        _detailLines.Offset = 0;
        _focus = HelpBrowserFocus.Detail;
        InvalidateDetailCache();
        return true;
    }

    private HelpTopic? ResolveCurrentTopic()
    {
        if (string.IsNullOrWhiteSpace(_currentTopicName))
        {
            _resolvedTopicCacheName = null;
            _resolvedTopicCache = null;
            return null;
        }

        if (string.Equals(_resolvedTopicCacheName, _currentTopicName, StringComparison.OrdinalIgnoreCase))
        {
            return _resolvedTopicCache;
        }

        _resolvedTopicCacheName = _currentTopicName;
        _resolvedTopicCache = HelpCatalog.ResolveTopic(_runtime, _currentTopicName!);
        return _resolvedTopicCache;
    }

    private string GetDetailTitle()
    {
        if (_sidebar.TryGetSelected(out var selected) && ShouldPreferContextDetail(selected))
        {
            return selected.Kind switch
            {
                HelpBrowserListEntryKind.ClrAssembly => $"Assembly: {selected.Value}",
                HelpBrowserListEntryKind.ClrNamespace => $"Namespace: {selected.Value}",
                HelpBrowserListEntryKind.ClrFilterToggle => "View Options",
                HelpBrowserListEntryKind.ClrConstructor => "Constructor",
                HelpBrowserListEntryKind.ClrMember => "Member",
                HelpBrowserListEntryKind.ClrMethod => "Method Overloads",
                HelpBrowserListEntryKind.SectionHeader when _clrTypeScope is not null => $"Type: {_clrTypeScope}",
                HelpBrowserListEntryKind.Up when _clrTypeScope is not null => $"Type: {_clrTypeScope}",
                HelpBrowserListEntryKind.SectionHeader => selected.RawLabel,
                HelpBrowserListEntryKind.Up => "CLR / .NET",
                _ => "Topic",
            };
        }

        var currentTopic = ResolveCurrentTopic();
        if (currentTopic is not null)
        {
            return $"{currentTopic.Name} [{currentTopic.Kind}]";
        }

        if (!_sidebar.TryGetSelected(out selected))
        {
            return "Topic";
        }

        return selected.Kind switch
        {
            HelpBrowserListEntryKind.ClrAssembly => $"Assembly: {selected.Value}",
            HelpBrowserListEntryKind.ClrNamespace => $"Namespace: {selected.Value}",
            HelpBrowserListEntryKind.SectionHeader => selected.RawLabel,
            HelpBrowserListEntryKind.Up => "CLR / .NET",
            _ => "Topic",
        };
    }

    private IReadOnlyList<Type> FilterTypesForQuery(IReadOnlyList<Type> types)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            return types;
        }

        return types
            .Where(type => MatchesQuery(type.Name, type.FullName ?? string.Empty, ReflectionMetadataUtilities.GetDisplayName(type)))
            .ToArray();
    }

    private bool MatchesQuery(params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            return true;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                value.Contains(_query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ExpandAllSections()
    {
        if (_collapsedSections.Count == 0)
        {
            return;
        }

        _collapsedSections.Clear();
        InvalidateSidebarCache();
    }

    private void InvalidateDerivedCaches()
    {
        _filteredTopicsCacheQuery = null;
        _filteredTopicsCache = null;
        InvalidateSidebarCache();
        InvalidateDetailCache();
    }

    private void InvalidateSidebarCache()
    {
        _sidebarEntriesCacheKey = null;
        _sidebarEntriesCache = null;
    }

    private void InvalidateDetailCache()
    {
        _resolvedTopicCacheName = null;
        _resolvedTopicCache = null;
        _detailEntriesCacheKey = null;
        _detailEntriesCache = null;
    }

    private string BuildSidebarCacheKey()
    {
        var collapsed = _collapsedSections.Count == 0
            ? string.Empty
            : string.Join("|", _collapsedSections.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        var expandedClr = _expandedClrNamespaces.Count == 0
            ? string.Empty
            : string.Join("|", _expandedClrNamespaces.OrderBy(item => item, StringComparer.Ordinal));
        return $"{_activeGroup}|{_query}|asm:{_clrAssemblyScope}|ns:{_clrNamespaceScope}|type:{_clrTypeScope}|declared:{_clrDeclaredOnly}|collapsed:{collapsed}|expanded-clr:{expandedClr}";
    }

    private string BuildDetailCacheKey(int width)
    {
        var selectedIdentity = _sidebar.TryGetSelected(out var selected)
            ? $"{selected.Kind}:{selected.TopicName}:{selected.SectionKey}:{selected.Value}:{selected.IsCollapsed}"
            : "<none>";
        return $"{width}|topic:{_currentTopicName}|{selectedIdentity}";
    }

    private IEnumerable<(string Text, ToshTextStyleConfig Style)> BuildSidebarContentSegments(
        HelpBrowserListEntry item,
        bool isSelected,
        ToshTuiThemeConfig theme)
    {
        switch (item.Kind)
        {
            case HelpBrowserListEntryKind.SectionHeader:
                yield return (item.Label, theme.SectionHeading);
                yield break;
            case HelpBrowserListEntryKind.Up:
                yield return (item.Label, TuiRenderHelpers.MergeListStyles(theme.Meta, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            case HelpBrowserListEntryKind.ClrNamespace:
                foreach (var segment in BuildClrNamespaceSegments(item, isSelected, theme))
                {
                    yield return segment;
                }

                yield break;
            case HelpBrowserListEntryKind.ClrType:
                foreach (var segment in BuildClrTypeSegments(item, isSelected, theme))
                {
                    yield return segment;
                }

                yield break;
            case HelpBrowserListEntryKind.ClrConstructor:
                yield return (item.Label, TuiRenderHelpers.MergeListStyles(theme.Constructor, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            case HelpBrowserListEntryKind.ClrMember:
                yield return (item.Label, TuiRenderHelpers.MergeListStyles(theme.Property, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            case HelpBrowserListEntryKind.ClrMethod:
                yield return (item.Label, TuiRenderHelpers.MergeListStyles(theme.Method, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            default:
                yield return (item.Label, GetPlainListStyle(item, isSelected, theme));
                yield break;
        }
    }

    private static ToshTextStyleConfig GetPlainListStyle(HelpBrowserListEntry item, bool isSelected, ToshTuiThemeConfig theme)
    {
        var baseStyle = item.Kind is HelpBrowserListEntryKind.SectionHeader or HelpBrowserListEntryKind.Up
            ? theme.SectionHeading
            : theme.ListItem;
        return TuiRenderHelpers.MergeListStyles(baseStyle, theme.SelectedItem, isSelected, preserveForeground: false);
    }

    private static ToshTextStyleConfig GetDetailStyle(HelpDetailEntryKind kind, ToshTuiThemeConfig theme)
    {
        return kind switch
        {
            HelpDetailEntryKind.SectionHeading => theme.SectionHeading,
            HelpDetailEntryKind.Example => theme.Example,
            HelpDetailEntryKind.Meta => theme.Meta,
            _ => theme.DetailText,
        };
    }
}
