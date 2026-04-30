namespace Tosh.Runtime;

public sealed record class FileDescriptorInfo
{
    public string? Command { get; init; }

    public int? ProcessId { get; init; }

    public int? ThreadId { get; init; }

    public string? User { get; init; }

    public string? Association { get; init; }

    public int? FileDescriptor { get; init; }

    public string? Mode { get; init; }

    public string? ExtendedMode { get; init; }

    public string? Type { get; init; }

    public string? Source { get; init; }

    public int? MountId { get; init; }

    public long? Inode { get; init; }

    public string? Name { get; init; }

    public string? Flags { get; init; }

    public string? Owner { get; init; }

    public StorageSize? Size { get; init; }

    public long? Position { get; init; }

    public bool? Deleted { get; init; }

    public string? Device { get; init; }

    public string? MajorMinor { get; init; }

    public string? SocketType { get; init; }

    public string? SocketState { get; init; }

    public bool? SocketListening { get; init; }

    public string? ProtocolName { get; init; }

    public string? UnixPath { get; init; }

    public string? InetLocalAddress { get; init; }

    public string? InetRemoteAddress { get; init; }

    public string? Inet6LocalAddress { get; init; }

    public string? Inet6RemoteAddress { get; init; }

    public string? TcpLocalAddress { get; init; }

    public string? TcpRemoteAddress { get; init; }

    public int? TcpLocalPort { get; init; }

    public int? TcpRemotePort { get; init; }

    public string? UdpLocalAddress { get; init; }

    public string? UdpRemoteAddress { get; init; }

    public int? UdpLocalPort { get; init; }

    public int? UdpRemotePort { get; init; }

    public IReadOnlyDictionary<string, object?> AdditionalFields { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public object? GetFieldValue(string selectionKey)
    {
        return selectionKey.ToUpperInvariant() switch
        {
            "COMMAND" => Command,
            "PID" => ProcessId,
            "TID" => ThreadId,
            "USER" => User,
            "ASSOC" => Association,
            "FD" => FileDescriptor,
            "MODE" => Mode,
            "XMODE" => ExtendedMode,
            "TYPE" => Type,
            "SOURCE" => Source,
            "MNTID" => MountId,
            "INODE" => Inode,
            "NAME" => Name,
            "FLAGS" => Flags,
            "OWNER" => Owner,
            "SIZE" => Size,
            "POS" => Position,
            "DELETED" => Deleted,
            "DEV" => Device,
            "MAJ:MIN" => MajorMinor,
            "SOCK.TYPE" => SocketType,
            "SOCK.STATE" => SocketState,
            "SOCK.LISTENING" => SocketListening,
            "SOCK.PROTONAME" => ProtocolName,
            "UNIX.PATH" => UnixPath,
            "INET.LADDR" => InetLocalAddress,
            "INET.RADDR" => InetRemoteAddress,
            "INET6.LADDR" => Inet6LocalAddress,
            "INET6.RADDR" => Inet6RemoteAddress,
            "TCP.LADDR" => TcpLocalAddress,
            "TCP.RADDR" => TcpRemoteAddress,
            "TCP.LPORT" => TcpLocalPort,
            "TCP.RPORT" => TcpRemotePort,
            "UDP.LADDR" => UdpLocalAddress,
            "UDP.RADDR" => UdpRemoteAddress,
            "UDP.LPORT" => UdpLocalPort,
            "UDP.RPORT" => UdpRemotePort,
            _ => AdditionalFields.TryGetValue(selectionKey, out var value) ? value : null,
        };
    }

    public IEnumerable<string> GetAllFieldKeys()
    {
        yield return "COMMAND";
        yield return "PID";
        yield return "TID";
        yield return "USER";
        yield return "ASSOC";
        yield return "FD";
        yield return "MODE";
        yield return "XMODE";
        yield return "TYPE";
        yield return "SOURCE";
        yield return "MNTID";
        yield return "INODE";
        yield return "NAME";
        yield return "FLAGS";
        yield return "OWNER";
        yield return "SIZE";
        yield return "POS";
        yield return "DELETED";
        yield return "DEV";
        yield return "MAJ:MIN";
        yield return "SOCK.TYPE";
        yield return "SOCK.STATE";
        yield return "SOCK.LISTENING";
        yield return "SOCK.PROTONAME";
        yield return "UNIX.PATH";
        yield return "INET.LADDR";
        yield return "INET.RADDR";
        yield return "INET6.LADDR";
        yield return "INET6.RADDR";
        yield return "TCP.LADDR";
        yield return "TCP.RADDR";
        yield return "TCP.LPORT";
        yield return "TCP.RPORT";
        yield return "UDP.LADDR";
        yield return "UDP.RADDR";
        yield return "UDP.LPORT";
        yield return "UDP.RPORT";

        foreach (var key in AdditionalFields.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            yield return key;
        }
    }

    public override string ToString()
    {
        var processText = ProcessId is int pid
            ? $"{(string.IsNullOrWhiteSpace(Command) ? "pid" : Command)}[{pid}]"
            : (Command ?? "fd");

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return $"{processText} {Name}";
        }

        if (FileDescriptor is int fd)
        {
            return $"{processText} fd {fd}";
        }

        return processText;
    }
}
