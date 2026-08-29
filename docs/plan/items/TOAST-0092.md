---
id: TOAST-0092
title: "A value cannot be written to a file and read back as itself, in any format"
status: proposed
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

## Acceptance

- [ ] A grammar small enough to implement in another language, published as a specification
- [ ] `to ton` / `from ton`, and a decided file extension and verb
- [ ] Every declared shape round-trips: record, struct, class, enum, union variant, quantity,
      dict, set, array, and a heterogeneous stream
- [ ] Only Tōast-declared types resolve; a CLR type name is refused, with a test that names one
- [ ] Paths resolve by name against enum and union kinds only; a static property is refused
- [ ] Validation runs over the AST before evaluation, and a document containing a pipeline,
      variable, operator or call is refused with the construct named
- [ ] Comments, trailing commas and newline separators survive a read/write cycle
- [ ] `--typed` for json, toml and xml; csv refuses a nested value rather than flattening it
- [ ] A conformance corpus that a third-party implementation can run
- [ ] Interpreter and compiler agree

## Dependencies

`TOAST-0091` supplies the typed literal, without which a class whose state is not all
constructor arguments cannot be written down at all. `TOAST-0090` supplies the path operator
that makes the safety rule syntactic. `TOAST-0083` must decide `Option` and `Result`'s
serialised contract with their type arguments, since a `Result` without them cannot be narrowed
back to `Result<int, ParseError>` — retrofitting that later means changing a format everything
depends on.
