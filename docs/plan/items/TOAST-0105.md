---
id: TOAST-0105
title: "`is` silently returns false for a declared type when the type name is qualified"
status: proposed
area: toast
priority: 2
opened: 2026-08-31
---

## Problem

`is` answers correctly for a ToastScript-declared type only when the name is unqualified and the
test is inside the declaring module. Qualified, it returns `false` — not an error, not a
diagnostic, just the wrong answer:

```tosh
export partial module IR {
    export record Thing(Name: string)
    export func Inside(t) { return ($t is Thing) }            # true
    export func InsideQualified(t) { return ($t is IR.Thing) } # false
}

var t = (new IR.Thing(Name = "x"))
echo ($t is IR.Thing)     # false — from outside, there is no other spelling
```

A declared **class** behaves the same way. CLR types are unaffected: `"x" is string`,
`42 is int` and `$row is System.Dynamic.ExpandoObject` all answer correctly whether qualified or
not.

The practical consequence is that **`is` cannot be used on a declared type from outside its
module at all**, because the qualified name is the only name available there.

## How it surfaced

`ToastLib.Plot.AsSeries` dispatches on what it was handed. Its first arm was:

```tosh
if ($head is ToastLib.Plot.Series) { … }
```

which was always false, so a list of `Series` fell through to the numbers arm and died casting a
record to a double:

```
✖  Unable to cast object of type 'Tosh.Language.ToshRecordInstance' to type 'System.IConvertible'.
```

Caught by the first run of a new test suite, having gone unnoticed because every other call path
passed numbers or records.

## The workaround is worse than it looks

`.GetType().Name` does not substitute: every declared record's CLR type is
`Tosh.Language.ToshRecordInstance`, so `GetType` cannot tell one record from another.
`(type-of $x).Name` does report the declared name — `"Series"`, `"Point2D<Double>"` — so that is
what the library now compares, a string comparison standing in for a type test.

## Related

The fourth qualified-vs-unqualified inconsistency found in this area:

- `TOAST-0102` — an unqualified capitalised callee across files changes how a call *parses*
- `TOAST-0104` — an unqualified base type in a module declaration resolves to nothing, silently
- Plot's load-order rule — an unqualified call binds at load, a qualified one late
- this — a *qualified* declared type silently fails `is`, while the unqualified one works

Note the direction is inverted here: everywhere else qualifying is the fix, and here it is the
cause. Whatever resolves type names for `is` is not the resolver used for annotations, which
handle the qualified form correctly.

## Acceptance

- [ ] `$t is IR.Thing` is true for an `IR.Thing`, from inside and outside the module
- [ ] The same holds for a declared class and for a generic declared type
- [ ] A qualified name that resolves to no type is a diagnostic, not a silent `false`
- [ ] `is` and type annotations agree on every name either accepts
- [ ] Corpus covers record, class, qualified, unqualified, inside and outside the module
