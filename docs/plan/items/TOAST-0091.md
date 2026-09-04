---
id: TOAST-0091
title: "A value whose state is not entirely constructor arguments has no literal form"
status: complete
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

- [x] A typed record literal constructs a declared record, struct or class from named fields
- [x] Construct-versus-populate is decided, stated, and consistent between the two tiers
- [x] A field the type does not have, or cannot accept, is a diagnostic naming it
- [x] Required state a literal omits is a diagnostic rather than a default-initialised surprise
- [x] Untyped `{| … |}` is unchanged
- [x] Formatter, LSP completion and hover understand the form
- [~] Interpreter and compiler agree — **compiled backend diverges, recorded not fixed**

## Decisions and progress (2026-08-28)

**Spelled `new T {| … |}`, not the bare `T {| … |}` this item proposed.** The bare form is
grammatically identical to a command invocation passing a record — `f {| a = 7 |}` already works
— so telling them apart needs a type table in the parser, which is the heuristic class of
problem `TS-P2-16` recorded and `TOAST-0090` was built to retire. `new` already marks
construction. Both `new T {| … |}` and `new T(args) {| … |}` are accepted.

**The constructor runs, then the remaining named fields are assigned.** Not populate-only: a
struct is immutable unless declared `fluid`, so "allocate and assign" is not available for the
default struct at all and the two tiers could not have agreed under it. Recorded in
`DECISIONS.md`.

Assignment reuses the accessor behind `$value.Member = x`, which is what earns the diagnostics:

```
'LitBox' cannot take 'Nope': Member 'Nope' was not found on type 'LitBox'.
'LitVec' cannot take 'X': Cannot modify property 'X' on immutable struct 'LitVec'.
                          Declare the struct as 'fluid' to allow mutation.
```

The `fluid` advice is the one ordinary assignment already gives, rather than a second
explanation of the same rule that could drift from it.

`tests/Tosh.Tests/TypedRecordLiteralTests.cs` — 13 tests. The control that matters most is
`A_record_passed_to_a_function_is_still_a_call`: if that ever becomes a typed literal, the
ambiguity the spelling was chosen to avoid has arrived anyway.

**One parse route needed teaching separately.** `new T {| … |}` was dispatched as a *command*
invocation of `new`, because the expression lookahead only recognised `new T(`. The record
arrived as a constructor argument and reported "No constructor matched class 'Box' with 1
argument(s)" — the consequence rather than the cause.

### Compiled backend drops the initialiser silently

`echo ((new DiffLitBox {| X = 9 |}).X)` answers **9 interpreted and 0 compiled**: the emitter
does not read `NewObjectArgumentSyntax.Initializer`, so the object comes back with the
constructor's state and none of the literal's. Recorded in `KnownDivergences` rather than
implemented, since compiled ToastScript is an experiment and this would be new surface on it.

**Taken 2026-08-29: the emitter now refuses the form.** A dropped initialiser is a *wrong value
with no error*, which is worse than an unsupported one, so `BoundNewObject` carries
`HasObjectInitializer` and `EmitNewObject` throws on it. The fields are still not lowered — this
is a guard, not an implementation, and the interpreter remains authoritative. The divergence
stays recorded; it is now a refusal rather than a wrong answer.

## Progress (2026-09-03) — the editor surface

Measured first, as with `TOAST-0090`: the cursor inside `new Box {| … |}` returned the same 373
generic entries as anywhere else, with the type's own members in none of them. A field name is a
bare identifier, so nothing in the completion chain recognised the position.

A class's properties and a record's fields are offered now, inserted as `Name = ` so the common
next keystroke is already there, and hover on a field names the type it belongs to. Both use the
declaration index that `TOAST-0090` taught about declared types, so this was mostly wiring.

**The literal is found by scanning forward from the start of the document, not backward from the
cursor.** Backward is cheaper and wrong: a `"{|"` inside a string, or one in a `#` comment, opens
a literal that is not there. Both have tests.

Three things it deliberately does not do. An untyped `{| … |}` is left alone — it has no type to
complete against and must not acquire one. A position after `=` is a value, not a field name, so
the ordinary sources answer there. And a struct is not answered for at all: struct definitions are
not in the declaration index, so there is nothing to offer, and saying nothing beats guessing.
That last one is a real gap in the index rather than a decision, and worth its own item if struct
literals become common.

**The formatter box needed nothing**, for the reason recorded on `TOAST-0090`: it leaves every
expression interior alone, verified by control.

Closing the hover box required fixing `TOAST-0109` first — any `|` within three characters of the
cursor was read as a pipeline, so hovering a field in a *short* typed literal returned a pipeline
card. It worked for `Box` and failed for `Point`, purely because of the type name's length.
