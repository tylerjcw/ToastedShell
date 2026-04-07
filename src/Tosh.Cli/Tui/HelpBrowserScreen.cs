using System.Reflection;
using System.Text;
using Tosh.Core;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class HelpBrowserScreen : ITuiScreen
{
    private const int SearchBoxHeight = 4;
    private readonly ToshRuntime _runtime;
    private readonly IReadOnlyList<HelpSummary> _allTopics;
    private readonly TuiListState<HelpBrowserListEntry> _sidebar = new();
    private readonly TuiScrollState _detailScroll = new();
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private readonly HashSet<string> _collapsedSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedClrNamespaces = new(StringComparer.Ordinal);
    private string _query;
    private string? _currentTopicName;
    private string? _clrAssemblyScope;
    private string? _clrNamespaceScope;
    private string? _clrTypeScope;
    private bool _clrDeclaredOnly;
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
    private ClrBrowseIndex? _clrBrowseIndex;

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

    public TuiFrame Render(TuiSize size)
    {
        var width = Math.Max(60, size.Width);
        var height = Math.Max(14, size.Height);
        var root = new TuiRect(0, 0, width, height);
        var (searchRect, restRows) = TuiSplitLayout.SplitRows(root, SearchBoxHeight, gap: 0);
        var (contentRows, footerRow) = TuiSplitLayout.SplitRows(restRows, Math.Max(4, restRows.Height - 1), gap: 0);
        var listWidth = Math.Clamp(width / 3, 24, 40);
        var (listRect, detailRect) = TuiSplitLayout.SplitColumns(contentRows, listWidth, 1);

        SyncSidebar(Math.Max(1, listRect.Height - 2));
        var detailEntries = BuildDetailEntries(Math.Max(1, detailRect.Width - 2));
        _detailScroll.SetDimensions(detailEntries.Count, Math.Max(1, detailRect.Height - 2));

        var builder = new StringBuilder();
        builder.Append(RenderSearchBox(searchRect.Width));
        builder.Append(RenderContentRows(listRect, detailRect, detailEntries));
        builder.Append(RenderFooter(footerRow.Width));
        return new TuiFrame(builder.ToString());
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        if (_shouldExit)
        {
            return TuiScreenResult.Exit;
        }

        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            _shouldExit = true;
            return TuiScreenResult.Exit;
        }

        if (_focus == HelpBrowserFocus.Search && HandleSearchKey(key))
        {
            return _shouldExit ? TuiScreenResult.Exit : TuiScreenResult.Continue;
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
            case ConsoleKey.Oem4 when key.Modifiers == 0:
                NavigateBack();
                return TuiScreenResult.Continue;
            case ConsoleKey.Oem6 when key.Modifiers == 0:
                NavigateForward();
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
            case ConsoleKey.Oem2:
                if (key.KeyChar == '/')
                {
                    _focus = HelpBrowserFocus.Search;
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
            lines.Add(new($"  object: {FormatBoolean(topic.PipelineInput.Object)}  scalar: {FormatBoolean(topic.PipelineInput.Scalar)}  path-like: {FormatBoolean(topic.PipelineInput.PathLike)}  collection: {FormatBoolean(topic.PipelineInput.Collection)}", HelpDetailEntryKind.Meta));

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

    private IReadOnlyList<HelpDetailEntry> BuildClrTypeDetailEntries(HelpTopic topic, Type type, int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new($"{topic.Kind} • {topic.Category}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Description, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Identity", HelpDetailEntryKind.SectionHeading));
        lines.Add(new($"  Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Full Name: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Namespace: {type.Namespace ?? "<global>"}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Base: {ReflectionMetadataUtilities.GetDisplayName(type.BaseType ?? typeof(object))}", HelpDetailEntryKind.Meta));
        lines.Add(new($"  Kind: {GetClrTypeKindLabel(type)}", HelpDetailEntryKind.Meta));

        var interfaces = type.GetInterfaces()
            .Select(ReflectionMetadataUtilities.GetDisplayName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        if (interfaces.Length > 0)
        {
            lines.Add(new($"  Interfaces: {interfaces[0]}", HelpDetailEntryKind.Meta));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    string.Join(", ", interfaces.Skip(1)),
                    width,
                    "    ",
                    "    ")
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Meta)));
        }

        AddClrGenericSection(lines, type, width);

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Usage", HelpDetailEntryKind.SectionHeading));
        lines.AddRange(TextDocumentFormatter.WrapParagraph(topic.Usage, width, "  ", "    ")
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Example)));

        var constructors = ReflectionMetadataUtilities.GetConstructorDescriptors(type);
        AddSignatureSection(
            lines,
            "Constructors",
            constructors.Select(constructor => constructor.Signature).ToArray(),
            constructors.Count,
            width);

        var factoryMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => !method.IsSpecialName && method.ReturnType == type)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ReflectionMetadataUtilities.FormatMethodSignature)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        if (constructors.Count == 1 && constructors[0].ParameterCount == 0 && type.IsValueType && factoryMethods.Length > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Factory Methods", HelpDetailEntryKind.SectionHeading));

            foreach (var factory in factoryMethods)
            {
                lines.AddRange(TextDocumentFormatter.WrapParagraph(factory, width, "  ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }

        var memberRows = new List<string>();
        memberRows.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(property => property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property =>
            {
                var staticPrefix = ((property.GetMethod ?? property.SetMethod)?.IsStatic ?? false) ? "static " : string.Empty;
                var writableSuffix = property.CanWrite ? string.Empty : " readonly";
                return $"{staticPrefix}property {property.Name}: {ReflectionMetadataUtilities.GetDisplayName(property.PropertyType)}{writableSuffix}";
            }));
        memberRows.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Select(field =>
            {
                var staticPrefix = field.IsStatic ? "static " : string.Empty;
                var writableSuffix = field.IsInitOnly || field.IsLiteral ? " readonly" : string.Empty;
                return $"{staticPrefix}field {field.Name}: {ReflectionMetadataUtilities.GetDisplayName(field.FieldType)}{writableSuffix}";
            }));
        AddSignatureSection(lines, "Properties & Fields", memberRows.ToArray(), memberRows.Count, width, take: 12);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(method => method.GetParameters().Length)
            .Select(ReflectionMetadataUtilities.FormatMethodSignature)
            .ToArray();
        AddSignatureSection(lines, "Methods", methods, methods.Length, width, take: 12);

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"constructors {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"members {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"methods {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
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

    private IReadOnlyList<HelpDetailEntry> BuildClrAssemblyDetailEntries(string assemblyName, int width)
    {
        var index = EnsureClrBrowseIndex();
        if (!index.ByName.TryGetValue(assemblyName, out var assembly))
        {
            return [new HelpDetailEntry("The selected assembly is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var lines = new List<HelpDetailEntry>
        {
            new("CLR Assembly", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {assembly.Name}", HelpDetailEntryKind.Meta),
            new($"Name: {assembly.Name}", HelpDetailEntryKind.Meta),
            new($"Full Name: {assembly.FullName}", HelpDetailEntryKind.Text),
            new($"Types: {assembly.Types.Count}  Namespaces: {assembly.Namespaces.Count}", HelpDetailEntryKind.Meta),
        };

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("Press Enter to drill into this assembly's namespaces and visible types.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        if (assembly.Namespaces.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Namespaces", HelpDetailEntryKind.SectionHeading));
            foreach (var ns in assembly.Namespaces.Take(10))
            {
                lines.Add(new($"  {ns}", HelpDetailEntryKind.Text));
            }

            if (assembly.Namespaces.Count > 10)
            {
                lines.Add(new($"  … {assembly.Namespaces.Count - 10} more", HelpDetailEntryKind.Meta));
            }
        }

        if (assembly.Types.Count > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Sample Types", HelpDetailEntryKind.SectionHeading));
            foreach (var type in assembly.Types.Take(10))
            {
                lines.Add(new($"  {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Text));
            }

            if (assembly.Types.Count > 10)
            {
                lines.Add(new($"  … {assembly.Types.Count - 10} more", HelpDetailEntryKind.Meta));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrNamespaceDetailEntries(string namespaceName, int width)
    {
        var types = GetClrTypesForCurrentScope()
            .Where(type => string.Equals(type.Namespace ?? string.Empty, namespaceName, StringComparison.Ordinal))
            .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasDescendants = ClrNamespaceHasChildren(namespaceName);

        var lines = new List<HelpDetailEntry>
        {
            new("CLR Namespace", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {namespaceName}", HelpDetailEntryKind.Meta),
            new($"Namespace: {namespaceName}", HelpDetailEntryKind.Meta),
            new("Assembly Scope: merged tree across all discoverable CLR assemblies", HelpDetailEntryKind.Meta),
            new($"Assemblies Contributing: {CountClrAssembliesForNamespace(namespaceName)}", HelpDetailEntryKind.Meta),
            new($"Direct Types: {types.Length}", HelpDetailEntryKind.Meta),
            new($"Subtree Types: {CountClrSubtreeTypes(namespaceName)}", HelpDetailEntryKind.Meta),
            new($"Child Namespaces: {CountClrChildNamespaces(namespaceName)}  Descendants: {FormatBoolean(hasDescendants)}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        var guidance = ClrNamespaceHasTreeChildren(namespaceName)
            ? "Press Enter to collapse or expand this namespace branch. Press RightArrow or Tab to move into the detail pane."
            : "This namespace has no visible child namespaces or direct types in the current view.";
        lines.AddRange(TextDocumentFormatter.WrapParagraph(guidance, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));

        if (types.Length > 0)
        {
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Sample Types", HelpDetailEntryKind.SectionHeading));

            foreach (var type in types.Take(8))
            {
                lines.Add(new($"  {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Text));
            }

            if (types.Length > 8)
            {
                lines.Add(new($"  … {types.Length - 8} more", HelpDetailEntryKind.Meta));
            }
        }

        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrScopeDetailEntries(int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new("CLR Browser", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        var text = _clrNamespaceScope is not null
            ? $"You are browsing CLR type scope under namespace '{_clrNamespaceScope}'. Use LeftArrow or Backspace to return to the unified namespace tree."
            : _clrAssemblyScope is not null
                ? $"You are browsing assembly '{_clrAssemblyScope}'. Press Enter or LeftArrow to go up to the CLR root."
                : "Browse the unified CLR namespace tree, expand down to the type you want, and inspect assembly information in the detail pane.";

        lines.AddRange(TextDocumentFormatter.WrapParagraph(text, width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrTypeScopeDetailEntries(int width)
    {
        if (_clrTypeScope is null)
        {
            return [new HelpDetailEntry("Select a CLR type to browse its API surface.", HelpDetailEntryKind.Meta)];
        }

        var topic = HelpCatalog.ResolveTopic(_runtime, _clrTypeScope);
        if (topic is not null && TryResolveClrType(topic, out var clrType))
        {
            return BuildClrTypeDetailEntries(topic, clrType, width);
        }

        return [new HelpDetailEntry("The selected CLR type is no longer available.", HelpDetailEntryKind.Meta)];
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrFilterDetailEntries(int width)
    {
        var lines = new List<HelpDetailEntry>
        {
            new("CLR Browser Options", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Declared Only: {(_clrDeclaredOnly ? "on" : "off")}", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
        };

        lines.AddRange(TextDocumentFormatter.WrapParagraph(
                _clrDeclaredOnly
                    ? "Only members declared directly on the current CLR type are shown in the sidebar. Press Enter to include inherited members again."
                    : "The sidebar currently includes inherited public members. Press Enter to switch to declared-only browsing for the current CLR type.",
                width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrConstructorDetailEntries(string signature, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected constructor is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(
                    ReflectionMetadataUtilities.FormatConstructorSignature(candidate),
                    signature,
                    StringComparison.OrdinalIgnoreCase));
        var isImplicitValueTypeDefault = constructor is null &&
                                         type.IsValueType &&
                                         !type.IsEnum &&
                                         string.Equals(signature, $"{ReflectionMetadataUtilities.GetDisplayName(type)}()", StringComparison.OrdinalIgnoreCase);

        var lines = new List<HelpDetailEntry>
        {
            new("Constructor", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / .ctor", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Signature: {signature}", HelpDetailEntryKind.Example),
        };

        if (constructor is not null)
        {
            lines.Add(new($"Parameter Count: {constructor.GetParameters().Length}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {constructor.IsStatic}", HelpDetailEntryKind.Meta));
            AddParameterSection(lines, constructor.GetParameters(), width);

            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Example Invocation", HelpDetailEntryKind.SectionHeading));
            lines.Add(new($"  new {BuildConstructorInvocationExample(constructor)}", HelpDetailEntryKind.Example));
        }
        else if (isImplicitValueTypeDefault)
        {
            lines.Add(new("Parameter Count: 0", HelpDetailEntryKind.Meta));
            lines.Add(new("Static: no", HelpDetailEntryKind.Meta));
            lines.Add(new("This is the implicit default constructor available on CLR value types.", HelpDetailEntryKind.Text));
            lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
            lines.Add(new("Example Invocation", HelpDetailEntryKind.SectionHeading));
            lines.Add(new($"  new {ReflectionMetadataUtilities.GetDisplayName(type)}()", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"constructors {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("Use RightArrow or Tab to move into the detail pane, then scroll the full CLR type page for the surrounding API context.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrMemberDetailEntries(string value, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected member is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var parts = value.Split('|', 3);
        var memberKind = parts.Length > 0 ? parts[0] : "member";
        var declaringAssemblyQualifiedName = parts.Length > 1 ? parts[1] : null;
        var memberName = parts.Length > 2 ? parts[2] : value;

        var declaringType = !string.IsNullOrWhiteSpace(declaringAssemblyQualifiedName)
            ? Type.GetType(declaringAssemblyQualifiedName!, throwOnError: false)
            : type;
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var property = memberKind == "property" && declaringType is not null
            ? declaringType.GetProperty(memberName, flags)
            : null;
        var field = memberKind == "field" && declaringType is not null
            ? declaringType.GetField(memberName, flags)
            : null;

        var lines = new List<HelpDetailEntry>
        {
            new(memberKind == "property" ? "Property" : "Field", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / {memberName}", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Scope Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Name: {memberName}", HelpDetailEntryKind.Meta),
        };

        if (property is not null)
        {
            lines.Add(new($"Member Type: {ReflectionMetadataUtilities.GetDisplayName(property.PropertyType)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Declared On: {ReflectionMetadataUtilities.GetDisplayName(property.DeclaringType ?? type)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Assembly: {(property.DeclaringType ?? type).Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {((property.GetMethod ?? property.SetMethod)?.IsStatic ?? false)}  Writable: {property.CanWrite}", HelpDetailEntryKind.Meta));
        }
        else if (field is not null)
        {
            lines.Add(new($"Member Type: {ReflectionMetadataUtilities.GetDisplayName(field.FieldType)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Declared On: {ReflectionMetadataUtilities.GetDisplayName(field.DeclaringType ?? type)}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Assembly: {(field.DeclaringType ?? type).Assembly.GetName().Name}", HelpDetailEntryKind.Meta));
            lines.Add(new($"Static: {field.IsStatic}  Writable: {!(field.IsInitOnly || field.IsLiteral)}", HelpDetailEntryKind.Meta));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"members {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("This member is part of the current CLR type scope. Use LeftArrow or Backspace to go back up to the type-level browser.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private IReadOnlyList<HelpDetailEntry> BuildClrMethodDetailEntries(string methodName, int width)
    {
        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return [new HelpDetailEntry("The selected method is no longer available.", HelpDetailEntryKind.Meta)];
        }

        var methods = type.GetMethods(GetClrMemberBindingFlags())
            .Where(candidate => !candidate.IsSpecialName)
            .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.GetParameters().Length)
            .ThenBy(candidate => ReflectionMetadataUtilities.FormatMethodSignature(candidate), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = new List<HelpDetailEntry>
        {
            new("Method Overloads", HelpDetailEntryKind.Meta),
            new(string.Empty, HelpDetailEntryKind.Blank),
            new($"Path: CLR / .NET / {type.Namespace ?? "<global>"} / {ReflectionMetadataUtilities.GetDisplayName(type)} / {methodName}", HelpDetailEntryKind.Meta),
            new($"Type: {ReflectionMetadataUtilities.GetDisplayName(type)}", HelpDetailEntryKind.Meta),
            new($"Assembly: {type.Assembly.GetName().Name}", HelpDetailEntryKind.Meta),
            new($"Name: {methodName}", HelpDetailEntryKind.Meta),
            new($"Overloads: {methods.Length}  Declared Only: {(_clrDeclaredOnly ? "yes" : "no")}", HelpDetailEntryKind.Meta),
        };

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Overloads", HelpDetailEntryKind.SectionHeading));

        if (methods.Length == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
        }
        else
        {
            foreach (var method in methods.Take(12))
            {
                var declaringType = ReflectionMetadataUtilities.GetDisplayName(method.DeclaringType ?? type);
                var declaringAssembly = (method.DeclaringType ?? type).Assembly.GetName().Name;
                lines.Add(new($"  {ReflectionMetadataUtilities.FormatMethodSignature(method)}", HelpDetailEntryKind.Example));
                lines.Add(new($"    declared on {declaringType} | assembly: {declaringAssembly} | static: {method.IsStatic}", HelpDetailEntryKind.Meta));

                var parameters = method.GetParameters();
                if (parameters.Length > 0)
                {
                    foreach (var parameterLine in BuildParameterSummaryLines(parameters, width, "      "))
                    {
                        lines.Add(new(parameterLine, HelpDetailEntryKind.Text));
                    }
                }
            }

            if (methods.Length > 12)
            {
                lines.Add(new($"  … {methods.Length - 12} more", HelpDetailEntryKind.Meta));
            }
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Shell Helpers", HelpDetailEntryKind.SectionHeading));
        foreach (var helper in new[]
                 {
                     $"methods {ReflectionMetadataUtilities.GetDisplayName(type)}",
                     $"describe-type {ReflectionMetadataUtilities.GetDisplayName(type)}",
                 })
        {
            lines.Add(new($"  {helper}", HelpDetailEntryKind.Example));
        }

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.AddRange(TextDocumentFormatter.WrapParagraph("This method is part of the current CLR type scope. Use LeftArrow or Backspace to go back up to the type-level browser.", width)
            .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        return lines;
    }

    private Type? ResolveCurrentClrTypeScope()
    {
        return string.IsNullOrWhiteSpace(_clrTypeScope)
            ? null
            : ResolveClrTypeByDisplayName(_clrTypeScope!);
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

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _detailScroll.LineUp();
                break;
            case ConsoleKey.DownArrow:
                _detailScroll.LineDown();
                break;
            case ConsoleKey.PageUp:
                _detailScroll.PageUp();
                break;
            case ConsoleKey.PageDown:
                _detailScroll.PageDown();
                break;
            case ConsoleKey.Home:
                _detailScroll.Home();
                break;
            case ConsoleKey.End:
                _detailScroll.End();
                break;
        }

        return TuiScreenResult.Continue;
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
        _detailScroll.Home();
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

    private IReadOnlyList<HelpBrowserListEntry> BuildClrSidebarEntriesCore()
    {
        var entries = new List<HelpBrowserListEntry>();
        var index = EnsureClrBrowseIndex();

        if (_clrTypeScope is not null)
        {
            var type = ResolveClrTypeByDisplayName(_clrTypeScope);
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:type"));

            if (type is not null)
            {
                AddClrTypeNavigationEntries(entries, type);
                AddClrTypeOptionEntries(entries);
                AddClrConstructorEntries(entries, type);
                AddClrMemberEntries(entries, type);
                AddClrMethodEntries(entries, type);
            }

            return entries;
        }

        if (_clrNamespaceScope is not null)
        {
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:namespace"));
            AddClrTypeSection(entries, $"clr:namespace:{_clrNamespaceScope}:types", GetClrTypesForCurrentScope()
                .Where(type => string.Equals(type.Namespace ?? string.Empty, _clrNamespaceScope, StringComparison.Ordinal))
                .ToArray(), compactLabels: true);
            return entries;
        }

        if (_clrAssemblyScope is not null)
        {
            entries.Add(HelpBrowserListEntry.Up(".. CLR / .NET", "clr:up:assembly"));

            if (index.ByName.TryGetValue(_clrAssemblyScope, out var assembly))
            {
                AddClrNamespaceSection(entries, "Namespaces", "clr:assembly:namespaces", assembly.Namespaces);

                var assemblyTypes = FilterTypesForQuery(assembly.Types);
                var rootTypes = assemblyTypes
                    .Where(type => string.IsNullOrWhiteSpace(type.Namespace))
                    .ToArray();

                if (_query.Length > 0)
                {
                    AddClrTypeSection(entries, "clr:assembly:types", assemblyTypes);
                }
                else if (rootTypes.Length > 0)
                {
                    AddClrTypeSection(entries, "clr:assembly:types", rootTypes);
                }
            }

            return entries;
        }

        AddClrNamespaceSection(
            entries,
            "Namespaces",
            "clr:namespaces",
            index.AllTypes
                .Select(type => type.Namespace)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        var globalTypes = index.AllTypes
            .Where(type => string.IsNullOrWhiteSpace(type.Namespace))
            .ToArray();
        AddClrTypeSection(entries, "clr:global-types", globalTypes, "Global Types", compactLabels: true);

        if (_query.Length > 0)
        {
            var matchingTypes = FilterTypesForQuery(index.AllTypes).Take(60).ToArray();
            AddClrTypeSection(entries, "clr:types", matchingTypes, "Matching Types");
        }

        var clrHelpTopics = FilterTopics()
            .Where(summary => DetermineGroup(summary) == HelpBrowserGroup.Clr)
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (clrHelpTopics.Length > 0)
        {
            var collapsed = _collapsedSections.Contains("clr:commands");
            entries.Add(HelpBrowserListEntry.SectionHeader("Commands", "clr:commands", collapsed));
            if (!collapsed)
            {
                foreach (var summary in clrHelpTopics)
                {
                    entries.Add(HelpBrowserListEntry.Topic(summary));
                }
            }
        }

        return entries;
    }

    private void AddClrTypeNavigationEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var sectionKey = $"clr:type:{_clrTypeScope}:navigation";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Navigation", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        if (type.BaseType is not null)
        {
            var baseDisplayName = ReflectionMetadataUtilities.GetDisplayName(type.BaseType);
            if (MatchesQuery(baseDisplayName, "base"))
            {
                entries.Add(HelpBrowserListEntry.ClrTypeLink($"base: {baseDisplayName}", type.BaseType));
            }
        }

        foreach (var iface in type.GetInterfaces()
                     .OrderBy(iface => ReflectionMetadataUtilities.GetDisplayName(iface), StringComparer.OrdinalIgnoreCase))
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(iface);
            if (MatchesQuery(displayName, "interface"))
            {
                entries.Add(HelpBrowserListEntry.ClrTypeLink($"interface: {displayName}", iface));
            }
        }
    }

    private void AddClrTypeOptionEntries(List<HelpBrowserListEntry> entries)
    {
        var sectionKey = $"clr:type:{_clrTypeScope}:options";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("View Options", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        entries.Add(HelpBrowserListEntry.ClrFilterToggle(_clrDeclaredOnly));
    }

    private void AddClrAssemblySection(
        List<HelpBrowserListEntry> entries,
        string label,
        string sectionKey,
        IReadOnlyList<ClrAssemblyBrowseInfo> assemblies)
    {
        if (assemblies.Count == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var assembly in assemblies)
        {
            entries.Add(HelpBrowserListEntry.ClrAssembly(assembly));
        }
    }

    private void AddClrNamespaceSection(
        List<HelpBrowserListEntry> entries,
        string label,
        string sectionKey,
        IReadOnlyList<string> namespaces)
    {
        var filtered = FilterClrNamespacesForTree(namespaces);

        if (filtered.Length == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        var typesByNamespace = GetClrTypesForCurrentScope()
            .Where(type => !string.IsNullOrWhiteSpace(type.Namespace))
            .GroupBy(type => type.Namespace!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(type => BuildCompactClrTypeLabel(type), StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.Ordinal);

        var childNamespaces = filtered
            .GroupBy(candidate => GetClrParentNamespace(candidate) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.Ordinal);

        if (!childNamespaces.TryGetValue(string.Empty, out var roots))
        {
            return;
        }

        for (var index = 0; index < roots.Length; index += 1)
        {
            AddClrNamespaceTreeNode(
                entries,
                roots[index],
                prefix: string.Empty,
                isLast: index == roots.Length - 1,
                childNamespaces,
                typesByNamespace);
        }
    }

    private void AddClrNamespaceTreeNode(
        List<HelpBrowserListEntry> entries,
        string namespaceName,
        string prefix,
        bool isLast,
        IReadOnlyDictionary<string, string[]> childNamespaces,
        IReadOnlyDictionary<string, Type[]> typesByNamespace)
    {
        var sectionKey = BuildClrNamespaceTreeSectionKey(namespaceName);
        var childNamespaceList = childNamespaces.TryGetValue(namespaceName, out var childNamespaceCandidates)
            ? childNamespaceCandidates
            : Array.Empty<string>();
        var directTypes = typesByNamespace.TryGetValue(namespaceName, out var namespaceTypes)
            ? namespaceTypes
            : Array.Empty<Type>();
        var visibleTypes = FilterClrTypesForTree(directTypes);
        var hasTreeChildren = childNamespaceList.Length > 0 || visibleTypes.Length > 0;
        var isExpanded = IsClrNamespaceExpanded(namespaceName);
        var connector = isLast ? "└" : "├";
        var marker = hasTreeChildren ? (isExpanded ? "▾ " : "▸ ") : "─ ";
        var leafName = namespaceName.Split('.').Last();
        var treeStyle = _runtime.Config.Theme.Tui.TreeStyle;
        var label = treeStyle == ToshTuiTreeStyle.Clean && prefix.Length == 0
            ? $"{marker}{leafName} [{directTypes.Length}T/{childNamespaceList.Length}N]"
            : $"{prefix}{connector}{marker}{leafName} [{directTypes.Length}T/{childNamespaceList.Length}N]";

        entries.Add(HelpBrowserListEntry.ClrNamespaceTree(namespaceName, label, sectionKey, collapsed: !isExpanded));

        if (!hasTreeChildren || !isExpanded)
        {
            return;
        }

        var childPrefix = treeStyle == ToshTuiTreeStyle.Clean && prefix.Length == 0
            ? "  "
            : prefix + (isLast ? "  " : "│ ");

        for (var index = 0; index < childNamespaceList.Length; index += 1)
        {
            var isLastNamespace = index == childNamespaceList.Length - 1 && visibleTypes.Length == 0;
            AddClrNamespaceTreeNode(
                entries,
                childNamespaceList[index],
                childPrefix,
                isLastNamespace,
                childNamespaces,
                typesByNamespace);
        }

        for (var index = 0; index < visibleTypes.Length; index += 1)
        {
            var type = visibleTypes[index];
            var typeConnector = index == visibleTypes.Length - 1 ? "└" : "├";
            var typeLabel = $"{childPrefix}{typeConnector}─ {BuildCompactClrTypeLabel(type)}";
            entries.Add(HelpBrowserListEntry.ClrType(type, typeLabel));
        }
    }

    private void AddClrTypeSection(
        List<HelpBrowserListEntry> entries,
        string sectionKey,
        IReadOnlyList<Type> types,
        string label = "Types",
        bool compactLabels = false)
    {
        if (types.Count == 0)
        {
            return;
        }

        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader(label, sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var type in types
                     .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
                     .Take(120))
        {
            entries.Add(compactLabels
                ? HelpBrowserListEntry.ClrType(type, BuildCompactClrTypeLabel(type))
                : HelpBrowserListEntry.ClrType(type));
        }
    }

    private void AddClrConstructorEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var constructors = ReflectionMetadataUtilities.GetConstructorDescriptors(type);

        var sectionKey = $"clr:type:{_clrTypeScope}:constructors";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Constructors", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var constructor in constructors)
        {
            entries.Add(HelpBrowserListEntry.ClrConstructor(constructor.Signature));
        }
    }

    private void AddClrMemberEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var flags = GetClrMemberBindingFlags();
        var members = new List<HelpBrowserListEntry>();
        members.AddRange(type.GetProperties(flags)
            .Where(property => property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property => HelpBrowserListEntry.ClrMember(
                $"property|{property.DeclaringType?.AssemblyQualifiedName}|{property.Name}",
                BuildClrMemberLabel(type, property.Name, ReflectionMetadataUtilities.GetDisplayName(property.PropertyType), property.DeclaringType))));
        members.AddRange(type.GetFields(flags)
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Select(field => HelpBrowserListEntry.ClrMember(
                $"field|{field.DeclaringType?.AssemblyQualifiedName}|{field.Name}",
                BuildClrMemberLabel(type, field.Name, ReflectionMetadataUtilities.GetDisplayName(field.FieldType), field.DeclaringType))));

        var sectionKey = $"clr:type:{_clrTypeScope}:members";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Properties & Fields", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        entries.AddRange(members);
    }

    private void AddClrMethodEntries(List<HelpBrowserListEntry> entries, Type type)
    {
        var methods = type.GetMethods(GetClrMemberBindingFlags())
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(method => method.GetParameters().Length)
            .ToArray();

        var sectionKey = $"clr:type:{_clrTypeScope}:methods";
        var collapsed = _collapsedSections.Contains(sectionKey);
        entries.Add(HelpBrowserListEntry.SectionHeader("Methods", sectionKey, collapsed));
        if (collapsed)
        {
            return;
        }

        foreach (var group in methods
                     .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = BuildClrMethodGroupLabel(type, group);
            entries.Add(HelpBrowserListEntry.ClrMethodGroup(group.Key, label));
        }
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

    private void ExpandClrNamespace(string namespaceName)
    {
        foreach (var ancestor in GetClrNamespaceAncestors(namespaceName).Reverse())
        {
            _expandedClrNamespaces.Add(ancestor);
        }

        _expandedClrNamespaces.Add(namespaceName);
        InvalidateSidebarCache();
        InvalidateDetailCache();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private void ToggleClrNamespaceExpansion(string namespaceName)
    {
        if (!_expandedClrNamespaces.Add(namespaceName))
        {
            _expandedClrNamespaces.Remove(namespaceName);
        }

        InvalidateSidebarCache();
        InvalidateDetailCache();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private void OpenClrTypeScope(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        _activeGroup = HelpBrowserGroup.Clr;
        _clrAssemblyScope = null;
        _clrNamespaceScope = type.Namespace;
        _clrTypeScope = ReflectionMetadataUtilities.GetDisplayName(type);
        _currentTopicName = _clrTypeScope;
        InvalidateDerivedCaches();
        ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
    }

    private bool NavigateClrUp()
    {
        if (_clrTypeScope is not null)
        {
            _clrTypeScope = null;
            _clrAssemblyScope = null;
            _clrNamespaceScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        if (_clrNamespaceScope is not null)
        {
            _clrNamespaceScope = null;
            _clrAssemblyScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        if (_clrAssemblyScope is not null)
        {
            _clrAssemblyScope = null;
            _currentTopicName = null;
            InvalidateDerivedCaches();
            ApplyFilter(_sidebar.Scroll.PageSize > 0 ? _sidebar.Scroll.PageSize : 10);
            return true;
        }

        return false;
    }

    private BindingFlags GetClrMemberBindingFlags()
    {
        return BindingFlags.Public |
               BindingFlags.Instance |
               BindingFlags.Static |
               (_clrDeclaredOnly ? BindingFlags.DeclaredOnly : 0);
    }

    private static string BuildClrMemberLabel(Type scopeType, string memberName, string memberTypeName, Type? declaringType)
    {
        if (declaringType is null || declaringType == scopeType)
        {
            return $"{memberName} : {memberTypeName}";
        }

        return $"{memberName} : {memberTypeName} [from {ReflectionMetadataUtilities.GetDisplayName(declaringType)}]";
    }

    private string[] FilterClrNamespacesForTree(IReadOnlyList<string> namespaces)
    {
        var directNamespaces = namespaces
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expanded = new HashSet<string>(directNamespaces, StringComparer.Ordinal);
        foreach (var ns in directNamespaces)
        {
            foreach (var ancestor in GetClrNamespaceAncestors(ns))
            {
                expanded.Add(ancestor);
            }
        }

        var all = expanded
            .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (string.IsNullOrWhiteSpace(_query))
        {
            return all;
        }

        var allSet = all.ToHashSet(StringComparer.Ordinal);
        var visible = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ns in all.Where(ns => MatchesQuery(ns)))
        {
            visible.Add(ns);

            foreach (var ancestor in GetClrNamespaceAncestors(ns))
            {
                if (allSet.Contains(ancestor))
                {
                    visible.Add(ancestor);
                }
            }
        }

        return all.Where(visible.Contains).ToArray();
    }

    private bool HasCollapsedClrNamespaceAncestor(string namespaceName)
    {
        foreach (var ancestor in GetClrNamespaceAncestors(namespaceName))
        {
            if (_collapsedSections.Contains(BuildClrNamespaceTreeSectionKey(ancestor)))
            {
                return true;
            }
        }

        return false;
    }

    private bool ClrNamespaceHasChildren(string namespaceName)
    {
        return GetClrNamespaceDescendants(namespaceName)
            .Any(candidate => !string.Equals(candidate, namespaceName, StringComparison.Ordinal));
    }

    private bool ClrNamespaceHasTreeChildren(string namespaceName)
    {
        return CountClrChildNamespaces(namespaceName) > 0 || GetClrDirectTypes(namespaceName).Length > 0;
    }

    private Type[] GetClrDirectTypes(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Where(type => string.Equals(type.Namespace ?? string.Empty, namespaceName, StringComparison.Ordinal))
            .OrderBy(type => BuildCompactClrTypeLabel(type), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int CountClrSubtreeTypes(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Count(type =>
            {
                var candidate = type.Namespace ?? string.Empty;
                return string.Equals(candidate, namespaceName, StringComparison.Ordinal) ||
                       IsClrNamespaceDescendant(namespaceName, candidate);
            });
    }

    private int CountClrChildNamespaces(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Select(type => type.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Count(candidate => string.Equals(GetClrParentNamespace(candidate), namespaceName, StringComparison.Ordinal));
    }

    private int CountClrAssembliesForNamespace(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Where(type =>
            {
                var candidate = type.Namespace ?? string.Empty;
                return string.Equals(candidate, namespaceName, StringComparison.Ordinal) ||
                       IsClrNamespaceDescendant(namespaceName, candidate);
            })
            .Select(type => type.Assembly.GetName().Name ?? type.Assembly.FullName ?? "<unknown>")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private IEnumerable<string> GetClrNamespaceDescendants(string namespaceName)
    {
        return GetClrTypesForCurrentScope()
            .Select(type => type.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(candidate => string.Equals(candidate, namespaceName, StringComparison.Ordinal) || IsClrNamespaceDescendant(namespaceName, candidate))
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetClrNamespaceAncestors(string namespaceName)
    {
        var current = namespaceName;
        while (true)
        {
            var lastDot = current.LastIndexOf('.');
            if (lastDot <= 0)
            {
                yield break;
            }

            current = current[..lastDot];
            yield return current;
        }
    }

    private static string? GetClrParentNamespace(string namespaceName)
    {
        var lastDot = namespaceName.LastIndexOf('.');
        return lastDot <= 0 ? null : namespaceName[..lastDot];
    }

    private static bool IsClrNamespaceDescendant(string parentNamespace, string candidateNamespace)
    {
        return candidateNamespace.Length > parentNamespace.Length &&
               candidateNamespace.StartsWith(parentNamespace + ".", StringComparison.Ordinal);
    }

    private static int GetClrNamespaceDepth(string namespaceName) => namespaceName.Count(character => character == '.');

    private bool IsClrNamespaceExpanded(string namespaceName)
    {
        return !string.IsNullOrWhiteSpace(_query) || _expandedClrNamespaces.Contains(namespaceName);
    }

    private static string BuildClrNamespaceTreeSectionKey(string namespaceName) => $"clr:namespace-tree:{namespaceName}";

    private static string BuildCompactClrTypeLabel(Type type)
    {
        var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
        var ns = type.Namespace;

        if (!string.IsNullOrWhiteSpace(ns) &&
            displayName.StartsWith(ns + ".", StringComparison.Ordinal))
        {
            return displayName[(ns.Length + 1)..];
        }

        return displayName;
    }

    private Type[] FilterClrTypesForTree(IReadOnlyList<Type> types)
    {
        if (types.Count == 0)
        {
            return Array.Empty<Type>();
        }

        if (string.IsNullOrWhiteSpace(_query))
        {
            return types.ToArray();
        }

        return types
            .Where(type =>
                MatchesQuery(ReflectionMetadataUtilities.GetDisplayName(type)) ||
                MatchesQuery(BuildCompactClrTypeLabel(type)) ||
                MatchesQuery(type.FullName ?? string.Empty))
            .ToArray();
    }

    private static string BuildClrMethodGroupLabel(Type scopeType, IGrouping<string, MethodInfo> group)
    {
        var count = group.Count();
        var label = count == 1 ? $"{group.Key}(...)" : $"{group.Key}(...) × {count}";
        var declaringTypes = group
            .Select(method => method.DeclaringType)
            .Where(type => type is not null)
            .Cast<Type>()
            .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (declaringTypes.Length == 0)
        {
            return label;
        }

        if (declaringTypes.Length == 1 &&
            !string.Equals(declaringTypes[0].AssemblyQualifiedName, scopeType.AssemblyQualifiedName, StringComparison.Ordinal))
        {
            return $"{label} [from {ReflectionMetadataUtilities.GetDisplayName(declaringTypes[0])}]";
        }

        return declaringTypes.Length > 1 ? $"{label} [mixed]" : label;
    }

    private static void AddClrGenericSection(List<HelpDetailEntry> lines, Type type, int width)
    {
        if (!type.IsGenericType)
        {
            return;
        }

        var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
        var definitionParameters = definition.GetGenericArguments();
        var concreteArguments = type.GetGenericArguments();

        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Generic Parameters", HelpDetailEntryKind.SectionHeading));

        for (var index = 0; index < definitionParameters.Length; index += 1)
        {
            var parameter = definitionParameters[index];
            var concrete = concreteArguments.Length > index ? concreteArguments[index] : parameter;
            var constraints = BuildGenericConstraintDescription(parameter);
            var resolvedSuffix = concrete.IsGenericParameter
                ? string.Empty
                : $" = {ReflectionMetadataUtilities.GetDisplayName(concrete)}";

            lines.Add(new($"  {parameter.Name}{resolvedSuffix}", HelpDetailEntryKind.Meta));

            if (!string.IsNullOrWhiteSpace(constraints))
            {
                lines.AddRange(TextDocumentFormatter.WrapParagraph(constraints, width, "    ", "    ")
                    .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
            }
        }
    }

    private static string BuildGenericConstraintDescription(Type parameter)
    {
        if (!parameter.IsGenericParameter)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var attributes = parameter.GenericParameterAttributes;

        if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
        {
            parts.Add("class");
        }

        if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
        {
            parts.Add("struct");
        }

        if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
        {
            parts.Add("new()");
        }

        foreach (var constraint in parameter.GetGenericParameterConstraints())
        {
            parts.Add(ReflectionMetadataUtilities.GetDisplayName(constraint));
        }

        return parts.Count == 0
            ? "No explicit constraints."
            : $"Constraints: {string.Join(", ", parts)}";
    }

    private static void AddParameterSection(List<HelpDetailEntry> lines, IReadOnlyList<ParameterInfo> parameters, int width)
    {
        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new("Parameters", HelpDetailEntryKind.SectionHeading));

        if (parameters.Count == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
            return;
        }

        foreach (var parameterLine in BuildParameterSummaryLines(parameters, width, "  "))
        {
            lines.Add(new(parameterLine, HelpDetailEntryKind.Text));
        }
    }

    private static IReadOnlyList<string> BuildParameterSummaryLines(IReadOnlyList<ParameterInfo> parameters, int width, string indent)
    {
        var lines = new List<string>();

        foreach (var parameter in parameters)
        {
            var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            var defaultSuffix = parameter.HasDefaultValue
                ? $" = {FormatDefaultValue(parameter.DefaultValue)}"
                : string.Empty;
            var text = $"{prefix}{ReflectionMetadataUtilities.GetDisplayName(UnwrapByRef(parameter.ParameterType))} {parameter.Name}{defaultSuffix}";
            lines.AddRange(TextDocumentFormatter.WrapParagraph(text, width, indent, indent));
        }

        return lines;
    }

    private static string BuildConstructorInvocationExample(ConstructorInfo constructor)
    {
        var typeName = constructor.DeclaringType is null
            ? ".ctor"
            : ReflectionMetadataUtilities.GetDisplayName(constructor.DeclaringType);
        var parameters = string.Join(", ", constructor.GetParameters().Select(parameter => parameter.Name ?? "value"));
        return $"{typeName}({parameters})";
    }

    private static string FormatDefaultValue(object? value)
    {
        return value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            char c => $"'{c}'",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? string.Empty,
        };
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

    private string BuildGroupLine(int width, ToshTuiThemeConfig theme)
    {
        var groups = new[]
        {
            HelpBrowserGroup.All,
            HelpBrowserGroup.ToastedShell,
            HelpBrowserGroup.ToastScript,
            HelpBrowserGroup.Clr,
        };

        var segments = new List<object?>();

        for (var index = 0; index < groups.Length; index += 1)
        {
            if (index > 0)
            {
                segments.Add(theme.Meta.Apply("  "));
            }

            var group = groups[index];
            var style = group == _activeGroup ? theme.Title : theme.Meta;
            segments.Add(style.Apply(BuildGroupLabel(group)));
        }

        var rendered = StyledText.RenderSegments(segments);
        var visible = StyledText.GetVisibleLength(rendered);
        if (visible > width)
        {
            return theme.Meta.Apply(TrimOrPadPlain(GetGroupTitle(_activeGroup), width)).ToAnsi();
        }

        return rendered + new string(' ', width - visible);
    }

    private string RenderSearchBox(int width)
    {
        var label = _focus == HelpBrowserFocus.Search ? "Search*" : "Search";
        var theme = _runtime.Config.Theme.Tui;
        var box = TuiBoxDrawing.GetBoxCharacters(theme.BoxStyle);
        var builder = new StringBuilder();
        builder.Append(RenderTopBorder(width, "Help Browser", theme, box));

        var innerWidth = Math.Max(1, width - 2);
        var groupLine = BuildGroupLine(innerWidth, theme);
        builder.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        builder.Append(groupLine);
        builder.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        builder.AppendLine();

        var labelText = $"{label}: ";
        var queryWidth = Math.Max(0, innerWidth - labelText.Length);
        var clippedQuery = ClipPlain(_query, queryWidth);
        var labelStyled = theme.SearchLabel.Apply(labelText).ToAnsi();
        var queryStyled = theme.SearchInput.Apply(clippedQuery).ToAnsi();
        var visibleLength = StyledText.GetVisibleLength(labelStyled) + StyledText.GetVisibleLength(queryStyled);
        var padding = new string(' ', Math.Max(0, innerWidth - visibleLength));

        builder.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        builder.Append(labelStyled);
        builder.Append(queryStyled);
        builder.Append(padding);
        builder.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        builder.AppendLine();
        builder.Append(RenderBottomBorder(width, theme, box));
        return builder.ToString();
    }

    private string RenderContentRows(TuiRect listRect, TuiRect detailRect, IReadOnlyList<HelpDetailEntry> detailEntries)
    {
        var builder = new StringBuilder();
        var listRange = _sidebar.Scroll.GetVisibleRange();
        var detailRange = _detailScroll.GetVisibleRange();
        var theme = _runtime.Config.Theme.Tui;
        var box = TuiBoxDrawing.GetBoxCharacters(theme.BoxStyle);
        var selectedTopicTitle = GetDetailTitle();

        var listInnerWidth = Math.Max(1, listRect.Width - 2);
        var detailInnerWidth = Math.Max(1, detailRect.Width - 2);
        var listContentRows = Math.Max(1, listRect.Height - 2);
        var detailContentRows = Math.Max(1, detailRect.Height - 2);

        builder.Append(RenderTopBorder(listRect.Width, GetGroupTitle(_activeGroup), theme, box));
        builder.Append(' ');
        builder.Append(RenderTopBorder(detailRect.Width, selectedTopicTitle, theme, box));
        builder.AppendLine();

        for (var row = 0; row < Math.Max(listContentRows, detailContentRows); row++)
        {
            string listLine;

            if (row < listContentRows && row < listRange.Length)
            {
                var itemIndex = listRange.Start + row;
                var item = _sidebar.Items[itemIndex];
                var isSelected = itemIndex == _sidebar.SelectedIndex;
                listLine = RenderSidebarLine(item, isSelected, listRect.Width, theme, box);
            }
            else
            {
                listLine = RenderBoxContentLine(string.Empty, listRect.Width, theme.ListItem, theme, box);
            }

            string detailLine;

            if (row < detailContentRows && row < detailRange.Length)
            {
                var entry = detailEntries[detailRange.Start + row];
                var text = TrimOrPadPlain(entry.Text, detailInnerWidth);
                detailLine = RenderBoxContentLine(text, detailRect.Width, GetDetailStyle(entry.Kind, theme), theme, box);
            }
            else
            {
                detailLine = RenderBoxContentLine(string.Empty, detailRect.Width, theme.DetailText, theme, box);
            }

            builder.Append(listLine);
            builder.Append(' ');
            builder.Append(detailLine);
            builder.AppendLine();
        }

        builder.Append(RenderBottomBorder(listRect.Width, theme, box));
        builder.Append(' ');
        builder.Append(RenderBottomBorder(detailRect.Width, theme, box));
        builder.AppendLine();

        return builder.ToString();
    }

    private string RenderFooter(int width)
    {
        var focus = _focus.ToString().ToLowerInvariant();
        var theme = _runtime.Config.Theme.Tui;
        var text = TrimOrPadPlain($"focus:{focus}  F1-F4 groups  / search  Enter open/toggle  i insert  [ back  ] forward  Left up  1-9 related  q quit", width);
        return theme.Footer.Apply(text).ToAnsi();
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
        _detailScroll.Home();
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
        _detailScroll.Home();
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
        _detailScroll.Home();
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

    private IReadOnlyList<Type> GetClrTypesForCurrentScope()
    {
        var index = EnsureClrBrowseIndex();
        if (_clrAssemblyScope is not null && index.ByName.TryGetValue(_clrAssemblyScope, out var assembly))
        {
            return assembly.Types;
        }

        return index.AllTypes;
    }

    private bool TryResolveClrType(HelpTopic topic, out Type type)
    {
        ArgumentNullException.ThrowIfNull(topic);

        if (_sidebar.TryGetSelected(out var selected) &&
            selected.Kind == HelpBrowserListEntryKind.ClrType &&
            !string.IsNullOrWhiteSpace(selected.Value))
        {
            var selectedType = ResolveClrTypeByDisplayName(selected.Value!);
            if (selectedType is not null)
            {
                type = selectedType;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_currentTopicName))
        {
            var currentType = ResolveClrTypeByDisplayName(_currentTopicName!);
            if (currentType is not null)
            {
                type = currentType;
                return true;
            }
        }

        var resolved = ResolveClrTypeByDisplayName(topic.Name);
        if (resolved is not null)
        {
            type = resolved;
            return true;
        }

        type = typeof(object);
        return false;
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

    private ClrBrowseIndex EnsureClrBrowseIndex()
    {
        if (_clrBrowseIndex is not null)
        {
            return _clrBrowseIndex;
        }

        var assemblies = TypeCatalog.GetAssemblies()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
            .Select(assembly =>
            {
                var types = TypeCatalog.GetAssemblyTypes(assembly)
                    .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var namespaces = types
                    .Select(type => type.Namespace)
                    .Where(ns => !string.IsNullOrWhiteSpace(ns))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(ns => ns, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var name = assembly.GetName().Name ?? assembly.FullName ?? "<unknown>";
                return new ClrAssemblyBrowseInfo(name, assembly.FullName ?? name, types, namespaces);
            })
            .ToArray();

        var byName = new Dictionary<string, ClrAssemblyBrowseInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            byName.TryAdd(assembly.Name, assembly);
        }
        var allTypes = assemblies
            .SelectMany(assembly => assembly.Types)
            .DistinctBy(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(type => ReflectionMetadataUtilities.GetDisplayName(type), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _clrBrowseIndex = new ClrBrowseIndex(assemblies, byName, allTypes);
        return _clrBrowseIndex;
    }

    private Type? ResolveClrTypeByDisplayName(string displayName)
    {
        var resolved = _runtime.TypeResolver.Resolve(displayName);
        if (resolved is not null)
        {
            return resolved;
        }

        return EnsureClrBrowseIndex().AllTypes.FirstOrDefault(type =>
            string.Equals(ReflectionMetadataUtilities.GetDisplayName(type), displayName, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddSignatureSection(
        List<HelpDetailEntry> lines,
        string heading,
        IReadOnlyList<string> rows,
        int totalCount,
        int width,
        int take = 8)
    {
        lines.Add(new(string.Empty, HelpDetailEntryKind.Blank));
        lines.Add(new(heading, HelpDetailEntryKind.SectionHeading));

        if (totalCount == 0)
        {
            lines.Add(new("  <none>", HelpDetailEntryKind.Meta));
            return;
        }

        foreach (var row in rows.Take(take))
        {
            lines.AddRange(TextDocumentFormatter.WrapParagraph(row, width, "  ", "    ")
                .Select(line => new HelpDetailEntry(line, HelpDetailEntryKind.Text)));
        }

        if (totalCount > take)
        {
            lines.Add(new($"  … {totalCount - take} more", HelpDetailEntryKind.Meta));
        }
    }

    private static string GetClrTypeKindLabel(Type type)
    {
        if (type.IsInterface)
        {
            return "Interface";
        }

        if (type.IsEnum)
        {
            return "Enum";
        }

        if (type.IsArray)
        {
            return "Array";
        }

        if (type.IsValueType)
        {
            return "Struct";
        }

        if (type.IsClass && type.IsAbstract && type.IsSealed)
        {
            return "Static Class";
        }

        if (type.IsAbstract)
        {
            return "Abstract Class";
        }

        return type.IsClass ? "Class" : "Type";
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

    private string RenderSidebarLine(
        HelpBrowserListEntry item,
        bool isSelected,
        int width,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(1, width - 2);
        var gutterSegments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (isSelected ? "› " : "  ", isSelected ? theme.SelectedGutter : theme.Meta),
        };
        var contentSegments = BuildSidebarContentSegments(item, isSelected, theme);
        var renderedInner = RenderStyledSegments(gutterSegments.Concat(contentSegments), innerWidth);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.Vertical.ToString()),
            renderedInner,
            theme.Border.Apply(box.Vertical.ToString()),
        ]);
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
                yield return (item.Label, MergeListStyles(theme.Meta, theme.SelectedItem, isSelected, preserveForeground: true));
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
                yield return (item.Label, MergeListStyles(theme.Constructor, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            case HelpBrowserListEntryKind.ClrMember:
                yield return (item.Label, MergeListStyles(theme.Property, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            case HelpBrowserListEntryKind.ClrMethod:
                yield return (item.Label, MergeListStyles(theme.Method, theme.SelectedItem, isSelected, preserveForeground: true));
                yield break;
            default:
                yield return (item.Label, GetPlainListStyle(item, isSelected, theme));
                yield break;
        }
    }

    private IEnumerable<(string Text, ToshTextStyleConfig Style)> BuildClrNamespaceSegments(
        HelpBrowserListEntry item,
        bool isSelected,
        ToshTuiThemeConfig theme)
    {
        var label = item.Label;
        var leafName = item.RawLabel.Split('.').Last();
        var leafIndex = label.LastIndexOf(leafName, StringComparison.Ordinal);
        if (leafIndex < 0)
        {
            yield return (label, MergeListStyles(theme.Namespace, theme.SelectedItem, isSelected, preserveForeground: true));
            yield break;
        }

        var countsIndex = label.IndexOf(" [", leafIndex, StringComparison.Ordinal);
        var prefix = label[..leafIndex];
        var name = countsIndex >= 0 ? label[leafIndex..countsIndex] : label[leafIndex..];
        var suffix = countsIndex >= 0 ? label[countsIndex..] : string.Empty;

        if (prefix.Length > 0)
        {
            yield return (prefix, theme.TreeGuide);
        }

        yield return (name, MergeListStyles(theme.Namespace, theme.SelectedItem, isSelected, preserveForeground: true));

        if (suffix.Length > 0)
        {
            yield return (suffix, theme.Meta);
        }
    }

    private IEnumerable<(string Text, ToshTextStyleConfig Style)> BuildClrTypeSegments(
        HelpBrowserListEntry item,
        bool isSelected,
        ToshTuiThemeConfig theme)
    {
        var label = item.Label;
        var guideIndex = label.LastIndexOf("─ ", StringComparison.Ordinal);
        if (guideIndex >= 0)
        {
            var prefix = label[..(guideIndex + 2)];
            var name = label[(guideIndex + 2)..];

            if (prefix.Length > 0)
            {
                yield return (prefix, theme.TreeGuide);
            }

            yield return (name, MergeListStyles(theme.Type, theme.SelectedItem, isSelected, preserveForeground: true));
            yield break;
        }

        yield return (label, MergeListStyles(theme.Type, theme.SelectedItem, isSelected, preserveForeground: true));
    }

    private static string RenderStyledSegments(IEnumerable<(string Text, ToshTextStyleConfig Style)> segments, int width)
    {
        var builder = new StringBuilder();
        var remaining = width;

        foreach (var (text, style) in segments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var clipped = text.Length <= remaining ? text : text[..remaining];
            builder.Append(style.Apply(clipped).ToAnsi());
            remaining -= clipped.Length;
        }

        if (remaining > 0)
        {
            builder.Append(' ', remaining);
        }

        return builder.ToString();
    }

    private static ToshTextStyleConfig GetPlainListStyle(HelpBrowserListEntry item, bool isSelected, ToshTuiThemeConfig theme)
    {
        var baseStyle = item.Kind is HelpBrowserListEntryKind.SectionHeader or HelpBrowserListEntryKind.Up
            ? theme.SectionHeading
            : theme.ListItem;
        return MergeListStyles(baseStyle, theme.SelectedItem, isSelected, preserveForeground: false);
    }

    private static ToshTextStyleConfig MergeListStyles(
        ToshTextStyleConfig baseStyle,
        ToshTextStyleConfig selectedStyle,
        bool isSelected,
        bool preserveForeground)
    {
        if (!isSelected)
        {
            return baseStyle;
        }

        return new ToshTextStyleConfig(
            foreground: preserveForeground ? baseStyle.Foreground : selectedStyle.Foreground ?? baseStyle.Foreground,
            background: selectedStyle.Background ?? baseStyle.Background,
            bold: baseStyle.Bold || selectedStyle.Bold,
            italic: baseStyle.Italic || selectedStyle.Italic,
            underline: baseStyle.Underline || selectedStyle.Underline,
            dim: selectedStyle.Dim && baseStyle.Dim);
    }

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private string? BuildConstructorInsertionText(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        var type = ResolveCurrentClrTypeScope();
        if (type is null)
        {
            return null;
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(
                    ReflectionMetadataUtilities.FormatConstructorSignature(candidate),
                    signature,
                    StringComparison.OrdinalIgnoreCase));

        if (constructor is not null)
        {
            return $"new {BuildConstructorInvocationExample(constructor)}";
        }

        if (type.IsValueType &&
            !type.IsEnum &&
            string.Equals(signature, $"{ReflectionMetadataUtilities.GetDisplayName(type)}()", StringComparison.OrdinalIgnoreCase))
        {
            return $"new {ReflectionMetadataUtilities.GetDisplayName(type)}()";
        }

        return $"new {ReflectionMetadataUtilities.GetDisplayName(type)}(";
    }

    private static string? ExtractClrMemberInsertionText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('|', 3);
        return parts.Length == 3 ? parts[2] : value;
    }

    private static string? BuildMethodInsertionText(string? methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        return methodName + "(";
    }

    private static string RenderTopBorder(int width, string title, ToshTuiThemeConfig theme, TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(0, width - 2);
        var clippedTitle = ClipPlain(title, Math.Max(0, innerWidth - 2));
        var titleText = string.IsNullOrWhiteSpace(clippedTitle) ? string.Empty : $" {clippedTitle} ";
        var fillWidth = Math.Max(0, innerWidth - titleText.Length);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.TopLeft.ToString()),
            theme.Title.Apply(titleText),
            theme.Border.Apply(new string(box.Horizontal, fillWidth) + box.TopRight),
        ]);
    }

    private static string RenderBottomBorder(int width, ToshTuiThemeConfig theme, TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        return theme.Border.Apply($"{box.BottomLeft}{new string(box.Horizontal, Math.Max(0, width - 2))}{box.BottomRight}").ToAnsi();
    }

    private static string RenderBoxContentLine(
        string plainText,
        int width,
        ToshTextStyleConfig contentStyle,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(1, width - 2);
        var padded = TrimOrPadPlain(plainText, innerWidth);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.Vertical.ToString()),
            contentStyle.Apply(padded),
            theme.Border.Apply(box.Vertical.ToString()),
        ]);
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

    private static string TrimOrPadPlain(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var clipped = ClipPlain(text, width);
        return clipped.PadRight(Math.Max(0, width - clipped.Length) + clipped.Length);
    }

    private static string ClipPlain(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length > width)
        {
            return text[..Math.Max(0, width - 1)] + "…";
        }

        return text;
    }

    private enum HelpBrowserFocus
    {
        Search,
        List,
        Detail,
    }

    private enum HelpBrowserGroup
    {
        All,
        ToastedShell,
        ToastScript,
        Clr,
    }

    private enum HelpBrowserListEntryKind
    {
        SectionHeader,
        Topic,
        ClrAssembly,
        ClrNamespace,
        ClrType,
        ClrFilterToggle,
        ClrConstructor,
        ClrMember,
        ClrMethod,
        Up,
    }

    private readonly record struct HelpBrowserListEntry(
        HelpBrowserListEntryKind Kind,
        string Label,
        string RawLabel,
        string? TopicName,
        string? SectionKey,
        string? Value,
        bool IsCollapsed)
    {
        public static HelpBrowserListEntry SectionHeader(string label, string sectionKey, bool collapsed)
        {
            var marker = collapsed ? "▸ " : "▾ ";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.SectionHeader,
                marker + label,
                label,
                TopicName: null,
                SectionKey: sectionKey,
                Value: null,
                IsCollapsed: collapsed);
        }

        public static HelpBrowserListEntry Topic(HelpSummary summary)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.Topic,
                "  " + summary.Name,
                summary.Name,
                TopicName: summary.Name,
                SectionKey: summary.Category,
                Value: null,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrAssembly(ClrAssemblyBrowseInfo assembly)
        {
            var label = $"{assembly.Name} [{assembly.Types.Count}T/{assembly.Namespaces.Count}N]";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrAssembly,
                "  " + label,
                assembly.Name,
                TopicName: null,
                SectionKey: "Assemblies",
                Value: assembly.Name,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrNamespace(string namespaceName)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrNamespace,
                "  " + namespaceName,
                namespaceName,
                TopicName: null,
                SectionKey: "Namespaces",
                Value: namespaceName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrNamespaceTree(string namespaceName, string label, string sectionKey, bool collapsed)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrNamespace,
                label,
                namespaceName,
                TopicName: null,
                SectionKey: sectionKey,
                Value: namespaceName,
                IsCollapsed: collapsed);
        }

        public static HelpBrowserListEntry ClrType(Type type)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + displayName,
                displayName,
                TopicName: displayName,
                SectionKey: "Types",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrType(Type type, string label)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + label,
                displayName,
                TopicName: displayName,
                SectionKey: "Types",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrTypeLink(string label, Type type)
        {
            var displayName = ReflectionMetadataUtilities.GetDisplayName(type);
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrType,
                "  " + label,
                label,
                TopicName: displayName,
                SectionKey: "Navigation",
                Value: displayName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrFilterToggle(bool declaredOnly)
        {
            var label = declaredOnly ? "Declared Only: on" : "Declared Only: off";
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrFilterToggle,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "View Options",
                Value: declaredOnly ? "declared" : "all",
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrConstructor(string signature)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrConstructor,
                "  " + signature,
                signature,
                TopicName: null,
                SectionKey: "Constructors",
                Value: signature,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrMember(string value, string label)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrMember,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "Members",
                Value: value,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry ClrMethodGroup(string methodName, string label)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.ClrMethod,
                "  " + label,
                label,
                TopicName: null,
                SectionKey: "Methods",
                Value: methodName,
                IsCollapsed: false);
        }

        public static HelpBrowserListEntry Up(string label, string sectionKey)
        {
            return new HelpBrowserListEntry(
                HelpBrowserListEntryKind.Up,
                label,
                label,
                TopicName: null,
                SectionKey: sectionKey,
                Value: null,
                IsCollapsed: false);
        }
    }

    internal readonly record struct HelpDetailEntry(string Text, HelpDetailEntryKind Kind, int? RelatedIndex = null);

    internal enum HelpDetailEntryKind
    {
        Blank,
        Meta,
        Text,
        SectionHeading,
        Example,
        RelatedTopic,
    }

    private readonly record struct SectionDescriptor(string Key, string Label, int GroupOrder, int SectionOrder);

    private sealed record ClrAssemblyBrowseInfo(
        string Name,
        string FullName,
        IReadOnlyList<Type> Types,
        IReadOnlyList<string> Namespaces);

    private sealed record ClrBrowseIndex(
        IReadOnlyList<ClrAssemblyBrowseInfo> Assemblies,
        IReadOnlyDictionary<string, ClrAssemblyBrowseInfo> ByName,
        IReadOnlyList<Type> AllTypes);
}
