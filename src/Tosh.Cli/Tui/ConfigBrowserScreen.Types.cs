using System.Globalization;
using System.Text;
using Tosh.Runtime;
using Tosh.Cli;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

/// <summary>The value types the config browser renders and edits through.</summary>
internal sealed partial class ConfigBrowserScreen
{
    private enum ConfigBrowserFocus
    {
        Search,
        Tree,
        Detail,
        Editor,
    }

    private enum ConfigBrowserEditMode
    {
        None,
        Text,
        Path,
        Enum,
        Color,
        Collection,
        PromptLayout,
        Group,
    }

    private enum ConfigBrowserConfirmAction
    {
        None,
        Exit,
        ReloadStartup,
        InitializeStartup,
    }

    private sealed record ConfigBrowserListEntry(ConfigBrowserNode Node, int Depth, bool IsExpanded, bool IsDirty)
    {
        public string Label
        {
            get
            {
                var indent = new string(' ', Depth * 2);
                var dirtySuffix = IsDirty ? " *" : string.Empty;

                if (Node.Kind == ConfigBrowserNodeKind.Group)
                {
                    var glyph = IsExpanded ? "▾" : "▸";
                    return $"{indent}{glyph} {Node.DisplayName}{dirtySuffix}";
                }

                return $"{indent}• {Node.DisplayName}{dirtySuffix}";
            }
        }
    }

    private sealed record ConfigDetailEntry(string Text, ConfigDetailEntryKind Kind);

    private enum ConfigDetailEntryKind
    {
        SectionHeading,
        Meta,
        Body,
        Preview,
    }

    private sealed record PromptLayoutEditorItem(string Name, bool Included);
    private sealed record ConfigEditSnapshotEntry(string Path, bool WasStaged, object? Value);
    private sealed record ColorEditorOption(string Label, string? Value);
    private sealed record PathValueDescription(string BaseDirectory, string ResolvedPath, string ExistenceLabel);
}
