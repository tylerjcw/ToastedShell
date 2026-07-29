# RFC: Brace Disambiguation (`TS-P2-25`)

**Status:** Accepted with modification — July 28, 2026
**Owning item:** `TS-P2-25`, gating the remainder of parser step 2 (`TS-P2-24`)
**Prepared:** July 27, 2026

## Accepted decision

ToastScript assigns one ordinary expression construct to each brace
delimiter:

```tosh
{ echo "block" }                 ## block
{| name: "Ada", active: true |}  ## record
{% "name" => "Ada" %}            ## dictionary
{: "red", "green" :}             ## set
```

The lexer emits six paired-literal tokens — `{:`, `:}`, `{|`, `|}`, `{%`,
and `%}` — so the construct is known from its opening token and recovery
can name the required closing token. Empty values are `{::}`, `{||}`, and
`{%%}`.

In ordinary expression and command-argument parsing, a plain `{` starts a
block. Plain braces that belong to a more specific grammar production
remain plain braces: class and member bodies, switch and match arms,
destructuring, import lists, projections, property accessors, and the
other parser-owned structural groups are not collection literals and are
not changed by this decision.

This accepts Option B's structural principle but modifies its spelling.
Separate paired delimiters were chosen over the historical `@{ ... }`
proposal because they:

- distinguish record, dictionary, and set without content lookahead;
- identify the construct at both boundaries, improving diagnostics and
  structural recovery; and
- leave no record-versus-dictionary classifier for `LiteParser` or the
  recursive-descent parser to keep synchronized.

The old unpaired record/dictionary forms and the generic brace collection
form are removed rather than retained as compatibility syntax. The
pre-decision measurements and options below are preserved as the design
record; references there to `@{ ... }` are historical and superseded by
this section.

`LiteParser` pairs these delimiters without assigning semantic roles to
plain braces. Each candidate boundary records the exact plain-brace
opener that owns it; `PromoteBoundariesForBlock` accepts an opener the
recursive parser has already proven is a block. This preserves candidates
inside specialized brace groups without pretending they are statements.
The fuller typed-region model for parsers, formatters, and language
services is filed separately as `TS-P3-08`.

`TS-P2-25` is filed as "`{` is structurally ambiguous and this now blocks
the structural pass." Before choosing a fix, the current behaviour was
measured rather than assumed. Some of what follows contradicts the item
as filed, and it changes which options are worth considering.

## 1. What the token stream actually says

### 1.1 There are five syntactic forms, plus a predicate variant

> **Corrected 2026-07-28.** This section originally claimed "four forms,
> not five" and that the `where` special case was not load-bearing. Both
> were wrong, and both were wrong the same way: the parser's *dispatch*
> was read without enumerating what it dispatches to. The corrected
> account follows.

The forms are block, record, dict, set, and — missed entirely on the first
pass — a generic brace collection: `{ 1, 2, 3 }` evaluated to an
`array<int>` via `ParseBraceCollectionLiteralArgument`. It was undocumented,
had zero occurrences anywhere in `examples/`, `tests/`, `docs/`, or the
ToastScript embedded in the C# test sources, and duplicated `[1, 2, 3]`.
It is removed by this decision rather than given a delimiter.

A predicate is not a sixth *parse*, but neither is it an ordinary block.
`ToshParser.ParseCommandArgument` dispatches on a hardcoded command name:

```csharp
case SyntaxTokenKind.OpenBrace:
    if (string.Equals(commandName, "where", StringComparison.OrdinalIgnoreCase))
    {
        return ParsePredicateBlockArgument();
    }
```

That special case **is** load-bearing, contrary to the original claim.
`ParsePredicateBlockArgument` does not call the ordinary statement parser;
it calls `ParseWherePredicateExpression`, a separate expression grammar.
The evidence originally offered against it — that `filter { $_ > 1 }`
behaves identically — proved nothing, because that form uses an explicit
`$_`. The specification's own idiom is the bare underscore
(`where _.Name =~ "\.cs$"`), which depends on the dedicated grammar.
Deleting the special case would have broken documented syntax.

The spelling-driven dispatch is still worth removing, but it belongs to
`TS-P2-23` (identity from a table rather than from a name), not here —
`ParseContext` already carries the host's command names and is the natural
place to record which commands take a predicate block.

