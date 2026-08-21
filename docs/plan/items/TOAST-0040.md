---
id: TOAST-0040
title: "Two forms the parser accepts do not lower, and one of them takes the whole file with it"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

Found by measuring, not by a bug report. A reflective walk over every `.tosh` in the
repository and in the author's own `~/.config/tosh` reported:

| | |
|---|---|
| files lowered | **53 of 57** |
| IR nodes | 39,205 |
| un-lowered (`BoundDynamic*`) | 32 — **0.08%** |
| distinct node kinds seen | 87 of 117 defined |

So the IR is not a subset of the language in any meaningful sense. What it had were two
holes, and they were different in kind.

### A `bind` block inside a class threw

```
InvalidOperationException: Unknown class member kind: ClassBindMemberSyntax
```

`Sdl.tosh`, `Gl.tosh`, `Gtk.tosh` and `System.tosh` — every library that binds a native
surface — produced **no IR at all**. Not a degraded node: nothing, for the whole file.

`LowerBindStatement` already existed and was reachable for a top-level `bind`. Only the
class-member wrapper was never routed to it.

**Lowering and emitting are different questions**, and that is the distinction this was
conflating. A bind block being non-emittable is deliberate and documented — the emitter
says so, and they stay Tier 3. The lowerer throwing is not the same decision, and it locks
every tool that reads the tree out of exactly the files most worth reading.

### `...` in pipeline position did not lower, and could not compile

`TOAST-0032` added `...` as a pipeline stage and taught the interpreter. The lowerer was
never taught, so a head spread became a `BoundDynamicExpression` — and the emitter then
**refused the entire unit**:

```
compiled tosh: dynamic argument expressions (SpreadElementArgumentSyntax) are not yet emitted
toshc: refusing to write incomplete output.
```

Not a fallback. A hard failure, on the spelling `TOAST-0028` and `TOAST-0039` tell people
to migrate onto — so code written against the current collection-shape rule could not be
compiled at all. That is the part that made this priority 2 rather than tidy-up.

## Resolution — 2026-08-21

`BoundClassBindMember` and `BoundSpreadElement`, both lowered, and the head spread emitted
through a new `ToshHost.SeedFromSpread` — the counterpart to `SeedFromValue`. One says
"this is a value and the language decides its shape"; the other is the author having
already said it.

`var n: int = (...$xs | count)` now compiles and answers 3, matching interpreted. The four
native libraries get past lowering; `Gl.tosh` still reports an ordinary "unsupported shape"
at emit, which is Tier 3 behaving as designed rather than a crash.

### Not the same as the other two spread forms

A spread inside an array literal is `BoundArrayLiteralItem.IsSpread`; argument position is
`SplatArgumentSyntax`. Both already lowered. Routing all three through one node would have
been the obvious wrong fix, and the control test asserts they are untouched.

## Acceptance

- [x] A `bind` block inside a class lowers rather than throwing
- [x] `...` in pipeline position lowers to a typed node, not a dynamic one
- [x] `...` in pipeline position compiles, and the compiled program agrees with the
      interpreted one
- [x] The other two spread forms are unchanged, asserted as a control
- [x] The four native libraries get past lowering
- [x] A negative control — reverting only `Lowerer.cs` keeps the types, so the tests build
      and **fail** rather than failing to compile, which is the stronger signal

## Notes

Both were found while answering a question about whether the IR could drive UML diagrams.
The answer was yes, and the measurement taken to support it is what surfaced these.
`TOAST-0041` carries the generator.
