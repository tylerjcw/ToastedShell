using System.Reflection;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed partial class ToshLanguageFeatures
{
    private static readonly IReadOnlyDictionary<string, string> SpecialVariables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["$tosh"] = "The live ToSh runtime namespace.",
        ["$env"] = "Read environment-variable values directly by name.",
        ["_"] = "The current pipeline item in predicates and block-driven pipeline commands.",
    };

    private static readonly IReadOnlyDictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["func"] = "Define a function with an optional CLR return type.",
        ["var"] = "Declare a variable. Reference it later as `$name`.",
        ["const"] = "Declare an immutable constant. Like `var` but the binding cannot be reassigned.",
        ["alloc"] = "Allocate a native buffer into a variable by byte count or interop type size.",
        ["class"] = "Define a ToSh class with properties, constructors, and methods.",
        ["interface"] = "Define a structural interface with required method signatures. Classes conform via `fulfills`/`implements`. Example: `interface IShape { func area() }`.",
        ["union"] = "Define a discriminated union (sum type) with named variants. Example: `union Result { Ok(value); Error(msg) }`.",
        ["module"] = "Define a named ToSh module object with its own lexical scope.",
        ["enum"] = "Define a named enum with symbolic members and optional underlying numeric storage.",
        ["record"] = "Define a named data shape with positional construction and structural equality.",
        ["rune"] = "Define a computed value type — a named, parameterized computation that caches its result. Sealed by default; use `leaky` to allow subclassing.",
        ["event"] = "Define a named event on a class with optional payload fields. Example: `event DataReceived { data }`.",
        ["required"] = "Modifier on `event` definitions — callers must handle the event (it cannot be silently ignored).",
        ["prop"] = "Declare a class property, computed member, or accessor-backed property.",
        ["shy"] = "Keep a declaration in the current lexical scope, or hide a class member from public inspection and external access.",
        ["static"] = "Mark a class member as belonging to the class rather than an instance.",
        ["shared"] = "Mark a class member as belonging to the class rather than an instance (alias for static).",
        ["sealed"] = "Prevent a class from being inherited.",
        ["hollow"] = "Mark a class or method as abstract, requiring subclass implementation.",
        ["fixed"] = "Mark a class property as read-only after initialization.",
        ["vital"] = "Mark a class property as required during construction.",
        ["guarded"] = "Restrict member access to the defining class and its subclasses (protected).",
        ["overrule"] = "Override an inherited method from a parent class.",
        ["hermit"] = "Mark a class as static-only; all members are auto-promoted to shared.",
        ["strict"] = "Make all properties in a class read-only (immutable).",
        ["lazy"] = "Defer property initialization until first access.",
        ["fading"] = "Mark a member as deprecated; emits a warning on use.",
        ["local"] = "Restrict member visibility to the defining assembly (internal).",
        ["raw"] = "Mark a method for unsafe/native interop.",
        ["partial"] = "Allow a class definition to be split across multiple declarations.",
        ["extend"] = "Add methods to an existing type: `extend Color { func ToGl() -> GlColor => ... }`. `$this` is the receiver. A real member always wins, so an extension can only add a name the type did not have.",
        ["flags"] = "Declare an enum whose members are meant to be combined: bitwise operators over its members yield a value of the same enum, named from the bits it carries. Distinct from `flag`, which declares a script flag.",
        ["proud"] = "Explicitly mark a member as public.",
        ["public"] = "Explicitly mark a member as public (no-op, members are public by default).",
        ["fluid"] = "Mark a struct as mutable, allowing field reassignment after construction.",
        ["leaky"] = "Modifier on `rune` definitions — allows the rune to be subclassed (non-sealed).",
        ["struct"] = "Define a value-type with positional fields, structural equality, and copy-on-assign semantics.",
        ["trait"] = "Define a trait with required and default method/property signatures that classes can adopt via 'uses'.",
        ["callback"] = "Declare the shape of a C function pointer so native code can call back into ToastScript: `raw callback Comparator(a: ptr, b: ptr) -> int`. The name is then usable as a parameter type inside a `bind native` block.",
        ["fulfills"] = "Declare that a class conforms to one or more interfaces.",
        ["implements"] = "Declare interface conformance — alias for `fulfills`. Example: `class Circle implements IShape { ... }`.",
        ["extends"] = "Specify a base class for inheritance. Example: `class Dog extends Animal { ... }`.",
        ["uses"] = "Declare that a class adopts one or more traits.",
        ["handles"] = "Attach a method as an event handler. Example: `func onData handles DataReceived { ... }`. Combine with `when`, `priority`, and `once`.",
        ["when"] = "Guard predicate on an event handler. Example: `func f(event) handles Changed when { $event.value > 0 } { ... }`. The parameter list is required.",
        ["priority"] = "Numeric execution order for a `handles` clause. Lower numbers run first. Example: `func f handles E priority 10 { ... }`.",
        ["once"] = "Mark a `handles` clause to run the handler only once, then auto-deregister. Example: `func f handles E once { ... }`.",
        ["global"] = "Publish a declaration to the session-wide scope.",
        ["export"] = "Publish a module declaration or export an environment value.",
        ["using"] = "Import CLR namespaces or type aliases in the current lexical scope.",
        ["require"] = "Load a `.tosh` module, `.dll`, `.csproj`, or native shared library once per session and import it lexically.",
        ["native"] = "In `require native`, load a native shared library for binding and invocation.",
        ["bind"] = "Attach native exports to a required native module as callable ToSh functions.",
        ["from"] = "Select a source path in selective `require` forms.",
        ["as"] = "Type-cast operator or import alias keyword. As an infix operator, casts a value to the named type (e.g. `5 as float`). In `using` / `require` / `bind`, renames an imported symbol.",
        ["out"] = "Mark a native binding parameter as output-only and return the updated value after the call.",
        ["ref"] = "Mark a native binding parameter as by-reference so the call can read and update it.",
        ["callconv"] = "Override the native calling convention for a bound export.",
        ["if"] = "Run a block conditionally.",
        ["else"] = "Fallback branch for `if`.",
        ["for"] = "Iterate an enumerable source.",
        ["in"] = "Membership operator and `for` loop keyword.",
        ["while"] = "Repeat while a condition is true.",
        ["until"] = "Repeat until a condition is true.",
        ["return"] = "Return early from a function or script.",
        ["yield"] = "Emit a value from a generator function. Each `yield` produces one pipeline item.",
        ["defer"] = "Execute a block when the current scope exits. Multiple defers run in reverse declaration order (like Go's defer). Example: `defer { close $file }`.",
        ["throw"] = "Raise an error value.",
        ["try"] = "Begin a `try` / `catch` / `finally` block.",
        ["catch"] = "Handle a thrown value or failure.",
        ["finally"] = "Always run cleanup code.",
        ["switch"] = "Match a value against `case` clauses.",
        ["match"] = "Expression-style branching with ordered `=>` arms.",
        ["case"] = "A `switch` branch.",
        ["default"] = "Fallback `switch` branch or `match` arm.",
        ["break"] = "Exit the current loop.",
        ["continue"] = "Continue to the next loop iteration.",
        ["and"] = "Logical conjunction.",
        ["band"] = "Bitwise AND over whole numbers or enum members. Binds tighter than comparison: `$f band Mask == 0` groups as `($f band Mask) == 0`.",
        ["bnot"] = "Bitwise complement of a whole number. Prefix, alongside `not` and unary `-`.",
        ["bor"] = "Bitwise OR. Over members of a `flags` enum the result keeps the enum type. Example: `InitFlag.Video bor InitFlag.Audio`.",
        ["bxor"] = "Bitwise exclusive OR.",
        ["shl"] = "Shift left. Example: `1 shl 5`.",
        ["shr"] = "Shift right. Example: `32 shr 2`.",
        ["has"] = "Whether every bit of the right flag is set in the left value. Example: `$flags has InitFlag.Audio`.",
        ["or"] = "Logical disjunction.",
        ["not"] = "Logical negation.",
        ["not-in"] = "Negated membership operator. Returns `true` if the value is not found in the collection. Example: `4 not-in [1,2,3]`.",
        ["is"] = "Infix type-check operator. Returns `true` if the value matches the named type. Example: `5 is int`. Use `is not` or `is-not` as the negated form.",
        ["is-not"] = "Negated type-check operator. Returns `true` if the value does not match the named type. Example: `5 is-not string`. The two-word form `is not` is equivalent.",
        ["is-in"] = "Membership form of `is`. Written as `is in` (two words): `3 is in [1,2,3]` → `true`. The normalized form `is-in` is produced internally by the parser.",
        ["is-not-in"] = "Negated membership form of `is not`. Written as `is not in` (three words): `4 is not in [1,2,3]` → `true`. The normalized form `is-not-in` is produced internally by the parser.",
        ["contains"] = "Ordinal substring or collection-membership operator. Dictionaries search keys; other collections use canonical equality. Example: `\"hello\" contains \"ell\"`.",
        ["starts-with"] = "String prefix operator. Returns `true` if the string starts with the given prefix. Example: `\"hello\" starts-with \"he\"`.",
        ["ends-with"] = "String suffix operator. Returns `true` if the string ends with the given suffix. Example: `\"hello\" ends-with \"lo\"`.",
        ["where"] = "Filter clause in a comprehension, after the `<|`. Example: `[$x <| for x in 1..6 where ($x % 2) == 0]`. Also a pipeline command for filtering objects.",
        ["let"] = "Binds a variable inside a comprehension clause list. Example: `[$y <| for x in 1..3 let y = ($x * 2)]`. Comprehensions are body-first — the expression comes before `<|`.",
        ["set"] = "Property accessor inside a `prop` body, paired with `get`. Example: `class C { prop X { get { return $_x } set { $_x = $value } } }`.",
        ["get"] = "Property accessor inside a `prop` body. Example: `class C { prop X { get { return 1 } } }`. Also a pipeline command projecting members from objects, aliased as `pick` and `select`.",
        ["quote"] = "Capture a code block as a first-class value without evaluating it. Example: `var block = quote { ls | sort }`.",
        ["new"] = "Construct a CLR object or ToSh named type instance.",
        ["nameof"] = "Return the name of a variable or member path.",
        ["name-of"] = "Command-style alias for `nameof`.",

        // ── Script inputs and subcommands ──────────────────────────────────────
        ["arg"] = "Declare a positional script argument. Example: `arg target: string`. Reference it as `$target`; `--help` renders it from the doc comment's `@arg` tag.",
        ["flag"] = "Declare a boolean script flag. Example: `flag clean`. Passing `--clean` sets `$clean` to true.",
        ["subcommand"] = "Declare a named subcommand, turning a script into a structured CLI. The body runs when the user selects it, and subcommands nest for command trees.",
        ["subcmd"] = "Short spelling of `subcommand`.",
        ["eager"] = "Subcommand modifier: run this body even when dispatch descends into a child, for setup work. Cannot combine with `hollow`.",
        ["hidden"] = "Subcommand modifier: exclude from generated help and \"did you mean\" suggestions. Still fully callable.",

        // ── Control flow and types ─────────────────────────────────────────────
        ["unless"] = "Run the body when the condition is false — the inverse of `if`. Example: `unless ($done) { retry }`.",
        ["type"] = "Declare a type alias, or a refinement type narrowed by `where` predicates with optional `coerce` repair. Example: `type Port = int { where _ >= 1 and _ <= 65535 }`.",
        ["coerce"] = "Repair clause in a refinement type. `if <guard> coerce <expr>` normalises before validation; a bare `coerce <expr>` fires only after a `where` predicate has failed.",

        // ── Literals ───────────────────────────────────────────────────────────
        ["true"] = "Boolean literal.",
        ["false"] = "Boolean literal.",
        ["null"] = "The absent value.",

        // ── C#-familiar spellings ──────────────────────────────────────────────
        // Accepted wherever the ToastScript word is, and documented so a reader
        // coming from C# is not stopped by vocabulary. `TS-P2-30`.
        ["private"] = "C#-familiar spelling of `shy`.",
        ["abstract"] = "C#-familiar spelling of `hollow`.",
        ["readonly"] = "C#-familiar spelling of `fixed`.",
        ["protected"] = "C#-familiar spelling of `guarded`.",
        ["override"] = "C#-familiar spelling of `overrule`.",
        ["obsolete"] = "C#-familiar spelling of `fading`.",
    };



    private readonly ToshRuntime _runtime;
    private IReadOnlyDictionary<string, CommandMetadata>? _metadataCache;

    public ToshLanguageFeatures()
    {
        _runtime = ToshRuntime.CreateDefault();
    }

    private IReadOnlyDictionary<string, CommandMetadata> GetMetadataLookup()
    {
        return _metadataCache ??= CommandMetadataExporter.BuildMetadata(_runtime.Commands)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CommandMetadata> GetAllCommandMetadata()
    {
        return CommandMetadataExporter.BuildMetadata(_runtime.Commands);
    }

    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string text, string sourceName)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var map = new TextCoordinateMap(parseResult.SourceText);

        // `TS-P2-09`. Severity is carried rather than discarded. It used to be
        // dropped here and every diagnostic reported to the editor as
        // `Severity: 1` — LSP for *Error* — so a warning underlined red and read as
        // a failure. A surface that calls everything an error trains people to
        // ignore all of it.
        var diagnostics = new List<(string Code, string Title, string? Help, TextSpan Span, ToshDiagnosticSeverity Severity)>();

        // A parse diagnostic has no severity field: the parser reports only things
        // that stop the parse, so Error is the fact rather than a default.
        foreach (var d in parseResult.Diagnostics)
            diagnostics.Add((d.Code, d.Title, d.Help, d.Span, ToshDiagnosticSeverity.Error));

        // Run binder pass and collect binder diagnostics
        var binderDiagnostics = Tosh.Language.Binding.Binder.Bind(
            parseResult,
            _runtime.Commands,
            isInteractive: false // Tome is not a REPL, so strict binder
        );
        // If a binder diagnostic has null Span, map it to the whole file
        var fileWideSpan = new Tosh.Runtime.TextSpan(0, parseResult.SourceText.Length);
        foreach (var d in binderDiagnostics)
        {
            var span = d.Span ?? fileWideSpan;
            diagnostics.Add((d.Code, d.Title, d.Help, span, d.Severity));
        }

        // Lower + type-check pass: surfaces builtin arity / argument-type
        // diagnostics (tosh.type.command_arity, tosh.type.command_argument,
        // …) that the binder alone cannot detect. Wrapped in try/catch
        // because lowering must never block the editor from showing the
        // parse + binder diagnostics it already produced.
        try
        {
            var unit = Tosh.Language.Binding.Lowerer.Lower(parseResult, _runtime.Commands);
            var typeDiagnostics = Tosh.Language.Binding.TypeChecker.Check(unit);
            foreach (var d in typeDiagnostics)
            {
                var span = d.Span ?? fileWideSpan;
                diagnostics.Add((d.Code, d.Title, d.Help, span, d.Severity));
            }
        }
        catch
        {
            // Lowering failures shouldn't suppress the diagnostics
            // already collected above.
        }

        // Map all diagnostics to LspDiagnostic
        return diagnostics
            .Select(diagnostic => new LspDiagnostic(
                map.ToRange(diagnostic.Span.Start, diagnostic.Span.End),
                Severity: ToLspSeverity(diagnostic.Severity),
                Code: diagnostic.Code,
                Source: "tosh",
                Message: diagnostic.Help is { Length: > 0 }
                    ? $"{diagnostic.Title}\n{diagnostic.Help}"
                    : diagnostic.Title))
            .ToArray();
    }

    /// <summary>
    /// Translates a ToastScript severity to the LSP scale.
    /// </summary>
    /// <remarks>
    /// The two enumerations agree on order and differ by one — ToastScript counts
    /// `Error` from 0, LSP from 1 — so this is an offset rather than a table.
    /// Written out anyway: a silent `+ 1` in the middle of a projection is the
    /// kind of thing that survives a renumbering it should not.
    /// </remarks>
    private static int ToLspSeverity(ToshDiagnosticSeverity severity) => severity switch
    {
        ToshDiagnosticSeverity.Error => 1,
        ToshDiagnosticSeverity.Warning => 2,
        ToshDiagnosticSeverity.Info => 3,
        ToshDiagnosticSeverity.Hint => 4,
        _ => 1,
    };

    public IReadOnlyList<LspLocation> GetReferences(string text, string sourceName, LspPosition position, bool includeDeclaration)
    {
        return DeclarationIndex.Create(sourceName, text).FindReferences(position, includeDeclaration);
    }

    public LspPrepareRenameResult? PrepareRename(string text, string sourceName, LspPosition position)
    {
        return DeclarationIndex.Create(sourceName, text).PrepareRename(position);
    }

    public LspWorkspaceEdit? Rename(string text, string sourceName, LspPosition position, string newName)
    {
        return DeclarationIndex.Create(sourceName, text).BuildRenameEdits(position, newName);
    }

    public IReadOnlyList<LspCodeAction> GetCodeActions(
        string text,
        string sourceName,
        LspCodeActionContext context)
    {
        var actions = new List<LspCodeAction>();
        foreach (var diag in context.Diagnostics)
        {
            switch (diag.Code)
            {
                case "tosh.parser.variable_references_require_dollar":
                    {
                        var insertAt = new LspRange(diag.Range.Start, diag.Range.Start);
                        var edit = new LspWorkspaceEdit(
                            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
                            {
                                [sourceName] = [new LspTextEdit(insertAt, "$")]
                            });
                        actions.Add(new LspCodeAction(
                            Title: "Add '$' prefix",
                            Kind: "quickfix",
                            Diagnostics: [diag],
                            Edit: edit));
                        break;
                    }

                case "tosh.parser.missing_statement_separator":
                    {
                        // Insert a semicolon immediately before the offending token
                        var insertAt = new LspRange(diag.Range.Start, diag.Range.Start);
                        var edit = new LspWorkspaceEdit(
                            new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
                            {
                                [sourceName] = [new LspTextEdit(insertAt, ";\n")]
                            });
                        actions.Add(new LspCodeAction(
                            Title: "Insert ';' separator",
                            Kind: "quickfix",
                            Diagnostics: [diag],
                            Edit: edit));
                        break;
                    }
            }
        }

        // Offer doc comment generation refactoring for un-documented symbols
        try
        {
            var index = DeclarationIndex.Create(sourceName, text);
            foreach (var sym in index.GetSymbols())
            {
                if (sym.DocComment == null && sym.SymbolKind is 2 or 5 or 10 or 12 or 23)
                {
                    var insertPos = new LspPosition(sym.Range.Start.Line, 0);
                    var docTemplate = $"## @summary {sym.Name} description.\n";
                    var edit = new LspWorkspaceEdit(
                        new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
                        {
                            [sourceName] = [new LspTextEdit(new LspRange(insertPos, insertPos), docTemplate)]
                        });
                    actions.Add(new LspCodeAction(
                        Title: $"Generate doc comment (##) for {sym.Name}",
                        Kind: "refactor.documentation",
                        Edit: edit));
                }
            }
        }
        catch { }

        return actions;
    }

    public LspSemanticTokens GetSemanticTokens(string text, string sourceName)
    {
        var map = new TextCoordinateMap(text);
        var rawTokens = new List<(int Line, int Start, int Length, int Type, int Modifiers)>();

        // 1. Scan for comments (lexer skips them)
        var lines = text.Split('\n');
        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            var inString = false;
            var stringChar = '\0';
            for (var ci = 0; ci < line.Length; ci++)
            {
                var ch = line[ci];
                if (inString)
                {
                    if (ch == '\\') { ci++; continue; }
                    if (ch == stringChar) inString = false;
                    continue;
                }
                if (ch is '"' or '\'') { inString = true; stringChar = ch; continue; }
                if (ch == '#')
                {
                    // A lone '#' only opens a comment when whitespace or the end of
                    // the line follows it; glued to a word it is a bareword character
                    // (`#ff0000`, `issue#42`). ToshCommentSyntax owns the rule.
                    if (!ToshCommentSyntax.OpensComment(line, ci)) continue;
                    var isDocComment = ci + 1 < line.Length && line[ci + 1] == '#';
                    var modifiers = isDocComment ? 0x04 : 0; // documentation modifier for ##
                    rawTokens.Add((lineIdx, ci, line.Length - ci, 0, modifiers)); // comment
                    break;
                }
            }
        }

        // 2. Lex the source to get tokens
        var lexer = new ToshLexer(text);
        IReadOnlyList<SyntaxToken> tokens;
        try { tokens = lexer.Lex(); }
        catch { return new LspSemanticTokens([]); }

        var index = DeclarationIndex.Create(sourceName, text);
        var builtinNames = new HashSet<string>(
            _runtime.Commands.All.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxTokenKind.EndOfFile) continue;

            var pos = map.ToPosition(token.Position);

            switch (token.Kind)
            {
                case SyntaxTokenKind.String or SyntaxTokenKind.InterpolatedString:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 2, 0));
                    break;

                case SyntaxTokenKind.Number or SyntaxTokenKind.UnitLiteral or SyntaxTokenKind.UnitSuffix:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 3, 0));
                    break;

                case SyntaxTokenKind.Boolean or SyntaxTokenKind.Null:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 1, 0x02)); // keyword + defaultLibrary
                    break;

                case SyntaxTokenKind.Bareword:
                    ClassifyBareword(token, pos, rawTokens, builtinNames, index);
                    break;

                case SyntaxTokenKind.Pipe
                    or SyntaxTokenKind.DoublePipe or SyntaxTokenKind.DoubleAmpersand
                    or SyntaxTokenKind.Ampersand
                    or SyntaxTokenKind.GreaterThanGreaterThan
                    or SyntaxTokenKind.LessThanLessThanLessThan
                    or SyntaxTokenKind.GreaterThanEqual or SyntaxTokenKind.LessThanEqual
                    or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                    or SyntaxTokenKind.Bang
                    or SyntaxTokenKind.QuestionQuestion or SyntaxTokenKind.QuestionDot
                    or SyntaxTokenKind.DotDot or SyntaxTokenKind.FatArrow
                    // LSP has no standard punctuation semantic-token type. Paired
                    // collection delimiters use the closest standard category so
                    // clients color each two-character token as a single unit.
                    //
                    // `<` and `>` are deliberately absent: the lexer cannot tell a
                    // comparison from a generic delimiter, and an `operator` token
                    // painted over `Point2D<T>` overrides the grammar's generic
                    // punctuation — semantic tokens always win over TextMate. The
                    // grammar distinguishes the two by adjacency, so it keeps the
                    // call (TS-P3-12).
                    or SyntaxTokenKind.OpenBraceColon or SyntaxTokenKind.ColonCloseBrace
                    or SyntaxTokenKind.OpenBracePipe or SyntaxTokenKind.PipeCloseBrace
                    or SyntaxTokenKind.OpenBracePercent or SyntaxTokenKind.PercentCloseBrace:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 7, 0)); // operator
                    break;
            }
        }

        // 3. Encode as delta-encoded LSP data
        rawTokens.Sort((a, b) =>
        {
            var lineCmp = a.Line.CompareTo(b.Line);
            return lineCmp != 0 ? lineCmp : a.Start.CompareTo(b.Start);
        });

        var data = new List<int>(rawTokens.Count * 5);
        var prevLine = 0;
        var prevStart = 0;
        foreach (var (line, start, length, type, modifiers) in rawTokens)
        {
            var deltaLine = line - prevLine;
            var deltaStart = deltaLine == 0 ? start - prevStart : start;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(length);
            data.Add(type);
            data.Add(modifiers);
            prevLine = line;
            prevStart = start;
        }

        return new LspSemanticTokens(data);
    }

    private void ClassifyBareword(
        SyntaxToken token,
        LspPosition pos,
        List<(int Line, int Start, int Length, int Type, int Modifiers)> rawTokens,
        HashSet<string> builtinNames,
        DeclarationIndex index)
    {
        var word = token.Text;

        if (word.StartsWith('`') &&
            UnitExpressionParser.TryParseConversion(word[1..], out _, out _, out _))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 3, 0)); // number/unit
            return;
        }

        // Variable reference ($name). For a dotted reference — `$this.X`, `$o.Y` — only the
        // head is the variable: one token across the whole path painted over the grammar's
        // accessor and member scopes, which is why `$this.X` rendered as a single flat colour
        // in any theme with semantic highlighting (TS-P3-12).
        if (word.StartsWith('$'))
        {
            var headLength = word.IndexOf('.') is var dot && dot > 0 ? dot : word.Length;
            rawTokens.Add((pos.Line, pos.Character, headLength, 4, 0)); // variable
            return;
        }

        // Language keywords
        if (Keywords.ContainsKey(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 1, 0)); // keyword
            return;
        }

        // Built-in commands
        if (builtinNames.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 5, 0x02)); // function + defaultLibrary
            return;
        }

        // User-defined function names
        var offset = token.Position;
        var visibleFunctions = index.GetVisibleFunctions(offset);
        if (visibleFunctions.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 5, 0)); // function
            return;
        }

        // User-defined type names
        var visibleTypes = index.GetVisibleTypeLikeSymbols(offset);
        if (visibleTypes.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 6, 0)); // type
            return;
        }
    }


}
