---
id: TOAST-0028
title: "Collection shape is decided by counting what arrives, so producing more data changes what the earlier data meant"
status: open
area: toast
priority: 2
opened: 2026-08-20
supersedes: TS-P3-04
---

## Problem

A sequence reaching a stage as the **only** item is expanded into its elements; several
are left as items. So a collection's meaning depends on how many of them there are:

```tosh
func a() { yield [1, 2, 3] }
func b() { yield [1, 2, 3]; yield [4] }

a | count      # 3   -- the elements
b | count      # 2   -- the arrays
a | first      # 1
b | first      # [1, 2, 3]
```

**Adding a second batch changed what the first batch meant.** A function that sometimes
yields one collection and sometimes several hands its consumers a different shape
depending on the data, and no annotation or spelling at the call site can say which was
intended.

## The second cost: an over-pull

Deciding "is this the only item?" requires reading one item further than the consumer
asked for. Measured:

```tosh
var produced = 0
func gen() {
    $produced = $produced + 1
    yield [1, 2]
    $produced = $produced + 1
    yield [3, 4]
}
var r = (gen | first 1)     # [1, 2] — correct
echo $produced              # 2 — the generator ran a step nobody asked for
```

For an expensive or side-effecting producer that is a real extra unit of work, and if the
surplus step raises, the error is reported for work nobody requested. `TS-P1-08` already
removed half of this lookahead — the case where the first item is *not* expandable — and
the remaining half is the one that cannot be removed without changing the rule.

## Decision — 2026-08-20

**The producer decides the shape, not the consumer.** A collection literal, or a variable
holding one, is a *sequence* and spreads. A command or generator yielding a collection
yields it *as a value*. Shape is then known where the value is produced, so no lookahead
is needed and no item is pulled speculatively.

What changes and what does not:

| | today | after |
|---|---:|---:|
| `[1,2,3] \| count` | 3 | 3 |
| `$x \| count`, `var x = [1,2,3]` | 3 | 3 |
| `1..3 \| count` | 3 | 3 |
| generator yielding one array | 3 | **1** |
| generator yielding two arrays | 2 | 2 |
| `[[1,2],[3]] \| count` | 2 | 2 |

The generator row is the whole behavioural change, and it is the row that stops depending
on how much data arrives.

## The constraint that sank the first attempt

`TS-P2-74` records that spreading every list-valued head **was tried and is wrong**:

> `[] | to json` must serialize the empty array rather than send nothing downstream, and
> eight tests said so, across `to json`, format round-trips and comprehensions.

So "the head spreads" cannot be unconditional. A command that consumes a collection *as a
value* has to be able to say so — and the metadata for it already exists, unused for this
purpose: `PipelineInputAttribute` carries `AcceptsScalar`, `AcceptsRecord` and
`AcceptsList`. The design this item needs is how a stage's declared input shape and the
producer's declared output shape meet, which is exactly what `TS-P3-04` meant by
*explicit* stream/collection shape.

`PreExpandedSequence` (`TS-P2-113`) is the existing half of the mechanism: a stream already
carrying elements, marked so nothing expands it again. The inverse marker — a stream
carrying values that must not be spread — is what is missing.

## Acceptance

- [ ] A generator yielding one collection and one yielding several agree about what a
      collection is: `a | first` and `b | first` both yield the collection
- [ ] No item is read further than the consumer asked for — the `$produced` probe above
      answers 1
- [ ] `[1,2,3] | count`, `$x | count`, `1..3 | count` and `[[1,2],[3]] | count` are
      unchanged, pinned as controls
- [ ] `[] | to json` still serialises the empty array, and the other seven cases
      `TS-P2-74` names are enumerated and pinned before the change
- [ ] Dictionaries, records and strings remain single values
- [ ] `docs/spec/` §Collection Shape is rewritten with the change, and its "known defect"
      notebox removed
- [ ] A negative control

## Notes

Supersedes `TS-P3-04`, which asked for this in one line — "remove cardinality lookahead
while preserving object-valued pipelines and a reasonable migration path" — and named the
same motivating asymmetry. That item was `research` with no design; this one has the
decision, the measurement and the constraint that defeated the previous attempt.

Filed from `TOAST-0018`'s collection-shape box rather than done inside it, because the
change is to how every pipeline stage receives its input and wants its own corpus and
negative control. The specification records the current rule and names this item, so the
two cannot drift apart silently.
