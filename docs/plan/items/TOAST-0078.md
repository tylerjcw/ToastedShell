---
id: TOAST-0078
title: "A bare name resolves to the runtime's internal types, so `Sys` means `Interop+Sys`"
status: complete
area: toast
priority: 1
opened: 2026-08-28
---

## Problem

`DotNetTypeResolver` enumerated `Assembly.GetTypes()`, which returns internal types, and asked
`Type.GetType(name)`, which searches `System.Private.CoreLib` for an unqualified name and does
not filter visibility either. So a script naming `Sys` got `Interop+Sys` — a private class with
no namespace — and every `Sys.Math.Clamp` failed with:

> Static member 'Math' was not found on type 'Interop+Sys'.

A type the author had never heard of, named in an error about code that looked right. `Interop`
resolved the same way, and so did anything else the runtime happens to call by a plausible name.

Found from `examples/gl_mouse_cube.tosh`, where `Sys.Math.Clamp` sits in the drag branch and
would have thrown the first time the cube was dragged. The same construction appears four times
in one expression in the author's `Graphics.tosh`.

## What was actually wrong

Two things, and only the first is about visibility.

**Internals were reachable.** Fixed by filtering to `Type.IsVisible` — not `IsPublic`, which is
false for every nested type however public, and true for a public type nested inside an internal
one. Five reflection lookups had to agree: filtering only the index left `Type.GetType` finding
`Interop` directly.

**`Sys` was never a real alias.** It reads as one — enough to be written fourteen times across
the author's library before anyone noticed — so it is one now, as a namespace prefix alias
rewriting `Sys.X` to `System.X`. A prefix alias rather than an import, so it cannot make an
unqualified name ambiguous; and a type declared in the script still wins, so the
`export hermit class Sys` in the author's SDL bindings keeps its name.

## The cache had to be invalidated

The platform type index is written to disk (`TOAST-0064`). A cache from an earlier build still
holds the runtime's internals under their simple names, so without bumping `PlatformCacheVersion`
the fix would have done nothing on any machine that had run tosh before — which is every machine
that matters. It took a rebuild and a re-run to notice, because the first fix looked like it had
failed.

## Three tests encoded the old accident

None of them were wrong when written; all three depended on an internal type occupying a slot.

`TypeResolutionCacheTests` used `SpinLock` to prove a `using` changes which type wins, and its
own comment says why it worked: *"without `System.Threading`, `SpinLock` is a private nested
field type inside `ReaderWriterLockSlim`"*. With internals gone that premise is gone, so the
probe is now `Timer` — `System.Threading.Timer` and `System.Timers.Timer` are both public, and
which wins is genuinely what the import decides.

`TypeResolutionPrecedenceTests` asserted `FileStatus` resolves to `System.IO.FileStatus`, which
is internal. The reorder that test was written for moved it from one implementation detail to
another — an improvement, but not the answer. It resolves to nothing now, and a new case says so
for `Sys`, `Interop` and `FileStatus` together.

## And one real defect the change exposed

`list<Token>` where `Token` is a program-declared class: a *conversion* failure began reporting
itself as an unknown type annotation. `IsKnownAnnotatedType` had no branch for a collection over
a declared element, so it fell through to the CLR resolver — which had been answering only
because `Token` matched an internal type, making `List<T>` constructible.

The conversion path had the shape test all along. It is shared now, rather than the "is it
known?" check having a second, weaker implementation of the same rule.

## Acceptance

- [x] A bare name never resolves to a type a script cannot legally name — index and all five
      reflection lookups agree
- [x] `Sys` is a namespace alias for `System`, and a script-declared `Sys` still wins
- [x] The on-disk platform index is invalidated, so the fix reaches machines that ran tosh before
- [x] Public types are unaffected — including public types nested in public ones
- [x] Tests that depended on an internal type occupying a slot are repointed, with the reason
      recorded rather than the assertion merely changed
- [x] A conversion failure over a program-declared element type reports itself as one
