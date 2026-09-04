---
id: TOAST-0110
title: "A bare variant name in a match arm silently matches a string instead of the variant"
status: complete
area: toast
priority: 2
opened: 2026-09-03
---

## Problem

A unit variant is written `None()`. Written without the parentheses it is not a variant pattern
at all — a bareword arm is a **string literal** pattern, so this compares the value against the
text `"None"`:

```tosh
match ($o) {
    Some(v) => "some"
    None    => "none"      # never matches an Option
}
```

Measured: falls through to a runtime `non_exhaustive_match`. Nothing reported it at bind time, and
**the exhaustiveness check bailed as well**, because an arm that is not a variant pattern means
"not a union-shaped match" — the rule that keeps ordinary shell code free of the check. So the
author got no error, no warning, and no exhaustiveness checking, then a runtime failure.

Since `TOAST-0083` put `Option` and `Result` in the core prelude, `None()` and `Ok()`/`Err()` are
about to be written constantly, and dropping the parentheses is the obvious slip.

## Why not simply make the bareword work

Because it already means something. `match ("hello") { hello => … }` matches, and that is a real
feature rather than an accident. Making a bareword resolve to a variant when one is in scope would
change what an existing string arm means, and would make the meaning of an arm depend on which
unions happen to be declared nearby — exactly the kind of type-table heuristic `TOAST-0090`
retired the language from.

## The rule

A **warning**, not an error, and narrow: another arm in the same match must destructure a variant
of the union the bareword names. That way the author has already demonstrated what they are
matching, and matching a plain string against the bareword `Ok` is untouched.

It stays a warning because the binder has no types. A value that is sometimes a union and
sometimes the string `"None"` would make the arm meaningful, and the check cannot see that.
This follows `tosh.bind.pattern_shadows_variable`, the language's existing precedent for a legal
construct that is probably not what was meant.

## Acceptance

- [x] A bare variant name beside an arm destructuring the same union is reported, at warning
      severity, naming the union
- [x] The help gives the parenthesised form, and the quoting alternative for when a string really
      was meant
- [x] `None()` is not reported
- [x] A plain string match against a bareword is not reported
- [x] A bareword naming a *different* union's variant is not reported
- [x] An ordinary binding arm is not mistaken for a variant name

## Blast radius

Zero. No bare capitalised arm exists in the 33 `.tosh` files of `examples/`, `scripts/`, `tests/`
and `editor/`, nor in the 77 files of the author's own `~/.config/tosh`.

## Notes

The exhaustiveness interaction is the part worth remembering: a construct that silently disables a
check is worse than one that merely misbehaves, because the author loses the diagnostic that would
have pointed at the mistake. The warning restores the signal without weakening the "not
union-shaped" rule that keeps shell code quiet.
