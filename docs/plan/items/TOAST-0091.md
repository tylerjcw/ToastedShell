---
id: TOAST-0091
title: "A value whose state is not entirely constructor arguments has no literal form"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

A class or record can only be written as a value through its constructor, so any state set
after construction cannot be expressed:

```tosh
class Villager(p: Profession, l: Level) {
    prop Profession = $p
    prop Level = $l
    prop Name = ""
    prop Trades: List<Trade> = new List<Trade>()
}

new Villager(Profession.Librarian, Level.Novice)   # cannot say Name or Trades
new Villager(...) { Name = "Steve" }               # error today
Exchange {| Item = "x", Amount = 1 |}              # error today
```

An anonymous record can be written in full; a *declared* one cannot. The type that carries more
information is the one with the weaker literal form.

## Candidate surface

```tosh
Villager {|
    Profession = Profession::Librarian
    Level      = Level::Novice
    Name       = "Steve"
    Trades     = [ … ]
|}
```

A typed record literal: the same `{| … |}` an anonymous record uses, with the type in front. An
untyped literal remains exactly what it is today, so nothing existing changes spelling.

Two questions the design must answer rather than leave implicit:

- **Construct or populate.** Calling the constructor keeps invariants and runs its body;
  allocating and assigning runs no constructor but may leave invariants unmet. Deserialisers
  almost universally populate. The choice decides whether `Villager` above is expressible at all,
  since its constructor cannot set `Name`.
- **Which members are settable.** A computed or genuinely read-only property cannot be assigned,
  so a type may be only partly expressible. That has to be a diagnostic, not a silent omission.

## Why it is worth having on its own

`Villager {| Name = "Steve" |}` is a thing to want by hand — it is the object initialiser the
language lacks, and every comparable language has one. `TOAST-0092` needs it, but does not
justify it alone.

## Acceptance

- [ ] A typed record literal constructs a declared record, struct or class from named fields
- [ ] Construct-versus-populate is decided, stated, and consistent between the two tiers
- [ ] A field the type does not have, or cannot accept, is a diagnostic naming it
- [ ] Required state a literal omits is a diagnostic rather than a default-initialised surprise
- [ ] Untyped `{| … |}` is unchanged
- [ ] Formatter, LSP completion and hover understand the form
- [ ] Interpreter and compiler agree; the differential corpus covers it
