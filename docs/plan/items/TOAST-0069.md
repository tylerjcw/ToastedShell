---
id: TOAST-0069
title: "A rune call site forces whole-script source replay, so a program using a macro is not compiled at all"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

Runes are ToastScript's macros: arguments captured lazily as syntax thunks, with
`quote { … }` giving first-class AST values. They are the language's metaprogramming
surface, and **a program that calls one is not compiled**.

Measured 2026-08-22 under `--profile runtime`, which refuses source replay:

| Program | Result |
|---|---|
| rune *defined*, never mentioned | compiles |
| rune *defined and called* | `tier 3 feature: whole-script replay (rune expansion)` |

"Whole-script replay" is the most severe fallback the emitter has. A refinement type replays
*its own declaration*; a rune call makes the emitted assembly embed the entire source text
and hand it to the interpreter at start-up. The artifact is a carrier, not a compilation.

## Why expansion is the answer

A rune reads like call-by-name — the body decides when, whether, and how often to evaluate
its arguments — but for the common case that is exactly what a macro is, and macros belong in
the binder. Boo's AST macros run at compile time for the same reason.

Expanding a call whose target is a rune declared in the unit:

- binds each parameter to the **syntax** of its argument;
- splices the body at the call site, duplicating an argument's syntax wherever the body
  mentions it more than once — which is `do-twice`'s existing semantics rather than an
  approximation of them;
- leaves nothing rune-shaped at run time, so nothing needs replaying.

**Hygiene falls out of the bound tree.** `sealed` — the default — means declarations inside
the expansion get fresh `BoundSymbol`s; `leaky` reuses the caller's. The IR already carries
symbols, so renaming needs no new machinery.

It also removes the textual call-site scan, which is its own defect (`TOAST-0070`): whether a
rune is called becomes a bound fact rather than a regex over source text.

## The semantics expansion must preserve — read 2026-08-22

Read out of `ExpandRuneAsync` and `RuneThunk` rather than assumed, because two of these are
not what "splice the body at the call site" implies on its own.

**1. Hygiene is two-sided.** A `RuneThunk` carries `CallerScopes`, captured at the call site,
and the *body* runs under `PushCapturedScopes(rune.CapturedScopes)` — the scopes captured
where the rune was **declared** — plus a fresh scope. So an argument is evaluated where it
was written, and the body sees where it was defined. Splicing the argument's syntax at each
use site gets the first direction for free; the second needs the body's own declarations
renamed to fresh symbols, and its free references resolved against the definition site rather
than the call site.

**2. `leaky` is not "skip renaming".** It executes the body in the caller's binding store
with **no new scope**, so declarations inside the rune stay visible after it returns — and
the parameter bindings are restored afterwards while the body's declarations are not. An
expansion has to reproduce that asymmetry: parameters are temporary, the body's variables
are not.

**3. Pipeline input is a parameter.** `locals["_input"] = input` binds the stage's input for
the body to read as `$_`. A rune used as a pipeline stage therefore has an implicit argument,
and expansion has to thread the incoming stage rather than assume statement position.

**4. Arity is checked at expansion.** Fewer arguments than parameters raises `RUNE001` naming
both counts. At compile time that becomes a diagnostic rather than a runtime failure, which
is an improvement worth keeping rather than dropping.

### What is already in place

`BoundRuneDefinition` carries a **lowered** `BoundBlock Body`, not syntax. Unlike refinement
types — whose clause model falls through `LowerExpression`'s catch-all and reaches the
emitter as a `BoundDynamicExpression` — the rune body is already bound IR. The groundwork
this needs mostly exists; what does not exist is the substitution and the renaming.

## What expansion cannot cover

Both should be diagnosed rather than silently replayed:

- **`quote { … }` values manipulated at run time.** That is genuine AST data and needs a
  representation in the artifact, or a stated exclusion.
- **A rune reached indirectly** — through a variable or callable — where the target is not
  statically known.

Recursive runes need an expansion depth limit.

## Acceptance

- [ ] A rune call whose target is declared in the unit is expanded during lowering
- [ ] A program defining and calling a rune compiles under `--profile runtime`
- [ ] `sealed` hygiene is preserved by renaming, and `leaky` still writes to the caller's
      scope — asserted per modifier rather than for one example
- [ ] The interpreted and compiled backends agree, in the differential corpus, including a
      rune whose body evaluates its argument more than once
- [ ] Recursive expansion terminates with a diagnostic rather than a stack overflow
- [ ] `quote` and indirect invocation are each implemented or recorded as a deliberate
      exclusion
- [ ] A negative control

## Notes

Raised by the user asking whether runes compile, "like ASTs in Boo". Measuring first turned
a design question into a severity ranking: this is a *larger* payoff than the refinement work
`TOAST-0035` is sequenced around, for probably less binder work — no new IR node kinds are
needed for the common case, only an expansion pass at lowering — because a single rune call
disables compilation of an entire program rather than one declaration.