### 1.2 Position already determines the meaning, totally

**In expression position, `{` is always a literal.** A block is not
merely disfavoured there — it does not parse:

```tosh
var b = { echo hi }     # tosh.parser.missing_list_separator
```

**In command-argument position, `{` is always a block** unless it is
set- or dict-shaped. A record is unreachable:

```tosh
echo (type-of { a = 1 })   # tosh.parser.variable_references_require_dollar
                           # — parsed as a block, then `a = 1` rejected
```

So the two contexts are already disjoint. That is a stronger starting
position than the item implies, and it is also the inconsistency worth
fixing: `{ a = 1 }` means two different things depending on where it
appears.

### 1.3 Every current decision is bounded

| Form | Rule | Lookahead |
|---|---|---|
| set | token after `{` is a bareword spelled `:` or `::` | `Peek(2)` |
| dict | `{ <key> => ` for bareword/string/number key | `Peek(2)` |
| record | `{}`, `{ (expr) =`, `{ ...$x`, `{ name:`, or `Peek(2)` is `=`/`:` | `Peek(2)` |
| block | none of the above | — |

No rule needs unbounded lookahead. Worth noting that `{:` is not a
token: the lexer emits `:` as a *bareword* and the set check compares its
text, which is the same spelling-based identity noted above.

### 1.4 The genuine ambiguity, and it resolves silently

The item claims `var r = { a = 1 \n b = 2 }` is "token-for-token
indistinguishable from a two-statement block." It is not, because
ToastScript assignment targets require `$` — a two-statement block is
`$a = 1 \n $b = 2`, which is a different token stream.

But that is exactly where the real ambiguity lives, and it currently
resolves the wrong way without saying so:

```tosh
var b = { $x = 1 }
echo (type-of $b)     # → table  (a record whose key is $x)
```

`$x` lexes as a bareword, so `{ $x = ` satisfies the record rule at
`Peek(2)` and the block reading is never considered. A user writing a
two-statement block in expression position gets a record instead, with no
diagnostic. This is live, not theoretical.

### 1.5 Migration corpus is small

Approximate counts of record literals, the form any delimiter change
would touch:

| Location | Sites |
|---|---|
| `docs/spec/*.tex` | ~17 |
| `docs/cheatsheets/` | 14 |
| `examples/` | 3 |
| `tests/` (embedded ToastScript) | ~23 |
| **Total** | **~57** |

Set literals: 7 in the spec, 4 in `.tosh` sources. Dict literals: ~11 in
the spec. These are grep-findable, not scattered through user code that
does not exist — TōSh has two users and no external consumers.

## 2. The decision is two separable questions

**Q1 — Structural.** How does `LiteParser` decide whether a line break
inside `{ … }` separates statements? This is what blocks step 2.

**Q2 — Consistency.** Should `{ a = 1 }` mean the same thing in every
position? This is a language-contract question and is independent of Q1.

The item conflates them. Q1 has a cheap answer that needs no grammar
change; Q2 is where the real design choice is.

## 3. Historical options

### Option A — Bounded classification in the structural pass

Let `LiteParser` classify each `{` using the token before it and at most
two after, exactly as the parser already does. Share one predicate
between them, as `TS-P2-06` did for `IsExpressionStartToken`.

- **Grammar change:** none. **Migration:** none.
- **Answers:** Q1 only.
- **Cost:** `LiteParser` stops being meaning-free, which was its stated
  design virtue. The classifier must stay in sync with the parser — a
  shared predicate makes drift unlikely but not impossible.
- **Leaves standing:** §1.2's positional inconsistency and §1.4's silent
  misparse.

### Option B — One construct per delimiter

`{` opens a block and nothing else. Every literal form takes a sigil:

```tosh
{ … }            ## block, always — including after if/for/func/where
@{ a = 1 }       ## record
@{ "k" => 1 }    ## dict
@{: 1, 2 :}      ## set
```

See §6 for why `@` and not `#`.

- **Grammar change:** yes. **Migration:** ~57 sites, all grep-findable.
- **Answers:** Q1 and Q2.
- **Why it settles Q1 completely:** the pre-pass decides from the opening
  delimiter alone, with *zero* lookahead — `{` means newlines separate,
  `#{` means they do not. Record-versus-dict stays a parser concern at
  `Peek(2)`, but that no longer affects structure, so the pre-pass never
  needs to care.
