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

## Attempted 2026-08-21, reverted to green — what measuring showed

**The engine change is small and it works.** Two files: a `SpreadableSequence` marker, the
counterpart to `PreExpandedSequence`, set by an expression stage; and
`ReplaySingleInputCollectionAsync` reading the mark instead of looking ahead. Every
intended row of the table above came out right, `a | first` and `b | first` agreed for the
first time, and the over-pull went from 2 batches to 1.

**`TS-P2-74`'s constraint turned out not to apply.** It defeated an attempt that made the
*head spread*; this design does not. `ToCommand` reads `context.Input` directly and never
calls the expansion helper, so `[] | to json` is `[]` and `[1] | to json` is `[1]`,
unchanged. Confirmed rather than assumed.

**The cost lands somewhere else: commands that yield a whole collection meaning a
sequence.** 39 tests fail across 8 classes, and 24 of them are one cause —
`from csv` yields its rows as a single `ExpandoObject[]` and relies on a downstream stage
expanding it, so `from csv | select n` becomes "Member 'n' was not found on
`ExpandoObject[]`". Under a rule where the producer decides, such a command is the producer
and has to yield elements.

That is arguably a defect in those commands rather than a cost of this one — collecting a
whole table into one value also defeats streaming — but it is a **semantic judgement per
command**, and there are at least six distinct causes behind the 39 failures.

### Second attempt, 2026-08-21: Option A tried, and the estimate was wrong

`from csv` was fixed — it yields one row per item now instead of the whole table, which is
a strict improvement and took the failures from 39 to 16. One test that read
`Assert.Single(results)` and enumerated inside was updated to read rows directly.

**Then the estimate broke.** The remaining failures are not "five more commands like
`from csv`". They are the discovery that **expansion does not happen in one place**:

```tosh
var v = [1, 2, 3]
echo $v | count               # 1   -- `count` reads the helper, which now passes through
echo $v | where $_ > 1        # 2, 3 -- `where` sees the elements anyway
```

`count` goes through `ReplaySingleInputCollectionAsync`, which the change made honest.
`where` goes through `PeekForTreeAsync` and receives elements regardless. So after the
change two commands disagree about the same input — a worse state than the defect being
fixed, and one that no amount of per-command work resolves until every expansion path is
found and unified.

It also reaches documented behaviour: `echo [1, 2, 3] | cast list<int> | count` is the
`cast` command's own first documented example and answers 1 rather than 3, because
`cast list<int>` produces one list and nothing spreads it any more. Whether that should be
1 or 3 is a real question, and it is a different question from the one this item asked.

**Reverted to green again.** The two things worth keeping from the attempt: `from csv`
yielding rows is right on its own merits and can be done independently, and the true scope
of this item is *unify the expansion mechanisms*, not *mark the producer*.

### The narrower variant, and why it was not taken unilaterally

A command's output could be marked spreadable too, leaving only *user-defined generators*
unmarked. That fixes the defect exactly as filed — the example is a generator — with no
command changes and a green suite. It is also an inconsistency of its own: a builtin
command's collection would spread and a user function's would not, with no principle
separating them beyond where the code lives.

Choosing between "fix the commands" and "narrow the rule" is a decision, not an
implementation detail, so the work stopped here with the tree green and the experiment
recorded.

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

## The missing prerequisite, now built — `TOAST-0032`

`TS-P3-04`'s one-line ask included "a reasonable migration path", and that clause was the
one nobody had built. Both attempts here failed the same way: with no spelling for "spread
this", changing the default was all-or-nothing across the whole standard library, and every
regression had to be absorbed rather than migrated.

`...` now works in pipeline position — `...$xs | count` is 3 — so there *is* something to
write instead. `echo [1,2,3] | count` changing from 3 to 1 stops being a cliff and becomes
a rename, and the same is true of every command that yields a collection meaning a
sequence.

That does not decide anything below. It removes the reason the decision was unaffordable.

## Design — 2026-08-21

Written after two attempts, before a third. No code changed.

### Every path that expands, and which ones decide shape

Four things expand a collection, and only **two** of them make a *shape decision*. That
distinction is what the earlier attempts lacked.

| Path | Callers | Decides shape? |
|---|---:|---|
| `ReplaySingleInputCollectionAsync` | 43 | **yes** — expands a lone collection |
| `PeekForTreeAsync` | 4 | **yes** — same rule, written separately |
| `ExpandIterationItems` / `…Async` | 15 sites | no — unconditional iteration of a *value* |
| Head expansion in `ExecuteExpressionStageCoreAsync` | 1 | partly — ranges, and the variable replay |

The third group is `for`, comprehensions, `join`, `chain`, `cartesian-product` — commands
iterating a collection given as an **argument**. They take a value and walk it. No
cardinality is consulted and nothing about this item touches them.

