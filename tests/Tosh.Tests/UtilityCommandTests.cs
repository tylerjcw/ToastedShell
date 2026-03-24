using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class UtilityCommandTests
{
    [Fact]
    public async Task Seq_generates_numeric_sequences()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("seq 3");

        Assert.Equal([1L, 2L, 3L], results.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Dirname_and_basename_split_paths()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var dirname = await engine.ExecuteToListAsync("dirname /usr/bin/bash");
        var basename = await engine.ExecuteToListAsync("basename /usr/bin/bash");

        Assert.Equal("/usr/bin", Assert.Single(dirname));
        Assert.Equal("bash", Assert.Single(basename));
    }

    [Fact]
    public async Task Head_tail_wc_uniq_cut_tr_and_grep_work_on_text_pipelines()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var head = await engine.ExecuteToListAsync("echo one two three | head -n 2");
        var tail = await engine.ExecuteToListAsync("echo one two three | tail -n 2");
        var wc = await engine.ExecuteToListAsync("echo one two three | wc");
        var uniq = await engine.ExecuteToListAsync("echo a a b | uniq");
        var uniqCount = await engine.ExecuteToListAsync("echo a a b | uniq -c");
        var cut = await engine.ExecuteToListAsync("echo \"alpha,beta,gamma\" | cut -d \",\" -f 2");
        var translated = await engine.ExecuteToListAsync("echo abc | tr a-z A-Z");
        var grep = await engine.ExecuteToListAsync("echo one two three | grep tw");

        Assert.Equal(["one", "two"], head.Cast<string>().ToArray());
        Assert.Equal(["two", "three"], tail.Cast<string>().ToArray());

        var stats = Assert.IsType<TextStatistics>(Assert.Single(wc));
        Assert.Equal(3, stats.Lines);
        Assert.Equal(3, stats.Words);

        Assert.Equal(["a", "b"], uniq.Cast<string>().ToArray());

        var countProjection = Assert.IsType<ProjectedObject>(uniqCount[0]);
        Assert.True(countProjection.TryGetValue("Count", out var countValue));
        Assert.Equal(2, Assert.IsType<int>(countValue));

        Assert.Equal("beta", Assert.IsType<ShellTextLine>(Assert.Single(cut)).Text);
        Assert.Equal("ABC", Assert.IsType<ShellTextLine>(Assert.Single(translated)).Text);

        var match = Assert.IsType<GrepMatchInfo>(Assert.Single(grep));
        Assert.Equal("two", match.Text);
        Assert.Equal(2, match.LineNumber);
    }

    [Fact]
    public async Task Xargs_invokes_nested_commands()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo alpha beta | xargs echo prefix");

        Assert.Equal(["prefix", "alpha", "beta"], results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Free_and_uptime_return_system_info_objects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var free = await engine.ExecuteToListAsync("free");
        var uptime = await engine.ExecuteToListAsync("uptime");

        Assert.NotEmpty(free);
        Assert.All(free, item => Assert.IsType<MemoryUsageInfo>(item));
        Assert.IsType<SystemUptimeInfo>(Assert.Single(uptime));
    }

    [Fact]
    public async Task Du_find_and_stat_operate_on_file_system_entries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "b.txt"), "world");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var du = await engine.ExecuteToListAsync($"du -s {Quote(temporaryDirectory.Path)}");
        var find = await engine.ExecuteToListAsync($"find {Quote(temporaryDirectory.Path)} -maxdepth 1 -type d | get Name");
        var stat = await engine.ExecuteToListAsync($"stat {Quote(Path.Combine(temporaryDirectory.Path, "b.txt"))}");

        var usage = Assert.IsType<PathUsageInfo>(Assert.Single(du));
        Assert.True(usage.Size.Bytes >= 10);
        Assert.Contains(Path.GetFileName(temporaryDirectory.Path), find.Cast<string>());
        Assert.IsType<FileSystemEntry>(Assert.Single(stat));
    }

    [Fact]
    public async Task Readlink_realpath_ln_and_chmod_work_for_local_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        var linkPath = Path.Combine(temporaryDirectory.Path, "link.txt");
        await File.WriteAllTextAsync(targetPath, "toast");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var linkResults = await engine.ExecuteToListAsync($"ln -s {Quote(targetPath)} {Quote(linkPath)}");
        var readlinkResults = await engine.ExecuteToListAsync($"readlink {Quote(linkPath)}");
        var realpathResults = await engine.ExecuteToListAsync($"realpath {Quote(linkPath)}");
        await engine.ExecuteToListAsync($"chmod 600 {Quote(targetPath)}");

        Assert.IsType<FileSystemEntry>(Assert.Single(linkResults));
        Assert.Equal(targetPath, Assert.Single(readlinkResults));
        Assert.Equal(targetPath, Assert.Single(realpathResults));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(targetPath));
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-utility-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
