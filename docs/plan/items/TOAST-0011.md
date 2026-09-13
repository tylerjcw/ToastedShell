---
id: TOAST-0011
title: "A TōSh closure cannot be passed where C wants a function pointer"
status: complete
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

The one interop shape still missing. `func qsort(nint, nuint, nuint, callback)` is
rejected — there is no way to hand a TōSh closure to native code expecting a function
pointer.

Everything around it landed in 2026-07: `raw struct`, inline arrays and char buffers,
struct-by-value in both directions, pointer-to-struct walking, `out`/`ref`, success
contracts and errno. Callbacks are what is left, and they are the shape that turns FFI
from "can call a C function" into "can participate in a C API".

The cost is concrete and currently being paid. `~/.config/tosh/lib/Bluetooth.tosh`
cannot use sd-bus, so it re-spawns `bluetoothctl` on **every property read** —
`$h.Name`, `$h.IsPaired`, `$h.Battery` is three processes. libarchive progress
callbacks and every `qsort`-shaped API are blocked the same way.

## Verified done — 2026-08-28

**This item was filed on 2026-08-16 and the language work landed since, without the item being
updated.** `raw callback` exists, is parsed, bound, thunked and documented; what follows is
what was checked rather than assumed, on `6c2d8d1`.

`qsort` — the canonical test this item names — sorts end to end from a ToastScript closure:

```tosh
raw callback Comparer(a: ptr, b: ptr) -> int

hermit class Libc {
    bind native "libc.so.6" {
        func qsort(base: ptr, count: nuint, size: nuint, compare: Comparer) -> void
    }
}
```

`[5, 3, 9, 1, 7]` comes back `1, 3, 5, 7, 9`.

**Lifetime**, both documented and checked. `§Native Interop` states the rule — *a thunk passed
to a library is kept alive for as long as that library is loaded, so a callee that stores it
(GLFW does) keeps working after the registering call returns*. Empirically, the same
`&compare_ints` sorts a second array after a forced `GC.Collect` / `WaitForPendingFinalizers` /
`GC.Collect`, so the thunk is genuinely rooted rather than incidentally alive.

**A throw inside a callback** is carried out rather than unwound across the C frames: a `try`
around the native call catches it, and the process keeps running. `NativeCallbackScope` is what
does it, and the spec's `§Threading` says so.

**The author's own `Gl.tosh`** registers real GLFW key and error callbacks and its comment now
reads "not a language limit any more" — the deferred-dispatch case in practice.

### One boundary found

`on_exit` from libc registers successfully and returns 0, but the handler does not run at
process exit. Almost certainly because the engine is gone by teardown, so there is nothing left
to re-enter. Worth knowing before someone reaches for an atexit-shaped API; it is not the case
this item was filed for, which is a library storing a callback and calling it during a later
call the script makes.

### What is genuinely left is not language work

`~/.config/tosh/lib/Bluetooth.tosh` still spawns `bluetoothctl` — eleven times. Nothing stops
it using sd-bus now; the binding simply has not been written, and it lives in the author's
shell configuration rather than in this repository. The box stays open because the item chose
it as the proof, but it is an application of a finished feature, not a gap in one.

## Acceptance

- [x] A closure can be passed where a C function pointer is expected — `raw callback`, and `&name` satisfies it
- [x] A lifetime story exists and is documented — the thunk lives as long as the library is loaded; checked against a forced GC, not only read
- [x] A callback that outlives the call that registered it works — GLFW in `Gl.tosh`, and thunk reuse after a full GC. `on_exit` at process teardown is the one boundary; see above
- [x] Exceptions thrown inside a callback are handled without unwinding across native frames — a `try` around the native call catches, and the process continues
- [x] `qsort` works end to end as the canonical test
- [x] sd-bus is reachable well enough for `Bluetooth.tosh` to stop spawning a process per
      property read — **done 2026-09-12**, measured against live hardware

## The binding — 2026-09-12

`~/.config/tosh/lib/Net/SdBus.tosh` is a general sd-bus binding, not a Bluetooth one:
string, boolean and byte property reads, method calls, writable-property sets, and
errors that carry the D-Bus error *name* rather than only an errno.
`Net/Bluetooth.tosh` is rewritten on it and no longer spawns anything.

**No varargs were needed, which is what made this safe.** `sd_bus_call_method` is
variadic and calling one through a fixed signature is the kind of thing that works
until it does not. The message API is not variadic — `sd_bus_message_new_method_call`,
`sd_bus_message_append_basic`, `sd_bus_message_open_container`, `sd_bus_call` — so the
whole surface is built from prototyped calls.

### Measured, against a connected Stealth 600X

| | before | after |
|---|---|---|
| `Snapshot()` — every property | 8.24 ms | **4.01 ms** |
| one property, read fresh | 8.24 ms | **0.44 ms** |
| processes spawned for a snapshot | 1 | **0** |

`strace -e execve` over a full snapshot counts **one** `execve` — the shell itself.

The cache is gone with the spawn. It existed only to stop a status display running
`bluetoothctl` ten times, and it cost staleness: `Refresh()` had to be called and
remembered. Values are live now, and `Refresh()` remains as a no-op for compatibility.

### `GetAll` was measured and rejected

The obvious next step — one round trip for every property instead of nine — is wrong
here, and the measurement says why: a bus round trip is **~230 µs** while each FFI call
costs **~35 µs**. Walking the returned `a{sv}` takes roughly four calls per property, so
parsing seventeen of them would cost more than the nine round trips it saved. Recorded
so nobody optimises it the wrong way later.

### Two things found on the way

**`write-buffer` emits a pipeline value.** Zeroing a slot before a trivial read made the
function return *two* values, and the caller's `!= 0` answered against the wrong one —
so every boolean came back inverted while every string was fine. `| ignore` fixes it.
The same shape as the void-call rule, in a place that produced a plausible wrong answer
rather than an error.

**`sd_bus_message_append_basic` means two different things by its pointer.** For a
numeric type it points *at* the value; for `s`, `o` and `g` it is the characters
themselves. One signature cannot express both, so the symbol is bound twice and each
call site picks the marshalling its type code needs.

### What is not covered

`Connect`, `Disconnect`, `Pair` and `Remove` are written against the bus but were not
run against the hardware — disconnecting a headset someone is listening to is not a
test worth running. The *call path* they use is proven: `CancelPairing` on an
unpairing device reaches BlueZ and comes back as `org.bluez.Error.DoesNotExist`, name
and message intact, which exercises message construction, `sd_bus_call` and the error
extraction end to end. `Trust`/`Untrust` were round-tripped for real and restored.

ToastLib's own suite stays at 431/431 and the login profile loads. The library is not
version controlled, so this row is the record of it.

## Notes

`NativeCallbackScope` already exists because a callback cannot throw across the C
frames it runs on — so half the hard thinking is done and the constraint is known.

This is the item that decides whether Tōast's FFI is comparable to Nim's or C#'s.
SDL2, OpenGL, GTK3, GtkSharp and Avalonia have all been driven from TōSh already; every
one of them is event-driven, and callbacks are how events arrive.
