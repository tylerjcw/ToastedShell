using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class MemberPathTests
{
    [Fact]
    public void Nullable_member_path_supports_optional_segments()
    {
        var accessor = new ReflectionObjectAccessor();
        var root = new Root(new Child("toast"));

        var value = accessor.GetValue(root, "Inner?.Name");

        Assert.Equal("toast", value);
    }

    [Fact]
    public void Nullable_member_path_propagates_nulls_for_optional_segments()
    {
        var accessor = new ReflectionObjectAccessor();
        var root = new Root(null);

        var value = accessor.GetValue(root, "Inner?.Name");

        Assert.Null(value);
    }

    [Fact]
    public void Enum_numeric_value_is_available_through_member_paths()
    {
        var accessor = new ReflectionObjectAccessor();

        var value = accessor.GetValue(DayOfWeek.Friday, "NumericValue");

        Assert.Equal(5, Assert.IsType<int>(value));
    }

    [Fact]
    public void Enum_names_are_available_through_member_paths()
    {
        var accessor = new ReflectionObjectAccessor();

        var value = accessor.GetValue(DayOfWeek.Friday, "Names");

        Assert.Equal(["Friday"], Assert.IsType<string[]>(value));
    }

    [Fact]
    public void Block_device_shell_aliases_are_available_through_member_paths()
    {
        var accessor = new ReflectionObjectAccessor();
        var device = new BlockDeviceInfo
        {
            FileSystemType = "ntfs",
            FileSystemAvailable = StorageSize.FromBytes(10_000),
        };

        Assert.Equal("ntfs", accessor.GetValue(device, "FsType"));
        Assert.Equal(StorageSize.FromBytes(10_000), accessor.GetValue(device, "FsAvail"));
    }

    [Fact]
    public void Mount_shell_aliases_are_available_through_member_paths()
    {
        var accessor = new ReflectionObjectAccessor();
        var mount = new MountInfo
        {
            FileSystemType = "ext4",
            FileSystemRoot = "/",
            TaskId = 42,
        };

        Assert.Equal("ext4", accessor.GetValue(mount, "FsType"));
        Assert.Equal("/", accessor.GetValue(mount, "FsRoot"));
        Assert.Equal(42, accessor.GetValue(mount, "Tid"));
    }

    [Fact]
    public void File_descriptor_shell_aliases_are_available_through_member_paths()
    {
        var accessor = new ReflectionObjectAccessor();
        var fd = new FileDescriptorInfo
        {
            Association = "cwd",
            ExtendedMode = "rw----",
            MountId = 99,
        };

        Assert.Equal("cwd", accessor.GetValue(fd, "Assoc"));
        Assert.Equal("rw----", accessor.GetValue(fd, "XMode"));
        Assert.Equal(99, accessor.GetValue(fd, "MntId"));
    }

    private sealed record Root(Child? Inner);

    private sealed record Child(string Name);
}
