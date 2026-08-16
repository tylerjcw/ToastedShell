---
id: TOAST-0001
title: "A free function called inside a closure resolves as an instance method on the pipeline item"
status: open
area: toast
priority: 1
opened: 2026-08-16
---

## Problem

Inside a closure, `f($_)` is resolved as a **member of the pipeline item** rather than
as a function in scope. The same call one line earlier works.

```tosh
func double(n) { return ($n * 2) }

echo (double 3)                                   # 6
echo ([1, 2, 3] | where { double($_) > 2 } | count)
```

The second line fails:

```
No overload matched instance method 'double' on 'System.Int32' with 1 argument(s).
```

The diagnostic is the tell — it did not report an unknown function, it reported a
missing *method on Int32*. So the name is being looked up against `$_` before, or
instead of, the enclosing scope.

Found writing `scripts/plan.tosh`, where a `board_of($_)` helper had to be abandoned
and its result precomputed into the item dictionary instead. That workaround is in
that script with a pointer here.

This matters more than the workaround suggests: `where`, `map` and `sort-by` are the
places a helper function is most natural, and a language built on object pipelines
that cannot call its own functions inside a pipeline stage pushes every author toward
inlining. It also fails *at runtime*, only on the branch that runs.

## Acceptance

- [ ] `f($_)` inside a closure resolves to a function in the enclosing scope
- [ ] a real member on the item still wins where both exist, and that precedence is pinned
- [ ] the same holds for `map`, `sort-by` and any other closure-taking stage
- [ ] a genuinely unknown name still reports as an unknown function, not as a missing method
- [ ] a negative control: reverting the fix fails the new tests

## Notes

Worth checking against the `TS-P2-93` precedence rule — a callable held in a property
already beats a command of the same name — and against extension-method dispatch,
which deliberately resolves last. Whatever is decided here should agree with both
rather than introduce a third order.
