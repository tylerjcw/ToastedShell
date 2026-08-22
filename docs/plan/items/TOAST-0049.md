---
id: TOAST-0049
title: "Recursion is capped at 128 frames, and compiled code inherits a limit it does not need"
status: open
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

`ToshExecutionDepthGuard.MaximumSafeDepth = 128`, and it is a **hard ceiling** rather than a
default:

```csharp
if (maximumDepth is < 1 or > MaximumSafeDepth) { throw new ArgumentOutOfRangeException(...); }
```

`$tosh.Config.Shell.MaxRecursionDepth` can lower it and cannot raise it. Setting `500`
fails — with `Exception has been thrown by the target of an invocation`, an unwrapped
`TargetInvocationException` rather than a Tōast diagnostic, which is a second small defect.

## Why it matters for compiler-shaped code

Recursive descent is *the* compiler shape, and it spends a frame per grammar level per
nesting level. Measured against `bench/probes/compiler_shape.tosh`, whose grammar has five
precedence levels:

| Input | Result |
|---|---|
| `((((1))))` — 5 deep | parses |
| 10 deep | parses |
| 20 deep | parses |
| 40 deep | **`recursion_limit_exceeded`** |

Forty nested parentheses is not exotic. Machine-generated source, a deeply nested
expression, or a long chain of binary operators in a right-associative grammar all reach it.

## The part that is arguably a defect rather than a limit

The comment on the constant says why it exists: it *"stays deliberately below the first
observed CLR stack-overflow boundary for the interpreter's heaviest class-dispatch path"*.
That is a fact about the **interpreter**.

Compiled code has no such path — its frames are ordinary CLR frames — and yet it inherits
the same cap, enforced by a `ToshHost.EnterExecutionFrame` call emitted on **every function
entry**:

```
Unhandled exception: Maximum ToastScript recursion depth was exceeded.
   at Tosh.Runtime.ToshExecutionDepthGuard.Enter(...)
   at Tosh.Compiler.Runtime.ToshHost.EnterExecutionFrame(String frameName)
   at d.Program.d(Int32 n)
```

So a compiled program pays a host call per call *and* is limited by a stack it does not use.
That is also a performance question — `TOAST-0037` owns budgets — but the correctness half
is here.

## Options

1. **Raise the interpreter's ceiling** by making the evaluator's deep path cheaper. Most
   work; helps every script.
2. **Let the compiled backend use a much higher limit**, or none, since the CLR raises its
   own `StackOverflowException`. Cheapest real improvement, and it is where compiler-shaped
   code will run anyway.
3. **Make the cap configurable upward with a documented risk**, so a program that knows its
   own depth can ask for it.
4. **Trampoline deep recursion** in the evaluator. Largest change; removes the limit rather
   than moving it.

## Acceptance

- [ ] A recursive-descent parser handles nesting far past 40 levels on at least one backend
- [ ] The compiled backend's limit is justified by the CLR stack rather than the
      interpreter's
- [ ] Raising `MaxRecursionDepth` past the cap reports a Tōast diagnostic, not a
      `TargetInvocationException`
- [ ] The limit and how to change it are in `docs/spec/`
- [ ] A negative control

## Notes

Found by asking what would make the language better at compiler design, and measuring the
readiness probe rather than reasoning about it. This is the largest single obstacle found.
