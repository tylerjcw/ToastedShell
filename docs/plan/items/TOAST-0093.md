---
id: TOAST-0093
title: "A compiled unit claims its type names process-wide, so a bare name resolves to another script's compiled output"
status: complete
area: toast
priority: 1
opened: 2026-08-28
---

## Problem

Compiling a script produces an assembly whose CLR types carry the script's own names. Once
that assembly is loaded, bare type-name resolution finds those types, because the resolver's
rescan matched on `Type.Name` as well as `Type.FullName`:

```csharp
var newMatch = SafeGetTypes(allAssemblies[i]).FirstOrDefault(t =>
    TypeNameMatches(t.FullName, name) || TypeNameMatches(t.Name, name));
```

So a compiled unit declaring `enum Fuel` makes the bare name `Fuel` resolvable to *every*
later script in the process — including scripts that never declared it and for which the name
should resolve to nothing at all.

```tosh
class Reactor {
    enum Fuel : int { Mox = 3 }
}
var x: Fuel = Fuel.Mox     # must fail: `Fuel` belongs to `Reactor`
```

That is a language defect, not only a test artefact. TōSh is a login shell: a session that
compiles anything declaring a type leaves the type's bare name resolvable for the rest of the
session.

## Why it looked like flakiness

Four tests failed only in full-suite runs and always passed in isolation:

- `NestedTypeTests.A_nested_name_does_not_leak_into_the_surrounding_scope`
- `NestedTypeTests.The_bare_name_is_confined_to_the_declaring_class`
- `CastToDeclaredTypeTests.A_value_that_will_not_convert_has_a_different_code`
- `CastToDeclaredTypeTests.A_failed_enum_conversion_lists_the_members`

All four assert that something *fails*; all four failed with
`Assert.ThrowsAny() Failure: No exception was thrown`. `BoundUnitEmitterTests` and
`DifferentialExecutionTests` compile and load units declaring `enum Fuel`, which supplied the
name the four tests require to be absent.

The non-determinism came from the rescan window. It runs from `_platformIndexedAssemblyCount`
— a watermark snapshotted when the platform type index is built — to the end of the assembly
list. Where a compiled unit falls relative to that watermark depends on when the index
happened to be built, which races the background warm-up and varies with the on-disk index
cache. So the same filter passed and failed across runs.

Ruled out along the way, each by measurement rather than argument: concurrency (it fails with
`xUnit.ParallelizeTestCollections=false`), the on-disk index cache (cleared it, still
non-deterministic), the type checker's `[ThreadStatic]` class tables (reset per check), and a
name collision between the five suites that declare `enum Fuel`.

The probe that settled it dumped every loaded CLR type whose name contains "Fuel" at the
moment of a wrong pass:

```
clrFuel=[ToshTest_31877749b12a4b568604197484d080b3.Fuel in ToshTest_31877749b12a4b568604197484d080b3]
```

## This is the general form of TS-P2-39

`TS-P2-39` is the same mechanism, reported seven times before it was reproduced, where an
emitted `Circle` captured an interpreted script's own `Circle`. The fix taken then guarded one
caller — `ToshEngine.ResolveTypeArgument` checks the script's own named types first — which
covers only the case where **the script declares the colliding name itself**. It cannot cover
the case where the script declares nothing and the name must resolve to nothing, which is what
these four tests need. `CrossTestTypeLeakTests` documented the mechanism but both of its tests
were of the already-covered shape.

## Fix

Refuse the name at the source rather than at one caller. An assembly with no
`Assembly.Location` was never loaded from disk — exactly the compiled-unit case, since
`load-assembly` loads from a path — so it may answer a fully-qualified name but not a bare
one.

`src/Toast.Runtime/DotNetTypeResolver.cs`, applied to both rescan loops (the ordinary one and
the generic-definition one):

```csharp
private static bool MayAnswerUnqualifiedName(Assembly assembly) =>
    !string.IsNullOrEmpty(assembly.Location);
```

The platform type index is unaffected: it is built from `EnumerateTrustedPlatformAssemblies()`
only, so a compiled unit never enters it. `TypeNameMatches` is exact equality (modulo generic
arity), not a suffix match, so gating the `Type.Name` arm alone is sufficient — a
fully-qualified `ToshTest_….Fuel` still resolves.

## Verification

- Two tests added to `tests/Tosh.Tests/CrossTestTypeLeakTests.cs`, which already forces the
  emit-then-run order deterministically rather than waiting for it:
  `An_emitted_name_the_script_never_declares_stays_unresolvable` and
  `A_nested_type_is_not_supplied_by_an_emitted_assembly`.
- Negative control: with `MayAnswerUnqualifiedName` stubbed to `true`, exactly those two fail,
  with `Assert.ThrowsAny() Failure: No exception was thrown` — the original message.
- The filter that reproduced the flake at roughly one run in two: 6/6 green.
- Full suite: 4/4 consecutive green runs, 6,808 passed, 0 failed.

## Left open

The suggestion path `ToshEngine.ResolveNearestAnnotatedTypeSuggestion` picks its best match by
iterating a `HashSet<string>` and keeping the first strictly-smaller distance, so which of two
equally-near names is suggested depends on hash order. It affects only the "did you mean" text
and nothing asserts it, but it is a second, cosmetic source of run-to-run variation.

`~/.cache/tosh` accumulates one platform-type index per distinct loaded-assembly set and never
evicts: 1,327 files totalling 1.5 GB on this machine. Filed separately.
