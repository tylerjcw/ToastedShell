namespace Tosh.Runtime;

public sealed record ObjectInspectionMember(
    string Name,
    string MemberKind,
    string TypeName,
    string Display);
