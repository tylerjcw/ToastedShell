using Tosh.Core;
using Tosh.Cli;

namespace Tosh.Tests;

public sealed class InspectTreeTests
{
    [Fact]
    public void Object_tree_frame_includes_root_value_preview()
    {
        var runtime = ToshRuntime.CreateDefault();
        var builder = new ObjectTreeBuilder(runtime.Formatter);

        var frame = builder.BuildFrame(new { Name = "toast", Count = 3 });

        Assert.False(string.IsNullOrWhiteSpace(frame.ValuePreview));
    }

    [Fact]
    public void Expandable_nodes_keep_current_value_previews()
    {
        var runtime = ToshRuntime.CreateDefault();
        var builder = new ObjectTreeBuilder(runtime.Formatter);

        var frame = builder.BuildFrame(new InspectContainer { Child = new InspectChild { Value = 42 } });
        var child = Assert.Single(frame.Nodes.SelectMany(node => node.GetChildren()), node => node.Text == "Child");

        Assert.NotNull(child.InspectValue);
        Assert.False(string.IsNullOrWhiteSpace(child.ValuePreview));
    }

    [Fact]
    public void Inspect_browser_insertion_uses_property_names_for_property_nodes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new InspectTreeState(new ObjectTreeBuilder(runtime.Formatter), new InspectContainer { Child = new InspectChild { Value = 42 } });

        while (state.SelectedNode?.Node.Text != "Child")
        {
            state.MoveDown();
        }

        Assert.Equal("Child", ConsoleInlinePromptProvider.BuildInspectInsertionText(state));
    }

    [Fact]
    public void Inspect_browser_insertion_uses_callable_text_for_method_nodes()
    {
        var runtime = ToshRuntime.CreateDefault();
        var state = new InspectTreeState(new ObjectTreeBuilder(runtime.Formatter), 5, rootExpression: "5");

        while (state.SelectedNode?.Node.Text != "Methods")
        {
            state.MoveDown();
        }

        Assert.True(state.ExpandSelected());

        while (state.SelectedNode?.Node.Text is not "System.Type GetType()")
        {
            state.MoveDown();
        }

        var inserted = ConsoleInlinePromptProvider.BuildInspectInsertionText(state);

        Assert.Equal("(5).GetType()", inserted);
    }

    private sealed class InspectContainer
    {
        public InspectChild? Child { get; init; }
    }

    private sealed class InspectChild
    {
        public int Value { get; init; }
    }
}
