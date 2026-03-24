using Tosh.Core;

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

    private sealed record Root(Child? Inner);

    private sealed record Child(string Name);
}