### The correction that makes this tractable

Attempt two reported that `count` and `where` "disagree about the same input". **They do
not disagree today.** `PeekForTreeAsync` ends with:

```csharp
// Not a tree. Replay as single-collection expansion.
var hasMore = await enumerator.MoveNextAsync();
if (!hasMore) return (null, ExpandIterationItemsAsync(first, cancellationToken));
```

— the same lone-collection rule, with the same lookahead. The two are **two copies of one
rule**, and the disagreement appeared only because the attempt changed one copy.

So this is not "find and reconcile several competing rules". It is: one rule, written
twice, that needs changing once.

### The plan, in stages that each end green

1. **Unify.** Give `PeekForTreeAsync` its tree-detection job only, and have it obtain items
   through `ReplaySingleInputCollectionAsync`. Behaviour identical; the suite must pass
   unchanged. This is the step that makes the rest a one-place change, and it can be
   committed on its own.
2. **Mark the producer.** `SpreadableSequence` set by an expression stage, and the unified
   helper reading the mark instead of counting. Attempt two showed this works and costs two
   files.
3. **Fix the producers that mean a sequence.** `from csv` is done in principle — one row
   per item rather than the whole table, which also restores streaming. The rest are found
   by reading the failures, not by grepping; attempt two's 16 remaining failures came from
   about four causes.
4. **Update the tests that pin the old rule.** Roughly eleven — `EngineTests`'
   `*_expands_single_collection_input` family, `ShortCircuitPullTests`, and the two in
   `CollectionShapeTests` that assert the defect deliberately and are *supposed* to flip.

### The `echo` and `cast` question, and a recommended answer

Both change from 3 to 1:

```tosh
echo [1, 2, 3] | count               # 3 today, 1 after
echo [1,2,3] | cast list<int> | count   # 3 today, 1 after — a documented example
```

**Recommended: let them change, and spell the old meaning `...`.**

- `cast list<int>` producing *one list* and `| count` answering **1** is not a regression,
  it is the honest answer: a cast to a list type makes a list. Getting 3 required the
  pipeline to undo the cast's whole point.
- `echo` emitting one value for one argument is consistent with every other command; what
  made it look different was the consumer guessing.
- `...` now exists, so `echo ...$xs | count` and `...(cast list<int> $xs) | count` say the
  old meaning explicitly. That spelling did not exist during either attempt, which is why
  both had to treat this as breakage rather than migration.

This wants confirming before stage 3, because it is the only part of the change a user
would notice in a session they had already written.

### What is already known about the cost

From attempt two, measured rather than estimated: 39 failures, of which 24 were `from csv`
alone, about 11 were tests pinning the old rule, and the rest were `echo`/`cast` and their
kin. Nothing in that set was mysterious once named.

## Stage 1 done — 2026-08-21

Unifying the two copies was supposed to be a behaviour-preserving refactor. It was not:
the copies had **drifted**, and merging them fixed a live bug.

`TS-P2-113` established that a stream whose producer already enumerated a collection into
it must not be expanded again, and taught `ReplaySingleInputCollectionAsync` to read a
`PreExpandedSequence` marker. `PeekForTreeAsync` carried its own copy of the surrounding
logic and never learned the rule. So for `var r = [[1, 2, 3]]`:

| | before | after |
|---|---|---|
| `$r \| first` | the inner array | unchanged |
| `$r \| where true` | **three integers** | the inner array |
| `$r \| sort` | **three integers** | the inner array |

`var w = ($r | where true)` failed outright with "requires exactly one object", because a
one-item stream had silently become three.

### What the measurement had to survive

The obvious probe is wrong. `$r | where true | count` answers 3 both before and after,
because the *trailing* `count` applies the lone-collection rule to a stream that arrives
unmarked — a second, legitimate expansion at the stage boundary. `for item in (...)` and
rendering are equally ambiguous, each expanding in their own right. What discriminated was
binding the result and asking for `.Length`, where "one array" and "three integers" cannot
look alike.

That is worth writing down because it is how the drift survived: every casual way of
looking at it shows the same number.

### Not the whole item

Stage 1 makes the rule exist once. It does not change the rule — a lone collection with no
marker still expands, which the negative control pins. Stages 2–4 remain, and the
`echo`/`cast` question is still the thing to confirm before stage 3.

## Scope, restated after two attempts — superseded by the design above

This section said the expansion paths "respond differently to the same stream" and that
finding them all was the open question. **Both claims were wrong**, and the design pass
above corrects them: the two shape-deciding paths implement the *same* rule, and they were
found by reading `PeekForTreeAsync` to the end rather than by a search. Kept because the
wrong version is what two attempts were planned against.

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
