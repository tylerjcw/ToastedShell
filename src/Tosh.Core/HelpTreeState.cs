namespace Tosh.Core;

public enum HelpTreeNodeKind
{
    Category,
    Topic,
}

public sealed record HelpTreeVisibleNode(
    HelpTreeNodeKind Kind,
    string Category,
    HelpTopic? Topic,
    int Depth,
    int? ParentIndex,
    bool IsExpanded,
    int TotalCount,
    int VisibleCount);

public sealed class HelpTreeState
{
    private readonly ToshRuntime _runtime;
    private readonly List<HelpCategoryGroup> _categories;
    private readonly Dictionary<string, HelpTopic> _topicIndex;
    private readonly Dictionary<string, HelpTopic> _aliasIndex;
    private readonly Dictionary<string, HelpSearchEntry> _searchEntries;
    private readonly Dictionary<string, IReadOnlyDictionary<string, double>> _filterCache;
    private IReadOnlyList<HelpTreeVisibleNode> _visibleNodes = Array.Empty<HelpTreeVisibleNode>();
    private string _filter = string.Empty;
    private string _lastScoredFilter = string.Empty;
    private IReadOnlyDictionary<string, double> _lastScoredMatches = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public HelpTreeState(ToshRuntime runtime, string? initialQuery = null, string? initialTopicName = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _topicIndex = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase);
        _aliasIndex = new Dictionary<string, HelpTopic>(StringComparer.OrdinalIgnoreCase);
        _searchEntries = new Dictionary<string, HelpSearchEntry>(StringComparer.OrdinalIgnoreCase);
        _filterCache = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        _categories = BuildInitialCategories(runtime);

        if (!string.IsNullOrWhiteSpace(initialTopicName))
        {
            var resolved = HelpCatalog.ResolveTopic(runtime, initialTopicName);

            if (resolved is not null)
            {
                EnsureTopicTracked(resolved);
                initialTopicName = resolved.Name;
            }
        }

        _filter = initialQuery?.Trim() ?? string.Empty;
        Refresh();

