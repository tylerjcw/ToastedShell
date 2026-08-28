---
id: TOAST-0077
title: "Native writes take their width from the value, so a buffer's layout depends on its data"
status: complete
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

`write-buffer` writes `Marshal.SizeOf(value.GetType())` bytes. The width comes from what the
value happens to be at runtime, not from anything the author declared:

| written | bytes |
|---|---|
| `7` | `7 0 0 0` |
| `cast int64 7` | `7 0 0 0 0 0 0 0` |
| `cast int16 7` | `7 0` |
| `cast byte 7` | `7` |

A bare integer literal is `Int32` *while it fits*. So a buffer of four-byte slots is correct
right up until one value arrives as a `long` — and then it writes eight bytes into a four-byte
slot and destroys its neighbour without a word:

```tosh
var b = (alloc 12)
write-buffer $b (cast int32 11) --at 0
write-buffer $b (cast int32 22) --at 4
write-buffer $b (cast int32 33) --at 8
# before: 11 22 33

var n = (cast int64 99)      # a file length, a tick count, any CLR `long`
write-buffer $b $n --at 0
# after:  99 0 33
```

Slot 1 went from 22 to 0. No error, no warning.

## Why the existing guards do not catch it

`NativeBuffer` bounds-checks, and well — writing past the end, reading past the end, and
writing a wide value into a buffer too small for it are all reported. But this write is
*inside* the buffer. Bounds checking cannot see a layout it was never told about.

The rest of the native surface was audited for the same class of defect and is clean.
A native call's parameters are declared, so a too-large argument is rejected
(`tosh.runtime.native_argument_type_conversion_failed`) rather than truncated. `read-buffer`
names its type positionally, so reads are always declared. `size-of` and `offset-of` take type
names. `raw struct` declares its field widths. Only the write side infers.

## The asymmetry is the tell

`read-buffer int32 $b --at 4` says what it expects. `write-buffer $b $v --at 4` does not, and
there is no spelling that lets it. `cast int32 $v` works — it changes the value's runtime type,
which is what the width is read from — but it says the width by side effect, and it is not
discoverable from the command's own surface.


## Resolution — 2026-08-28

`write-buffer` takes `--as <type>`, which states the width:

```tosh
write-buffer $buffer $n --as int32 --at 8    # four bytes, whatever $n is
write-buffer $buffer $n --at 8               # as many bytes as $n's own type
```

The vocabulary is the one `size-of`, `alloc` and `read-buffer` take, a `raw struct` name
included. A value that does not fit is refused rather than truncated: narrowing would move the
silent corruption from the next slot into this one.

**The inferred width stays the default.** Changing it would break every existing script, and
there is no better default available — without a stated type there is nothing to infer from
*but* the value. What was missing was a way to say otherwise, and now there is one.

### Two more of the same defect, found by auditing the rest

The item was filed about one command. The audit that followed found the same shape twice more —
a declared type that does not match reality, silent except for a symptom that looks like
something else.

**`alloc` declared `IntPtr` and returns a `NativeBuffer`.** The buffer carries the pointer, the
length that makes every bounds check possible, and disposal. The mismatch surfaced only as a
false `tosh.type.member_not_found` on `$buffer.Pointer` — a warning on code that works, which
is the kind that teaches people to ignore warnings.

**`offset-of` returned `Int64` while declaring `int`.** `size-of` returns a real `int`, and the
two are used together in every layout calculation, so `(size-of T) + (offset-of T.f)` widened to
`Int64` — putting a value of exactly the type that makes an unstated write eight bytes wide into
the middle of the arithmetic that decides layout. Two existing tests pinned the old return by
asserting `4L`; the values were always right, and only the type changed.

### What the audit found clean

The rest of the native surface holds. `NativeBuffer` bounds-checks reads, writes, and wide
values in buffers too small for them. A native call's parameters are declared, so a too-large
argument is refused (`tosh.runtime.native_argument_type_conversion_failed`) rather than
truncated. `read-buffer` names its type positionally. `raw struct` declares its field widths.
Varargs promote correctly for `int32`, `int64` and mixed arguments — checked against
`snprintf`, since C requires value-derived promotion there and it was the one remaining place
inference is by design.

The bounds checks could never have caught the original defect, and that is not a gap in them:
the write is *inside* the buffer. Bounds checking cannot see a layout it was never told about,
which is why the answer was to let the layout be stated.

## Acceptance

- [x] `write-buffer` accepts a written width, so a slot's size is stated rather than inferred
- [x] A value that does not fit the stated width is refused, naming the value and the width
- [x] The vocabulary is the one `read-buffer`, `size-of` and `alloc` already accept, including
      `raw struct` names
- [x] Existing spellings keep working — the inferred width stays the default
- [x] The hazard is documented where the command is — on the `--as` argument itself, and in
      `§Alloc (Native Buffer)` under *Stating the Width*
- [x] A test pins the silent-corruption case above, and fails without the fix — with the two
      further defects the audit found
- [x] `alloc` declares what it returns, and `offset-of` returns what it declares

## Notes

Found while rewriting `qsort.tosh` to use tosh's own native memory commands instead of
`Marshal`. The example was accidentally correct: every element fitted in `Int32`, so every
write happened to be four bytes.
