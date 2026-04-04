using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class StreamCommandTests
{
    [Fact]
    public async Task Managed_file_io_commands_cover_text_lines_bytes_and_filesystem_entry_helpers()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var textPath = Path.Combine(temporaryDirectory.Path, "notes.txt");
        var binaryPath = Path.Combine(temporaryDirectory.Path, "data.bin");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var writeResults = await engine.ExecuteToListAsync($"write-file {Quote(textPath)} hello world");
        var appendResults = await engine.ExecuteToListAsync($"append-file {Quote(textPath)} \" more\"");
        var readTextResults = await engine.ExecuteToListAsync($"read-file {Quote(textPath)}");
        await engine.ExecuteToListAsync($"write-file {Quote(textPath)} \"alpha\\nbeta\"");
        var readLinesResults = await engine.ExecuteToListAsync($"read-lines {Quote(textPath)}");
        var writeBytesResults = await engine.ExecuteToListAsync($"write-bytes {Quote(binaryPath)} [1, 2, 3, 255]");
        var readBytesResults = await engine.ExecuteToListAsync($"read-bytes {Quote(binaryPath)}");
        var helperTextResults = await engine.ExecuteToListAsync($"stat {Quote(textPath)} | call ReadAllText");
        var helperByteResults = await engine.ExecuteToListAsync($"stat {Quote(binaryPath)} | call ReadAllBytes");

        Assert.IsType<FileSystemEntry>(Assert.Single(writeResults));
        Assert.IsType<FileSystemEntry>(Assert.Single(appendResults));
        Assert.Equal("hello world more", Assert.Single(readTextResults));
        Assert.Equal(["alpha", "beta"], readLinesResults.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
        Assert.IsType<FileSystemEntry>(Assert.Single(writeBytesResults));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, Assert.IsType<byte[]>(Assert.Single(readBytesResults)));
        Assert.Equal("alpha\nbeta", Assert.Single(helperTextResults));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, Assert.IsType<byte[]>(Assert.Single(helperByteResults)));
    }

    [Fact]
    public async Task Read_file_and_read_lines_accept_pipeline_path_input()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var textPath = Path.Combine(temporaryDirectory.Path, "notes.txt");
        await File.WriteAllTextAsync(textPath, "alpha\nbeta");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var wholeText = await engine.ExecuteToListAsync($"echo {Quote(textPath)} | read-file");
        var lines = await engine.ExecuteToListAsync($"echo {Quote(textPath)} | read-lines");

        Assert.Equal("alpha\nbeta", Assert.Single(wholeText));
        Assert.Equal(["alpha", "beta"], lines.Cast<ShellTextLine>().Select(line => line.Text).ToArray());
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-stream-tests-{Guid.NewGuid():N}");
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
