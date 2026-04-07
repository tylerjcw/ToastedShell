namespace Tosh.Core;

public sealed record InspectTreeFrame(
    object? RootValue,
    string? RootExpression,
    string TypeName,
    string? AssemblyName,
    string? BaseTypeName,
    string ValuePreview,
    IReadOnlyList<string> Breadcrumb,
    IReadOnlyList<InspectTreeNode> Nodes,
    int SummaryMemberCount);
