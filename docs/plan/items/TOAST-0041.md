---
id: TOAST-0041
title: "Generate class, module and pipeline diagrams from the bound tree"
status: proposed
area: toast
priority: 3
opened: 2026-08-21
---

## Idea

The IR already carries a complete declaration model, so diagrams are a rendering problem
rather than an analysis one.

`BoundClassDefinition` has `Name`, `BaseClassName`, `ImplementedInterfaces`, `UsedTraits`,
`TypeParameters`, `IsSealed`, `IsAbstract`, `IsHermit`. `BoundClassPropertyMember` has
`Name`, `TypeName`, `IsShy` (private), `IsStatic`. `BoundClassMethodMember` wraps a
`BoundFunctionDefinition` with parameters and a return type, plus `IsAbstract` and
`IsOverride`. Interfaces, traits, unions, enums, structs, records and modules each have
their own node.

Measured across the repository and the author's library: 48 classes, 45 modules, 16
records, 5 enums, 3 interfaces, 2 structs, 1 trait, 1 union.

## What falls out, and what does not

| Diagram | Source | Notes |
|---|---|---|
| Class diagram | the declaration nodes | inheritance, realization, generics, visibility, signatures — no inference needed |
| Package / module | `BoundModuleDefinition`, `require`, `using` | |
| Call graph | `BoundCommandCall`, `BoundMethodCall`, `BoundStaticMethodCall`, `BoundNewObject` | |
| Association edges | property `TypeName` | needs name resolution against declared types |
| **Pipeline / dataflow** | `BoundPipeline` → `BoundPipelineStage` | the interesting one: these chains *are* dataflow graphs, and no other language's tooling draws them |
| Multiplicity (`0..1`, `1..*`) | inferable from `list<T>` / `T[]` | not stated anywhere |
| Aggregation vs composition | — | **no source for it.** The distinction is not expressible in Tōast; drawing it would be fabrication |
| Sequence diagram | statement order within a body | approximate across pipelines and async |

## Recommended shape

**Mermaid `classDiagram` text**, emitted by a walk over `BoundUnit`. It renders in GitHub,
VS Code, and the artifact viewer, needs no toolchain, and the walk is about thirty lines —
the measurement probe that produced the numbers above was exactly that. PlantUML gives
better-looking output and wants a Java toolchain.

## Timing

Phase C's first bullet is "freeze the canonical bound tree and lowered IR contracts". The
IR is **not** frozen, so anything built now tracks a moving target. That argues for a small
script over a feature, and for doing it after Phase C rather than during Phase B.

`TOAST-0040` was a prerequisite and is done: before it, four of the author's libraries
produced no IR at all, so any such tool would have silently skipped exactly the files with
the most structure to draw.

## Acceptance

- [ ] A walk over `BoundUnit` emits Mermaid `classDiagram` for classes, interfaces, traits
      and their relationships
- [ ] Visibility, static, abstract and generics are rendered
- [ ] A module-level diagram
- [ ] Output for the repository and for `~/.config/tosh` is checked by eye and committed as
      a fixture, so a change to the IR shows up as a diff
- [ ] Nothing is fabricated — aggregation/composition is not guessed at
- [ ] A negative control
