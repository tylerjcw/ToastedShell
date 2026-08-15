namespace Tosh.Runtime;

/// <summary>
/// What an operator is for. Categories are what a consumer groups by, so they exist to
/// be rendered rather than to drive parsing — precedence stays with the parser.
/// </summary>
public enum OperatorCategory
{
    /// <summary><c>+ - * / // % **</c></summary>
    Arithmetic,

    /// <summary><c>== != &lt; &lt;= &gt; &gt;= =~ !~</c> and the word forms.</summary>
    Comparison,

    /// <summary><c>and or not &amp;&amp; ||</c></summary>
    Logical,

    /// <summary><c>band bor bxor bnot shl shr has</c> — `TS-P3-14`.</summary>
    Bitwise,

    /// <summary><c>= += -= *= /= //= %= **= ??=</c></summary>
    Assignment,

    /// <summary><c>?? ?.</c></summary>
    NullHandling,

    /// <summary><c>..</c></summary>
    Range,

    /// <summary><c>| &lt;|</c></summary>
    Pipeline,

    /// <summary><c>is is-not as</c> — membership and conversion.</summary>
    TypeTest,
}

/// <summary>
/// The one place that says what ToastScript's operators are (<c>TS-P2-78</c>).
/// </summary>
/// <remarks>
/// <para>
/// The <c>operators</c> half of <c>TS-P2-10</c>, which needed a registry of its own:
/// <see cref="LanguageSurface"/> is word-shaped — <c>and</c>, <c>is</c>,
/// <c>starts-with</c> — and has nowhere to put <c>//</c> or <c>??=</c>. Measured before
/// this existed, the MCP server's operator table was missing **fifteen** operators the
/// language has, including floor division and every compound assignment, so a client
/// asking what ToastScript supports was told a smaller language than the one it was
/// writing for.
/// </para>
/// <para>
/// The authority is the parser's own precedence predicates —
/// <c>IsMultiplicativeOperatorToken</c> names <c>*</c>, <c>/</c>, <c>//</c>, <c>%</c>;
/// <c>IsAssignmentOperatorToken</c> names the nine assignment forms — and
/// <c>OperatorSurfaceParityTests</c> holds those predicates against this table so the two
/// cannot drift. As with <see cref="LanguageSurface"/>, prose stays with whichever
/// consumer renders it; what must not differ is *which operators exist*.
/// </para>
/// <para>
/// Word operators appear here and in <see cref="LanguageSurface"/> both, because they are
/// genuinely both: <c>and</c> is a word the highlighter colours and an operator the MCP
/// table describes. The two registries answer different questions about it.
/// </para>
/// </remarks>
public static class OperatorSurface
{
    private static readonly Dictionary<string, OperatorCategory> Table =
        new(StringComparer.Ordinal)
        {
            // ── Arithmetic ─────────────────────────────────────────────────────
            ["+"] = OperatorCategory.Arithmetic,
            ["-"] = OperatorCategory.Arithmetic,
            ["*"] = OperatorCategory.Arithmetic,
            ["/"] = OperatorCategory.Arithmetic,
            // Floor division. Spaces are required around it so a path keeps its slashes,
            // which is also why it is easy to forget it exists — it was the headline
            // omission from the MCP table.
            ["//"] = OperatorCategory.Arithmetic,
            ["%"] = OperatorCategory.Arithmetic,
            ["**"] = OperatorCategory.Arithmetic,

            // ── Comparison ─────────────────────────────────────────────────────
            ["=="] = OperatorCategory.Comparison,
            ["!="] = OperatorCategory.Comparison,
            ["<"] = OperatorCategory.Comparison,
            ["<="] = OperatorCategory.Comparison,
            [">"] = OperatorCategory.Comparison,
            [">="] = OperatorCategory.Comparison,
            ["=~"] = OperatorCategory.Comparison,
            ["!~"] = OperatorCategory.Comparison,
            ["contains"] = OperatorCategory.Comparison,
            ["starts-with"] = OperatorCategory.Comparison,
            ["ends-with"] = OperatorCategory.Comparison,
            ["in"] = OperatorCategory.Comparison,
            ["not-in"] = OperatorCategory.Comparison,
            ["is-in"] = OperatorCategory.Comparison,
            ["is-not-in"] = OperatorCategory.Comparison,

            // ── Type tests ─────────────────────────────────────────────────────
            ["is"] = OperatorCategory.TypeTest,
            ["is-not"] = OperatorCategory.TypeTest,
            ["as"] = OperatorCategory.TypeTest,

            // ── Bitwise ────────────────────────────────────────────────────────
            // Word forms because the symbols are taken: `&` is the background
            // operator and the function-reference sigil, `|` separates pipeline
            // stages (`TS-P3-14`).
            ["band"] = OperatorCategory.Bitwise,
            ["bor"] = OperatorCategory.Bitwise,
            ["bxor"] = OperatorCategory.Bitwise,
            ["bnot"] = OperatorCategory.Bitwise,
            ["shl"] = OperatorCategory.Bitwise,
            ["shr"] = OperatorCategory.Bitwise,
            ["has"] = OperatorCategory.Bitwise,

            // ── Logical ────────────────────────────────────────────────────────
            ["and"] = OperatorCategory.Logical,
            ["or"] = OperatorCategory.Logical,
            ["not"] = OperatorCategory.Logical,
            ["&&"] = OperatorCategory.Logical,
            ["||"] = OperatorCategory.Logical,

            // ── Assignment ─────────────────────────────────────────────────────
            ["="] = OperatorCategory.Assignment,
            ["+="] = OperatorCategory.Assignment,
            ["-="] = OperatorCategory.Assignment,
            ["*="] = OperatorCategory.Assignment,
            ["/="] = OperatorCategory.Assignment,
            ["//="] = OperatorCategory.Assignment,
            ["%="] = OperatorCategory.Assignment,
            ["**="] = OperatorCategory.Assignment,
            ["??="] = OperatorCategory.Assignment,

            // ── Null handling ──────────────────────────────────────────────────
            ["??"] = OperatorCategory.NullHandling,
            ["?."] = OperatorCategory.NullHandling,

            // ── Range and pipeline ─────────────────────────────────────────────
            [".."] = OperatorCategory.Range,
            ["|"] = OperatorCategory.Pipeline,
            ["<|"] = OperatorCategory.Pipeline,
        };

    /// <summary>Every operator, with the category a consumer groups it under.</summary>
    public static IReadOnlyDictionary<string, OperatorCategory> Operators => Table;

    /// <summary>The operators in one category, ordinal-sorted for stable output.</summary>
    public static IReadOnlyList<string> InCategory(OperatorCategory category) =>
        Table.Where(pair => pair.Value == category)
             .Select(pair => pair.Key)
             .OrderBy(symbol => symbol, StringComparer.Ordinal)
             .ToArray();

    /// <summary>
    /// The word-shaped operators, which are also <see cref="LanguageSurface"/> words.
    /// </summary>
    public static IReadOnlyList<string> WordOperators =>
        Table.Keys.Where(symbol => char.IsLetter(symbol[0]))
             .OrderBy(symbol => symbol, StringComparer.Ordinal)
             .ToArray();
}
