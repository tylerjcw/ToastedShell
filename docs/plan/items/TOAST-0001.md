---
id: TOAST-0001
title: "A free function called inside a closure resolves as an instance method on the pipeline item"
status: complete
area: toast
priority: 1
opened: 2026-08-16
closed: 2026-08-17
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

- [x] `f($_)` inside a closure resolves to a function in the enclosing scope
- [x] a real member on the item still wins where both exist, and that precedence is pinned
- [x] ~~the same holds for `map`, `sort-by` and any other closure-taking stage~~ — **the premise was wrong**, see below
- [x] a genuinely unknown name still reports as an unknown function, not as a missing method — it reports **both** readings, see below
- [x] a negative control: reverting the fix fails the new tests — 9 of 14 fail; the 5 that pass are the ones written to pass either way

## Resolution — 2026-08-17

Inside a predicate a bare name is **implicit member access**: `ParseImplicitCurrentItemArgument`
synthesizes a `$_` receiver, so `double($_)` arrives as `$_.double($_)`. That is a real
feature — `where { Length == 2 }` depends on it — so the parser was not wrong to build it.
What was missing is that the synthesized reading was the *only* one.

`MethodCallArgumentSyntax` now records whether its receiver was synthesized. Only that
form may fall back, and only after the item has been asked: the order is **member, then
extension, then function**. That is one order rather than a third rule — an `extend`
method already resolves only where the receiver has no such member (`TS-P3-27`), and a
free function is one step further out again. An explicitly written `$_.f()` never falls
back; the reader asked for a member, and if both spellings meant the same thing there
would be no way to say "the member".

The probes are lookups, never invocations. `ToshClassDefinition.HasInstanceMember` reads
the method and property tables without running a getter, `IShellInvocableObject` gained a
`HasInstanceMember` that **defaults to `true`**, and a class with a CLR base also answers
`true` — so every receiver that cannot answer honestly says "I have it", and the only
names that reach the fallback are ones a receiver positively disclaimed.

### Two recorded premises were wrong

**"The same holds for `map`, `sort-by` and any other closure-taking stage."** It does not,
because those stages never had the bug. Implicit member access belongs to a specific set:
`IsPredicateExpressionCommand`'s thirteen names in the brace form, and `map`/`sort-by` and
friends in the **parenthesized** form. `map { f($_) }` with braces is a command pipeline,
where the name is in command position and always resolved correctly — `map { Length }` is
in fact an error today, which is how the difference was found. Written the assumed way,
three of the new tests passed with the fix reverted.

**"A genuinely unknown name still reports as an unknown function, not as a missing
method."** Filed when the diagnostic looked simply wrong. It is not: at the point of
failure the name is known not to be a member, not to be an extension, and not to be in
scope, and a reader who meant either reading is helped only by being told about the other.
`tosh.runtime.unknown_implicit_call` says both.

## Notes

Checked against `TS-P2-93` — a callable held in a property beats a command of the same
name — and against extension dispatch, which resolves after real members. The new rule
extends that same chain by one step rather than competing with it.

Found writing `scripts/plan.tosh`, where a `board_of($_)` helper had to be abandoned. That
script's workaround note is now removed.
