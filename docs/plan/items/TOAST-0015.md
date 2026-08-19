---
id: TOAST-0015
title: "Redirection rebinds the session's writer instead of targeting a Tōast stream handle"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-17
---

## Problem

`cmd > file` is parsed by `ToshParser`, so redirection is language syntax. But the
pipeline evaluator implements it by swapping the *shell session's* writer:

```csharp
originalOutput = Runtime.Output;
Runtime.Output = CreateCompositeWriter(outputTargets);
// ... run the redirected stages ...
Runtime.Output = originalOutput;
```

Eleven references do this for stdout and stderr — eight in `ToshEngine.Pipelines.cs`
(lines 247, 248, 253, 254, 273, 274, 287, 292), two in `ToshEngine.cs` (1034, 1758) and
one in `ToshEngine.Subcommands.cs` (725); four of them are the save/swap/restore pattern.
It works, and it is the last thing keeping `Output`/`Error` on the language's side of
`TOAST-0006`.

The problem is what it means for a host with no session. A `no_clr` Tōast program that
writes `run-report > out.txt` should not need a shell's stdout to exist, let alone
mutate it. Redirection should target a **Tōast stream handle** — a language value — and
the shell's writer should be one possible handle among files, pipes and buffers rather
than the thing being rebound.

## Acceptance

- [x] Redirection targets a Tōast stream rather than assigning `Runtime.Output`/`Runtime.Error`
- [x] `out>`, `err>`, `out>>` and multiple targets behave identically — the existing corpus passes unchanged, and each form was re-checked by hand
- [x] A host with no session can redirect to a file, verified without constructing a shell
- [x] The session's writer is expressible as a stream, so an unredirected `cmd` is the same mechanism with a different destination
- [x] Composite targets still work
- [x] The language no longer reads the shell's `Output`/`Error`

## Resolution — 2026-08-17

`IToastStream` is the destination contract — `CanWrite`, `WriteText`, `WriteTextLine`,
`Flush`, with async members defaulting to the sync ones so an implementer writes three
methods and overrides async only where it can do better.

**The shape was taken, not invented.** `ManagedFileHandle` already had those exact members
and now simply *is* an `IToastStream`. `ToastStreams` adds the three destinations every host
needs: `Null` (the default, because "nowhere to write" is a legitimate configuration for a
`no_clr` program rather than an error), `FromWriter` (the adapter that makes a session's
writer *a* destination rather than *the* destination — the load-bearing piece), and
`Composite`.

`ToastRuntime` owns `Output` and `Error`. The engine writes to them; it no longer reads a
`TextWriter` from the shell.

### The half that stays a shell mechanism, and why

Redirection still moves `ToshRuntime.Output`/`Error` as well, and that is deliberate rather
than unfinished. Measured: 17 `Runtime.Output` references and 39 `Runtime.Error` ones, and
the stdlib ones are commands writing diagnostics and passthrough text to the *session*.
`ExternalProcessCommand` also decides whether a child inherits the terminal by
`ReferenceEquals(context.Runtime.Output, Console.Out)`, so replacing the writer with an
adapter would silently change how every external process is spawned.

So the session swap is now a **shell** compatibility mechanism, commented as such, and the
language path is inert without it. A host with no session redirects with that whole branch
unused, which is what the third acceptance box tests.

**Restore order matters and is commented:** assigning `Runtime.Output` re-derives
`Language.Output` from the writer, so the session is restored first and the language second.
Restoring the other way left an equivalent-but-different adapter in place — caught by a
reference-identity assertion, which is the only check that would have.

## Notes

~~**This wants the stream-handle concept to exist first, which is why it is filed rather
than done.** `ManagedFileHandle` is the nearest thing today~~ — **corrected 2026-08-17 by
measurement.**

`ManagedFileHandle` is 473 lines and already has text and binary modes, read and write,
append, encodings, `CanRead`/`CanWrite`/`CanSeek`, `Position`, `Length`, `Seek`, `CopyTo`,
`Flush`, `Close`, `Dispose`, `DisposeAsync`, an id, and a global open-handle registry.
`Tosh.Stdlib/Filesystem/` already ships `OpenFileCommand`, `CloseCommand`, `FlushCommand`,
`PositionCommand`, `ReadFileCommand`, `ReadBytesCommand`, `AppendFileCommand`,
`CopyToCommand` and `ReadFromCommand` against it.

**The concept exists.** What does not exist is the *unification*: redirection targets a
`TextWriter`, the file commands target a `ManagedFileHandle`, and the session's own writer
is expressible as neither. That is a narrower and better-defined job than inventing an
abstraction, and it can start now.

The `SELF_HOSTING_RFC` still puts "iterators and streams" in the portable core library, so
this remains Phase A work — but as a reconciliation of two existing shapes, not as a
design exercise.

Inventing a stream abstraction as a side effect of moving code is the same trap that
stopped `TOAST-0014`: it would be deciding language semantics inside a mechanical diff,
and the result would be whatever redirection happened to need rather than what the
language wants.

Sequence with `TOAST-0014`, not after it — Phase A is scoped to exactly these two
concerns (`DECISIONS.md`, 2026-08-17). The two are the same question seen from
different ends — `TOAST-0007`'s correction notes that `read-file`, `write-file`, `open`
and `close` are language-level for a self-hosting language, and a stream handle is what
they all return.
