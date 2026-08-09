using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tosh.Runtime;

/// <summary>
/// How this shell writes JSON (<c>TS-P2-70</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Text.Json</c> defaults to an encoder that escapes anything with meaning
/// in HTML — <c>"</c> becomes <c>"</c>, <c>'</c> becomes <c>'</c>, and every
/// non-ASCII character becomes its <c>\uXXXX</c> form. That default exists so JSON can
/// be dropped into a <c>&lt;script&gt;</c> block without escaping it again. A shell
/// writing a file to disk or a value to a pipeline is not that case, and the output it
/// produced was unreadable next to every other emitter:
/// <c>{| name = "TōSh" |} | to json</c> gave <c>"TōSh"</c>.
/// </para>
/// <para>
/// The policy lives here because it was written down seven times — three
/// <c>JsonOptions</c> statics and four options objects constructed inline — and only
/// two of them had been given a relaxed encoder. That is the same shape as
/// <c>TS-P2-10</c>: a rule stated once per consumer drifts, and the two that were right
/// were right by hand.
/// </para>
/// <para>
/// <b>Naming policy is deliberately not unified.</b> The history and directory-stack
/// stores serialize with <see cref="JsonSerializerDefaults.Web"/>, which is camelCase,
/// and their files are already on disk in that shape. Changing the casing here would
/// silently orphan every existing one, so those keep their own defaults and take only
/// the encoder.
/// </para>
/// <para>
/// These instances are shared rather than constructed per call, which also fixes a cost
/// that was never the point of the item: <c>System.Text.Json</c> caches its type
/// metadata per options instance, so a fresh <c>JsonSerializerOptions</c> inside
/// <c>to json</c> rebuilt that cache on every invocation.
/// </para>
/// </remarks>
public static class ToshJson
{
    /// <summary>
    /// Escapes what JSON requires and nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name is alarming and the alternative is worse. This was first written as
    /// <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c>, to state the intent rather
    /// than inherit a name containing "unsafe", and that encoder is **not** equivalent:
    /// <see cref="UnicodeRanges"/> widens which *Unicode* characters pass through and
    /// leaves the HTML-sensitive ASCII set — <c>" ' &lt; &gt; &amp; +</c> — escaped as
    /// <c>\uXXXX</c> regardless. It also covers only the Basic Multilingual Plane, so
    /// anything above U+FFFF still came out as a surrogate escape and an emoji in a
    /// record survived as <c>🍞</c>.
    /// </para>
    /// <para>
    /// "Unsafe" here means only "do not assume this will be pasted into an HTML
    /// document without further escaping". Every consumer of this shell's JSON is a
    /// file, a pipe, or another program's parser.
    /// </para>
    /// <para>
    /// <b>Known limit, measured:</b> this still escapes characters above U+FFFF as
    /// surrogate pairs, so an emoji is written <c>🍞</c>. That is .NET's
    /// behaviour and not a configuration mistake — a freshly constructed
    /// <c>UnsafeRelaxedJsonEscaping</c> does the same, and every range-based encoder is
    /// worse on both counts. The value round-trips correctly; only its readability is
    /// affected. Changing it means writing a custom <see cref="JavaScriptEncoder"/>,
    /// which is security-adjacent code and belongs in its own deliberate change.
    /// </para>
    /// </remarks>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>Human-facing JSON: indented, and the default for <c>to json</c>.</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = Encoder,
    };

    /// <summary>One-line JSON, for <c>to json --compact</c> and internal round-trips.</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = Encoder,
    };

    /// <summary>
    /// The encoder applied to an existing policy, for callers that must keep their own
    /// naming or read behaviour.
    /// </summary>
    public static JsonSerializerOptions With(JsonSerializerOptions options) =>
        new(options) { Encoder = Encoder };
}
