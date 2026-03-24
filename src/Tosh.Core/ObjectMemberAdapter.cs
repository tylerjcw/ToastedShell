using System.IO;

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
        };

    public static bool TryGetMember(Type targetType, string memberName, out AdaptedObjectMember member)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

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
}
