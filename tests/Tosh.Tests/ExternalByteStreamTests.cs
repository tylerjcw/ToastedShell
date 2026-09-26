using System.Globalization;
using System.IO.Compression;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A program's bytes reach the next program, a redirected file, or come from an <c>in&lt;</c>
/// file unchanged — <c>TOSH-0012</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every one of those paths used to decode a program's stdout as UTF-8 lines and re-encode them
/// on the other side. 100,000 random bytes piped from <c>cat</c> into <c>sha256sum</c> arrived
/// as 181,127, because each invalid sequence became U+FFFD; <c>cat x | gzip -c out&gt; x.gz</c>
/// wrote an archive that decompressed to nothing; and output without a final newline gained
/// one. The payload here is chosen to break all of that at once: every byte value, sequences
/// that are not UTF-8, and no final newline.
/// </para>
/// <para>
/// The programs are named by absolute path because <c>cat</c>, <c>head</c> and <c>wc</c> are
/// TōSh builtins, and the bare names would never start a process. On Windows these return
/// early: the project references no dynamic-skip package, the same limitation
/// <see cref="TtyCaptureTests"/> records.
/// </para>
/// </remarks>
public sealed class ExternalByteStreamTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"tosh-byte-stream-tests-{Guid.NewGuid():N}");

    public ExternalByteStreamTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_program_piped_into_a_program_receives_every_byte()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");

        var results = await RunAsync($"{Program("cat")} {input} | {Program("od")} -An -v -tx1");

        Assert.Equal(Payload, ParseOctalDump(results));
    }

    [Fact]
    public async Task A_redirected_program_writes_its_bytes_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");
        var output = Path.Combine(_directory, "out.bin");

        await RunAsync($"{Program("cat")} {input} out> {output}");

        Assert.Equal(Payload, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task A_compressed_stream_redirected_to_a_file_round_trips()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");
        var archive = Path.Combine(_directory, "out.gz");

        await RunAsync($"{Program("cat")} {input} | {Program("gzip")} -c out> {archive}");

        Assert.Equal(Payload, await DecompressAsync(archive));
    }

    [Fact]
    public async Task An_input_redirection_feeds_a_program_the_files_bytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");

        var results = await RunAsync($"{Program("od")} -An -v -tx1 in< {input}");

        Assert.Equal(Payload, ParseOctalDump(results));
    }

    [Fact]
    public async Task Output_without_a_final_newline_gains_none()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = Path.Combine(_directory, "unterminated.txt");
        await File.WriteAllTextAsync(input, "no newline at the end");
        var piped = Path.Combine(_directory, "piped.txt");
        var redirected = Path.Combine(_directory, "redirected.txt");

        await RunAsync($"{Program("cat")} {input} | {Program("cat")} out> {piped}");
        await RunAsync($"{Program("cat")} {input} out> {redirected}");

        Assert.Equal("no newline at the end", await File.ReadAllTextAsync(piped));
        Assert.Equal("no newline at the end", await File.ReadAllTextAsync(redirected));
    }

    [Fact]
    public async Task A_reader_that_stops_early_ends_the_writer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // `yes` never stops by itself. Once `head` has its lines and exits, the only thing
        // that ends `yes` is its pipe closing; a pipe left open with nobody draining it blocks
        // `yes` forever and the pipeline with it.
        var results = await RunAsync($"{Program("yes")} | {Program("head")} -n 3");

        Assert.Equal(["y", "y", "y"], results.Select(result => result?.ToString()!).ToArray());
    }

    [Fact]
    public async Task A_tosh_stage_between_two_programs_still_sees_lines()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A byte path runs only between adjacent programs. `where` is a command stage too, and
        // must neither be skipped nor handed bytes it cannot filter.
        var results = await RunAsync(
            $"{Program("printf")} 'a\\nbb\\nccc\\n' | where _.Length > 1 | {Program("wc")} -l");

        Assert.Equal("2", Assert.Single(results)?.ToString()?.Trim());
    }

    [Fact]
    public async Task A_program_before_a_tosh_stage_still_yields_lines()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var results = await RunAsync($"{Program("printf")} 'alpha\\nbeta\\n' | type-of | get Name");

        Assert.Equal(["ShellTextLine", "ShellTextLine"], results.Select(result => result?.ToString()!).ToArray());
    }

    [Fact]
    public async Task A_producer_speaking_tssp_still_yields_records()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var producer = WriteTsspProducer();

        var records = await RunAsync($"./{producer} | get name");
        var intoProgram = await RunAsync($"./{producer} | {Program("cat")}");

        Assert.Equal("alpha", Assert.Single(records)?.ToString());

        // Records, serialised for the program — not the protocol's own bytes, which is what a
        // claim made before the header was read would have sent.
        var text = string.Join('\n', intoProgram.Select(item => item?.ToString()));
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOSHSTREAM", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_background_pipeline_redirects_its_bytes_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");
        var archive = Path.Combine(_directory, "background.gz");

        await RunInBackgroundAsync($"{Program("cat")} {input} | {Program("gzip")} -c out> {archive} &");

        Assert.Equal(Payload, await DecompressAsync(archive));
    }

    [Fact]
    public async Task A_background_redirection_writes_no_byte_order_mark()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var single = Path.Combine(_directory, "single.txt");
        var first = Path.Combine(_directory, "first.txt");
        var second = Path.Combine(_directory, "second.txt");

        // One target takes the byte path; two take the text path. Both used to begin with the
        // three bytes of a UTF-8 byte-order mark.
        await RunInBackgroundAsync($"{Program("printf")} 'hi\\n' out> {single} &");
        await RunInBackgroundAsync($"{Program("printf")} 'hi\\n' out> {first} out> {second} &");

        Assert.Equal("hi\n"u8.ToArray(), await File.ReadAllBytesAsync(single));
        Assert.Equal("hi\n"u8.ToArray(), await File.ReadAllBytesAsync(first));
        Assert.Equal("hi\n"u8.ToArray(), await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task A_background_input_redirection_feeds_the_files_bytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var input = WritePayload("in.bin");
        var dump = Path.Combine(_directory, "dump.txt");

        await RunInBackgroundAsync($"{Program("od")} -An -v -tx1 in< {input} out> {dump} &");

        Assert.Equal(Payload, ParseOctalDump(await File.ReadAllLinesAsync(dump)));
    }

    [Fact]
    public async Task A_background_reader_that_stops_early_ends_the_writer()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stayed "running" forever: the pipe pump gave up when `head` left, but kept its end of
        // `yes`'s stdout open, so `yes` blocked on a pipe nothing would ever drain.
        await RunInBackgroundAsync($"{Program("yes")} | {Program("head")} -n 1 &");
    }

    [Fact]
    public void A_context_derived_with_with_cannot_use_the_byte_path()
    {
        var runtime = ToshRuntime.CreateDefault();
        var handoff = new RawByteHandoff();
        var context = new CommandContext(
            runtime.Language,
            AsyncEnumerableExtensions.Empty<object?>(),
            [],
            CancellationToken.None)
        {
            RawInput = handoff,
            RawOutput = handoff,
        };
        handoff.BindProducer(context);
        handoff.BindConsumer(context);
        handoff.Offer(new RawByteDestination(Stream.Null, ReaderCanLeave: true));

        // What a renderer or a callback is handed: a copy that carries the reference.
        var derived = context with { Arguments = ["something else"] };

        Assert.Same(handoff, derived.RawOutput);
        Assert.Null(derived.RawInputForThisStage);
        Assert.False(derived.TryClaimRawOutput(out _));

        Assert.Same(handoff, context.RawInputForThisStage);
        Assert.True(context.TryClaimRawOutput(out var destination));
        Assert.Same(Stream.Null, destination.Stream);

        // A claim is exclusive.
        Assert.False(context.TryClaimRawOutput(out _));
    }

    [Fact]
    public async Task A_reader_that_leaves_ends_the_copy_without_an_error_but_a_failed_file_write_is_one()
    {
        var source = new MemoryStream(new byte[256 * 1024]);

        var toPipe = new RawByteDestination(new FailingStream(), ReaderCanLeave: true);
        Assert.False(await toPipe.CopyFromAsync(source, ReadOnlyMemory<byte>.Empty, CancellationToken.None));

        source.Position = 0;
        var toFile = new RawByteDestination(new FailingStream(), ReaderCanLeave: false);
        await Assert.ThrowsAsync<IOException>(
            () => toFile.CopyFromAsync(source, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task The_protocol_sniff_gives_up_at_the_first_byte_that_differs()
    {
        // A program that writes two bytes and then waits. The sniff used to wait for as many
        // bytes as the protocol's magic is long, so those two reached nobody until the program
        // wrote nine more or exited.
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync("ab"u8.ToArray());
        var parser = new Tosh.Stdlib.Tssp.TsspParser(pipe.Reader.AsStream());

        var header = await parser.TryReadHeaderAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Null(header);
        Assert.Equal("ab"u8.ToArray(), parser.SniffedBytes.ToArray());
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task A_copy_sends_the_sniffed_prefix_first()
    {
        var source = new MemoryStream("world"u8.ToArray());
        var destination = new MemoryStream();

        Assert.True(await new RawByteDestination(destination, ReaderCanLeave: false)
            .CopyFromAsync(source, "hello "u8.ToArray(), CancellationToken.None));

        Assert.Equal("hello world"u8.ToArray(), destination.ToArray());
    }

    /// <summary>Every byte value, then sequences that are not UTF-8, then no final newline.</summary>
    private static byte[] Payload { get; } = CreatePayload();

    private static byte[] CreatePayload()
    {
        var bytes = new List<byte>();

        for (var repeat = 0; repeat < 64; repeat++)
        {
            for (var value = 0; value < 256; value++)
            {
                bytes.Add((byte)value);
            }
        }

        // A lone continuation byte, an overlong '/', and a sequence cut off by the end of input.
        bytes.AddRange([0x80, 0xC0, 0xAF, 0xE2, 0x82]);
        return bytes.ToArray();
    }

    private string WritePayload(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, Payload);
        return path;
    }

    /// <summary>A script that writes a TSSP stream holding one record, whatever it is asked.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string WriteTsspProducer()
    {
        const string record = """{"name":"alpha","size":3}""";
        var path = Path.Combine(_directory, "tssp-producer");
        File.WriteAllText(
            path,
            "#!/bin/sh\n"
            + "printf '\\033TOSHSTREAM\\036{\"v\":1}\\n'\n"
            + $"printf '\\036rec {record.Length}\\n%s' '{record}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Path.GetFileName(path);
    }

    /// <summary>The program's absolute path, failing loudly when a Unix system lacks it.</summary>
    private static string Program(string name)
    {
        var path = new[] { $"/usr/bin/{name}", $"/bin/{name}" }.FirstOrDefault(File.Exists);
        Assert.True(path is not null, $"'{name}' is required by this test and was not found in /usr/bin or /bin.");
        return path!;
    }

    private async Task<IReadOnlyList<object?>> RunAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = _directory;
        var engine = new ToshEngine(runtime.Language);

        return await engine.ExecuteToListAsync(source).WaitAsync(Timeout);
    }

    private async Task RunInBackgroundAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = _directory;
        var engine = new ToshEngine(runtime.Language);

        await engine.ExecuteToListAsync(source).WaitAsync(Timeout);
        var job = Assert.IsType<ShellJobInfo>(runtime.LastResult);

        await engine.ExecuteToListAsync($"wait-for {job.Id}").WaitAsync(Timeout);
    }

    private static byte[] ParseOctalDump(IEnumerable<object?> lines)
        => lines
            .SelectMany(line => (line?.ToString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(pair => byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();

    private static async Task<byte[]> DecompressAsync(string path)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed);
        return decompressed.ToArray();
    }

    /// <summary>A stream whose every write fails the way a closed pipe or a full disk does.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe");
    }
}
