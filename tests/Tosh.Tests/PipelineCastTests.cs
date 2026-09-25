using Tosh.Runtime;
using Xunit;

namespace Tosh.Tests;

public sealed class PipelineCastTests
{
    [Fact]
    public async Task As_casts_primitives_in_pipeline()
    {
        var engine = ShellEngine.CreateFullShell();

        var intResults = await engine.ExecuteToListAsync("echo \"42\" | as int");
        var strResults = await engine.ExecuteToListAsync("echo 100 | as string");

        Assert.Equal(42, Assert.Single(intResults));
        Assert.Equal("100", Assert.Single(strResults));
    }

    [Fact]
    public async Task As_casts_quantities_in_pipeline()
    {
        var engine = ShellEngine.CreateFullShell();

        var timeResults = await engine.ExecuteToListAsync("echo \"10s\" | as TimeSpan");
        var storageResults = await engine.ExecuteToListAsync("echo \"16384 kB\" | as StorageSize");

        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(timeResults));
        var storage = Assert.IsType<StorageSize>(Assert.Single(storageResults));
        Assert.True(storage.Bytes > 0);
    }

    [Fact]
    public async Task As_casts_records_to_user_declared_fluid_records()
    {
        var engine = ShellEngine.CreateFullShell();

        var script = """
            fluid record TestProcess(Pid: int, Name: string)
            var r = {| Pid: 1234, Name: "worker" |}
            $r | as TestProcess
            """;

        var results = await engine.ExecuteToListAsync(script);
        var instance = Assert.Single(results);
        var typed = Assert.IsAssignableFrom<IShellTypedObject>(instance);
        Assert.Equal("TestProcess", typed.ShellTypeDescriptor.ShellTypeName);
        var record = Assert.IsAssignableFrom<IShellRecordObject>(instance);
        Assert.True(record.TryGetMember("Pid", out var pid));
        Assert.Equal(1234, pid);
        Assert.True(record.TryGetMember("Name", out var name));
        Assert.Equal("worker", name);
    }

    [Fact]
    public async Task As_and_cast_are_synonymous_in_pipeline()
    {
        var engine = ShellEngine.CreateFullShell();

        var asResults = await engine.ExecuteToListAsync("echo \"99\" | as int");
        var castResults = await engine.ExecuteToListAsync("echo \"99\" | cast int");

        Assert.Equal(Assert.Single(asResults), Assert.Single(castResults));
    }
}
