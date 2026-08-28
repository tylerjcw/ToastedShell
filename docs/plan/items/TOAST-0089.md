---
id: TOAST-0089
title: "A declared record's collection fields vanish from a table, but an anonymous record's do not"
status: complete
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

```tosh
record Q(A: array<int>, B: int)
[new Q([1, 2], 1), new Q([3, 4], 2)]     # column A is simply gone

[{| A = [1, 2], B = 1 |}, {| A = [3, 4], B = 2 |}]   # both columns render
```

The two values are structurally identical and their fields have the same runtime type
(`Int32[]`), and they render differently.

`record Trade(Give: array<Exchange>, Receive: array<Exchange>)` has *only* collection fields, so
a list of trades lost every column and fell back to a bare `ShellTypeName` column — which looks
like the data was lost rather than the columns.

**One row was unaffected.** The single-row path asks for structured values explicitly
(`allowStructuredValues: true`); the multi-row path takes the default, `false`. So the same
value rendered correctly alone and wrongly in a list of two, which is what made this look like a
serialisation problem.

## Cause

An anonymous record is an `ExpandoObject`, which is an `IDictionary<string, object?>` and so
matched that display profile — and `BuildRecordColumns` filters nothing. A declared record is a
`ToshRecordInstance`, which matched **no profile at all**, and fell to the display engine's
generic record-like builder, whose column filter is
`allowStructuredValues || IsRenderableTableCellType(field.ValueType)`. An array is neither an
intrinsic cell type nor a profiled one, so the column was dropped.

Declared structs, classes and union variants had it too.

## Resolution

The four declared instance types get the same profile the dictionary shapes already had.

**Not `IShellRecordObject`.** The first attempt targeted the interface, which is broader than it
looks: `Quantity` implements it so introspection can reach its `base-value`, while display must
keep rendering it as the scalar `483.06 MW`. Targeting the interface turned quantities into
tables, which `ObjectFormatterTests` caught.

## Acceptance

- [x] A declared record's collection-valued fields keep their columns in a multi-row table
- [x] Structs, classes and union variants behave the same
- [x] A declared record renders identically to a structurally identical anonymous one
- [x] Quantities still render as scalars
