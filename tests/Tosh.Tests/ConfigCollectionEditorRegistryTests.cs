using Tosh.Cli.Tui;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ConfigCollectionEditorRegistryTests
{
    [Fact]
    public void Collection_registry_supports_simple_scalar_list_like_types()
    {
        Assert.True(ConfigCollectionEditorRegistry.SupportsEditing("Future.Tags", typeof(string[])));
        Assert.True(ConfigCollectionEditorRegistry.SupportsEditing("Future.Counts", typeof(List<int>)));
        Assert.True(ConfigCollectionEditorRegistry.SupportsEditing("Future.Flags", typeof(IReadOnlyList<bool>)));
        Assert.False(ConfigCollectionEditorRegistry.SupportsEditing("Future.Objects", typeof(Dictionary<string, object?>)));
    }

    [Fact]
    public void Collection_registry_can_edit_simple_scalar_collections()
    {
        var runtime = ToshRuntime.CreateDefault();
        var node = new ConfigBrowserNode(
            Id: "Future.Tags",
            Name: "Tags",
            DisplayName: "Tags",
            Path: "Future.Tags",
            Kind: ConfigBrowserNodeKind.Value,
            EditorKind: ConfigBrowserEditorKind.Collection,
            ValueType: typeof(string[]),
            TypeName: "String[]",
            IsNullable: false,
            IsResettable: false,
            IsEditable: true,
            Children: Array.Empty<ConfigBrowserNode>());

        var currentValue = new[] { "alpha", "beta" };
        var items = ConfigCollectionEditorRegistry.GetItems(runtime, node, currentValue);

        Assert.Equal(2, items.Count);
        Assert.Equal("[0]", items[0].Label);
        Assert.Equal("alpha", items[0].Summary);

        Assert.True(ConfigCollectionEditorRegistry.TryAddItem(
            runtime,
            node,
            currentValue,
            "gamma",
            out var addedValue,
            out var addStatus,
            out var selectedKey));
        Assert.Equal("2", selectedKey);
        Assert.Contains("Staged collection item", addStatus, StringComparison.Ordinal);

        var addedItems = ConfigCollectionEditorRegistry.GetItems(runtime, node, addedValue);
        Assert.Equal(["alpha", "beta", "gamma"], addedItems.Select(item => item.Summary).ToArray());

        Assert.True(ConfigCollectionEditorRegistry.TryUpdateItem(
            runtime,
            node,
            addedValue,
            "1",
            "delta",
            out var updatedValue,
            out var updateStatus));
        Assert.Contains("Staged collection item [1]", updateStatus, StringComparison.Ordinal);

        var updatedItems = ConfigCollectionEditorRegistry.GetItems(runtime, node, updatedValue);
        Assert.Equal(["alpha", "delta", "gamma"], updatedItems.Select(item => item.Summary).ToArray());

        Assert.True(ConfigCollectionEditorRegistry.TryRemoveItem(
            runtime,
            node,
            updatedValue,
            "0",
            out var removedValue,
            out var removeStatus));
        Assert.Contains("Removed collection item [0]", removeStatus, StringComparison.Ordinal);

        var removedItems = ConfigCollectionEditorRegistry.GetItems(runtime, node, removedValue);
        Assert.Equal(["delta", "gamma"], removedItems.Select(item => item.Summary).ToArray());
    }

    [Fact]
    public void Collection_registry_builds_managed_config_lines_for_simple_scalar_collections()
    {
        var runtime = ToshRuntime.CreateDefault();
        var node = new ConfigBrowserNode(
            Id: "Future.Counts",
            Name: "Counts",
            DisplayName: "Counts",
            Path: "Future.Counts",
            Kind: ConfigBrowserNodeKind.Value,
            EditorKind: ConfigBrowserEditorKind.Collection,
            ValueType: typeof(List<int>),
            TypeName: "List<Int32>",
            IsNullable: false,
            IsResettable: false,
            IsEditable: true,
            Children: Array.Empty<ConfigBrowserNode>());

        var lines = ConfigCollectionEditorRegistry.BuildManagedConfigLines(
            runtime,
            node,
            new List<int> { 1, 2, 3 },
            text => $"\"{text}\"");

        Assert.Single(lines);
        Assert.Equal("$tosh.Config.Future.Counts = [1, 2, 3]", lines[0]);
    }
}
