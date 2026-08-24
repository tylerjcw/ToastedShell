---
id: TOAST-0067
title: "`echo` with several arguments emits one value each interpreted and one joined string compiled"
status: complete
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

```tosh
echo 1 2
```

| Backend | Output |
|---|---|
| interpreted | two pipeline values — `1` and `2`, rendered as a two-row table |
| compiled | one value — the string `1 2` |

The same holds for `echo "a" "b"`. It is the arity that matters, not the type: every corpus
case using a single argument agrees on both backends, which is why this went unnoticed.

## Why it matters

`echo` is the most-used command in the language, and this is not a rendering difference — it
is a difference in **how many values reach the pipeline**:

```tosh
(echo 1 2) | count      # 2 interpreted, 1 compiled
```

So anything downstream of a multi-argument `echo` sees a different shape. `TOAST-0028`
settled that the producer decides a collection's shape; this is a producer disagreeing with
itself across backends.

## Which is right

The interpreted answer looks correct: `echo a b` yielding two values is what makes
`echo $items` and `echo a b` behave the same way, and the compiled backend joining them is
the special case. But this has not been specified, and `§Value Rendering` does not say what
`echo` yields for several arguments — so the item needs the rule stated before either side is
called wrong.

## Acceptance

- [x] `docs/spec/` states what `echo` yields for one argument and for several — a new
      *How many values a command yields* paragraph in the pipeline-shape section, with all
      four of its claims run against the implementation rather than asserted
- [x] Both backends agree, and `echo 1 2 | count` is 2 on each
- [x] The case moves from `KnownDivergences()` into `Corpus()`, with four companions: one
      argument, none, a list argument, and a splat — the splat goes through a different
      emitter path than the fixed-arity one
- [x] A negative control

## Resolution — 2026-08-23

One value per argument, on both backends. The compiled emitter built a `string[]` and
`String.Join`ed it; it now writes one line per argument, and `ToshHost.EchoArgs` — the splat
path — does the same.

The rule stated in the spec is the one that already governed everything else: each
*argument* is a value. That is why `echo $xs` is one value and `echo ...$xs` is three, and
joining made the count depend on how the arguments were spelled rather than on how many
there were.

**Fifteen emitter tests had pinned the joined output** — `echo "hi" "world"` asserting
`hi world`, and a dozen others using multi-argument `echo` as a probe for tuple assignment,
record spread, and function references. They were per-backend unit tests encoding a
divergence, which is exactly what the differential corpus exists to catch and what a
backend-local test cannot. Their expectations now read the same as the interpreter's output.

Closing this also made `TOAST-0073` reachable: with `echo 1 2` yielding two values on both
sides, a two-value `echo` inside a subexpression argument is the shape the one-value rule
exists to refuse, and the compiled backend does not refuse it.

## Notes

Found while adding a record-literal case to the differential corpus for `TOAST-0034` — the
case was written as `echo $r.a $r.b`, and the divergence it reported was `echo`'s rather than
the record's. The corpus case was rewritten to use interpolation, since it is about
inference; this is the finding it turned up on the way.
