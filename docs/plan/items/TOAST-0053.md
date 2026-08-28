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

## Second slice — 2026-08-28

Patterns became *recursive*: a sub-pattern is an `ArgumentSyntax`, the same type the outer
pattern is built from, so nesting fell out rather than being added. `Some(Add(Lit(a), Lit(b)))`
binds two levels down, and named and positional forms mix freely — `Some(Lit { v })`.

Field patterns by name arrived with the same change. `Lit { v }` is shorthand for `Lit { v: v }`;
`Lit { v: got }` renames; and because the right of the colon is a *pattern* rather than a name,
`Node { kind: "if", body }` tests one field and binds another in one arm.

**The paren must abut but the brace need not.** `Ok (v)` stays a command and its argument, which
is what kept the first slice from taking anything away. `Node { … }` has no such collision — a
bareword followed by a block is not something a pattern could already have been — so requiring
the brace to abut only rejected the spelling everyone writes.

### A `$variable` sub-pattern matched anything

The lexer hands a variable reference back as a `Bareword` token whose *text* carries the `$`.
`ParseSubPattern` asked only for the token kind, so `$x` took the "a plain name binds" path:
`Lit($x)` bound a fresh `x` and matched every `Lit`, and `Node { kind: $expected }` matched every
node. Silently — rebinding a name that already exists is legal, so nothing failed.

`Lit(5)` and `Lit((2 + 3))` compared correctly the whole time, which is exactly what hid it. Only
the *miss* case can catch this, and the first test written for the form asserted a hit. Both
directions are now asserted, and the negative control which drops the guard fails those two tests
and nothing else.

Three sites needed the same guard — the pattern's own name, a field name, and a sub-pattern — so
the predicate is named `IsVariableToken` rather than repeated.

### Capture analysis had to learn the node

`SyntaxTraversalExhaustivenessTests` failed the moment `VariantPatternSyntax` existed:
`VariableBinder` did not walk it, so a reference inside a pattern was invisible to capture
analysis. Walking the sub-patterns was the fix rather than recording a gap, because
`Node { kind: $expected }` inside a closure is the case that needs the captured value — it is
pinned by a test that builds a matcher from a captured string.

### The unknown-field diagnostic is raised at runtime, not at binding time

Naming a field the variant does not have is now an error that names the field and suggests the
nearest declared one, instead of a silent miss. But it fires when the arm is *reached*, not when
the pattern is bound, so a bad pattern in an arm that never runs is still not reported.

Binding time is where it belongs, and it is not reachable yet: a `union` is an ordinary statement
evaluated at runtime, so at lowering there is no variant declaration to check the field against.
That wants union definitions visible to the binder, which is `TOAST-0052`'s territory rather than
a detail of this item. The box stays partial for that reason.

## What is left

List patterns with a rest binding, or-patterns and `as`, the shadowing diagnosis, compiled
emission with the differential corpus, and the spec table.

## Acceptance

- [x] Variant patterns bind fields positionally — `Ok(v)`, `Add(l, r)` — **interpreted**
- [x] Variant patterns bind fields by name, with shorthand — `Lit { v }`, `Lit { v: got }`,
      and a literal or `$variable` on the right to test rather than bind — **interpreted**
- [ ] Record and class patterns bind fields by name
- [ ] List patterns with a rest binding — `[first, ..rest]`
- [x] Patterns nest to arbitrary depth — `Some(Add(Lit(a), Lit(b)))`, mixing both forms
- [ ] Or-patterns, and `as` to bind the whole while destructuring the parts
- [~] Bound names are scoped to their arm — done, and pinned by three tests. Shadowing is *silent* rather than diagnosed, which is still open
- [~] A pattern naming a field the variant does not have names the field and suggests the
      nearest one, rather than missing silently — but at **runtime**, when the arm is reached,
      not at binding time. See the second slice: the binder cannot see a `union` yet
- [x] Guards compose with bindings — the guard sees the bound names, **interpreted**
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `§Match Expressions` documents the full pattern grammar in one table
