---
id: TOAST-0049
title: "Recursion is capped at 128 frames, and the cap is a stack size nobody can change"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-22
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

- [x] A recursive-descent parser handles nesting far past 40 levels on at least one backend —
      the probe's grammar parses 120 nested parentheses on a 64 MB stack, against 40 failing
      before
- [x] The compiled backend's limit is justified by the CLR stack rather than the
      interpreter's — it is justified by the *worst* compiled path, which turned out to be
      the same number, and the reasoning is recorded rather than the flattering measurement
- [x] Raising `MaxRecursionDepth` past the cap reports a Tōast diagnostic, not a
      `TargetInvocationException` — reflection's wrapper is now unwrapped for property and
      field assignment, the same way `TS-P2-95` did it for calls
- [x] The limit and how to change it are in `docs/spec/`
- [x] A negative control — replacing the derived limit with the old constant fails four of
      the nineteen new tests, and leaves the floor and parsing cases passing

## Resolution — 2026-08-22

**The ceiling is derived from the stack the process has, instead of being a constant.**

```
MaximumDepthForStack(stackBytes) = clamp(stackBytes / 64 KB, 128, 10_000)
```

8 MB — the default — yields exactly the 128 that was already measured safe for it, so
nothing changes for a shell nobody has configured. `DOTNET_Thread_DefaultStackSize=0x4000000`
gives every thread 64 MB and the limit becomes 1,024.

| Stack | Limit | Measured wall |
|---|---|---|
| 8 MB (default) | 128 | aborts between 250 and 300 |
| 64 MB | 1,024 | completes 4,000, aborts by 6,000 |
| 256 MB | 4,096 | completes 15,000 |

### Three levers were measured, and two were rejected

- **A thread with a large explicit stack does nothing on its own.** The evaluator's `await`s
  suspend and their continuations are posted to the thread pool, so the recursion resumes on
  a pool thread with the ordinary stack. A 64 MB thread left the wall exactly where it was,
  still aborting at depth 300. This is the one that looked obviously right.
- **Pumping those continuations back onto that thread works, and is not safe.** It reached
  depth 8,900. But it makes every `await` in the engine single-threaded, and the engine
  bridges sync to async in 23 places — list comprehensions among them — by blocking on
  `GetAwaiter().GetResult()`. A blocking call whose continuation is queued behind it is a
  deadlock, and the test suite cannot see it, because only `Program.cs` goes through that
  path. A hang in a login shell is worse than a shallow stack. It also cost about 10%
  throughput.
- **`setrlimit(RLIMIT_STACK)` from `Main` is too late**, because the runtime has already
  cached the default stack size, and the same setting in `runtimeconfig.json` is ignored
  outright — measured, not assumed: with it in `configProperties` even depth 300 still
  aborted.

What is left is the CLR's own environment setting. It costs nothing — startup measured
286 ms with it against 330 ms without, a compute loop 726 ms against 740 ms — and changes no
scheduling behaviour, because *every* thread gets the larger stack and there is none for the
recursion to hop onto that has the small one. It has to be set before the process starts, so
the guard reads it rather than declaring the limit.

### The compiled ceiling was wrong, and the suite caught it

Compiled code was given its own limit of 10,000 on the strength of direct compiled recursion
surviving depth 50,000. That is the *cheapest* compiled path. The full suite aborted — not a
failed assertion, a `SIGABRT` that took the run down — on
`Compiled_direct_constructor_recursion_uses_structured_depth_guard`, which asserts that
runaway construction produces a diagnostic rather than a crash.

`new` on an emitted class is constructed through reflection, and bisecting it put its wall
between depth 200 and 300 — where the interpreter's is. A ceiling has to hold for the worst
path, so both backends now share the derived limit. The flattering measurement was of a path
real programs do not stay on.

Worth stating plainly: 10,000 was justified by a measurement that was correct and
unrepresentative, and nothing but running the whole suite would have shown it.

### Not covered

The reflection round-trip is why construction is dear. Emitting `newobj` for a class the
compiler has itself emitted would let the compiled ceiling rise well above the interpreter's,
which is the shape `TOAST-0037` cares about. Filed separately rather than folded in here.

Making the larger stack the default needs the environment variable set before `tosh` starts,
which means a wrapper around the binary — a packaging decision, and one that touches a login
shell, so it is deliberately not taken here.

## Notes

Found by asking what would make the language better at compiler design, and measuring the
readiness probe rather than reasoning about it. This is the largest single obstacle found.

Every number here came from bisecting out-of-process, because the failure is a `SIGABRT`
that no in-process assertion can observe. Three separate readings were wrong along the way
and were caught only by re-measuring: a startup "regression" that was really the older
installed binary being compared against current `master`; an environment variable reported as
not working when a leftover thread was the difference; and four "crashes" that were the
guard's own validator rejecting a test value above its bound.
