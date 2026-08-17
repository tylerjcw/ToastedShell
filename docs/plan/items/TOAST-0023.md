---
id: TOAST-0023
title: "An interpolation hole spreads a variable holding a collection"
status: open
area: toast
priority: 2
opened: 2026-08-17
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

- [ ] `$"{$xs}"` renders the collection the variable holds
- [ ] A hole containing a genuine multi-value *pipeline* — `$"{ls | get Name}"` — keeps
      whatever behaviour is decided for it, and that decision is written down rather than
      inherited from how the branch happens to work
- [ ] The two are distinguishable in the spec: what a hole does with several results is a
      rule, not an accident of pipeline evaluation
- [ ] Interpreted and compiled agree; the case moves from
      `DifferentialExecutionTests.KnownDivergences()` into `Corpus()`
- [ ] A negative control: reverting fails the new tests

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
