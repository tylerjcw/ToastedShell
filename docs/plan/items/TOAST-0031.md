---
id: TOAST-0031
title: "A runtime diagnostic has no Tōast name, so catching one is written against a CLR type"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Dividing by zero, indexing out of range, or reaching a member of `null` raises a
*diagnostic*: the language reporting that an operation had no answer. It is catchable and
carries a message, and `§Errors and catch` says it is deliberately **not** an `Error` —
one is the language reporting a failure, the other is a program raising something on
purpose.

What it has no name for is itself. A handler can only ask:

```tosh
try { (1 / 0) } catch (e) {
    ($e is Error)          # false — correct, it is not a declared error
    ($e is Exception)      # true  — but `Exception` is a CLR type
}
```

`TOAST-0029` made `is Exception` reachable, which is enough to tell a diagnostic from a
declared error from a plain thrown value. It is not enough to be **portable**: a target
without the CLR has no `Exception` to name, so every script that catches a runtime failure
today is written against a host type.

## Why this is the last thread of Phase A's exit

Phase A asked for core behaviour "specified in Tōast terms". Exception semantics is
specified except here: the one category of value the language raises by itself can only be
named in .NET's vocabulary.

## The decision this needs

What is a diagnostic called, and what does it carry?

1. **A built-in `Diagnostic` type**, beside `Error`, so `$e is Diagnostic` is the portable
   spelling. Symmetric with `Error` and easy to explain; adds a second root to the error
   hierarchy, and the two need a stated relationship — is a `Diagnostic` an `Error`? The
   specification currently says no, deliberately.
2. **Make a diagnostic an `Error` after all**, and distinguish the two by a property such
   as `Code` or a `Raised`/`Reported` flag. One root, simpler to catch broadly; loses the
   distinction the specification just drew, and `catch (e) { $e is Error }` stops meaning
   "a program raised this".
3. **Name the CLR type in Tōast terms** — an alias, the way `Error` already aliases
   `ToshError`, so `is Diagnostic` resolves to whatever the host uses. Cheapest, and it
   makes the *name* portable without making the *model* portable: a `no_clr` backend must
   still have something for the alias to point at.

Option 1 or 3 in practice. The question underneath both is whether a diagnostic is a value
the language *defines* or a host detail it *exposes*.

## Acceptance

- [ ] A caught runtime diagnostic is identifiable without naming a CLR type
- [ ] The relationship between a diagnostic and an `Error` is stated, and the tests pin it
- [ ] `$e is Error` is still false for a diagnostic, or the specification says why it changed
- [ ] `§Errors and catch` carries the decision
- [ ] The differential corpus covers it, so both backends must agree
- [ ] A negative control

## Notes

Split from `TOAST-0029`, which fixed the half that was a defect — `is` could not resolve a
bare CLR name, so `$e is Exception` was false along with `[1,2] is IEnumerable`. What
remained is not a defect but a gap: there is no name to give. That is a design decision,
and it was not one `TOAST-0029` was scoped to make.

Related: `TOAST-0030` records five semantics the compiled backend does not implement. This
one is a semantics the *language* does not yet have.
