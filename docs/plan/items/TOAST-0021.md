---
id: TOAST-0021
title: "DisplayEngine walks values itself, so a table cell shows an enum's implementation"
status: open
area: tosh
priority: 2
opened: 2026-08-17
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

- [ ] An enum in a table cell shows its member name
- [ ] A container in a cell renders on one line, with no CLR type name and no embedded
      newlines
- [ ] Display profiles still win where one applies — that is what they are for, and a
      `DateTime` cell showing relative time must keep doing so
- [ ] Table structure is unchanged: this is about what goes *in* a cell, not about columns,
      widths or nesting into sub-tables
- [ ] The rule is that `DisplayEngine` asks `ToastRenderer` for any value it has no display
      opinion about, rather than duplicating a walk that can disagree
- [ ] A negative control: reverting fails the new tests

## Notes

This is a **TōSh** item, not a Tōast one, which is why it is filed separately rather than
folded into `TOAST-0014`. What goes in a table cell is display; what `$"{x}"` produces is
language. The two should agree wherever display has no opinion, and today they cannot,
because each has its own walk.

Found by checking whether stage 3 had done what it was predicted to do. It had not, and
the prediction had been stated to the user as fact — the table cells were the stated reason
stage 3 was worth doing.

Sequence after `TOAST-0014` stage 4. The renderer is the thing `DisplayEngine` would call,
and it should be settled first.
