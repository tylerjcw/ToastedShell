---
id: TOAST-0092
title: "A value cannot be written to a file and read back as itself, in any format"
status: partial
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

`$v | to json` then `| from json` returns an `ExpandoObject`, never the type that went in.
`from json` has no target-type option, and `cast` declines by design:

> `'Exchange' is a declared type, and cast converts only a value that already is one.`

So there is no path from a serialised value back to a declared type — not by accident, but
because nothing in any format carries the type. A `Villager` written out and read back is a
bag of fields that no longer matches `match`, narrowing, or its own methods.

What each format carries today:

| shape | emitted | recoverable |
|---|---|---|
| union variant | `{"Variant": "Ok", "value": 5}` | the variant, not which union |
| record | `{"X": 1, "Y": 2}` | nothing |
| class | `{"W": 1}` | nothing |
| enum | `"Novice"` | nothing (`TOAST-0088` made it legible; it was never recoverable) |

## The notation

Rather than a tagging convention bolted onto JSON, **Tōast Object Notation is the subset of
Tōast's own value syntax that is meaningful without a schema** — the relationship JSON has to
JavaScript, EDN to Clojure, `literal_eval` to Python.

The language already has a literal form for nearly everything, and it already works as a file
format. Verified: multi-line, trailing commas, newline separators in records, nested values,
`null`, hex, exponents, block strings, and **comments inside literals** — the omission JSON's
authors have publicly regretted.

```tosh
Villager {|
    # bumped after the trade rebalance
    Profession = Profession::Librarian
    Level      = Level::Novice
    Name       = "Steve"
    Trades     = [
        Trade {|
            Give    = [ Exchange {| Item = "Emerald", Amount = 1 |} ]
            Receive = [ Exchange {| Item = "Book",    Amount = 1 |} ]
        |},
    ]
|}
```

Other shapes need no new machinery, because the language already spells them:

