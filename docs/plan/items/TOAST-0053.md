---
id: TOAST-0053
title: "`match` cannot bind a union's fields, so dispatch is a switch on a string"
status: partial
area: toast
priority: 1
opened: 2026-08-22
---

## Problem

`§Match Expressions` lists five pattern forms: a literal, `_ <op> <value>`, `_ is <type>`,
bare `_`, and `default`. Every one of them tests the matched value as a whole. None binds
anything out of it.

So the central operation of writing a compiler cannot be written:

```tosh
match ($node) {
    Add(l, r)     => (eval $l) + (eval $r)
    Lit(v)        => $v
}
# error while evaluating this expression
```

What the specification offers instead is the form in `§Union Definitions`:

```tosh
switch ($r.Variant) {
    case "Ok"    { echo $r.value }
    case "Error" { echo $r.message }
}
```

A string comparison, then unchecked member access. A typo in `"Ok"` is a silent miss; a
typo in `.value` is a runtime error. Neither is reported where it is written.

## What is missing

Beyond variant patterns, every pattern form that binds:

```tosh
match ($node) {
    Add(l, r) if ($l is Lit)  => ...     # variant, binding, guard
    Node { kind: "if", body } => ...     # field patterns, shorthand bind
    [first, ..rest]           => ...     # list patterns with rest
    (Lit(0) | Lit(1)) as lit  => ...     # or-patterns, @-binding
    Some(Point(x, y))         => ...     # nesting
}
```

Guards already exist and work — `if ((<condition>))` after the pattern — so the arm
structure is in place. What is absent is anything on the left of the arrow that introduces
a name.

## Why this is the largest of the three

`TOAST-0052` gives variants types; this makes them reachable; `TOAST-0054` makes the set
complete. Of the three this is the one with the most grammar and binding work, and it is
the one whose absence is most visible: a compiler written without it is written in the
`switch`-on-string form above, a hundred thousand lines of it.

The parser work in `TS-P2-11` and `TOAST-0002` is adjacent — patterns are a
new expression-position grammar, and filing them into a parser that is being restructured
by hand is the more expensive order.

## First slice — 2026-08-28

Positional variant patterns, interpreted. `Ok(v)`, `Add(l, r)`, `Add(_, r)` and `Lit()` all
bind and dispatch; a guard sees what the pattern bound; a binding is scoped to its arm.

**The parser recognises the form only when the paren abuts the name.** `Ok (v)` with a space
is a command and its argument and stays that way, so adding this took nothing away from an
existing arm. Anything other than "bareword, paren, plain names, paren" — a call with an
expression argument, a literal, a nested pattern — is left to `ParseArgument`.

**Binding reads the variant's declared field names, not `GetMembers()`.** The first
implementation used the latter, which prepends a `Variant` entry, so `Ok(v)` bound `v` to the
string `"Ok"` and every pattern sat one position out — while still *matching*, so nothing
failed loudly. `Add(l, r)` on `Add(3, 4)` returned `"Add3"` rather than `7`. A test now names
that specifically, and the negative control which restores the old lookup fails six of the ten.

Arity is checked where the pattern is matched rather than where it binds, so a pattern naming
three fields of a two-field variant does not match and then bind null.

## The compiled backend does not have this yet

`--compile` refuses the shape by name — *"dynamic argument expressions (VariantPatternSyntax)
are not yet emitted"* — under every profile, so the differential corpus cannot carry a case:
its harness requires a clean emit, and this is not one. Refusing by name is the right failure,
but it means acceptance is **interpreted-only** for this slice and the corpus box stays open.

Emission needs its own bound node, lowering, and somewhere for the bound names to live as
locals the arm body can read — the same scope-pushing shape rune expansion needed. That is a
slice of its own rather than a detail of this one.

## Acceptance

- [x] Variant patterns bind fields positionally — `Ok(v)`, `Add(l, r)` — **interpreted**
- [ ] Variant patterns bind fields by name, with shorthand — `Lit { value }`
- [ ] Record and class patterns bind fields by name
- [ ] List patterns with a rest binding — `[first, ..rest]`
- [ ] Patterns nest to arbitrary depth
- [ ] Or-patterns, and `as` to bind the whole while destructuring the parts
- [~] Bound names are scoped to their arm — done, and pinned by three tests. Shadowing is *silent* rather than diagnosed, which is still open
- [ ] A pattern naming a field the variant does not have is a *binding-time* diagnostic
      naming the field, not a runtime miss
- [x] Guards compose with bindings — the guard sees the bound names, **interpreted**
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `§Match Expressions` documents the full pattern grammar in one table
