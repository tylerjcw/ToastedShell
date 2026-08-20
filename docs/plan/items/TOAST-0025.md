---
id: TOAST-0025
title: "`[1,2,3] | sort | first` returns the unsorted array, because the fusion does not expand a collection the way `sort` does"
status: complete
area: toast
priority: 1
opened: 2026-08-19
closed: 2026-08-19
---

## Problem

`sort | first` is the idiom for "the smallest one". With an inline collection literal as
the source it silently returns **the whole input, unsorted**:

```tosh
[3,1,2] | sort              # 1, 2, 3          — correct
[3,1,2] | sort | first      # 3, 1, 2          — the original array, unsorted
[3,1,2] | sort | first 2    # 3, 1, 2          — the same
([3,1,2] | sort | first).GetType().Name
                            # Int32[]          — one item, and it is the array
```

Nothing reports an error. A caller asking for a minimum is handed the unsorted
collection, and only the type reveals what happened.

## Cause

Lowering recognises `... | sort | first N` and attaches a `SortFirstFusion`
(`PipelineFusionTests` pins the lowering). `EvaluatePipelineAsync` then runs
`stageCount - StagesConsumed` stages and replaces the trailing two with
`ExecuteSortFirstFusionAsync` (`ToshEngine.Pipelines.cs:558` and `:596`).

The fusion consumes whatever the upstream stage yields. For a collection-valued
expression stage that is **one item — the collection itself** — because expanding a
collection into its elements is work the *command* stages do, and the fusion replaced
them. So the heap receives a single element, sorts a one-element sequence, and `first`
returns it.

`sort` alone is correct because `SortCommand` expands its input.

## What is and is not affected

Measured 2026-08-19:

| Source | `sort \| first` yields | |
|---|---|---|
| `[3,1,2]` — collection literal | `Int32[]` | **wrong** |
| `["b","a"]` — collection literal | `String[]` | **wrong** |
| `[3,1,2] \| sort -r \| first` | `Int32[]` | **wrong** |
| `$v` where `var v = [3,1,2]` | `Int32` | correct |
| `ls` — command | `FileSystemEntry` | correct |
| `1..5` — range | `Int32` | correct |
| `[3,1,2] \| each { $_ }` | `Int32` | correct |

A variable is correct because the replay path (`TS-P2-113`,
`ExecuteExpressionStageAsync` → `PreExpandedSequence`) already expands it. The literal
is the one source that reaches the fusion unexpanded.

This is why the existing corpus misses it: `PipelineFusionTests` uses `1..100`,
`1..10`, and `["banana", "apple", "cherry"] | each { $_ }` — a range, a range, and a
literal *deliberately* pushed through `each`.

## Resolution — 2026-08-19

**The fusion expands its input**, chosen over refusing to fuse. Fusion stays transparent,
which is the rule worth having: a lowering that changes an answer is not an optimisation.

The expansion is `ShellIterationUtilities.ReplaySingleInputCollectionAsync` — the helper
`FirstCommand` itself calls — rather than a bare `foreach`. That matters, because the
naive repair is wrong twice over:

- **A string is a collection to the CLR and an atom to the shell.** Expanding every item
  turns `"hello" | sort | first` into `"e"`.
- **A replayed variable is already expanded.** `$v | sort | first` with
  `var v = [[3,4],[1,2]]` must answer `Int32[]`; expanding again turns a stream of arrays
  into a stream of numbers. `PreExpandedSequence` (`TS-P2-113`) is exactly the marker that
  says so, and the helper honours it.

Only a *lone* collection expands, and only one level, so `[[3,4],[1,2]] | sort | first` is
the array `[3,4]` and not `3`. Both halves are pinned.

`PipelineFusionTests` gains the source shape it never had. Its
`Fused_path_handles_strings` carried a comment explaining that the `each` was required
because "without the each, the array is a single pipeline value" — the defect, written
down as though it were the design. That test is kept as a control and the comment now says
which spelling is the control and which is the fix.

**Negative control: 7 of 18 fail with the expansion reverted.** The three that pass either
way are the ones written as controls — string non-expansion and the replayed variable.

Suite 5,868 passing.

## The decision this was taken against

Two repairs are available and they are not equivalent:

1. **The fusion expands collections**, matching what the stages it replaced did. Keeps
   the optimisation on the widest input set. Needs care that it expands exactly what
   `SortCommand` expands — a string is not a collection here, and a dictionary counts
   as one item (`TOAST-0018`'s collection-shape box records that asymmetry).
2. **Lowering refuses to fuse when the source is a collection-valued expression.**
   Smaller and safer; loses the O(N) memory win precisely where the input is an
   in-memory collection already, which is the case that needs it least.

Option 2 is the conservative one and option 1 is the correct one; the choice is whether
"fusion is transparent" is a rule or a best effort.

## Acceptance

- [x] `[3,1,2] | sort | first` is `1`, and `| first 2` is `1, 2`
- [x] `[3,1,2] | sort -r | first` is `3`
- [x] Every fused form equals its unfused form for a **collection-literal** source, not
      only for ranges — the corpus gains the source shape it was missing
- [x] A string source is not expanded into characters
- [x] The existing fusion tests still pass unchanged, pinned as controls
- [x] A negative control — 7 of 18

## Notes

Found opening `TOAST-0018`, whose ordering box names `SortFirstFusionComparer` as the
second of two ordering implementations. It is that — and it turned out the more
pressing defect is not that the two comparers *disagree* but that the fused path does
not receive the values it is meant to order.

The comparers do also differ, and that is still `TOAST-0018`'s to resolve:
`SortFirstFusionComparer` orders strings with `OrdinalIgnoreCase`, orders booleans, and
falls back to type-name ordering for mixed types, while `OperatorEvaluator`'s `<`
**raises** on booleans and on mixed string/number operands. `["B","a","C"] | sort`
answers `a, B, C` while `("a" < "B")` is a separate code path reaching a separate
answer for the same question.