```tosh
Option::None                        Result<int, ParseError>::Error( … )
{% "retries" => 3 %}                {: "read", "write" :}
483.06`MW                           0x54
```

A heterogeneous stream needs no envelope: every value carries its own type, which answers the
objection that a `--as <type>` flag cannot work for a pipeline of mixed values.

## Positional constructor arguments are not portable

`new Exchange("Emerald", 1)` parses anywhere but *means* nothing without knowing the field
order — and reordering a record's fields would silently corrupt every existing file. **Named
fields are required.** This is why the notation is "the subset that is meaningful without a
schema" rather than "a subset of the grammar".

Parsing is schema-free; *binding* needs the schema. A foreign reader produces
`{kind: "member", type: "Profession", name: "Librarian"}` faithfully and resolves nothing,
exactly as a JSON reader handles a `$type` key.

## Safety

Three rules, in decreasing order of how much they matter.

**Only Tōast-declared types are admitted.** Not CLR types. `new System.Diagnostics.ProcessStartInfo("ls")`
works in ordinary Tōast, and if the notation resolved through the same path, a document could
name any public type in any loaded assembly. That is precisely the `TypeNameHandling` and Java
gadget surface: the attacker supplies no code, only the *name of a type whose construction does
something*. Restricting to declared types closes the class structurally rather than by blocklist.

**A path is a lookup, never an invocation.** `Profession::Librarian` resolves by member name
against an enum or union. `Math::PI` is rejected on the kind of the type, and so is anything
.NET adds later. `TOAST-0090`'s operator makes this syntactic rather than semantic — member
access is not in the grammar, so no validator bug can admit `DateTime::Now`.

**Validation precedes evaluation.** The document is parsed with the real parser, then an AST
walk rejects everything outside the notation before any value is built. One grammar, one parser,
no second implementation to drift.

## Reconstruction, and what opts out

| kind | how | runs user code | default |
|---|---|---|---|
| enum, union variant | lookup by name | no | always |
| record, struct | field assignment | no | always |
| class | property assignment | only custom setters | yes |

Opt-out, not opt-in — a `Villager` needs no annotation. The exception is a class that cannot
honestly be rebuilt from data, such as one holding a live handle; that wants a modifier on the
*type* rather than a serialisation attribute, since it is a property of the type either way.
`shy` and `proud` are visibility and cannot be reused.

Two residual holes, stated rather than papered over: a custom setter still runs code, and
populating without a constructor does not re-establish invariants. `TOAST-0087` would eventually
let "reconstructable" mean "provably pure to populate" rather than a hand-maintained opt-out.

## The other formats

The tag convention generalises; fidelity does not, because the formats differ in what they can
represent. A typed write **round-trips or refuses** — no format silently degrades while claiming
to be typed.

| format | tag | nesting | verdict |
|---|---|---|---|
| toast | not needed — the value is its type | native | lossless |
| xml | an attribute | native | lossless |
| json | `$type` sibling key | native | lossless for data types |
| toml | `$type` key in the table | tables, arrays of tables | lossless, verbose |
| csv | a `$type` column | none | **flat records only; nesting is refused** |

## Decisions and progress (2026-08-29)

**`new` is kept, so a TON document is valid Tōast source.** The examples above were written
before `TOAST-0091` landed and omit it; the bare `Villager {| … |}` is grammatically identical
to a command invocation, which is why the typed literal requires `new`. Defining a terser
grammar for TON alone would break the premise the design rests on — a document only the
notation's parser accepts is not a subset of the language.

**Type arguments are written only where the payload cannot supply them.** `Option::Some(5)`
pins `T`; `Option::None<int>()` and `Result::Ok<int, string>(3)` cannot, so they carry theirs.
This settles the contract `TOAST-0083` deferred here.

Two other corrections to the examples above: the failure variant is `Err`, not `Error`, and the
spelling is `Result::Ok<int, string>(…)` rather than `Result<int, string>::Ok(…)`.

### Writing is implemented

`TonWriter` and `TonDataFormat`, registered beside json/csv/toml/xml. `to ton` works;
`from ton` declines with a reason rather than half-working.

```tosh
new Villager {|
    Name = "Steve"
    Job = Profession::Librarian
    Trades = [
        new Exchange(Item = "Emerald", Amount = 1),
    ]
|}
```

That document, evaluated as ordinary Tōast, rebuilds the value — which is the whole premise,
and is asserted directly in `TonWriterTests` rather than argued.

**No single spelling covers every shape**, so the writer picks per kind:

| shape | written as | why |
|---|---|---|
| record | `new V(Name = …)` | its fields *are* its constructor parameters, so there is no zero-argument constructor to fill afterwards |
| class, struct | `new C {\| … \|}` | the typed literal |
| named variant field | `U::Wrapped(value = 7)` | the name is declared |
| positional variant | `Option::Some(5)` | `Item1` is synthesised and does not read back; the variant is positional by declaration, so no order can be silently permuted |
| enum member | `P::Librarian` | a path, never a member access |
| quantity | `483.06`MW` | one value, not a shape — it implements `IShellRecordObject`, so this case must precede that one |

An immutable struct with no constructor and only properties has **no literal form at all**;
rare, and recorded rather than solved.

### Reading is implemented, and so are the three safety rules

`IContextualDataFormat` is the additive interface `to` and `from` prefer when a format
implements one; every other format stays a pure text transform and is untouched. TON is the
first that is not, and that is not incidental — resolving names against the program's own types
is what makes a value recoverable *as itself*.

`from ton` parses with the real parser, walks the tree with `TonValidator`, and evaluates only
what survives. There is no second parser to drift from the writer.

**The validator is an allowlist.** The language has forty argument node kinds and the notation
admits about ten. Naming what is permitted means a node kind added to the language later is
refused by default; naming what is forbidden would admit it by default, and the difference
between those two defaults is the whole security posture.

Refusals name the construct:

```
A TON document cannot contain a variable.
A TON document cannot contain a command.
A TON document cannot contain an operator.
A TON document cannot contain an index access.
A TON document cannot contain a positional constructor argument.
A TON document cannot contain 'System.Text.StringBuilder'.
A TON document cannot contain 'System.Math'.
```

