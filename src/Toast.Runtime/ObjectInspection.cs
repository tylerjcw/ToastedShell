namespace Tosh.Runtime;

public sealed record ObjectInspection(
    int Index,
    string TypeName,
    string? AssemblyName,
    string? BaseTypeName,
    string Display,
    bool IsEnumerable,
    int? ItemCount,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<ObjectInspectionMember> Members,
    IReadOnlyList<string> ItemsPreview,
    bool HasMoreItems);
