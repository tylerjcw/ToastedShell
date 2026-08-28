---
id: TOAST-0088
title: "A declared enum serialises its own internals, in every format"
status: complete
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

A shell-declared enum is a `ToshEnumValue` object, so `Type.IsEnum` is false for it. It missed
the scalar branch in `ShellDataSerializer.Normalize` and reached the reflection tail, which
emitted every public property — `Definition`, `Name`, `UnderlyingValue`, `ShellTypeDescriptor`
and `EnumTypeName`, with the type descriptor **twice**, since two properties return it.

```
System.DayOfWeek.Tuesday | to json    → 2            (one scalar)
Level.Novice             | to json    → 23 lines
Level.Novice             | to csv     → 5 columns, two of them embedded JSON
```

Every format shares `Normalize`, so json, csv, toml, xml and pipeline file materialisation were
all wrong at once.

## It was also a tier divergence

The compiler emits real CLR enums (`CanEmitClrEnumType`), so **compiled tosh was already
correct** — the same script printed `0` compiled and twenty-three lines interpreted. The
differential corpus does not cover `to json` of a declared enum.

## Why `ToshEnumValue` exists at all

Asked, because deleting it would have been the better fix. Two reasons, both REPL-specific:

- **Redefinition.** `enum L { A B }` followed by `enum L { A B C }` works today. A CLR enum type
  cannot be redefined; emitting a fresh type per redefinition leaks types, since dynamic
  assemblies are not collectible without collectible load contexts.
- **Runtime doc comments and source spans**, for `help` and for diagnostics that point at the
  declaration. XML docs are external to a CLR enum.

Everything else — flags, member names, underlying type, `ToString` — a CLR enum does natively,
and the compiler already uses one. So the type stays, and the rule is that it must *behave* like
a CLR enum at every boundary. This was one boundary; there may be others.

## Resolution

`Normalize` tests for `IShellEnumValue` — the interface `ToshEnumValue` already implements so
that operator dispatch can order enum members without the language assembly — and returns the
member name.

The name rather than the number: it is what `ToString` already gives, it is what survives a
round trip legibly, and a config file that says `"Librarian"` beats one that says `8`. A
composed flags value keeps its composed name (`"A, B"`).

CLR enums still serialise as numbers, which is .NET's default. Aligning those is a separate
decision and a `JsonStringEnumConverter` away.

## Acceptance

- [x] A declared enum serialises as a scalar in json, csv, toml and xml
- [x] A flags enum keeps its composed member name
- [x] The interpreter now agrees with what the compiler already produced
- [x] `ToshEnumValue` is reached through an interface, so the serialiser keeps its assembly boundary