The last two are the rule that matters: `System.Math::PI` is refused because `Math` is not a
type this program declared — on the *kind* of the type, not by naming the member, so whatever
.NET adds later is refused too. A blocklist would have to be kept up to date; this does not.

**Index access was a useful find.** `[1, 2, 3][0]` looks harmless and is evaluation, so it is
refused. Index the value after it is built, in ordinary Tōast.

### Specification and conformance corpus

`docs/spec/ton.md` publishes the grammar, the three rules a grammar cannot express, and the
per-shape spelling. `docs/spec/ton-conformance/` holds 16 accepted and 11 refused documents plus
the prelude declaring the types they name; `TonConformanceTests` runs them, so the corpus and the
implementation stay honest with each other rather than drifting.

Conformance is **accept-or-refuse**, not resolution: a reader in another language cannot know
which types the producing program declared, so it may return a faithful unresolved tree the way
a JSON reader handles `$type`. What it must not do is accept a document `refuse/` rejects.

**The corpus immediately earned its keep, twice.** Both findings were the same bug in different
clothes — the writer emitting something the reader could not take:

- The writer emitted `Shape::Circle(radius = 2)` for a variant with a declared field name. A
  union variant is constructed **positionally**; its field names belong to pattern matching and
  member access, so the named form parsed and then failed to convert deep in evaluation. That is
  precisely the bug a round trip is supposed to make impossible, and nothing else had caught it.
- The validator *accepted* that same form, letting the failure surface as a conversion error
  rather than a stated refusal. It now refuses it by name.

`.ton` is also served as `text/plain` by `http serve`, so a document is read rather than
downloaded as an unknown binary.

### `--typed` for the other formats

Placement is decided once, in `ShellDataSerializer.Normalize`, because every format reaches that
one method — so the tag nests with the value rather than sitting only at the root. Each format
then renders it the way its own syntax wants:

```
json   {"$type":"Exchange","Item":"Emerald","Amount":1}
toml   "$type" = "Exchange"
csv    $type,Item,Amount
xml    <root type="Exchange"><Item>Emerald</Item></root>
```

**XML uses an attribute because `$` is not a legal XML name character.** Left to the ordinary
path it sanitised to a `<__type>` element — a tag no XML reader would recognise as a type, which
is worse than none. A tagged enum becomes `<Job type="Profession">Librarian</Job>`: attribute
and text, since it has no fields to sit beside.

**CSV refuses a nested value rather than flattening it** — the "round-trips or refuses" rule
doing its job. CSV cannot nest, so the alternative is a document that claims to be typed and
cannot be read back:

```
'Trades' holds a nested value, which CSV cannot represent.
```

**A tagged enum changes shape**, from a bare string to `{"$type":…,"$value":…}`. Untagged there
is nowhere to say which enum a member belongs to — the gap `TOAST-0088` made legible but could
not close — and that shape change is what the flag opts into.

**TOML nearly shipped as a no-op.** It serialises the value directly rather than through
`Normalize`, so `--typed` was accepted and silently did nothing until it was wired separately —
exactly the failure the round-trips-or-refuses rule exists to prevent, arriving through the flag
meant to enforce it.

Untagged output is byte-for-byte unchanged, asserted directly.

### `from json --typed` honours what the writer promised

`TypedJsonDataFormat` wraps `JsonDataFormat` rather than replacing it — writing, compaction and
every untyped path are unchanged and still live in `Toast.Runtime` beside the other formats. The
wrapper exists only for the half that needs the program's declared types, which `Toast.Runtime`
cannot reach.

A record, class or enum tagged on write reads back as **itself**, not as a bag of fields:

```tosh
var j = (new Exchange(Item = "Emerald", Amount = 1) | to json --typed)
(from json --typed $j).ShellTypeName        # Exchange

var j = (Profession::Librarian | to json --typed)
(from json --typed $j) == Profession::Librarian    # true — identity, not just the name
```

**The same safety rule as TON, and it matters more here**, because JSON is the format that
actually receives untrusted input. A `$type` naming a CLR type is refused by name, so a document
cannot name a type whose *construction* does something:

