---
id: TOAST-0080
title: "Resource safety is a runtime convention, so an owned handle can be copied and used after release"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

Tōast uses garbage collection for ordinary objects and runtime checks for scarce resources.
`NativeBuffer`, for example, is an `IDisposable` reference with an `IsFreed` flag and a
finalizer. Copies of the reference remain callable after `native-free`; the failure is found
only when that path runs. Files, sockets, processes, subscriptions and foreign handles have
the same ownership question with different wrappers.

Rust demonstrates the useful guarantee here, but applying a borrow checker to every Tōast
value would fight the language's dynamic shell tier. The narrower target is **affine ownership
for resource types**: an owned value may be used at most once after transfer, while normal
garbage-collected values remain freely aliased.

## Candidate surface

The exact grammar is a design decision; this spelling makes the intended checks concrete:

```tosh
func upload(buffer: own NativeBuffer) {
    native-read bytes $buffer --count $buffer.ByteLength | send-to-gpu
}                                      # buffer is released on every exit

var buffer: own NativeBuffer = native-alloc 4096
upload(move $buffer)                   # ownership crosses here
native-read byte $buffer               # bind error: use after move
```

A temporary borrow does not transfer ownership. Borrowed contiguous data belongs to
`TOAST-0057`'s `span<T>` rules; raw borrowed pointers belong to `TOAST-0059`'s unsafe tier.
This item supplies the owner those views borrow from.

## Boundaries

- This is not reference counting and not a uniqueness rule for every class.
- Resource types opt in through a language protocol/manifest entry; a plain CLR
  `IDisposable` is not silently made affine across a dynamic boundary.
- Lexical exit, `return`, `throw`, cancellation and failed construction release each reached
  owner exactly once, using the existing exhaustive `defer` unwinding contract.
- The runtime keeps a consumed-state backstop for values that arrive through `Any`, reflection
  or untyped pipelines, but statically visible mistakes are binder/type-checker diagnostics.

## Acceptance

- [ ] The specification defines an affine resource type and how a type opts into it
- [ ] An explicit `move` transfers ownership; a later read, write, borrow or second move is a
      structured diagnostic at the later use
- [ ] Owned parameters and returns state whether a call borrows, consumes or produces a resource
- [ ] Every reached owner is released exactly once on normal, exceptional and cancelled exits
- [ ] Partial construction releases only the resources whose initialization completed
- [ ] `NativeBuffer` and at least one file/socket or process handle use the common protocol
- [ ] Borrowed spans cannot outlive or move independently of their owner, linked to `TOAST-0057`
- [ ] Dynamic and CLR interop paths have a runtime consumed-state backstop with stable diagnostics
- [ ] Capturing or sending an owner follows `TOAST-0086`'s transfer rules rather than copying it
- [ ] Interpreter, compiler, cancellation tests and the differential corpus agree

## Dependencies

`TOAST-0057` defines safe borrowed views; `TOAST-0059` defines typed raw pointers and the unsafe
boundary. `TOAST-0086` decides when ownership may cross a concurrent task or channel boundary.
