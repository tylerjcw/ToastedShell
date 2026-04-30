namespace Tosh.Runtime;

/// <summary>
/// Implemented by user-defined refinement type aliases so that
/// <see cref="HelpCatalog"/> can surface them as help topics without
/// taking a dependency on <c>Tosh.Language</c>.
/// </summary>
public interface IShellRefinementTypeDescriptor
{
    string Name { get; }
    string BaseTypeName { get; }
    string? Description { get; }
}