```
'System.Text.StringBuilder' is not a type this program declares.
```

Tagging is per-value: an untagged part of a document comes back exactly as the format produced
it, so a tagged record may hold plain dictionaries and a plain list may hold tagged values.

Unions and structs are not rebuilt yet and say so; `from ton` covers the full set of shapes.

### A pre-existing bug this surfaced

`from` assumed **every flag takes a value** and skipped the following argument unconditionally.
That was invisible while every flag it accepted did take one — `-d ,` and friends. `--typed` was
the first boolean flag, so `from json --typed $document` read the document as the flag's value
and then reported that no text had been supplied, naming the argument that was sitting right
there. Valueless flags are now named explicitly, with a control test keeping `-d` consuming its
delimiter.

### Interpreter and compiler do not agree, and why

Three recorded divergences with **one root**: a declared type is an emitted CLR class in
compiled code, so nothing keyed on `ToshRecordInstance` or the engine's declared-type view
recognises it.

- `to ton` finds no shell shape for the value.
- `from ton` and `from json --typed` resolve names against declared types the compiled unit does
  not present through the same view.

Recorded rather than implemented, per the standing decision that compiled ToastScript is an
experiment and the interpreter is authoritative. The box stays open rather than being reworded,
because the two genuinely differ.

**Measuring it found a real bug in the writer, on both backends.** `TonWriter` ended its scalar
switch with `value.ToString()`, so a value it did not recognise was written as its *type name in
quotes* — a document that looked like data:

```
"ToshDiff_5ea80643….DiffTonExchange"
```

It now refuses, naming the type. That is the same silent-wrongness as a dropped object
initialiser, and precisely what "round-trips or refuses" exists to prevent; the compiled backend
merely happened to be the thing that walked into it first.

### Piped output — found by using it

`ls | to ton` refused, and `new Point2D(2, 3) | to ton` emitted a document this very
implementation would not read back. Both broke the same rule from opposite sides: **the writer
must emit only what the reader accepts.**

- **A value TON cannot name is written as an anonymous record.** A `FileSystemEntry` is neither
  a declared shape nor a scalar, so it hit the refusal. TON already had the spelling — `{| … |}`
  is in the notation and the reader takes it — and it is what `to json` does with the same value.
  The fallback normalises through `ShellDataSerializer.Normalize`, so TON stops keeping a second
  opinion about what a value is.
- **A name is emitted only when the reader can resolve it.** `Point2D` is declared inside
  `module ToastLib.Math`, so its bare `ShellTypeName` resolves to nothing and the notation has no
  spelling for the qualified form. It now degrades to an anonymous record, which still carries
  the data and still reads back.
- **Computed properties are not written.** `prop Magnitude: double => System.Math.Sqrt(…)` is
  derived, not stored. Writing it asserted a value nothing can accept back — and one that would
  be wrong the moment a reader edited `X` by hand, which is exactly what a notation invites.
- **Dates are written round-trip, not culture-formatted.** `ToString()` gave
  `"1/8/2026 8:57:52 PM -05:00"` — unparseable elsewhere and different on a machine with other
  regional settings.

```tosh
ls /etc/hostname | to ton
{|
    Name = "hostname"
    Size = 8
    Modified = "2026-01-08T20:57:52.3340513-05:00"
|}
```

### Generic type arguments are written; module-scoped names still are not

Investigating the two together showed they are **not** the same missing piece.

**Generic arguments were purely a writer gap.** The reader already accepted
`new Box<int> {| … |}` — the grammar and the construction path both handle type arguments; the
writer simply dropped them, so a `Box<string>` and a `Box<int>` came out as the same document.
It now writes them, in the *language's* spelling rather than the descriptor's:
`Box<int>`, not `Box<Int32>`. A notation whose central rule is that it names no CLR type should
not name one in its type arguments either.

**Module-scoped names are a genuine gap in the type model, not the writer.** A class exported
from a module registers under its **bare name inside the module's scope**
(`moduleScope.Classes[name]`); the qualified path `ToastLib.Math.Point2D` is resolved by walking
module scopes at lookup time and is never stored. So there is nothing for the writer to read
back, and reconstructing it means walking the module tree to find where a definition lives.

