---
id: TOAST-0021
title: "DisplayEngine walks values itself, so a table cell shows an enum's implementation"
status: complete
area: tosh
priority: 2
opened: 2026-08-17
closed: 2026-08-17
---

## Problem

`DisplayEngine` does not ask the formatter how a value reads — it has its own structural
walk, and that walk claims values the renderer would have handled:

```tosh
enum Color { Red, Green }
[{| C = Color.Red |}]
```

renders a cell containing a nested table of `Definition`, `EnumTypeName`,
`ShellTypeDescriptor`, `UnderlyingType`, `MemberCount` and `UnderlyingValue` — the enum's
implementation, where the reader wrote `Color.Red`.

A list in a cell is the same shape: `String[] [` followed by newlines *inside* the cell.

`TOAST-0014` fixed this for `$"{x}"` and for `ObjectFormatter.Format`, and it was expected
to fix the display path with them. **It did not**, and that expectation is worth recording
as wrong: `DisplayEngine` calls `TryRenderProfile` itself at each surface
(`TableCell`, `RecordValue`, `Nested`) and then walks record fields on its own, so
`ToastRenderer` is never reached for a value it would have named.

## Acceptance

- [x] An enum in a table cell shows its member name
- [x] A container in a cell renders with no CLR type name and no embedded newlines —
      **already fixed by `TOAST-0014` stage 3**, which is why the acceptance's "on one line"
      wording is not met and should not be: a container expands into display's own nested
      table, which is a feature rather than the `String[] [⏎` leak it replaced
- [x] Display profiles still win where one applies
- [x] Table structure is unchanged — a container cell still expands, pinned as a control
- [x] `DisplayEngine` asks `ToastRenderer` whether a value is a scalar rather than deciding
      for itself
- [x] A negative control: 1 of 4 fails with `DisplayEngine` reverted; the other three are
      controls written to pass either way

## Resolution — 2026-08-17

`ToastRenderer.RendersAsScalar` is the shared answer, and display consults it in the two
places that decided for themselves: the table-cell path and
`CanRenderNestedStructuredValue`.

**The predicate runs the scalar writer rather than restating its cases.** That costs a small
builder per call and buys the guarantee the two can never disagree — a restated list is a
list that drifts, which is how this defect existed in the first place.

`DisplayEngine`'s test for "can I expand this?" was *does it have readable properties*, and
an enum member has them. The rule is now about values with a **name**: a container has parts
and still expands into a nested table; an enum member does not and goes in the cell as
`Red`.

### A harness that passed with the fix reverted

The first version of the test called `Display.Render(results)` with no options and passed
either way. The structural expansion only runs when `MaxWidth` is set, so a widthless
harness silently exercises a different path. The width is now passed explicitly and
commented as required rather than incidental — without the negative control this file would
have been four tests that assert nothing.

### Noticed, not fixed

A `DateTime` cell still shows `08:00:00` for a value written `12:00:00`, because the display
profile calls `ToLocalTime()` and .NET treats an `Unspecified` kind as UTC. `TOAST-0017`
fixed that for *rendering*; the display profile has the same wart and is out of scope here.

## Notes

## Notes

This is a **TōSh** item, not a Tōast one, which is why it is filed separately rather than
folded into `TOAST-0014`. What goes in a table cell is display; what `$"{x}"` produces is
language. The two should agree wherever display has no opinion, and today they cannot,
because each has its own walk.

Found by checking whether stage 3 had done what it was predicted to do. It had not, and
the prediction had been stated to the user as fact — the table cells were the stated reason
stage 3 was worth doing.

Done after `TOAST-0014` stage 4, as planned — the renderer had to be settled before display
could ask it anything.
