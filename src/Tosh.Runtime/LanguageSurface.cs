namespace Tosh.Runtime;

/// <summary>
/// What a word is in ToastScript. A word may be more than one thing —
/// <c>local</c> is both a declaration modifier and a member modifier — so these
/// combine.
/// </summary>
[Flags]
public enum LanguageWordKind
{
    None = 0,

    /// <summary>Introduces a binding, function, or type.</summary>
    Declaration = 1 << 0,

    /// <summary>A declaration whose next token is the name of a type.</summary>
    TypeDeclaration = 1 << 1,

    /// <summary>Changes execution flow, and is coloured distinctly for that reason.</summary>
    ControlFlow = 1 << 2,

    /// <summary>
    /// Visibility, parsed by <c>ParseDeclarationModifier</c>. Exactly three:
    /// <c>shy</c>, <c>global</c>, <c>export</c>.
    /// </summary>
    VisibilityModifier = 1 << 3,

    /// <summary>Shapes a type, parsed inside each declaration's own parser.</summary>
    TypeModifier = 1 << 4,

    /// <summary>Shapes a member, parsed inside class and struct bodies.</summary>
    MemberModifier = 1 << 5,

    /// <summary>Brings names in from elsewhere.</summary>
    Import = 1 << 6,

    /// <summary>Describes CLR interop at a call or parameter site.</summary>
    Interop = 1 << 7,

    /// <summary>Relates one type to another.</summary>
    Composition = 1 << 8,

    /// <summary>A word that behaves as an operator.</summary>
    OperatorWord = 1 << 9,

    /// <summary>A literal spelled as a word.</summary>
    Constant = 1 << 10,

    /// <summary>An expression form spelled as a word, such as <c>new</c>.</summary>
    LanguageForm = 1 << 11,

    /// <summary>A property accessor: <c>get</c>, <c>set</c>.</summary>
    Accessor = 1 << 12,

    /// <summary>
    /// A clause on an event handler — <c>handles</c>, <c>priority</c>, <c>when</c>,
    /// <c>once</c>.
    /// </summary>
    HandlerClause = 1 << 13,

    /// <summary>
    /// A keyword only in a particular position, such as <c>let</c> and <c>where</c>
    /// inside a comprehension.
    /// </summary>
    Contextual = 1 << 14,
}

/// <summary>
/// The one place that says what ToastScript's word-shaped surface is
/// (<c>TS-P2-10</c>).
/// </summary>
/// <remarks>
/// <para>
/// Eight consumers each kept their own list — the CLI highlighter, the REPL
/// classifier and completion engine, the Tome colorizer, the binder's suggestion
/// pool, the LSP feature table, the VS Code metadata emitter, and the help
/// catalogue. Measured on 2026-07-29 they held **115 distinct words between them,
/// of which 7 appeared in all eight**. The consequences were ordinary and
/// user-visible: eight real keywords went unhighlighted at the prompt, and the Tome
/// colorizer was missing every control-flow keyword.
/// </para>
/// <para>
/// Membership here is established by **executing** each word in its canonical
/// shape, not by finding its spelling in the parser, and
/// <c>LanguageSurfaceParityTests</c> holds a probe for every entry. A word cannot
/// join this list without demonstrating it is real.
/// </para>
/// <para>
/// That validation is **directional, and the limit matters**: a passing probe
/// proves the word is real, and a failing probe proves nothing except that the
/// probe is wrong. Building this registry produced three false accusations from
/// exactly that mistake — <c>let</c>, <c>quote</c>, and <c>once</c> were all
/// reported as documented-but-nonexistent, and all three are real in positions that
/// had not been tried: <c>let</c> is a comprehension clause
/// (<c>[$y &lt;| for x in 1..3 let y = ($x * 2)]</c>), <c>quote</c> takes a block in
/// argument position (<c>echo (quote { $x + 1 })</c>), and <c>once</c> is an
/// event-handler clause (<c>func f(e) handles X once { }</c>). So a word missing
/// from this registry is a claim about the registry, never about the language.
/// </para>
/// <para>
/// Prose stays with its consumer. The help catalogue's descriptions and the LSP's
/// hover text are editorial and differ legitimately; what must not differ is
/// *which words exist*. So this registry carries identity and category only.
/// </para>
/// </remarks>
public static class LanguageSurface
{
    private const LanguageWordKind Visibility = LanguageWordKind.VisibilityModifier;
    private const LanguageWordKind TypeMod = LanguageWordKind.TypeModifier;
    private const LanguageWordKind MemberMod = LanguageWordKind.MemberModifier;
    private const LanguageWordKind Decl = LanguageWordKind.Declaration;
    private const LanguageWordKind TypeDecl = LanguageWordKind.Declaration | LanguageWordKind.TypeDeclaration;
    private const LanguageWordKind Flow = LanguageWordKind.ControlFlow;

