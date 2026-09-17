namespace Tosh.Tui.Widgets;

/// <summary>A widget that can say what it is currently showing.</summary>
/// <remarks>
/// <para>
/// For a container whose contents change without the tree changing — tabs being the case
/// this was written for. A border around one has no way to know that the reader moved from
/// <c>Summary</c> to <c>Errors</c>, so it went on saying whatever it was titled when the
/// screen was built.
/// </para>
/// <para>
/// Called <c>Caption</c> and not <c>Title</c> on purpose. <see cref="TuiBorder.Title"/> is
/// what a border was *told* to say; this is what a child *offers*, and the border prefers
/// the former. Naming both the same would be the fifth member in this codebase to hide
/// another by accident.
/// </para>
/// </remarks>
public interface ITuiCaption
{
    /// <summary>What to call what is showing, or null to leave it to the container.</summary>
    string? Caption { get; }
}
