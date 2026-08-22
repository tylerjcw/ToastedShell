---
id: TOAST-0045
title: "A compiled function returning `record` cannot return a record literal"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

Three lines:

```tosh
func mk(a: string) -> record { return {| A = $a |} }
var r = mk("x")
writeline $"{$r.A}"
```

| | |
|---|---|
| interpreted | `x` |
| compiled | `ToshDiagnosticException: 'return value' produced a value that could not be converted to 'record'.` |

The value *is* a record. The annotation names `record`. The conversion refuses it.

Independent of the parameter list, of `export`, and of whether the caller annotates the
local — all four combinations fail identically, and `-> string` in the same shape works.

## Where it likely sits

`TOAST-0038` fixed the neighbouring case: a collection annotation whose element type is
declared in Tōast (`list<Token>`) could not convert, because a Tōast class is a
`ToshClassInstance` and no `List<Token>` exists to target. A bare `record` is the same kind
of problem one level up — there is no CLR type for "a record", so
`TryConvertAnnotatedValue` has nothing to convert to.

`TOAST-0034` left "a record literal infers a record type" open for the same underlying
reason: **there is no record `BoundType`.** The two are probably one piece of work.

## Acceptance

- [x] `func f() -> record` accepts a record literal, compiled and interpreted
- [x] A value that is *not* a record is still rejected — a dict literal is still a dict
- [x] `record` as a variable and parameter annotation is covered too — the fix is in the
      literal's *type*, so every annotation position sees it
- [x] A negative control

## Notes

Found while typing the readiness probe: its driver returns
`{| Tokens = …, Nodes = …, Text = …, Value = … |}`, and `record` is the only honest
annotation for it.

## Resolution — 2026-08-21

**The compiled record literal was a dict.**

`EmitRecordLiteral` built a `Dictionary<string, object?>`; the interpreter builds an
`ExpandoObject`. `BuiltInShellTypes` maps the first to `dict` and the second to `record`, so
`{| a = 1 |}` was a `record` interpreted and a `dict` compiled — and `-> record` was right
to refuse it. The annotation was never the problem.

It emits an `ExpandoObject` now and reports that as the expression's type, so `type-of`, the
annotation, and the interpreter all agree.

`ToshHost.SpreadRecord` took a `Dictionary<string, object?>` and now takes the interface —
an `ExpandoObject` is an `IDictionary<string, object?>` but not a `Dictionary`, which is
what `Compiled_record_spread_merges_source_record` caught within a minute of the change.