    private static readonly Dictionary<string, LanguageWordKind> WordKinds =
        new(StringComparer.Ordinal)
        {
            // ── Declarations ───────────────────────────────────────────────────
            ["var"] = Decl,
            ["alloc"] = Decl,
            ["const"] = Decl,
            ["func"] = Decl,
            ["prop"] = Decl,
            ["bind"] = Decl,
            ["native"] = Decl,
            ["event"] = Decl,

            // Declarations naming a type, which is what lets an editor colour the
            // following identifier as a type rather than a value.
            ["class"] = TypeDecl,
            ["struct"] = TypeDecl,
            ["record"] = TypeDecl,
            ["enum"] = TypeDecl,
            ["trait"] = TypeDecl,
            ["interface"] = TypeDecl,
            ["union"] = TypeDecl,
            ["rune"] = TypeDecl,
            ["module"] = TypeDecl,

            // ── Control flow ───────────────────────────────────────────────────
            ["if"] = Flow,
            ["else"] = Flow,
            ["for"] = Flow,
            ["in"] = Flow | LanguageWordKind.OperatorWord,
            ["while"] = Flow,
            ["until"] = Flow,
            ["break"] = Flow,
            ["continue"] = Flow,
            ["return"] = Flow,
            ["yield"] = Flow,
            ["throw"] = Flow,
            ["try"] = Flow,
            ["catch"] = Flow,
            ["finally"] = Flow,
            ["defer"] = Flow,
            ["switch"] = Flow,
            ["case"] = Flow,
            ["default"] = Flow,
            ["match"] = Flow,

            // ── Visibility: exactly what ParseDeclarationModifier accepts ──────
            // `shy` is also a member modifier — it is the only word in two families,
            // and categorising it as visibility alone stopped `shy prop X = 1` from
            // parsing the moment ParseClassMember started asking this registry.
            ["shy"] = Visibility | MemberMod,
            ["global"] = Visibility,
            ["export"] = Visibility,

            // ── Type modifiers ─────────────────────────────────────────────────
            ["sealed"] = TypeMod,

            // Also a member modifier: `hollow class C { hollow func f() { } }` uses
            // it in both positions.
            ["hollow"] = TypeMod | MemberMod,
            ["hermit"] = TypeMod,
            ["strict"] = TypeMod,
            ["partial"] = TypeMod,
            ["fluid"] = TypeMod,
            ["leaky"] = TypeMod,

            // ── Member modifiers ───────────────────────────────────────────────
            ["shared"] = MemberMod,
            ["static"] = MemberMod,
            ["fixed"] = MemberMod,
            ["vital"] = MemberMod,
            ["guarded"] = MemberMod,
            ["overrule"] = MemberMod,
            ["fading"] = MemberMod,
            ["lazy"] = MemberMod,
            ["raw"] = MemberMod,
            ["proud"] = MemberMod,
            ["public"] = MemberMod,
            ["local"] = MemberMod,

            // C#-familiar aliases for the member modifiers above, all parsed in the
            // same loop in ParseMemberModifiers. They are real, and every one of
            // them was absent from the CLI highlighter while present in the Tome
            // colorizer — the two consumers disagreed about the same nine words.
            ["private"] = MemberMod,     // shy
            ["abstract"] = MemberMod,    // hollow
            ["readonly"] = MemberMod,    // fixed
            ["required"] = MemberMod,    // vital
            ["override"] = MemberMod,    // overrule
            ["protected"] = MemberMod,   // guarded
            ["obsolete"] = MemberMod,    // fading

            // ── Imports ────────────────────────────────────────────────────────
            // `import` is deliberately absent: it is not a ToastScript word. It
            // reached this registry from Binder's typo-suggestion pool, and its probe
            // `import System.Text` "passed" only because any bareword line parses.
            // On this machine it resolves to /usr/bin/import, ImageMagick's
            // screenshot tool.
            ["using"] = LanguageWordKind.Import,
            ["require"] = LanguageWordKind.Import,
            ["from"] = LanguageWordKind.Import,
            ["as"] = LanguageWordKind.Import,

            // ── Interop ────────────────────────────────────────────────────────
            ["out"] = LanguageWordKind.Interop,
            ["ref"] = LanguageWordKind.Interop,
            ["callconv"] = LanguageWordKind.Interop,

            // ── Composition ────────────────────────────────────────────────────
            ["uses"] = LanguageWordKind.Composition,
            ["fulfills"] = LanguageWordKind.Composition,
            ["extends"] = LanguageWordKind.Composition,
            ["implements"] = LanguageWordKind.Composition,

            // ── Operator words ─────────────────────────────────────────────────
            ["and"] = LanguageWordKind.OperatorWord,
            ["or"] = LanguageWordKind.OperatorWord,
            ["not"] = LanguageWordKind.OperatorWord,
            ["is"] = LanguageWordKind.OperatorWord,
            ["is-not"] = LanguageWordKind.OperatorWord,
            ["not-in"] = LanguageWordKind.OperatorWord,
            ["is-in"] = LanguageWordKind.OperatorWord,
            ["is-not-in"] = LanguageWordKind.OperatorWord,
            ["contains"] = LanguageWordKind.OperatorWord,
            ["starts-with"] = LanguageWordKind.OperatorWord,
            ["ends-with"] = LanguageWordKind.OperatorWord,

            // ── Constants ──────────────────────────────────────────────────────
            ["true"] = LanguageWordKind.Constant,
            ["false"] = LanguageWordKind.Constant,
            ["null"] = LanguageWordKind.Constant,

            // ── Word-shaped expression forms ───────────────────────────────────
            ["new"] = LanguageWordKind.LanguageForm,
            ["nameof"] = LanguageWordKind.LanguageForm,
            ["name-of"] = LanguageWordKind.LanguageForm,
            ["quote"] = LanguageWordKind.LanguageForm,

            // ── Property accessors ─────────────────────────────────────────────
            ["get"] = LanguageWordKind.Accessor,
            ["set"] = LanguageWordKind.Accessor,

            // ── Event-handler clauses ──────────────────────────────────────────
            ["handles"] = LanguageWordKind.HandlerClause,
            ["priority"] = LanguageWordKind.HandlerClause,
            ["when"] = LanguageWordKind.HandlerClause,
            ["once"] = LanguageWordKind.HandlerClause,

            // ── Contextual ─────────────────────────────────────────────────────
            // Keywords only inside a comprehension clause list.
            ["let"] = LanguageWordKind.Contextual,
            ["where"] = LanguageWordKind.Contextual,
        };

