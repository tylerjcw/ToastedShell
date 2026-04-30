using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace Tosh.Runtime;

public sealed class ManagedFileHandle : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<int, ManagedFileHandle> OpenHandles = new();
    private static int _nextHandleId;

    private readonly bool _binary;
    private readonly string _mode;
    private readonly string _path;
    private readonly string? _requestedEncodingName;
    private StreamReader? _reader;
    private FileStream? _stream;
    private StreamWriter? _writer;

    private ManagedFileHandle(
        string path,
        string mode,
        bool binary,
        FileStream stream,
        StreamReader? reader = null,
        StreamWriter? writer = null,
        string? requestedEncodingName = null)
    {
        Id = Interlocked.Increment(ref _nextHandleId);
        _path = path;
        _mode = mode;
        _binary = binary;
        _stream = stream;
        _reader = reader;
        _writer = writer;
        _requestedEncodingName = requestedEncodingName;
        OpenHandles[Id] = this;
    }

    public int Id { get; }

    public string Path => _path;

    public string Name => System.IO.Path.GetFileName(_path);

    public string Kind => _binary ? "binary" : "text";

    public string Mode => _mode;

    public string? Encoding => _binary
        ? null
        : _writer?.Encoding.WebName ?? _reader?.CurrentEncoding.WebName ?? _requestedEncodingName;

    public bool IsBinary => _binary;

    public bool IsText => !_binary;

    public bool IsOpen => _stream is not null;

    public bool CanRead => IsOpen && (_reader is not null || (_binary && _stream?.CanRead == true));

    public bool CanWrite => IsOpen && (_writer is not null || (_binary && _stream?.CanWrite == true));

    public bool CanSeek => _stream?.CanSeek == true;

    public long? Position => TryGetPosition();

    public long? Length => TryGetLength();

    public static ManagedFileHandle OpenTextRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureFileExists(path, "open-file");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new ManagedFileHandle(path, "read", binary: false, stream, reader: reader);
    }

    public static ManagedFileHandle OpenTextWrite(string path, bool append, Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var effectiveEncoding = encoding ?? Utf8NoBom;
        var mode = append ? FileMode.Append : FileMode.Create;
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, effectiveEncoding);
        return new ManagedFileHandle(path, append ? "append" : "write", binary: false, stream, writer: writer, requestedEncodingName: effectiveEncoding.WebName);
    }

    public static ManagedFileHandle OpenBinaryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureFileExists(path, "open-file");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new ManagedFileHandle(path, "read", binary: true, stream);
    }

    public static ManagedFileHandle OpenBinaryWrite(string path, bool append)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
        return new ManagedFileHandle(path, append ? "append" : "write", binary: true, stream);
    }

    public static IReadOnlyList<ManagedFileHandle> GetOpenHandles()
    {
        return OpenHandles.Values
            .Where(handle => handle.IsOpen)
            .OrderBy(handle => handle.Id)
            .ToArray();
    }

    internal string? ReadLine()
    {
        EnsureOpen();

        if (_reader is null)
        {
            throw new InvalidOperationException("This handle is not opened for text reading.");
        }

        return _reader.ReadLine();
    }

    internal string ReadText(int count)
    {
        EnsureOpen();

        if (_reader is null)
        {
            throw new InvalidOperationException("This handle is not opened for text reading.");
        }

        if (count <= 0)
        {
            throw new InvalidOperationException("The read count must be greater than zero.");
        }

        var buffer = new char[count];
        var read = _reader.Read(buffer, 0, buffer.Length);
        return read <= 0 ? string.Empty : new string(buffer, 0, read);
    }

    internal string ReadToEndText()
    {
        EnsureOpen();

        if (_reader is null)
        {
            throw new InvalidOperationException("This handle is not opened for text reading.");
        }

        return _reader.ReadToEnd();
    }

    internal byte[] ReadBytes(int count)
    {
        EnsureOpen();

        if (_binary is false || _stream is null || !_stream.CanRead)
        {
            throw new InvalidOperationException("This handle is not opened for binary reading.");
        }

        if (count <= 0)
        {
            throw new InvalidOperationException("The read count must be greater than zero.");
        }

        var buffer = new byte[count];
        var read = _stream.Read(buffer, 0, buffer.Length);

        if (read == buffer.Length)
        {
            return buffer;
        }

        var result = new byte[read];
        Array.Copy(buffer, result, read);
        return result;
    }

    internal byte[] ReadToEndBytes()
    {
        EnsureOpen();

        if (_binary is false || _stream is null || !_stream.CanRead)
        {
            throw new InvalidOperationException("This handle is not opened for binary reading.");
        }

        using var memory = new MemoryStream();
        _stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal void WriteText(string text)
    {
        EnsureOpen();

        if (_writer is null)
        {
            throw new InvalidOperationException("This handle is not opened for text writing.");
        }

        _writer.Write(text);
    }

    internal void WriteTextLine(string text)
    {
        EnsureOpen();

        if (_writer is null)
        {
            throw new InvalidOperationException("This handle is not opened for text writing.");
        }

        _writer.WriteLine(text);
    }

    internal void WriteBytes(byte[] bytes)
    {
        EnsureOpen();

        if (_binary is false || _stream is null || !_stream.CanWrite)
        {
            throw new InvalidOperationException("This handle is not opened for binary writing.");
        }

        _stream.Write(bytes, 0, bytes.Length);
    }

    public void Flush()
    {
        EnsureOpen();

        if (_writer is not null)
        {
            _writer.Flush();
            return;
        }

        _stream?.Flush();
    }

    public long Seek(long offset, SeekOrigin origin)
    {
        EnsureOpen();

        if (_stream is null || !_stream.CanSeek)
        {
            throw new InvalidOperationException("This file handle does not support seeking.");
        }

        if (_reader is not null)
        {
            if (origin == SeekOrigin.Current)
            {
                throw new InvalidOperationException("Text reader handles do not support seek operations relative to the current position.");
            }

            _reader.DiscardBufferedData();
            return _reader.BaseStream.Seek(offset, origin);
        }

        if (_writer is not null)
        {
            _writer.Flush();
        }

        return _stream.Seek(offset, origin);
    }

    public long CopyTo(ManagedFileHandle target)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureOpen();
        target.EnsureOpen();

        if (!CanRead)
        {
            throw new InvalidOperationException("This file handle is not opened for reading.");
        }

        if (!target.CanWrite)
        {
            throw new InvalidOperationException("The target file handle is not opened for writing.");
        }

        if (IsBinary != target.IsBinary)
        {
            throw new InvalidOperationException("Copy-to requires the source and target handles to both be text or both be binary.");
        }

        return IsBinary ? CopyBinaryTo(target) : CopyTextTo(target);
    }

    public void Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_writer is not null)
        {
            _writer.Dispose();
            _writer = null;
            _stream = null;
            _reader = null;
            OpenHandles.TryRemove(Id, out _);
            return;
        }

        if (_reader is not null)
        {
            _reader.Dispose();
            _reader = null;
            _stream = null;
            OpenHandles.TryRemove(Id, out _);
            return;
        }

        _stream?.Dispose();
        _stream = null;
        OpenHandles.TryRemove(Id, out _);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString()
    {
        var status = IsOpen ? "open" : "closed";
        return $"#{Id} {Name} [{Kind} {_mode} {status}]";
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("This file handle has already been closed.");
        }
    }

    private long? TryGetPosition()
    {
        if (_reader is not null)
        {
            return null;
        }

        if (_writer is not null)
        {
            try
            {
                _writer.Flush();
            }
            catch
            {
                return null;
            }
        }

        return TryGetLong(stream => stream.Position);
    }

    private long? TryGetLength()
    {
        if (_writer is not null)
        {
            try
            {
                _writer.Flush();
            }
            catch
            {
                return null;
            }
        }

        return TryGetLong(stream => stream.Length);
    }

    private long? TryGetLong(Func<FileStream, long> getValue)
    {
        if (_stream is null)
        {
            return null;
        }

        try
        {
            return getValue(_stream);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureFileExists(string path, string commandName)
    {
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"'{commandName}' expects a file path, but '{path}' is a directory.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"File '{path}' does not exist.");
        }
    }

    private long CopyBinaryTo(ManagedFileHandle target)
    {
        if (_stream is null || target._stream is null)
        {
            throw new InvalidOperationException("Copy-to requires both handles to be open.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(StreamCommandUtilities.DefaultReadChunkSize);
        long total = 0;

        try
        {
            int read;

            while ((read = _stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                target._stream.Write(buffer, 0, read);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private long CopyTextTo(ManagedFileHandle target)
    {
        if (_reader is null || target._writer is null)
        {
            throw new InvalidOperationException("Copy-to requires a readable text source and a writable text target.");
        }

        var buffer = ArrayPool<char>.Shared.Rent(StreamCommandUtilities.DefaultReadChunkSize);
        long total = 0;

        try
        {
            int read;

            while ((read = _reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                target._writer.Write(buffer, 0, read);
                total += read;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        return total;
    }
}