        if (!string.IsNullOrWhiteSpace(initialTopicName))
        {
            SelectTopic(initialTopicName!);
        }
        else if (!string.IsNullOrWhiteSpace(_filter))
        {
            SelectFirstTopic();
        }
    }

    public IReadOnlyList<HelpTreeVisibleNode> VisibleNodes => _visibleNodes;

    public int TotalTopicCount => _topicIndex.Count;

    public int SelectedIndex { get; private set; }

    public string Filter => _filter;

    public HelpTreeVisibleNode? SelectedNode =>
        SelectedIndex >= 0 && SelectedIndex < _visibleNodes.Count ? _visibleNodes[SelectedIndex] : null;

    public HelpTopic? SelectedTopic => SelectedNode?.Topic;

    public void SetFilter(string value)
    {
        var next = value?.Trim() ?? string.Empty;

        if (string.Equals(_filter, next, StringComparison.Ordinal))
        {
            return;
        }

        _filter = next;
        Refresh();

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            SelectFirstTopic();
        }
    }

    public void SelectIndex(int index)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Clamp(index, 0, _visibleNodes.Count - 1);
    }

    public void MoveUp()
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - 1);
    }

    public void MoveDown()
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Min(_visibleNodes.Count - 1, SelectedIndex + 1);
    }

    public void MovePageUp(int amount)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - Math.Max(1, amount));
    }

    public void MovePageDown(int amount)
    {
        if (_visibleNodes.Count == 0)
        {
            return;
        }

        SelectedIndex = Math.Min(_visibleNodes.Count - 1, SelectedIndex + Math.Max(1, amount));
    }

    public void MoveHome()
    {
        SelectedIndex = 0;
    }

    public void MoveEnd()
    {
        SelectedIndex = Math.Max(0, _visibleNodes.Count - 1);
    }

    public bool ExpandSelected()
    {
        var selected = SelectedNode;

        if (selected is null || selected.Kind != HelpTreeNodeKind.Category || selected.IsExpanded || !string.IsNullOrWhiteSpace(_filter))
        {
            return false;
        }

        var category = GetCategory(selected.Category);

        if (category is null)
        {
            return false;
        }

        category.IsExpanded = true;
        Refresh();
        return true;
    }

    public bool CollapseSelected()
    {
        var selected = SelectedNode;

        if (selected is null)
        {
            return false;
        }

        if (selected.Kind == HelpTreeNodeKind.Category)
        {
            if (!selected.IsExpanded || !string.IsNullOrWhiteSpace(_filter))
            {
                return false;
            }

            var category = GetCategory(selected.Category);

            if (category is null)
            {
                return false;
            }

            category.IsExpanded = false;
            Refresh();
            return true;
        }

        if (selected.ParentIndex is int parentIndex)
        {
            SelectedIndex = parentIndex;
            return true;
        }

        return false;
    }

    public string? GetSelectedInsertionText() => SelectedTopic?.Name;

    private void Refresh()
    {
        var previousTopicName = SelectedTopic?.Name;
        var previousCategory = SelectedNode?.Category;
        var visible = new List<HelpTreeVisibleNode>();
        var filterScores = BuildFilterScoreMap();
        var filtered = !string.IsNullOrWhiteSpace(_filter);
        var orderedCategories = filtered
            ? _categories
                .OrderByDescending(category => category.Topics
                    .Where(topic => filterScores.ContainsKey(topic.Name))
                    .Select(topic => filterScores[topic.Name])
                    .DefaultIfEmpty(0.0d)
                    .Max())
                .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            : _categories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var category in orderedCategories)
        {
            List<HelpTopic> topics;

            if (!filtered)
            {
                topics = category.Topics
                    .OrderBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                topics = category.Topics
                    .Where(topic => filterScores.ContainsKey(topic.Name))
                    .OrderByDescending(topic => filterScores[topic.Name])
                    .ThenBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (topics.Count == 0)
                {
                    continue;
                }
            }

            var categoryIndex = visible.Count;
            visible.Add(new HelpTreeVisibleNode(
                HelpTreeNodeKind.Category,
                category.Name,
                null,
                0,
                null,
                category.IsExpanded,
                category.Topics.Count,
                topics.Count));

            if (!string.IsNullOrWhiteSpace(_filter) || category.IsExpanded)
            {
                foreach (var topic in topics)
                {
                    visible.Add(new HelpTreeVisibleNode(
                        HelpTreeNodeKind.Topic,
                        category.Name,
                        topic,
                        1,
                        categoryIndex,
                        false,
                        0,
                        0));
                }
            }
        }

        _visibleNodes = visible;

        if (!TryRestoreSelection(previousTopicName, previousCategory))
        {
            SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, _visibleNodes.Count - 1));
        }
    }

    private bool TryRestoreSelection(string? previousTopicName, string? previousCategory)
    {
        if (!string.IsNullOrWhiteSpace(previousTopicName))
        {
            var topicIndex = _visibleNodes
                .Select((node, index) => new { node, index })
                .FirstOrDefault(entry =>
                    entry.node.Topic is not null &&
                    string.Equals(entry.node.Topic.Name, previousTopicName, StringComparison.OrdinalIgnoreCase));

            if (topicIndex is not null)
            {
                SelectedIndex = topicIndex.index;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(previousCategory))
        {
            var categoryIndex = _visibleNodes
                .Select((node, index) => new { node, index })
                .FirstOrDefault(entry =>
                    entry.node.Kind == HelpTreeNodeKind.Category &&
                    string.Equals(entry.node.Category, previousCategory, StringComparison.OrdinalIgnoreCase));

            if (categoryIndex is not null)
            {
                SelectedIndex = categoryIndex.index;
                return true;
            }
        }

        return false;
    }

    private void SelectFirstTopic()
    {
        var topicIndex = _visibleNodes
            .Select((node, index) => new { node, index })
            .FirstOrDefault(entry => entry.node.Kind == HelpTreeNodeKind.Topic);

        if (topicIndex is not null)
        {
            SelectedIndex = topicIndex.index;
        }
    }

    private void SelectTopic(string topicName)
    {
        var category = _categories.FirstOrDefault(entry =>
            entry.Topics.Any(topic =>
                string.Equals(topic.Name, topicName, StringComparison.OrdinalIgnoreCase) ||
                topic.Aliases.Contains(topicName, StringComparer.OrdinalIgnoreCase)));

        if (category is not null && string.IsNullOrWhiteSpace(_filter))
        {
            category.IsExpanded = true;
            Refresh();
        }

        var match = _visibleNodes
            .Select((node, index) => new { node, index })
            .FirstOrDefault(entry =>
                entry.node.Topic is not null &&
                (string.Equals(entry.node.Topic.Name, topicName, StringComparison.OrdinalIgnoreCase) ||
                 entry.node.Topic.Aliases.Contains(topicName, StringComparer.OrdinalIgnoreCase)));

        if (match is not null)
        {
            SelectedIndex = match.index;
        }
    }

    private Dictionary<string, double> BuildFilterScoreMap()
    {
        if (string.IsNullOrWhiteSpace(_filter))
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        if (_filterCache.TryGetValue(_filter, out var cached))
        {
            _lastScoredFilter = _filter;
            _lastScoredMatches = cached;
            return new Dictionary<string, double>(cached, StringComparer.OrdinalIgnoreCase);
        }

        IEnumerable<HelpSearchEntry> candidates;

        if (!string.IsNullOrWhiteSpace(_lastScoredFilter) &&
            _filter.StartsWith(_lastScoredFilter, StringComparison.OrdinalIgnoreCase) &&
            _lastScoredMatches.Count > 0)
        {
            candidates = _lastScoredMatches.Keys
                .Select(name => _searchEntries.TryGetValue(name, out var entry) ? entry : null)
                .Where(entry => entry is not null)
                .Cast<HelpSearchEntry>();
        }
        else
        {
            candidates = _searchEntries.Values;
        }

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in candidates)
        {
            var score = ScoreTopic(entry, _filter);

            if (score > 0.0d)
            {
                scores[entry.Topic.Name] = Math.Round(score, 1);
            }
        }

        var exact = ResolveTrackedTopic(_filter);

        if (exact is not null)
        {
            EnsureTopicTracked(exact);
            scores[exact.Name] = Math.Max(scores.TryGetValue(exact.Name, out var existing) ? existing : 0.0d, 200.0d);
        }

        _filterCache[_filter] = new Dictionary<string, double>(scores, StringComparer.OrdinalIgnoreCase);
        _lastScoredFilter = _filter;
        _lastScoredMatches = _filterCache[_filter];

        return scores;
    }

    private void EnsureTopicTracked(HelpTopic topic)
    {
        if (_topicIndex.ContainsKey(topic.Name))
        {
            return;
        }

        _topicIndex[topic.Name] = topic;
        foreach (var alias in topic.Aliases)
        {
            _aliasIndex[alias] = topic;
        }

        IndexTopic(topic);
        var category = GetCategory(topic.Category);

        if (category is null)
        {
            category = new HelpCategoryGroup(topic.Category);
            _categories.Add(category);
        }

        category.Topics.Add(topic);
    }

    private HelpCategoryGroup? GetCategory(string name) =>
        _categories.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    private List<HelpCategoryGroup> BuildInitialCategories(ToshRuntime runtime)
    {
        var topics = HelpCatalog.BuildTopics(runtime)
            .GroupBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        foreach (var topic in topics)
        {
            _topicIndex[topic.Name] = topic;
            foreach (var alias in topic.Aliases)
            {
                _aliasIndex[alias] = topic;
            }

            IndexTopic(topic);
        }

        return topics
            .GroupBy(topic => topic.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HelpCategoryGroup(group.Key)
            {
                Topics = group
                    .OrderBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            })
            .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void IndexTopic(HelpTopic topic)
    {
        if (_searchEntries.ContainsKey(topic.Name))
        {
            return;
        }

        _searchEntries[topic.Name] = new HelpSearchEntry(
            topic,
            topic.Name.ToLowerInvariant(),
            topic.Category.ToLowerInvariant(),
            topic.Description.ToLowerInvariant(),
            topic.Usage.ToLowerInvariant(),
            (topic.Notes ?? string.Empty).ToLowerInvariant(),
            topic.Aliases.Select(alias => alias.ToLowerInvariant()).ToArray(),
            ExtractTokens(topic.Name),
            ExtractTokens(string.Join(' ', topic.Aliases)),
            ExtractTokens(
                topic.Name,
                topic.Category,
                topic.Description,
                topic.Usage,
                topic.Notes ?? string.Empty,
                string.Join(' ', topic.Aliases)));
    }

    private static double ScoreTopic(HelpSearchEntry entry, string query)
    {
        var trimmed = query.Trim();
        var queryLower = trimmed.ToLowerInvariant();

        if (trimmed.Length == 0)
        {
            return 0.0d;
        }

        double score = 0.0d;

        if (string.Equals(entry.LowerName, queryLower, StringComparison.Ordinal))
        {
            score = Math.Max(score, 500.0d);
        }

        if (entry.LowerAliases.Any(alias => string.Equals(alias, queryLower, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 480.0d);
        }

        if (entry.LowerName.StartsWith(queryLower, StringComparison.Ordinal))
        {
            score = Math.Max(score, 360.0d);
        }

        if (entry.LowerAliases.Any(alias => alias.StartsWith(queryLower, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 344.0d);
        }

        if (entry.NameTokens.Contains(queryLower))
        {
            score = Math.Max(score, 320.0d);
        }

        if (entry.AliasTokens.Contains(queryLower))
        {
            score = Math.Max(score, 304.0d);
        }

        if (entry.NameTokens.Any(token => token.StartsWith(queryLower, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 286.0d);
        }

        if (entry.AliasTokens.Any(token => token.StartsWith(queryLower, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 270.0d);
        }

        if (entry.LowerName.Contains(queryLower, StringComparison.Ordinal))
        {
            score = Math.Max(score, 236.0d);
        }

        if (entry.LowerAliases.Any(alias => alias.Contains(queryLower, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 220.0d);
        }

        if (entry.LowerCategory.Contains(queryLower, StringComparison.Ordinal))
        {
            score += 18.0d;
        }

        if (entry.LowerDescription.Contains(queryLower, StringComparison.Ordinal))
        {
            score += 14.0d;
        }

        if (entry.LowerUsage.Contains(queryLower, StringComparison.Ordinal))
        {
            score += 10.0d;
        }

        if (entry.LowerNotes.Contains(queryLower, StringComparison.Ordinal))
        {
            score += 6.0d;
        }

        var fuzzyNameScore = HelpFuzzyMatcher.FuzzyMatchLower(entry.LowerName, queryLower);
        var fuzzyAliasScore = entry.LowerAliases.Select(alias => HelpFuzzyMatcher.FuzzyMatchLower(alias, queryLower)).DefaultIfEmpty(0.0d).Max();

        if (fuzzyNameScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyNameScore * 120.0d);
        }

        if (fuzzyAliasScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyAliasScore * 108.0d);
        }

        var queryTokens = ExtractTokens(trimmed);
        score += queryTokens.Count(token => entry.NameTokens.Contains(token)) * 40.0d;
        score += queryTokens.Count(token => entry.AliasTokens.Contains(token)) * 32.0d;
        score += queryTokens.Count(token => entry.Tokens.Contains(token)) * 4.0d;

        return score;
    }

    private HelpTopic? ResolveTrackedTopic(string name)
    {
        if (_topicIndex.TryGetValue(name, out var topic))
        {
            return topic;
        }

        if (_aliasIndex.TryGetValue(name, out topic))
        {
            return topic;
        }

        return null;
    }

    private static HashSet<string> ExtractTokens(params string[] values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new System.Text.StringBuilder();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                FlushToken(builder, tokens);
            }

            FlushToken(builder, tokens);
        }

        return tokens;
    }

    private static void FlushToken(System.Text.StringBuilder builder, ISet<string> tokens)
    {
        if (builder.Length <= 1)
        {
            builder.Clear();
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    private sealed class HelpCategoryGroup
    {
        public HelpCategoryGroup(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool IsExpanded { get; set; }

        public List<HelpTopic> Topics { get; set; } = [];
    }

    private sealed record HelpSearchEntry(
        HelpTopic Topic,
        string LowerName,
        string LowerCategory,
        string LowerDescription,
        string LowerUsage,
        string LowerNotes,
        string[] LowerAliases,
        HashSet<string> NameTokens,
        HashSet<string> AliasTokens,
        HashSet<string> Tokens);
}
