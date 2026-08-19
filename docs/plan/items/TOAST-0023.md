---
id: TOAST-0023
title: "An interpolation hole spreads a variable holding a collection"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-17
---

## Problem

A hole containing a collection *literal* renders the collection; a hole containing a
*variable* that holds one spreads it:

```tosh
echo $"{[\"a b\", \"c\"]}"     # ["a b", "c"]

var xs = [\"a b\", \"c\"]
echo $"{$xs}"                  # a b c
```

The hole is evaluated as a pipeline. A literal yields one value — the array — while a
variable reference yields its *elements*, and the multi-result branch joins them with
single spaces (`ToshEngine.Arguments.cs`, the `InterpolatedStringExpressionPart` case).

So `$"{$xs}"` cannot render a collection at all, and the spacing is lossy: `["a b", "c"]`
and `["a", "b", "c"]` both interpolate to `a b c`.

## Acceptance

- [x] `$"{$xs}"` renders the collection the variable holds
- [x] A hole containing a genuine multi-value pipeline joins its results with a **single
      space**, decided rather than inherited
- [x] The two are distinguishable by a rule a reader can apply: a `|` in the source
- [x] Interpreted and compiled agree; the case moved from `KnownDivergences()` into `Corpus()`
- [x] A negative control: 3 of 11 fail with the change reverted

## Decision and resolution — 2026-08-17

**A hole is one value unless it contains a pipeline.** An expression renders; a pipeline
joins its results with a single space. The line is a `|` the reader can see in the source,
rather than a runtime property of whatever the value turned out to be.

Three options were weighed and rejected. *Always join* would have made `$"{[1, 2, 3]}"` give
`1 2 3` too — consistent, but it contradicts the container syntax `TOAST-0014` had just
specified and leaves no way to interpolate a collection at all. *Strictly one value*, with a
multi-result hole refused, is the most consistent and breaks every `$"{ls | get Name}"`.
*Leave and document* costs nothing now and leaves the two backends disagreeing forever.

The mechanism was already there: `TryEvaluateRawExpressionPipelineAsync` is what makes
`$"{($xs)}"` render, and the hole now takes that path first. So the parenthesised spelling
stopped being a workaround and became the same thing written twice.

### Two things worth keeping

**An earlier attempt failed silently.** `TryGetHolePipeline` matched
`ScriptStatementSyntax { Statements: [PipelineStatementSyntax] }`, and a hole's program is a
bare `PipelineStatementSyntax` — so the pattern never matched, the branch never ran, and the
behaviour was unchanged with no error anywhere. A probe found it in one run; reading would
not have.

**The most visible consequence is a rest argument.** `func f(items...)` interpolating
`$"{$items}"` now gives `["a", "b"]` rather than `a b`. That is the decision applied
consistently, and it is the shape most likely to appear in a real script, so it has its own
test.

## Notes

Found by the differential corpus in `TOAST-0014` stage 4: the compiled backend renders the
collection, the interpreter spreads it, and the disagreement is what surfaced the
interpreter's behaviour at all.

**Not a rendering bug.** `ToastRenderer` is never asked — the hole never presents it with a
collection. The question is what a hole *is*: an expression that produces one value, or a
pipeline whose results are joined. Today it is the second, and the first is what
`$"{$xs}"` needs.

Worth deciding alongside `TS-P3-04` (explicit stream/collection shape), which is the same
question one level up — the recorded asymmetry there is that `[1,2,3] | count` is 3 while a
piped dictionary counts as 1.
