using System.Text;
using System.Text.Json;

namespace Tosh.Client;

/// <summary>
/// Writes TSSP frames to a destination stream. Each writer instance
/// emits exactly one stream (one header, optional meta, any number of
/// rec/err/pres/progress frames).
///
/// Thread-safe via an internal lock — concurrent writes from multiple
/// emitter threads serialise correctly. Callers should still avoid
/// interleaving conceptually-related frames (e.g. <c>meta</c> after
/// <c>rec</c>).
/// </summary>
public sealed class ToshFrameWriter : IDisposable
{
    private const byte RecordSeparator = 0x1e;
    private static readonly byte[] s_headerMagic = Encoding.ASCII.GetBytes("\x1bTOSHSTREAM\x1e");

    private static readonly JsonSerializerOptions s_defaultJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly object _gate = new();
    private bool _headerWritten;
    private bool _disposed;

    public ToshFrameWriter(Stream output, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// TSSP wire version. Currently always 1.
    /// </summary>
    public int Version => 1;

    /// <summary>
    /// Write the stream header. Must be called exactly once before any
    /// record/meta/err frame. <paramref name="schema"/> is the namespaced
    /// schema name (e.g. <c>crumb.package</c>) that downstream renderers
    /// can key off. <paramref name="renderer"/> optionally requests a
    /// specific renderer name.
    /// </summary>
    public void WriteHeader(string schema, IReadOnlyList<string>? modes = null, string? renderer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        lock (_gate)
        {
            if (_headerWritten) throw new InvalidOperationException("TSSP header has already been written.");

            using var ms = new MemoryStream(128);
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("v", Version);
                w.WriteString("schema", schema);
                if (modes is { Count: > 0 })
                {
                    w.WriteStartArray("modes");
                    foreach (var m in modes) w.WriteStringValue(m);
                    w.WriteEndArray();
                }
                else
                {
                    w.WriteStartArray("modes");
                    w.WriteStringValue("records");
                    w.WriteEndArray();
                }
                if (!string.IsNullOrEmpty(renderer)) w.WriteString("renderer", renderer);
                w.WriteEndObject();
            }

            _output.Write(s_headerMagic, 0, s_headerMagic.Length);
            ms.Position = 0;
            ms.CopyTo(_output);
            _output.WriteByte((byte)'\n');
            _output.Flush();
            _headerWritten = true;
        }
    }

    /// <summary>Emit a <c>meta</c> frame carrying a schema descriptor (raw JSON).</summary>
    public void WriteMeta(string schemaJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        WriteFrame("meta", Encoding.UTF8.GetBytes(schemaJson));
    }

    /// <summary>Emit a <c>meta</c> frame carrying a schema descriptor (raw UTF-8 bytes).</summary>
    public void WriteMeta(ReadOnlySpan<byte> schemaJsonUtf8) => WriteFrame("meta", schemaJsonUtf8);

    /// <summary>Emit a <c>rec</c> frame from already-serialised UTF-8 JSON.</summary>
    public void WriteRecordJson(ReadOnlySpan<byte> payloadUtf8) => WriteFrame("rec", payloadUtf8);

    /// <summary>Emit a <c>rec</c> frame by serialising <paramref name="value"/> as JSON.</summary>
    public void WriteRecord<T>(T value, JsonSerializerOptions? options = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options ?? s_defaultJson);
        WriteFrame("rec", bytes);
    }

    /// <summary>Emit an <c>err</c> frame with a plain-text message.</summary>
    public void WriteError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        WriteFrame("err", Encoding.UTF8.GetBytes(message));
    }

    /// <summary>Emit a <c>progress</c> frame carrying caller-defined JSON.</summary>
    public void WriteProgress(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        WriteFrame("progress", Encoding.UTF8.GetBytes(payloadJson));
    }

    /// <summary>
    /// Convenience: emit a <c>progress</c> frame with the canonical
    /// <c>{message,current,total,percent}</c> shape ToSh's built-in
    /// progress renderer understands. Any null field is omitted.
    /// </summary>
    public void WriteProgress(string? message = null, double? current = null, double? total = null, double? percent = null)
    {
        var sb = new StringBuilder(64);
        sb.Append('{');
        var first = true;
        void Append(string key, string value)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(key).Append("\":").Append(value);
        }
        if (message is not null) Append("message", JsonSerializer.Serialize(message));
        if (current is not null) Append("current", current.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (total is not null) Append("total", total.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (percent is not null) Append("percent", percent.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');
        WriteFrame("progress", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>Emit a presentation-block start frame (advanced; v1 reserves the kind).</summary>
    public void WritePresStart(string payloadJson) => WriteFrame("pres", Encoding.UTF8.GetBytes(payloadJson));

    /// <summary>Emit a presentation-block end frame.</summary>
    public void WritePresEnd() => WriteFrame("pres-end", ReadOnlySpan<byte>.Empty);

    public void Flush()
    {
        lock (_gate) { _output.Flush(); }
    }

    private void WriteFrame(string kind, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ToshFrameWriter));
            if (!_headerWritten) throw new InvalidOperationException("Call WriteHeader before emitting frames.");

            // Frame format: \x1e <kind> SP <len> \n <payload>
            var prefix = Encoding.ASCII.GetBytes($"{kind} {payload.Length}\n");
            _output.WriteByte(RecordSeparator);
            _output.Write(prefix, 0, prefix.Length);
            if (payload.Length > 0) _output.Write(payload);
            _output.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _output.Flush(); } catch { }
            if (!_leaveOpen)
            {
                try { _output.Dispose(); } catch { }
            }
        }
    }
}
