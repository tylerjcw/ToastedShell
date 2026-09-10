---
id: TOAST-0123
title: "A function that yields nothing yields one null, so a 'zero or more' method poisons its own pipeline"
status: proposed
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

- [ ] A method whose body yields nothing yields an empty stream
- [ ] `($e.Loop(0) | collect)` is empty, and `($e.Loop(3) | collect)` is unchanged
- [ ] The same for a free function, a `for` loop, and a non-firing `if`
- [ ] `var x = (call-that-yields-nothing)` has a defined, documented meaning
- [ ] `ToastLib.Sdl`'s `Events.Drain` can go back to yielding, if that reads better
