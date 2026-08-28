---
id: TOAST-0079
title: "An array cannot reach native memory, so the FFI has no data plane"
status: complete
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

`write-buffer $b [1.5, 2.5]` was refused — correctly, since a list of doubles is not a byte
sequence and truncating it would have been worse. But nothing replaced it: there was no
spelling that moved an array of numbers into native memory at all. A vertex buffer, a texture,
an audio frame or a `glBufferData` upload had to be built one scalar at a time, re-entering
command dispatch for every number.

The cost is visible in the code. `examples/gl_mouse_cube.tosh` compiles its geometry into a
legacy display list rather than uploading it, and the comment says so: *"Geometry is compiled
once below."* That is not a stylistic choice about OpenGL versions — it is the only way to pay
the per-vertex cost once.

Reading back had the mirror of the same gap: `--count` meant a byte count for `bytes` and was
ignored for every other type, so recovering an array meant a loop of one command per element.

## Resolution — 2026-08-28

Both directions, through the flags that already existed rather than new commands:

```tosh
write-buffer $buffer $vertices --as float      # the whole array, four bytes each
read-buffer float $buffer --count 12           # and back again
```

`--as` states the element type, which is the same thing it means for a single write
(`TOAST-0077`) — the stride is decided once for the array rather than per value. `--count`
states how many, which is what it already meant for `bytes`: how many of the thing you asked
for. Without it a read is still a single value, and `bytes` still means bytes, so the flag
gained a meaning it did not have rather than changing one it did.

**20,000 doubles: 137 ms one at a time, 3 ms in bulk. Reading them back: 4 ms.**

### Refused whole, not half

The range is checked before anything is written. A sequence too long for the buffer fails
without writing, and an element that does not fit the stated type fails before the first
element is placed — every value is converted up front. A partially copied array leaves memory
in a state no reader can detect, which is worse than the write failing.

### A string is still a C string

`write-buffer $b "hi"` keeps its meaning. A string is a sequence to .NET and is not one here,
the same rule list patterns follow (`TOAST-0053`) and for the same reason: the alternative is a
string quietly taking a path written for an array.

## Acceptance

- [x] An array of numbers reaches native memory in one command, at a stated element stride
- [x] The same array reads back in one command
- [x] The stride is the stated type's, so a `float` array has the bytes a C library expects
- [x] A sequence too long for the buffer, or an element that does not fit, is refused before
      anything is written
- [x] Existing spellings are unchanged — single-value reads, `bytes`, and C strings
- [x] Documented in `§Alloc (Native Buffer)` under *Arrays*, with the measurement
