namespace Tosh.Runtime;

public sealed record UserIdentityInfo(
    FileSystemPrincipalInfo User,
    uint Uid,
    uint Euid,
    FileSystemPrincipalInfo Group,
    uint Gid,
    uint Egid,
    IReadOnlyList<FileSystemPrincipalInfo> Groups)
{
    public string UserName => User.DisplayName;

    public string GroupName => Group.DisplayName;
}
