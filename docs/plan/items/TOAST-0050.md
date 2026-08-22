---
id: TOAST-0050
title: "A tuple type resolves but cannot be written in an annotation"
status: complete
area: toast
priority: 2
opened: 2026-08-22
closed: 2026-08-22
---

## Problem

`TypeNameResolver` resolves `(int, string)` to a proper `TupleType` — measured, with a
`DisplayName` of `(Int32, String)`, and `()` and `(int, string, bool)` resolve too. The
*parser* rejects it in both positions where it would be used:

```tosh
func two() -> (int, string) { return (1, "a") }   # tosh.parser.expected_type_name
var t: (int, string) = (1, "a")                   # tosh.parser.expected_type_name
```

So the type exists, resolves, has a display form, and cannot be spelled.

## Why a compiler wants it

Two-value returns are what a compiler does constantly: a parsed node **and** the position
after it, a value **and** the diagnostics collected reaching it, a token **and** whether it
was synthesised. Written today, each needs a declared record — which is fine for a shape
used repeatedly, and heavy for one used once.

## Scope

The gap is in the *annotation* grammar, not the type model. `ToshParser` accepts a type name
where an annotation is expected and has no production for a parenthesised list; the resolver
behind it already parses `(a, b)` into a `TupleNode`.

Tuple *values* — `(1, "a")` — and tuple destructuring already work; only the type annotation
is missing.

## Acceptance

- [x] `func f() -> (int, string)` parses and binds
- [x] `var t: (int, string) = …` parses and binds
- [x] A parameter may be annotated with a tuple type
- [x] Arity and element mismatches are reported, not silently accepted — and at any depth,
      including inside an array of tuples
- [x] Nested — `(int, (string, bool))` — and empty `()` behave as the resolver already says
- [x] The interpreted and compiled backends agree — three cases in `Corpus()`, and the
      compiled backend needed no change, because the annotation is text and the resolver
      behind both already read it
- [x] `docs/spec/` states the annotation form
- [x] A negative control — removing the parser's tuple production fails 18 tests and leaves
      the five neighbouring-syntax controls passing

## Resolution — 2026-08-22

The type model needed nothing. `TypeNameResolver` already parsed `(a, b)` into a `TupleNode`
and already applied `[]` and `?` to it, so the whole change is that the annotation grammar
can now produce the text it was always able to read.

### The gap was in two places, and the second is the one worth remembering

`ParseTypeName` accepted only a bareword. That half is obvious, and is what
`func f() -> (int, string)` hit — a straightforward `expected_type_name`.

`var t: (int, string) = …` never reached it. **`TryGetTypeNameEndOffset`** — a *lookahead*
deciding whether `var` begins a declaration at all — also only knew barewords, so the whole
statement fell through to command dispatch and reported:

> Command 'var' is not a registered builtin or function declared in this source.

A message about `var` for a defect in the type annotation. `TS-P2-69` fixed exactly that
shape for the `[]` suffix and left a comment saying so; the predicate then failed the same
way for `(`. Both suffix walks are now one shared method, which is the smallest thing that
makes them unable to disagree again. `TOAST-0002` is the item about why they had to agree by
hand at all.

### Two defects found underneath, both of the same kind

**`TryResolveTypeName` threw.** `AnnotationConversionFailure` calls it to decide whether a
failed conversion was really a truncation, and the CLR type-name parser does not decline a
name it cannot tokenise — it throws `FileLoadException: The given assembly name was invalid`.
So a nested tuple with a bad element reported that sentence instead of a diagnostic. A
`Try…` that throws is the defect; the tuple annotation merely reached it first.
`IsKnownAnnotatedType` already carried a comment that the loader "can throw on
angle-bracketed names containing commas" and worked *around* it by checking generics earlier
— the same bug, avoided rather than fixed, in the same file.

**A type that could be written and never satisfied.** Twice:

- `()` is the empty tuple type, and the empty tuple literal `()` evaluates to `null` — which
  the null check rejected before the tuple branch could see it.
- `(int, string)[]` parses, because the suffix walk is shared, but a tuple has no CLR name
  for the array lookup to find.

Both now hold. Leaving either would have been this item's own defect, one step along:
spelling a type that nothing can satisfy is what the item was filed about.

### `(int)` is `int`

The resolver reads a single parenthesised type as that type rather than a one-tuple, which is
what every language with this syntax does. The runtime check has to agree, or `var x: (int)`
binds as one type and is checked against another — which is precisely what happened on the
first run, and is why the rule is asserted rather than assumed.

## Notes

Split out of `TOAST-0048`'s audit, which found three orphans in the type model. This is the
one with the clearest use and the smallest fix — the type is already built.