- **Cost:** the largest breaking change, and `#{` is more to type for the
  common record case.
- **Bonus:** §1.4 disappears — `#{ $x = 1 }` is unambiguously a record and
  `{ $x = 1 }` unambiguously a block.

### Option C — Formalize the positional rule

Write down what §1.2 already does: `{` in expression position is always a
literal; `{` in statement or argument position is always a block. Make
records reachable in argument position through parentheses. Remove the
`where` special case.

- **Grammar change:** small. **Migration:** near zero.
- **Answers:** Q2 partially — it makes the rule *stated* rather than
  consistent. `{ a = 1 }` still means two things.
- **Does not answer Q1** on its own: the pre-pass still has to determine
  position, which needs Option A's machinery.

### Option D — C then A

Formalize the contract, then let the shared classifier implement it.
Cheapest path that closes the item. Accepts that `{` stays overloaded and
that §1.4 keeps its silent misparse unless separately diagnosed.

## 4. Historical recommendation

**Option B**, on three grounds.

1. It is the only option that matches the acceptance already written for
   `TS-P2-25` — "a `{` opens exactly one construct decidable from the
   token stream" — rather than the escape clause after it.
2. The cost is known and small. ~57 mechanical sites, no external
   consumers, and the July 26 decision explicitly permits breaking
   syntax where the grammar is the root cause. This *is* a case where the
   grammar is the root cause.
3. It removes a live silent-wrong-answer (§1.4) rather than documenting
   around it, and it is the only option that leaves the structural pass
   needing no lookahead at all — which is what step 2 was for.

**If the migration is unwelcome, Option D** is a legitimate close: it
satisfies the item's second acceptance clause. It should then land with a
diagnostic for §1.4, so the silent misparse becomes an error rather than
a documented quirk.

I would not recommend Option A alone. It unblocks step 2 while leaving
both language-level defects in place, and those are the parts a user
actually encounters.

## 5. Historical Option B landing sketch

Per the item, specification, examples, cheatsheets, and test corpus change
in the same slice.

1. Lexer: emit `@{` as one token, alongside the existing `@(` handling;
   make `{:`/`:}` real tokens rather than barewords spelled `:` (closes a
   `TS-P2-23` spelling dependency).
2. Parser: `{` in every position parses a block; `@{` parses a literal and
   keeps the existing `Peek(2)` record-versus-dict rule; delete
   `LooksLikeRecordLiteral`'s positional callers and the `where`
   special case.
3. `LiteParser`: newline separates inside `{`, not inside `@{`.
   Promote brace-enclosed candidates to real boundaries — the change
   step 2 was blocked on.
4. Migrate ~57 sites; rebuild the specification and both cheatsheets.
5. `LexerCharacterizationTests` gains the new delimiters; the entries
   pinned to today's brace behaviour move groups in the same commit.
6. Decision-log entry recording the choice and this RFC.

## 6. Historical sigil analysis

`@{` is proposed. The candidates were checked against the lexer rather
than chosen by taste.

| Sigil | Verdict |
|---|---|
| `@{` | **Proposed.** The lexer already special-cases `@(` in two places, so `@{` is a symmetric extension rather than a new concept. `@` appears in the `.tosh` corpus only inside doc comments (`@summary`, `@returns`), which never reach code lexing. PowerShell spells its hashtable literal `@{`, which suits TōSh's lineage. |
| `#{` | **Rejected — would silently delete code.** Any `#` begins a comment: `##{` is a block comment, `##` a doc comment, and bare `#` falls through to `SkipComment()`. `#{ a = 1 }` therefore lexes as a line comment and the record disappears with no diagnostic. Making it work needs `#{` checked ahead of the comment branch, which makes `#` context-dependent — the exact class of spelling ambiguity `TS-P2-23` is removing. |
| `%{` | Workable but weaker. `%` has no lexer special case, but it is the modulo operator and appears 42 times in the corpus as one. Decidable, since `%` followed by `{` is not valid in operator position, but it trades a free sigil for a lookahead rule. |
| `${` | Rejected. Collides visually with interpolation and variable reference. |

The `#{` finding is worth recording on its own: it was the first spelling
proposed here, and reading the lexer rather than reasoning from other
languages is what caught it.