`new ToastLib.Math.Point2D<int>(…)` *does* parse and resolve, so the notation could carry it —
what is missing is a way to ask a definition for its own qualified name. Until then such a value
degrades to an anonymous record, which still carries the data and still reads back. Worth its
own item rather than being smuggled into this one.

**A class with a required primary constructor cannot use the literal form at all.**
`Point2D<T>(x: T, y: T)` has no zero-argument constructor to fill afterwards, and its parameters
(`x`, `y`) are not its property names (`X`, `Y`) — so even a qualified name would need a
property-to-parameter mapping the writer does not have.

### Comments cannot survive `to ton`, and that is not a bug to fix

Measured, not assumed:

```tosh
# the librarian was rebalanced in 1.21
new Villager {|
    Name = "Steve"      # placeholder name
    Level = 3
|}
```

read and written back becomes `new Villager {| Name = "Steve", Level = 3 |}`.

The box asked for comments to "survive a read/write cycle", and they cannot — because the cycle
is **document → value → document**, and the middle step is lossy by definition. `from ton`
produces a *value*; a value carries no comments; `to ton` serialises a value. There is nothing
for the writer to preserve. No amount of care in the writer changes that.

What a person editing a config file actually wants is a different operation: **document →
document**, where the parse tree and its trivia are kept, part of it is edited, and the rest is
written back byte-for-byte. That is an editing API, not a serialiser, and it should be a
separate verb rather than a promise `to ton` quietly fails to keep.

**The groundwork exists.** The lexer already captures comments with their offsets and whether
they stand on their own line:

```csharp
public sealed record LineComment(
    int Position, int EndPosition, int Line, bool IsFullLine, string Text);
```

`ParseResult` carries them. So the missing piece is a document model that holds the tree
alongside its trivia and can re-emit unchanged regions verbatim — `TOAST-0100`, which the
formatter wants for the same reason.

Trailing commas and newline separators are the same story: they parse, and they are formatting
the writer regenerates from values rather than reproduces.

### Every shape round-trips — and a set did not

Ten shapes written, read back, and still the same thing: record, struct, class, enum, union
variant, unit variant, quantity, array, dict, set. An array stays `array<int>`, a dict stays a
dict, a quantity stays a `PowerQuantity`.

**A set was written as an array.** `{: "a", "b" :}` came out `["a", "b"]` and read back as an
array, because a set *is* an `IEnumerable` and hit the sequence case first. A shape change
disguised as a round trip is the one thing a notation must not do quietly, and only writing out
all ten shapes side by side found it.

## Acceptance

- [x] A grammar small enough to implement in another language, published as a specification
- [x] `to ton` / `from ton`, `.ton`, and the `toast` alias
- [x] Every declared shape round-trips: record, struct, class, enum, union variant, quantity,
      dict, set, array, and a heterogeneous stream
- [x] Only Tōast-declared types resolve; a CLR type name is refused, with a test that names one
- [x] Paths resolve by name against enum and union kinds only; a static property is refused
- [x] Validation runs over the AST before evaluation, and a document containing a pipeline,
      variable, operator or call is refused with the construct named
- [x] Comments, trailing commas and newline separators are **read** — and are lost on write, for
      a structural reason stated below rather than a missing feature
- [x] `--typed` for json, toml and xml; csv refuses a nested value rather than flattening it
- [x] A conformance corpus that a third-party implementation can run
- [~] Interpreter and compiler **do not** agree; recorded, with the reason

## Dependencies

`TOAST-0091` supplies the typed literal, without which a class whose state is not all
constructor arguments cannot be written down at all. `TOAST-0090` supplies the path operator
that makes the safety rule syntactic. `TOAST-0083` must decide `Option` and `Result`'s
serialised contract with their type arguments, since a `Result` without them cannot be narrowed
back to `Result<int, ParseError>` — retrofitting that later means changing a format everything
depends on.
