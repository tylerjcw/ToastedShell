using Tosh.Core;

namespace Tosh.Tests;

public sealed class HelpTreeStateTests
{
    [Fact]
    public void Initial_topic_expands_and_selects_matching_topic()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new HelpTreeState(runtime, initialTopicName: "func");

        var selected = state.SelectedNode;
        Assert.NotNull(selected);
        var topic = selected!.Topic;
        Assert.NotNull(topic);

        Assert.Equal("func", topic!.Name);
        Assert.Contains(state.VisibleNodes, node => node.Kind == HelpTreeNodeKind.Category && node.Category == "Language" && node.IsExpanded);
    }

    [Fact]
    public void Filter_reveals_matching_topics_under_matching_categories()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new HelpTreeState(runtime);

        state.SetFilter("regex");

        Assert.NotEmpty(state.VisibleNodes);
        Assert.All(
            state.VisibleNodes.Where(node => node.Kind == HelpTreeNodeKind.Topic),
            node => Assert.Contains("regex", $"{node.Topic!.Name} {node.Topic.Description} {node.Topic.Usage} {string.Join(' ', node.Topic.Aliases)}", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HelpTreeNodeKind.Topic, state.SelectedNode?.Kind);
    }

    [Fact]
    public void Selected_topic_provides_insertion_text()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new HelpTreeState(runtime, initialTopicName: "func");

        Assert.Equal("func", state.GetSelectedInsertionText());
    }

    [Fact]
    public void Exact_topic_name_filter_ranks_exact_match_first()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new HelpTreeState(runtime);

        state.SetFilter("filter");

        Assert.Equal("filter", state.SelectedTopic?.Name);

        var firstTopic = state.VisibleNodes.FirstOrDefault(node => node.Kind == HelpTreeNodeKind.Topic);
        Assert.NotNull(firstTopic);
        Assert.Equal("filter", firstTopic!.Topic?.Name);
        Assert.Equal("Shell", firstTopic.Category);
    }
}
