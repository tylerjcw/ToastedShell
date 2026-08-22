---
id: TOAST-0053
title: "`match` cannot bind a union's fields, so dispatch is a switch on a string"
status: proposed
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

## Acceptance

- [ ] Variant patterns bind fields positionally — `Ok(v)`, `Add(l, r)`
- [ ] Variant patterns bind fields by name, with shorthand — `Lit { value }`
- [ ] Record and class patterns bind fields by name
- [ ] List patterns with a rest binding — `[first, ..rest]`
- [ ] Patterns nest to arbitrary depth
- [ ] Or-patterns, and `as` to bind the whole while destructuring the parts
- [ ] Bound names are scoped to their arm, and shadowing is diagnosed the way it is elsewhere
- [ ] A pattern naming a field the variant does not have is a *binding-time* diagnostic
      naming the field, not a runtime miss
- [ ] Guards compose with bindings — the guard sees the bound names
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `§Match Expressions` documents the full pattern grammar in one table