    /// <summary>Every word in the surface, with everything it is.</summary>
    public static IReadOnlyDictionary<string, LanguageWordKind> Words => WordKinds;

    /// <summary>
    /// Keywords: everything except operator words, constants, and word-shaped
    /// expression forms, which consumers colour and complete differently.
    /// </summary>
    public static IReadOnlySet<string> Keywords { get; } = Select(
        LanguageWordKind.Declaration | LanguageWordKind.ControlFlow |
        LanguageWordKind.VisibilityModifier | LanguageWordKind.TypeModifier |
        LanguageWordKind.MemberModifier | LanguageWordKind.Import |
        LanguageWordKind.Interop | LanguageWordKind.Composition);

    public static IReadOnlySet<string> ControlFlow { get; } = Select(LanguageWordKind.ControlFlow);

    public static IReadOnlySet<string> TypeDeclarations { get; } = Select(LanguageWordKind.TypeDeclaration);

    public static IReadOnlySet<string> OperatorWords { get; } = Select(LanguageWordKind.OperatorWord);

    public static IReadOnlySet<string> Constants { get; } = Select(LanguageWordKind.Constant);

    public static IReadOnlySet<string> LanguageForms { get; } = Select(LanguageWordKind.LanguageForm);

    /// <summary>
    /// C#-familiar spellings accepted for member modifiers, mapped to the
    /// ToastScript word they mean. Both spellings work; these exist so a reader
    /// coming from C# is not stopped by vocabulary.
    /// </summary>
    /// <remarks>
    /// Discovered by probing rather than by reading: `abstract class C { }` fails,
    /// because `abstract` is a *member* modifier and `hollow` is the type-level
    /// spelling. Trying a word in the wrong position and concluding it is not real
    /// is the mistake this mapping documents against.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> MemberModifierAliases { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["private"] = "shy",
            ["abstract"] = "hollow",
            ["readonly"] = "fixed",
            ["required"] = "vital",
            ["override"] = "overrule",
            ["protected"] = "guarded",
            ["obsolete"] = "fading",
            ["shared"] = "static",
            ["public"] = "proud",
        };

    /// <summary>
    /// Resolves a member-modifier spelling to the ToastScript word it means, so a
    /// caller handles nine fewer cases than there are spellings.
    /// </summary>
    /// <remarks>
    /// This exists to replace a 22-branch chain of <c>string.Equals</c> in
    /// <c>ParseClassMember</c>, where each alias was written out beside its
    /// canonical spelling twice — once to enter the loop and once to set the flag.
    /// The aliasing is data, and belongs here with the words rather than inline in
    /// the parser (<c>TS-P2-10</c>).
    /// </remarks>
    public static bool TryResolveMemberModifier(string? text, out string canonical)
    {
        if (text is not null && MemberModifierAliases.TryGetValue(text, out var mapped))
        {
            canonical = mapped;
            return true;
        }

        if (text is not null &&
            WordKinds.TryGetValue(text, out var kind) &&
            kind.HasFlag(LanguageWordKind.MemberModifier))
        {
            canonical = text;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>All three modifier families together.</summary>
    public static IReadOnlySet<string> Modifiers { get; } = Select(
        LanguageWordKind.VisibilityModifier | LanguageWordKind.TypeModifier |
        LanguageWordKind.MemberModifier);

    /// <summary>True when <paramref name="text"/> is a keyword.</summary>
    public static bool IsKeyword(string? text) => text is not null && Keywords.Contains(text);

    /// <summary>Everything <paramref name="text"/> is, or <see cref="LanguageWordKind.None"/>.</summary>
    public static LanguageWordKind KindOf(string? text) =>
        text is not null && WordKinds.TryGetValue(text, out var kind) ? kind : LanguageWordKind.None;

    private static IReadOnlySet<string> Select(LanguageWordKind mask) =>
        WordKinds
            .Where(pair => (pair.Value & mask) != 0)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
}
