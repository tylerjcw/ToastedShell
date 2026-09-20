---
id: TOAST-0123
title: "A function that yields nothing yields one null, so a 'zero or more' method poisons its own pipeline"
status: partial
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

A function whose body produces values yields them. A function whose body produces *none*
yields a single `null` rather than an empty stream:

```tosh
class E() {
    func Loop(times: int) {
        var i = 0
        while ($i < $times) { $i = ($i + 1)
                              $i }
    }
}

var e = new E()
($e.Loop(3) | collect)      # [1, 2, 3]
($e.Loop(0) | collect)      # [null]   — one value, and it is null
```

So any method meaning "zero or more of these" is unusable, because the empty case — often
the common one — puts a null where the caller expects an element. The failure surfaces at
the first member access after it, as `Cannot read member 'X' of null`, pointing at the
caller rather than at the method that yielded nothing.

## Where it bit

`ToastLib.Sdl`'s `Events.Drain` is exactly this shape: it yields each pending event and, on
an idle frame, yields nothing. Its own documented idiom —

```tosh
$win.Events.Drain() | where Kind == Sdl.EventKind.KeyDown
```

— therefore fails on the first frame with no input, which is most frames. The module has
been changed to gather into an array and return it, because an empty array *is* empty; the
docstring's promise was never true.

## Notes

Distinct from "a function with no `return`". `Loop` above has no return statement in either
case; what differs is only whether the loop body ran. So the null is not standing in for a
missing return value — it appears to be a single empty result being materialised as one
null rather than as no results.

Worth checking whether the same holds for a free function, for a `for` loop rather than a
`while`, and for a method whose body is a single `if` that does not fire, since those are
the shapes a library reaches for when it means "maybe some".

## Care needed

`var x = ($e.Loop(0))` currently binds null. If an empty result stops materialising as one
value, that becomes a binding of nothing, and every call site that relies on the null needs
to be considered — which is why this is filed rather than changed in passing.

## Acceptance

- [x] A method whose body yields nothing yields an empty stream
- [x] `($e.Loop(0) | collect)` is empty, and `($e.Loop(3) | collect)` is unchanged
- [x] The same for a free function, a `for` loop, and a non-firing `if` — the free
      function was *already* correct, because it reaches a pipeline as a command and
      streams; only the method path collapsed
- [x] `var x = (call-that-yields-nothing)` has a defined, documented meaning — it binds
      null, as it always has. Only a pipeline, which asked for items, is told there were
      none; a value position is unchanged, so no existing call site changes meaning
- [ ] `ToastLib.Sdl`'s `Events.Drain` can go back to yielding, if that reads better

## What was done

The cause was structural rather than a bad branch. A free function reaches a pipeline as an
`IShellCommand` and streams, so producing nothing is naturally an empty stream. A class
method returns one `InvocationResult`, whose `object?` cannot express "no values" — so
`FlattenCallResult` collapsed zero to null and the pipeline carried one null item.
`ReturnedVoid` looks like the missing channel but every consumer maps it to null as well.

`ToshEmptyCallResult` carries the distinction the short distance from the invocation to the
pipeline head, which is the only place it means anything:

- `FlattenMethodCallResult` returns it for zero values, at the **five method paths**. The
  **three property getters** keep the old behaviour deliberately: a property that yields
  nothing is a property with no value, and null reads correctly there.
- `EvaluateArgumentAsync` unwraps it at the one funnel every other reader passes through,
  so a binding, an argument and a condition all still see null.
- `EvaluateArgumentPreservingEmptyAsync` keeps it and has exactly one caller — the pipeline
  head, which `yield break`s. The synchronous fast path is untouched: it handles shapes that
  cannot call a method, so it can never produce the marker.

`EmptyCallResultTests` pins all of it, including the two boundaries that are easy to lose:
the property getter, and that the marker is never visible to a script. Note that a method
yielding *three* values contributes **one** item, an array — a call's collection is a value
rather than a sequence (`TOAST-0039`) — so zero contributing no item is consistent with it,
and `collect` is where a reader sees the difference.

Suite: 8559 pass, 0 fail.
