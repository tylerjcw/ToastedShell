---
id: TOAST-0109
title: "Any `|` or `>` within three characters of the cursor produces a pipeline hover"
status: complete
area: toast
priority: 3
opened: 2026-09-03
---

## Problem

`FindPipelineOrRedirectOffset` walks outward from the cursor and returns the first `|` or `>`
within three characters, and the hover then renders a *Pipeline Data Stream* card describing the
columns a pipeline would carry. The character it found is very often not a pipeline:

| written | the `|` or `>` in it |
|---|---|
| `{\| a = 1 \|}` | a record literal's delimiters |
| `[x <\| for i in $y]` | the comprehension separator |
| `$a \|\| $b` | logical or |
| `(Lit(v) \| Add(l, r))` | an or-pattern's alternatives |
| `A() => 1` | a match arm's arrow |
| `$n >= 2` | a comparison |

Found while closing `TOAST-0091`: hovering the field name in `new Point {| X = 1 |}` returned a
pipeline card, because the `|` of `{|` is two characters away. The same hover on
`new Box {| Name = "x" |}` worked, because there the delimiter is four away — so the bug appeared
and disappeared with the length of the type's name, which is the sort of thing that gets reported
as "hover is flaky".

## Fix

The candidate must actually be an operator. A `|` adjacent to `{`, `[`, `}`, `]`, another `|` or a
`<` is a delimiter or a different operator; a `>` preceded by `=` or `-`, or followed by `=`, is an
arrow or a comparison.

The three-character proximity search is left as it is. It is crude — it fires when the cursor is
not on the operator at all — but narrowing it is a change to which hovers appear, and this item is
about the ones that are simply wrong.

## Acceptance

- [x] A record literal, comprehension separator, `||`, or-pattern, `=>` and `>=` produce no
      pipeline hover
- [x] A real pipeline still produces one, and a real redirect still produces its hover
- [x] The `TOAST-0091` hover it was blocking works at every position in the field name

## Notes

A test case written for `{ |x| $x }` was **removed rather than made to pass**: that is not
ToastScript's anonymous-function syntax, which is `func (params) { … }`. Adding an exclusion rule
for a form the language does not have would have been a rule nothing could ever exercise.
