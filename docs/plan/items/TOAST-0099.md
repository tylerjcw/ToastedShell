---
id: TOAST-0099
title: "A declared type cannot say where it lives, so nothing can write its qualified name back"
status: proposed
area: toast
priority: 2
opened: 2026-08-30
---

## Problem

A type exported from a module registers under its **bare name inside that module's scope**:

```csharp
moduleScope.Classes[name] = definition;
moduleScope.Exports!.Types[name] = definition;
```

The qualified path `ToastLib.Math.Point2D` is resolved by *walking module scopes at lookup time*
and is never stored. A definition therefore cannot answer "what is my full name?" — and
`ShellFullName` returns the bare `Name`, which is not the same thing and reads as though it were:

```csharp
public string ShellFullName => Name;      // ToshClassDefinition
public string? ShellNamespace => null;
```

## Why it matters

Found by `TOAST-0092`. `to ton` on a `ToastLib.Math.Point2D` emitted `new Point2D {| … |}`, and
`from ton` refused that document — *the writer produced something its own reader rejects* —
because the bare name resolves to nothing at top level.

The notation is not the only consumer. Anything that has a value and wants to name its type is
in the same position: a diagnostic that says `Point2D` when two modules declare one, `help` and
type metadata, LSP hover, and any future typed-serialisation tag. Each of those currently either
guesses or omits.

`new ToastLib.Math.Point2D<int>(…)` parses and resolves, so the *language* has no gap here. The
gap is that a value cannot tell you the name that would work.

## Candidate surface

A qualified name recorded at registration, where the module path is already known, rather than
reconstructed by walking scopes afterwards:

```csharp
public string QualifiedName { get; }      // "ToastLib.Math.Point2D", or Name at top level
```

Registration happens in one place (`ToshEngine.RegisterNamedType`-ish, around
`moduleScope.Classes[name] = definition`), which is where the enclosing module chain is in hand.

Worth deciding alongside: whether `ShellFullName` should become the qualified name rather than a
synonym for `Name`, and what a *nested* type inside a class reports — `Outer.Inner` is already
resolvable, so its qualified form has a spelling to match.

## Acceptance

- [ ] A declared class, struct, record, enum, union and interface can report its qualified name
- [ ] A type declared at top level reports its bare name unchanged
- [ ] A nested type reports the form that resolves (`Outer.Inner`)
- [ ] `ShellFullName` is either the qualified name or documented as deliberately not being it
- [ ] `TOAST-0092`'s writer names a module-scoped type instead of degrading to an anonymous record
- [ ] Diagnostics that name a type disambiguate two same-named types from different modules
- [ ] Interpreter and compiler agree

## Not in scope

A class with a **required primary constructor** still cannot use the typed-literal form, and its
constructor parameters need not match its property names — `Point2D<T>(x: T, y: T)` against
`prop X`, `prop Y`. That is a separate limitation and would still stop `Point2D` round-tripping
by name even with this item done.
