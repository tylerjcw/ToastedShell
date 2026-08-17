---
id: TOAST-0015
title: "Redirection rebinds the session's writer instead of targeting a Tōast stream handle"
status: open
area: toast
priority: 2
opened: 2026-08-17
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

- [ ] Redirection targets a Tōast stream handle rather than assigning `Runtime.Output`/`Runtime.Error`
- [ ] `cmd > file`, `2>`, `&>` and append forms behave identically, pinned before the change and compared after
- [ ] A host with no session can redirect to a file, verified without constructing a shell
- [ ] The session's writer is expressible as a handle, so `cmd` with no redirection is the same mechanism with a different target rather than a separate path
- [ ] Composite targets — `>` to several destinations — still work; that is what `CreateCompositeWriter` exists for
- [ ] `Output`/`Error` leave `ToastRuntime`'s required set, closing the last member of `TOAST-0006` stage 2e

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
