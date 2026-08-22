---
id: TOAST-0058
title: "There is no memory model and no atomic type, so no lock-free structure can be written correctly"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

The specification contains no `atomic`, no `volatile`, no interlocked operation, and no
statement of a memory model. Searched, not assumed: none of those words appears.

The language nonetheless has both halves of the hazard. It has concurrency — `async`,
futures, channels, background jobs — and it has shared mutable memory, including
`raw struct` values and `alloc` buffers that native code writes into. What it does not have
is any way to say what one thread is guaranteed to see of another's writes.

## Why this is a correctness item rather than a feature request

Today a program that shares state across tasks is relying on whatever the CLR's memory model
and the JIT's reordering happen to do, through a language that has never stated a rule. That
is not a missing convenience; it is unspecified behaviour in a language that specifies
truthiness to the value.

Concretely unwritable: a ring buffer between a producer and a consumer, a lock-free queue,
double-checked initialisation, a counter incremented from several tasks, or any handshake
with native code over a shared buffer. Each of these is ordinary systems work and each
currently has to be written in C# and bound.

## What is needed

- An **`atomic<T>`** type with explicit orderings — relaxed, acquire, release, sequentially
  consistent — and compare-and-swap.
- A **stated memory model**: what a Tōast program may assume about the visibility and ordering
  of writes across tasks and threads. This is a specification chapter, not a type. Without it
  the type is decoration.
- A rule for **`raw struct` and `alloc` memory shared with native code**, which the CLR's own
  model does not cover on its own.

The `no_clr` and `native` profiles in `docs/SELF_HOSTING_RFC.md` make this harder to defer:
each target has its own memory model, and "whatever the host does" stops being an answer the
moment there is more than one host.

## Acceptance

- [ ] `atomic<T>` for the integer and reference widths, with load, store, exchange, and
      compare-and-swap
- [ ] Orderings are explicit at the operation, and the default is stated rather than implied
- [ ] A specification chapter states the memory model — visibility, ordering, and what is
      guaranteed across tasks, threads, and the native boundary
- [ ] The rule for memory shared with native code through `raw struct` and `alloc` is stated
- [ ] A ring buffer and a lock-free counter exist as conformance fixtures, run under
      contention
- [ ] The model is stated per target profile where the profiles differ
- [ ] Interpreted and compiled agree
