---
id: TOAST-0126
title: "A native callback is refused after any awaited builtin, because the engine moved threads and the guard compared the thread it started on"
status: complete
area: toast
priority: 1
opened: 2026-09-11
---

## Problem

**Reading a file stopped every callback in the program.**

`NativeCallbackThunkFactory` recorded `Environment.CurrentManagedThreadId` at the moment a
callback was *registered* and compared it on every invocation. Anything that moved the engine
to another thread afterwards invalidated every callback registered before it:

```tosh
$button.OnClick(func (b) { $clicks = $clicks + 1 })   # registered on thread 7
var text = (read-file "notes.txt")                    # engine resumes on thread 4
$button.Click()                                       # refused
```

```
Callback 'WidgetSignal' was invoked on thread 4, but the engine owns thread 7.
```

`read-file` and `write-file` are the two builtins that do it. Both `await` real async I/O, and
with no synchronization context the continuation resumes on a thread-pool thread — after which
the engine simply carries on there. Measured across the surface a script is likely to touch:

| operation | moves the engine |
|---|---|
| `read-file`, `write-file` | **yes** |
| `echo`, `which` | no |
| `count`, `join`, `collect`, `split`, `lines`, `first`, `skip` | no |
| `Sys.IO.File.ReadAllText` / `WriteAllText` | no |

The consequence is worst where it is least visible. A GTK application registers its handlers
while building the window, so the first document the user opens silently kills every button,
menu item and timer in the program — no error on screen, because a handler that is refused
looks exactly like a handler that was never connected. It is also why the failure had not been
noticed: the guard is correct for the case it was written for, and a script that only reads
files does not notice, and a GUI that never reads one does not either.

## Why the original guard was wrong, and what replaced it

The guard exists for a real hazard. ToSh's engine keeps its scopes in a plain `Stack`, so a
callback delivered on a library's **own** thread — SDL's audio thread is the standing example —
while the engine runs elsewhere is a data race.

But "the thread that registered this callback" is not the same question as "is the engine
somewhere else right now". The engine moving threads is not a fork; there is still exactly one
thread in it. What actually separates the safe case from the dangerous one is whether *this*
thread is currently inside a native call the engine made — because then the engine is below us
on this very stack, and re-entering it is what a signal handler is for.

`NativeCallbackScope` already answers exactly that. It is pushed around every native invocation
and is `[ThreadStatic]`, so `IsActive` is true on the engine's thread-of-the-moment and false on
a library's own thread. The registration thread is kept as a fast path:

```csharp
if (currentThreadId != _ownerThreadId && !NativeCallbackScope.IsActive)
```

## Evidence

The fix was confirmed under a real migration rather than a hoped-for one — thread ids vary per
run, so the probe was repeated until it moved and then checked it had:

```
run 1: reg=4 after=6   changed=1 clicks=1 timer=1
run 2: reg=8 after=4   changed=1 clicks=1 timer=1
run 5: reg=7 after=4   changed=1 clicks=1 timer=1
```

The safety property was checked in the same pass, using libc to start a thread the engine has
nothing to do with. The callback must not run there, and does not:

```
started=0 reached=false
```

## Worklist

- [x] Reproduce: a callback registered before `read-file` is refused after it
- [x] Establish which operations move the engine, and which do not
- [x] Replace the literal thread comparison with "is this thread inside an engine native call"
- [x] Test that a callback survives the engine moving threads
- [x] Test that a callback on a genuinely foreign thread still does not run
- [x] Full suite green (7436 passed, 0 failed, 1 skipped)

## Left alone

**The migration itself.** `read-file` resuming on a pool thread is the cause, and pinning the
engine to one thread — a synchronization context that posts continuations back — is the larger,
more principled fix. It is also a change to how every await in the runtime behaves, which is not
something to do on the way past a callback bug. The guard is now correct whether or not the
engine moves, so the migration is a performance and tidiness question rather than a correctness
one.
