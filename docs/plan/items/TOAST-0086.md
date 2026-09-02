---
id: TOAST-0086
title: "`async` detaches work with no parent cancellation or sendability boundary"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

The `async` command forks an executor, starts `Task.Run` and deliberately replaces the caller's
token with `CancellationToken.None`. A future may therefore outlive the scope that created it;
failure is observed only if somebody awaits it; cancellation does not flow from parent to child.
The same command can capture mutable values, and `ShellChannel` accepts arbitrary `object?`, so
there is no language boundary at which a cross-task data race can be rejected.

## Candidate surface

```tosh
taskgroup work {
    var config = $work.spawn { read-config $configPath }
    var data   = $work.spawn { fetch $uri }
    build (await $config) (await $data)
} # waits for both; one failure cancels its sibling before the scope exits

var daemon = detach { watch $path }     # lifetime escape is explicit
```

The final spelling may make `spawn` a command rather than a member. The invariant is lexical:
every non-detached child completes before its group exits. Cancellation and child failures are
owned by the group, not left to an unobserved `Task`.

## Sendable values

A value captured by a child or sent through a channel must be safe to cross that boundary:

- primitives, nominal value types and deeply frozen values are automatically sendable;
- an affine owner may cross only by `move`, leaving the parent slot consumed;
- borrowed spans, raw pointers, mutable collections and unknown CLR objects do not cross by
  default;
- a user type may satisfy a `Sendable` protocol only when all of its reachable fields do.

Dynamic pipelines still need a runtime backstop, but statically visible captures and sends must
fail before execution.

## Acceptance

- [ ] A lexical task-group construct owns every child spawned within it
- [ ] Scope exit awaits all children on success, failure, return and cancellation
- [ ] The first child failure cancels siblings and all failures are preserved in deterministic order
- [ ] Parent cancellation reaches children, and cleanup follows the existing shielded `defer` policy
- [ ] Detached lifetime is explicit; the existing `async` behavior has a migration/compatibility rule
- [ ] Task outputs have a deterministic policy—buffered/replayed, tagged, or explicitly unordered—rather
      than racing writes into the parent pipeline
- [ ] Static capture analysis diagnoses non-sendable values at the spawn site
- [ ] `channel-send` applies the same `Sendable` rule and has a runtime check for dynamic values
- [ ] Affine resources cross only by `move`; borrowed `span<T>`/raw pointers cannot cross or survive `await`
- [ ] A structural `Sendable` protocol works for user records, structs, unions and generic arguments
- [ ] Interpreter, compiler and stress tests cover sibling failure, cancellation, leaks and data races

## Dependencies

Deep frozen values come from `TOAST-0081`; affine transfer comes from `TOAST-0080`; span escape
rules come from `TOAST-0057`. The cross-thread visibility guarantees for explicitly shared atomic
state remain `TOAST-0058` rather than being invented by the scheduler.
