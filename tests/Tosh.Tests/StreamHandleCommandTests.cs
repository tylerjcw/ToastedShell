using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StreamHandleCommandTests
{
    [Fact]
    public async Task Managed_file_handles_support_open_read_write_methods_and_forget_cleanup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var textPath = Path.Combine(temporaryDirectory.Path, "notes.txt");
        var binaryPath = Path.Combine(temporaryDirectory.Path, "data.bin");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync($"var writer = open-file --write {Quote(textPath)}");
        await engine.ExecuteToListAsync("write-line-to $writer alpha");
        await engine.ExecuteToListAsync("write-line-to $writer beta");
        await engine.ExecuteToListAsync("flush $writer");
        await engine.ExecuteToListAsync("close $writer");

        var wholeText = await engine.ExecuteToListAsync($"read-file {Quote(textPath)} | raw");
        Assert.Equal($"alpha{Environment.NewLine}beta{Environment.NewLine}", Assert.IsType<ShellTextLine>(Assert.Single(wholeText)).Text);

        await engine.ExecuteToListAsync($"var reader = open-file {Quote(textPath)}");
        var firstLine = await engine.ExecuteToListAsync("read-line-from $reader");
        var remainder = await engine.ExecuteToListAsync("read-to-end $reader | raw");

        Assert.Equal(["alpha"], firstLine.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
        Assert.Equal($"beta{Environment.NewLine}", Assert.IsType<ShellTextLine>(Assert.Single(remainder)).Text);

        var methodOpenedLine = await engine.ExecuteToListAsync($"stat {Quote(textPath)} | call OpenText | read-line-from");
        Assert.Equal(["alpha"], methodOpenedLine.Cast<ShellTextLine>().Select(line => line.Text).ToArray());

        await engine.ExecuteToListAsync($"var binaryWriter = open-file --binary --write {Quote(binaryPath)}");
        await engine.ExecuteToListAsync("write-to $binaryWriter [1, 2, 3, 255]");
        await engine.ExecuteToListAsync("close $binaryWriter");

        var binaryChunk = await engine.ExecuteToListAsync($"stat {Quote(binaryPath)} | call OpenRead | read-from 2");
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(Assert.Single(binaryChunk)));

        await engine.ExecuteToListAsync($"var cleanup = open-file {Quote(textPath)}");
        var forgetResult = await engine.ExecuteToListAsync("forget cleanup | get { FreedValue, FreedValueKind }");
        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(forgetResult));
        Assert.Equal(true, record["FreedValue"]);
        Assert.Equal("ManagedFileHandle", record["FreedValueKind"]);
    }

    [Fact]
    public async Task Managed_file_handles_support_seek_copy_position_length_and_session_tracking()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.Path, "source.bin");
        var copyPath = Path.Combine(temporaryDirectory.Path, "copy.bin");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

        var baselineHandles = ManagedFileHandle.GetOpenHandles().Count;
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync($"var reader = open-file --binary {Quote(sourcePath)}");
        var openHandleCount = await engine.ExecuteToListAsync("echo $tosh.Session.OpenHandleCount");
        Assert.Equal(baselineHandles + 1, Assert.IsType<int>(Assert.Single(openHandleCount)));

        var seekChunk = await engine.ExecuteToListAsync("echo $reader | seek 1 begin | read-from 2");
        Assert.Equal(new byte[] { 2, 3 }, Assert.IsType<byte[]>(Assert.Single(seekChunk)));

        await engine.ExecuteToListAsync($"var writer = open-file --write {Quote(copyPath)}");
        await engine.ExecuteToListAsync("write-to $writer abcd");

        var position = await engine.ExecuteToListAsync("position $writer");
        var length = await engine.ExecuteToListAsync("length $writer");
        Assert.Equal(4L, Assert.IsType<long>(Assert.Single(position)));
        Assert.Equal(4L, Assert.IsType<long>(Assert.Single(length)));

        await engine.ExecuteToListAsync("close $writer");
        await engine.ExecuteToListAsync($"var src = open-file --binary {Quote(sourcePath)}");
        await engine.ExecuteToListAsync($"var dst = open-file --binary --write {Quote(copyPath)}");

        var copied = await engine.ExecuteToListAsync("copy-to $src $dst");
        Assert.Equal(4L, Assert.IsType<long>(Assert.Single(copied)));

        await engine.ExecuteToListAsync("close $reader $src $dst");

        var finalHandleCount = await engine.ExecuteToListAsync("echo $tosh.Session.OpenHandleCount");
        Assert.Equal(baselineHandles, Assert.IsType<int>(Assert.Single(finalHandleCount)));

        var copiedBytes = await engine.ExecuteToListAsync($"read-bytes {Quote(copyPath)}");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.IsType<byte[]>(Assert.Single(copiedBytes)));
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-stream-handle-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
