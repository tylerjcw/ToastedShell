---
id: TOAST-0012
title: "Span<T> and Memory<T> are not recognised as native parameter shapes, and marshalling cannot be overridden"
status: proposed
area: toast
priority: 3
opened: 2026-08-16
---

## Problem

Two gaps in native parameter handling, carried from the interop expansion that landed
in 2026-07:

- `Span<T>` and `Memory<T>` are not accepted as native parameter shapes, so a binding
  that would naturally take a borrowed buffer has to take a pointer and a length.
- There is no explicit `[MarshalAs]`-style override for the cases the inference gets
  wrong, so an inference mistake has no escape hatch.

## Acceptance

- [ ] `Span<T>` and `Memory<T>` work as native parameter shapes for blittable `T`
- [ ] An explicit marshalling override exists for cases the inference gets wrong, and is documented
- [ ] Lifetime rules are stated — a `Span` is only valid for the duration of the call
- [ ] Inference is unchanged where it is already correct, pinned by the existing native tests

## Notes

Lower priority than `TOAST-0011`: a pointer-and-length pair is a workaround that
*works*, whereas there is no workaround at all for a callback.

The override matters more than the `Span` support. Inference that cannot be overridden
turns every inference bug into a blocker.
