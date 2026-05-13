using System.Buffers;
using System.Text;
using System.Text.Json;
using Tosh.Runtime;

namespace Tosh.Stdlib.Tssp;

/// <summary>Wire-format version this parser understands.</summary>
public static class TsspVersion
{
    public const int Current = 1;
    public static string Magic => "\x1bTOSHSTREAM";
}

/// <summary>Parsed TSSP header (the first line of a TSSP stream).</summary>
public sealed record TsspHeader(int Version, string? Schema, string? Renderer, IReadOnlyList<string> Modes);

/// <summary>A parsed TSSP frame. <see cref="Record"/> is populated for <c>rec</c>
/// frames after JSON deserialization through ToSh's standard converter.</summary>
public sealed record TsspFrame(string Kind, ReadOnlyMemory<byte> Payload, object? Record);

/// <summary>
/// Reads a <see cref="System.IO.Stream"/> looking for a TSSP magic header.
/// If found, exposes a frame-by-frame async enumeration. If not found,
/// surfaces the bytes already buffered so the caller can fall back to
/// plain-text handling without losing any data.
/// </summary>
public sealed class TsspParser
{
    // Magic line is short; we never need more than a few hundred bytes to
    // either match or reject the header.
    private const int MaxHeaderLineBytes = 8 * 1024;

    private readonly Stream _stream;
    private byte[] _peek = Array.Empty<byte>();

    public TsspParser(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Bytes consumed during a failed header sniff. Caller should
    /// prepend these to any plain-text fallback reader.</summary>
    public ReadOnlyMemory<byte> SniffedBytes => _peek;

    public async ValueTask<TsspHeader?> TryReadHeaderAsync(CancellationToken ct)
    {
        var magic = Encoding.ASCII.GetBytes(TsspVersion.Magic);
        var buf = ArrayPool<byte>.Shared.Rent(MaxHeaderLineBytes);
        var consumed = 0;
        try
        {
            // Greedy read until we've either seen the magic or definitively not.
            while (consumed < magic.Length)
            {
                var read = await _stream.ReadAsync(buf.AsMemory(consumed, magic.Length - consumed), ct);
                if (read == 0) { _peek = buf.AsSpan(0, consumed).ToArray(); return null; }
                consumed += read;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (buf[i] != magic[i]) { _peek = buf.AsSpan(0, consumed).ToArray(); return null; }
            }

            // Magic matched. Read until LF for the JSON header.
            while (consumed < MaxHeaderLineBytes)
            {
                if (buf[consumed - 1] == (byte)'\n') break;
                var read = await _stream.ReadAsync(buf.AsMemory(consumed, 1), ct);
                if (read == 0) { _peek = buf.AsSpan(0, consumed).ToArray(); return null; }
                consumed += read;
            }
            if (buf[consumed - 1] != (byte)'\n')
            {
                _peek = buf.AsSpan(0, consumed).ToArray();
                return null; // header line exceeded limit
            }

            // After magic we expect \x1e + JSON + \n
            if (buf[magic.Length] != 0x1e)
            {
                _peek = buf.AsSpan(0, consumed).ToArray();
                return null;
            }

            var jsonStart = magic.Length + 1;
            var jsonLen = consumed - jsonStart - 1; // strip trailing \n
            if (jsonLen <= 0)
            {
                _peek = buf.AsSpan(0, consumed).ToArray();
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(buf.AsMemory(jsonStart, jsonLen));
                var root = doc.RootElement;
                var v = root.TryGetProperty("v", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetInt32() : 0;
                var schema = root.TryGetProperty("schema", out var sEl) ? sEl.GetString() : null;
                var renderer = root.TryGetProperty("renderer", out var rEl) ? rEl.GetString() : null;
                var modes = new List<string>();
                if (root.TryGetProperty("modes", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in mEl.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String) modes.Add(e.GetString()!);
                }
                return new TsspHeader(v, schema, renderer, modes);
            }
            catch (JsonException)
            {
                _peek = buf.AsSpan(0, consumed).ToArray();
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Reads frames until EOF. Throws <see cref="TsspProtocolException"/>
    /// when a frame is malformed.</summary>
    public async IAsyncEnumerable<TsspFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sep = await ReadByteAsync(ct);
            if (sep is null) yield break; // EOF
            if (sep != 0x1e) throw new TsspProtocolException($"expected 0x1e frame separator, got 0x{sep:x2}");

            // Read "<kind> <length>\n"
            var headerLine = await ReadAsciiLineAsync(ct);
            if (headerLine is null) throw new TsspProtocolException("unexpected EOF in frame header");
            var spaceIdx = headerLine.IndexOf(' ');
            if (spaceIdx <= 0) throw new TsspProtocolException($"malformed frame header '{headerLine}'");
            var kind = headerLine[..spaceIdx];
            if (!int.TryParse(headerLine.AsSpan(spaceIdx + 1), out var len) || len < 0)
                throw new TsspProtocolException($"bad frame length in '{headerLine}'");

            var payload = new byte[len];
            var off = 0;
            while (off < len)
            {
                var r = await _stream.ReadAsync(payload.AsMemory(off, len - off), ct);
                if (r == 0) throw new TsspProtocolException($"unexpected EOF reading {kind} payload");
                off += r;
            }

            object? record = null;
            if (kind == "rec")
            {
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    record = JsonValueConverter.Convert(doc.RootElement);
                }
                catch (JsonException ex)
                {
                    throw new TsspProtocolException($"rec frame is not valid JSON: {ex.Message}");
                }
            }

            yield return new TsspFrame(kind, payload, record);
        }
    }

    private async ValueTask<byte?> ReadByteAsync(CancellationToken ct)
    {
        var one = new byte[1];
        var r = await _stream.ReadAsync(one, ct);
        return r == 0 ? null : one[0];
    }

    private async ValueTask<string?> ReadAsciiLineAsync(CancellationToken ct)
    {
        var buf = new List<byte>(32);
        while (true)
        {
            var b = await ReadByteAsync(ct);
            if (b is null) return buf.Count == 0 ? null : Encoding.ASCII.GetString(buf.ToArray());
            if (b == (byte)'\n') return Encoding.ASCII.GetString(buf.ToArray());
            buf.Add(b.Value);
            if (buf.Count > 1024) throw new TsspProtocolException("frame header line too long");
        }
    }
}

public sealed class TsspProtocolException : Exception
{
    public TsspProtocolException(string message) : base(message) { }
}
