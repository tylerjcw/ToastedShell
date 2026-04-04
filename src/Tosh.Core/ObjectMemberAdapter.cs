using System.IO;
using System.Net;

namespace Tosh.Core;

internal sealed record AdaptedObjectMember(
    Type ValueType,
    bool IsNullable,
    Func<object, object?> GetValue);

internal static class ObjectMemberAdapter
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, AdaptedObjectMember>> MembersByType =
        new Dictionary<Type, IReadOnlyDictionary<string, AdaptedObjectMember>>
        {
            [typeof(DriveInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["AvailableFreeSpace"] = new(typeof(StorageSize), IsNullable: true, target => TryGetStorageSize(() => ((DriveInfo)target).AvailableFreeSpace)),
                ["TotalFreeSpace"] = new(typeof(StorageSize), IsNullable: true, target => TryGetStorageSize(() => ((DriveInfo)target).TotalFreeSpace)),
                ["TotalSize"] = new(typeof(StorageSize), IsNullable: true, target => TryGetStorageSize(() => ((DriveInfo)target).TotalSize)),
            },
            [typeof(BlockDeviceInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["KName"] = new(typeof(string), IsNullable: true, target => ((BlockDeviceInfo)target).KernelName),
                ["MajMin"] = new(typeof(string), IsNullable: true, target => ((BlockDeviceInfo)target).MajorMinor),
                ["FsType"] = new(typeof(string), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemType),
                ["FsVer"] = new(typeof(string), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemVersion),
                ["FsAvail"] = new(typeof(StorageSize), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemAvailable),
                ["FsSize"] = new(typeof(StorageSize), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemSize),
                ["FsUsed"] = new(typeof(StorageSize), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemUsed),
                ["FsUsePercent"] = new(typeof(int), IsNullable: true, target => ((BlockDeviceInfo)target).FileSystemUsePercent),
                ["FsRoots"] = new(typeof(IReadOnlyList<string>), IsNullable: false, target => ((BlockDeviceInfo)target).FileSystemRoots),
            },
            [typeof(IpInterfaceInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["MAC"] = new(typeof(string), IsNullable: true, target => ((IpInterfaceInfo)target).HardwareAddress),
                ["QDisc"] = new(typeof(string), IsNullable: true, target => ((IpInterfaceInfo)target).QueueDiscipline),
                ["PermAddr"] = new(typeof(string), IsNullable: true, target => ((IpInterfaceInfo)target).PermanentAddress),
            },
            [typeof(IpRouteInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["PrefSrc"] = new(typeof(IPAddress), IsNullable: true, target => ((IpRouteInfo)target).PreferredSource),
                ["Type"] = new(typeof(string), IsNullable: true, target => ((IpRouteInfo)target).RouteType),
            },
            [typeof(SystemdUnitInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Load"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitInfo)target).LoadState),
                ["Active"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitInfo)target).ActiveState),
                ["Sub"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitInfo)target).SubState),
                ["Type"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitInfo)target).UnitType),
                ["Name"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitInfo)target).Unit),
            },
            [typeof(SystemdUnitFileInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitFileInfo)target).UnitFile),
                ["Type"] = new(typeof(string), IsNullable: false, target => ((SystemdUnitFileInfo)target).UnitType),
                ["Enabled"] = new(typeof(bool), IsNullable: false, target => ((SystemdUnitFileInfo)target).IsEnabled),
                ["Masked"] = new(typeof(bool), IsNullable: false, target => ((SystemdUnitFileInfo)target).IsMasked),
            },
            [typeof(SystemdUnitPropertySet)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Load"] = new(typeof(string), IsNullable: true, target => ((SystemdUnitPropertySet)target).LoadState),
                ["Active"] = new(typeof(string), IsNullable: true, target => ((SystemdUnitPropertySet)target).ActiveState),
                ["Sub"] = new(typeof(string), IsNullable: true, target => ((SystemdUnitPropertySet)target).SubState),
                ["MainPID"] = new(typeof(int), IsNullable: true, target => ((SystemdUnitPropertySet)target).MainPid),
                ["ExecMainPID"] = new(typeof(int), IsNullable: true, target => ((SystemdUnitPropertySet)target).ExecMainPid),
                ["InvocationID"] = new(typeof(Guid), IsNullable: true, target => ((SystemdUnitPropertySet)target).InvocationId),
                ["Type"] = new(typeof(string), IsNullable: true, target => ((SystemdUnitPropertySet)target).UnitType),
                ["Logs"] = new(typeof(IReadOnlyList<SystemdJournalEntry>), IsNullable: false, target => ((SystemdUnitPropertySet)target).RecentLog),
                ["RecentLogs"] = new(typeof(IReadOnlyList<SystemdJournalEntry>), IsNullable: false, target => ((SystemdUnitPropertySet)target).RecentLog),
            },
            [typeof(SystemdJournalEntry)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["PID"] = new(typeof(int), IsNullable: true, target => ((SystemdJournalEntry)target).ProcessId),
                ["SyslogPID"] = new(typeof(int), IsNullable: true, target => ((SystemdJournalEntry)target).SyslogPid),
                ["UID"] = new(typeof(int), IsNullable: true, target => ((SystemdJournalEntry)target).UserId),
                ["GID"] = new(typeof(int), IsNullable: true, target => ((SystemdJournalEntry)target).GroupId),
                ["PriorityText"] = new(typeof(string), IsNullable: false, target => ((SystemdJournalEntry)target).PriorityName),
                ["InvocationID"] = new(typeof(Guid), IsNullable: true, target => ((SystemdJournalEntry)target).InvocationId),
                ["CMDLINE"] = new(typeof(string), IsNullable: true, target => ((SystemdJournalEntry)target).CommandLine),
                ["Facility"] = new(typeof(int), IsNullable: true, target => ((SystemdJournalEntry)target).Facility),
            },
            [typeof(SystemdLoginSessionInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = new(typeof(string), IsNullable: false, target => ((SystemdLoginSessionInfo)target).Session),
                ["UID"] = new(typeof(int), IsNullable: false, target => ((SystemdLoginSessionInfo)target).UserId),
                ["PID"] = new(typeof(int), IsNullable: true, target => ((SystemdLoginSessionInfo)target).Leader),
                ["TTY"] = new(typeof(string), IsNullable: true, target => ((SystemdLoginSessionInfo)target).Tty),
                ["UserName"] = new(typeof(string), IsNullable: false, target => ((SystemdLoginSessionInfo)target).User),
            },
            [typeof(SystemdLoginUserInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["UID"] = new(typeof(int), IsNullable: false, target => ((SystemdLoginUserInfo)target).UserId),
                ["UserName"] = new(typeof(string), IsNullable: false, target => ((SystemdLoginUserInfo)target).User),
            },
            [typeof(SystemdLoginSeatInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = new(typeof(string), IsNullable: false, target => ((SystemdLoginSeatInfo)target).Seat),
                ["Id"] = new(typeof(string), IsNullable: false, target => ((SystemdLoginSeatInfo)target).Seat),
            },
            [typeof(SystemdPropertySet)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = new(typeof(object), IsNullable: true, target => ((SystemdPropertySet)target).Id),
                ["UID"] = new(typeof(object), IsNullable: true, target => ((SystemdPropertySet)target).Properties.TryGetValue("UID", out var uid) ? uid : null),
                ["PID"] = new(typeof(object), IsNullable: true, target => ((SystemdPropertySet)target).Properties.TryGetValue("Leader", out var leader) ? leader : null),
                ["TTY"] = new(typeof(object), IsNullable: true, target => ((SystemdPropertySet)target).Properties.TryGetValue("TTY", out var tty) ? tty : null),
            },
            [typeof(SystemdHostInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["OS"] = new(typeof(string), IsNullable: true, target => ((SystemdHostInfo)target).OperatingSystem),
                ["Kernel"] = new(typeof(string), IsNullable: false, target => ((SystemdHostInfo)target).Kernel),
                ["HomeURL"] = new(typeof(Uri), IsNullable: true, target => ((SystemdHostInfo)target).OperatingSystemHomeUrl),
            },
            [typeof(SystemdNetworkLinkInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = new(typeof(string), IsNullable: false, target => ((SystemdNetworkLinkInfo)target).Link),
                ["State"] = new(typeof(string), IsNullable: false, target => ((SystemdNetworkLinkInfo)target).OperationalState),
                ["Operational"] = new(typeof(string), IsNullable: false, target => ((SystemdNetworkLinkInfo)target).OperationalState),
                ["Setup"] = new(typeof(string), IsNullable: false, target => ((SystemdNetworkLinkInfo)target).SetupState),
                ["Managed"] = new(typeof(bool), IsNullable: false, target => ((SystemdNetworkLinkInfo)target).IsManaged),
            },
            [typeof(FileDescriptorInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pid"] = new(typeof(int), IsNullable: true, target => ((FileDescriptorInfo)target).ProcessId),
                ["Tid"] = new(typeof(int), IsNullable: true, target => ((FileDescriptorInfo)target).ThreadId),
                ["Assoc"] = new(typeof(string), IsNullable: true, target => ((FileDescriptorInfo)target).Association),
                ["Fd"] = new(typeof(int), IsNullable: true, target => ((FileDescriptorInfo)target).FileDescriptor),
                ["XMode"] = new(typeof(string), IsNullable: true, target => ((FileDescriptorInfo)target).ExtendedMode),
                ["MntId"] = new(typeof(int), IsNullable: true, target => ((FileDescriptorInfo)target).MountId),
                ["MajMin"] = new(typeof(string), IsNullable: true, target => ((FileDescriptorInfo)target).MajorMinor),
                ["SockType"] = new(typeof(string), IsNullable: true, target => ((FileDescriptorInfo)target).SocketType),
                ["SockState"] = new(typeof(string), IsNullable: true, target => ((FileDescriptorInfo)target).SocketState),
                ["SockListening"] = new(typeof(bool), IsNullable: true, target => ((FileDescriptorInfo)target).SocketListening),
            },
            [typeof(MountInfo)] = new Dictionary<string, AdaptedObjectMember>(StringComparer.OrdinalIgnoreCase)
            {
                ["FsType"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).FileSystemType),
                ["FsRoot"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).FileSystemRoot),
                ["Avail"] = new(typeof(StorageSize), IsNullable: true, target => ((MountInfo)target).Available),
                ["FsOptions"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).FileSystemOptions),
                ["OptFields"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).OptionalFields),
                ["PartLabel"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).PartitionLabel),
                ["PartUuid"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).PartitionUuid),
                ["PartUUID"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).PartitionUuid),
                ["MajMin"] = new(typeof(string), IsNullable: true, target => ((MountInfo)target).MajorMinor),
                ["Tid"] = new(typeof(int), IsNullable: true, target => ((MountInfo)target).TaskId),
                ["UniqId"] = new(typeof(long), IsNullable: true, target => ((MountInfo)target).UniqueId),
                ["PassNo"] = new(typeof(int), IsNullable: true, target => ((MountInfo)target).PassNumber),
                ["InoTotal"] = new(typeof(long), IsNullable: true, target => ((MountInfo)target).InodesTotal),
                ["InoUsed"] = new(typeof(long), IsNullable: true, target => ((MountInfo)target).InodesUsed),
                ["InoAvail"] = new(typeof(long), IsNullable: true, target => ((MountInfo)target).InodesAvailable),
                ["InoUsePercent"] = new(typeof(int), IsNullable: true, target => ((MountInfo)target).InodeUsePercent),
            },
        };

    public static bool TryGetMember(Type targetType, string memberName, out AdaptedObjectMember member)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        if (TryGetEnumMember(targetType, memberName, out member))
        {
            return true;
        }

        foreach (var entry in MembersByType)
        {
            if (entry.Key.IsAssignableFrom(targetType) &&
                entry.Value.TryGetValue(memberName, out member!))
            {
                return true;
            }
        }

        member = null!;
        return false;
    }

    public static IEnumerable<string> GetMemberNames(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (targetType.IsEnum)
        {
            names.Add("NumericValue");
            names.Add("Names");
        }

        foreach (var entry in MembersByType)
        {
            if (!entry.Key.IsAssignableFrom(targetType))
            {
                continue;
            }

            foreach (var name in entry.Value.Keys)
            {
                names.Add(name);
            }
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetValue(object target, string memberName, out object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        if (TryGetMember(target.GetType(), memberName, out var member))
        {
            value = member.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    public static object? SafeGetValue(object target, string memberName)
    {
        try
        {
            return TryGetValue(target, memberName, out var value)
                ? value
                : null;
        }
        catch (Exception exception)
        {
            return $"<unavailable: {exception.GetType().Name}>";
        }
    }

    private static StorageSize? TryGetStorageSize(Func<long> getBytes)
    {
        try
        {
            return StorageSize.FromBytes(getBytes());
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetEnumMember(Type targetType, string memberName, out AdaptedObjectMember member)
    {
        if (!targetType.IsEnum)
        {
            member = null!;
            return false;
        }

        if (string.Equals(memberName, "NumericValue", StringComparison.OrdinalIgnoreCase))
        {
            var underlyingType = Enum.GetUnderlyingType(targetType);
            member = new AdaptedObjectMember(
                underlyingType,
                IsNullable: false,
                target => ReflectionMetadataUtilities.GetEnumNumericValue((Enum)target));
            return true;
        }

        if (string.Equals(memberName, "Names", StringComparison.OrdinalIgnoreCase))
        {
            member = new AdaptedObjectMember(
                typeof(string[]),
                IsNullable: false,
                target => ReflectionMetadataUtilities.GetEnumNames((Enum)target).ToArray());
            return true;
        }

        member = null!;
        return false;
    }
}
