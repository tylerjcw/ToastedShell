namespace Tosh.Tome.Workspace;

/// <summary>
/// One folder root in a Tōme workspace. <see cref="Path"/> is stored
/// absolute on load; <see cref="Alias"/> is an optional short name shown
/// in the explorer pane in place of the directory's basename.
/// </summary>
internal sealed record WorkspaceFolder(string Path, string? Alias = null);

/// <summary>
/// UI layout state persisted with the workspace. Keeps the explorer pane's
/// width and open/closed state across sessions so reopening a workspace
/// feels like resuming.
/// </summary>
internal sealed record WorkspaceLayout
{
    public int ExplorerWidth { get; init; } = 32;
    public bool ExplorerOpen { get; init; } = true;
}

/// <summary>
/// In-memory representation of a Tōme workspace. Constructed by
/// <see cref="WorkspaceFile.Load"/> and rendered back to disk by
/// <see cref="WorkspaceFile.Save"/>. The on-disk format is the
/// <c>.tome</c> declarative form (see <c>WorkspaceFile</c> docs).
/// </summary>
/// <remarks>
/// Step 1 surface area: model + file IO + command verb. The explorer
/// pane and tab restoration come in later steps.
/// </remarks>
internal sealed record Workspace
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Path to the .tome file this workspace was loaded from, when known.</summary>
    public string? SourcePath { get; init; }

    public IReadOnlyList<WorkspaceFolder> Folders { get; init; } = Array.Empty<WorkspaceFolder>();

    /// <summary>Glob-style patterns (matched against path segments) to hide in the explorer.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();

    /// <summary>Files to restore as tabs when the workspace is loaded.</summary>
    public IReadOnlyList<string> OpenFiles { get; init; } = Array.Empty<string>();

    public WorkspaceLayout Layout { get; init; } = new();
}
