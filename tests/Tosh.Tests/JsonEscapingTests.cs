using System.Text.Json;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// JSON escapes what JSON requires and nothing more — <c>TS-P2-70</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Text.Json</c> defaults to an HTML-safe encoder, so <c>to json</c> wrote
/// <c>"</c> for a double quote, <c>'</c> for an apostrophe, and <c>\uXXXX</c> for
/// every non-ASCII character: <c>{| name = "TōSh" |} | to json</c> gave
/// <c>"TōSh"</c>. Valid JSON that round-trips correctly, and unreadable beside
/// what every other emitter produces.
/// </para>
/// <para>
/// Found while porting <c>generate_vscode_grammar.py</c> to ToastScript — the port
/// could not be verified byte-for-byte against the Python it replaces.
/// </para>
/// </remarks>
public sealed class JsonEscapingTests
{
    private static async Task<string> ToJsonAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime.Language);
        var results = await engine.ExecuteToListAsync(script);

        return string.Join("\n", results.Select(value => value?.ToString() ?? string.Empty));
    }

    [Theory]
    // Each of these is escaped by the default encoder and must not be here.
    [InlineData("\\u0022")]
    [InlineData("\\u0027")]
    [InlineData("\\u003C")]
    [InlineData("\\u003E")]
    [InlineData("\\u0026")]
    [InlineData("\\u002B")]
    public async Task No_character_is_escaped_as_a_unicode_sequence(string forbidden)
    {
        var json = await ToJsonAsync("""{| quote = "a\"b", tick = "it's", tag = "<a>", amp = "x&y", plus = "1+1" |} | to json""");

        Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_ascii_survives_as_itself()
    {
        // The reported case. `ō` is in the shell's own name, so this is not exotic.
        var json = await ToJsonAsync("""{| name = "TōSh", jp = "日本語", sym = "Ω → °" |} | to json""");

        Assert.Contains("TōSh", json, StringComparison.Ordinal);
        Assert.Contains("日本語", json, StringComparison.Ordinal);
        Assert.Contains("Ω → °", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_supplementary_plane_character_is_still_escaped_and_still_correct()
    {
        // Characterization, not an aspiration. .NET's relaxed encoder passes the whole
        // Basic Multilingual Plane and still escapes anything above U+FFFF as a
        // surrogate pair, and a freshly constructed `UnsafeRelaxedJsonEscaping` does
        // the same — so this is the framework's behaviour rather than this shell's
        // configuration. Every range-based alternative is worse: `UnicodeRanges.All`
        // stops at U+FFFF *and* re-escapes `"`, `'`, `<`, `>` and `&`.
        //
        // Pinned rather than papered over: the value round-trips, so the cost is
        // readability for emoji only. Chasing it means writing a custom
        // `JavaScriptEncoder`, which is security-adjacent code, and that is a
        // deliberate decision rather than a side effect of this item.
        var json = await ToJsonAsync("""{| emoji = "🍞" |} | to json""");

        Assert.Contains("\\uD83C\\uDF5E", json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("🍞", document.RootElement.GetProperty("emoji").GetString());
    }

    [Fact]
    public async Task A_double_quote_is_escaped_the_JSON_way()
    {
        // Relaxed is not unescaped: the characters JSON *requires* escaping still are.
        var json = await ToJsonAsync("""{| quote = "a\"b" |} | to json""");

        Assert.Contains("\\\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{| tab = "a\tb" |} | to json""", "\\t")]
    [InlineData("""{| nl = "a\nb" |} | to json""", "\\n")]
    [InlineData("""{| back = "a\\b" |} | to json""", "\\\\")]
    public async Task Control_characters_are_still_escaped(string script, string expected)
    {
        Assert.Contains(expected, await ToJsonAsync(script), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_output_is_still_valid_json_that_round_trips()
    {
        // The guard that keeps "readable" from becoming "wrong".
        var json = await ToJsonAsync("""{| name = "TōSh", quote = "a\"b", tick = "it's" |} | to json""");

        using var document = JsonDocument.Parse(json);

        Assert.Equal("TōSh", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("a\"b", document.RootElement.GetProperty("quote").GetString());
        Assert.Equal("it's", document.RootElement.GetProperty("tick").GetString());
    }

    [Fact]
    public async Task Compact_output_follows_the_same_policy()
    {
        var json = await ToJsonAsync("""{| name = "TōSh" |} | to json --compact""");

        Assert.Contains("TōSh", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_policy_is_shared_rather_than_restated()
    {
        // The item's real finding: the rule was written down seven times and only two
        // of them were right. These are the same object, not two objects that agree
        // today — which is also what lets System.Text.Json keep its type-metadata
        // cache instead of rebuilding it on every `to json`.
        Assert.Same(ToshJson.Encoder, ToshJson.Indented.Encoder);
        Assert.Same(ToshJson.Encoder, ToshJson.Compact.Encoder);
        Assert.True(ToshJson.Indented.WriteIndented);
        Assert.False(ToshJson.Compact.WriteIndented);
    }

    [Fact]
    public void The_shared_options_really_do_pass_non_bmp_through()
    {
        // Isolates the encoder from everything `to json` does around it. A BMP
        // character and a supplementary-plane one are checked separately, because the
        // first encoder tried here passed the former and escaped the latter.
        Assert.Equal("\"日本語\"", JsonSerializer.Serialize("日本語", ToshJson.Compact));
        Assert.Equal("\"Ω ō é ° →\"", JsonSerializer.Serialize("Ω ō é ° →", ToshJson.Compact));
    }

    [Fact]
    public void The_default_encoder_really_would_have_failed_these()
    {
        // Negative control for the mechanism. If the default encoder also left these
        // alone, every assertion above would be vacuous.
        var withDefault = JsonSerializer.Serialize(new { name = "TōSh", tick = "it's" });

        Assert.Contains("\\u014D", withDefault, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u0027", withDefault, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_history_line_keeps_the_command_readable()
    {
        // History is JSON on disk that a person opens. Web defaults stay for the
        // property names — those files already exist — and only the encoder changes.
        var options = ToshJson.With(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var line = JsonSerializer.Serialize(new { text = """echo "TōSh" | grep 'it's'""" }, options);

        Assert.Contains("TōSh", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", line, StringComparison.OrdinalIgnoreCase);

        // camelCase preserved, or every history file already written becomes unreadable.
        Assert.Contains("\"text\"", line, StringComparison.Ordinal);
    }
}
