---
id: TOAST-0063
title: "A compiled class is constructed through reflection, which costs the recursion ceiling an order of magnitude"
status: proposed
area: toast
priority: 3
opened: 2026-08-22
---

## Problem

`new` on a class the compiler has itself emitted does not emit `newobj`. It emits a call to
`ToshHost.NewObject`, which resolves the type by name and constructs it through
`ReflectionInvoker.CreateInstance`:

```
ToshHost.NewObject(String, Object[])
ToshHost.NewObjectCore(...)
ReflectionInvoker.CreateInstance(Type, IReadOnlyList<Object>)
ReflectionInvoker.InvokeUnwrapped(Func<Object>)
System.Reflection.RuntimeConstructorInfo.Invoke(...)
System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(...)
DynamicClass.InvokeStub_Loop..ctor(...)
```

Seven CLR frames and a reflection stub for a construction whose type is known at emit time.

## Why it matters beyond speed

`TOAST-0049` set the recursion ceiling from the stack the process has, and had to size it for
the **worst** path on either backend. That path is this one. Bisected out-of-process on
2026-08-22:

| Compiled path | Wall |
|---|---|
| Direct self-recursive function call | survives depth 50,000 |
| Recursive construction (`prop Next = new Loop()`) | aborts between 200 and 300 |

So compiled code was first given a ceiling of 10,000 on the strength of the first row, and
the full suite aborted with a `SIGABRT` on the second — the guard could not fire before the
stack was gone. Both backends now share one derived limit, which is correct but means a
compiled recursive-descent parser is held to a number set by a path it never takes.

Emitting `newobj` for a type in the same unit would remove the reflection round trip, and the
compiled ceiling could then be justified by the cheap path it is actually on.

## Shape

The lookup already exists: `TryResolveToastTypeName` resolves the name for the current
`NewObject` path, and `CanEmitClrClassShell` already decides which classes get a real emitted
CLR type (`TOAST-0030`). Where the target is one of those, the constructor is a known
`ConstructorInfo` and `newobj` is emittable directly; everything else keeps the host call.

## Acceptance

- [ ] `new` on a class emitted in the same unit emits `newobj` rather than a host call
- [ ] Recursive construction's wall is measured again, and the compiled ceiling is set from
      the path compiled code is actually on
- [ ] `Compiled_direct_constructor_recursion_uses_structured_depth_guard` still reports a
      diagnostic rather than aborting — at whatever the new limit is
- [ ] The differential corpus covers construction, so the backends cannot disagree about it
- [ ] A negative control

## Notes

Split out of `TOAST-0049` rather than folded in: that item's job was the ceiling, and this is
a change to what the emitter writes. Related to `TOAST-0037`, which owns compiler performance
budgets — this is the same cost measured from the correctness side.
