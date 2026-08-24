---
id: TOAST-0075
title: "A compiled non-nullable function parameter accepts null while the interpreter rejects the call"
status: complete
area: toast
priority: 2
opened: 2026-08-24
---

## Problem

The parameter-side negative control added while closing `TOAST-0074` exposed a behavior
divergence rather than another wording difference:

```tosh
func f(value: string) { echo $value }
f null
```

| Backend | Result |
|---|---|
| interpreted | `Argument 'value' could not be converted to 'string'.` |
| compiled | accepts the call and renders `null` |

The return boundary correctly rejects `null` for the same non-nullable `string` annotation.
The compiled direct-call path instead reaches a CLR reference parameter without applying the
language's nullability rule when its inferred argument type already matches the emitted slot.

Found by deliberately checking the neighboring parameter conversion boundary required by
`TOAST-0074`; it is separate because that item changes a diagnostic for behavior on which the
backends already agree, while this call actually runs on only one backend.

## Acceptance

- [x] A compiled non-nullable reference parameter rejects `null`
- [x] The interpreted and compiled diagnostics agree for the refused call
- [x] Nullable reference parameters continue to accept `null`
- [x] Direct, packed/overload-dispatched, method, and constructor parameter paths are checked
- [x] The case moves from `KnownDivergences()` into `Corpus()` with a successful negative control

## Resolution — 2026-08-24

Every emitted callable prologue now applies the source annotation before its body observes
the argument. Fixed and packed functions, overload-dispatched functions, instance methods,
primary constructors, and explicit constructors all route annotated values through the same
runtime conversion boundary. The ordinary compiler bridge preserves refinement and
unknown-type diagnostics, and translates only an ordinary conversion refusal into the
callable-specific diagnostic the interpreter emits. Pure-profile artifacts cannot reference
the compiler host, so their CLR signatures retain conversion enforcement and a portable
`Tosh.Runtime` guard supplies the non-nullable-reference check and the same diagnostic.

The packed path carries the original argument count so overload diagnostics remain exact;
the fixed path also checks annotated parameters on functions whose return is untyped.
Nullable reference annotations remain the successful control.

Six uniquely named cases now live in the differential corpus: direct, packed/default,
overload-dispatched, method, constructor, and nullable. Unique declaration names are
intentional: the shared compiled-runtime engine retains registered callables across corpus
rows, so reusing `f` can turn an isolated single function into an apparent overload set and
change the diagnostic being measured. The focused differential suite passes all 157 cases.
