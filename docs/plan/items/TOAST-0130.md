---
id: TOAST-0130
title: "A module-qualified generic annotation never matched, so every generic type in a library was unusable in one"
status: complete
area: toast
priority: 1
opened: 2026-09-12
---

## Problem

**A generic type declared in a module could not be named in an annotation** — and a
library is a module, so this was every generic type in one:

```tosh
var p: ToastLib.Math.Point2D<double> = (ToastLib.Math.Point2D.Empty<double>())
# tosh.runtime.annotation_conversion_failed
#   'p' produced a value that could not be converted to 'ToastLib.Math.Point2D<double>'
```

The annotation rejected the value the expression on its right had just produced. The
identical *unqualified* spelling `Point2D<double>` accepted it, and the qualified
*non-generic* spelling `M.Box` accepted it too — only the two together failed. It hit
variable annotations, parameter annotations and return-type annotations alike.

Three lines reproduce it with nothing else involved:

```tosh
module M { export class Box<T>(v: T) { prop V: T = $v } }
var b: M.Box<int> = (new M.Box<int>(1))
```

## Cause

The generic branch of the annotation converter matches **textually**.
`ToshClassInstance.IsInstanceOf` splits the written name at its `<` and compares the
open part against the class and each of its ancestors — and a definition knows itself
only by its bare `Name`, so `M.Box` matched nothing and the branch returned a hard
mismatch rather than falling through.

The non-generic path never had this, because it resolves the annotation to a
definition and compares *that*. The generic path had already resolved the name too —
`TryGetGenericClassAnnotation` returns the matched `ToshClassDefinition` — and then
threw the resolution away and matched on the original string. The fix re-spells the
name from the definition the resolver returned.

## Acceptance

- [x] A module-qualified generic annotation accepts its own type, in all three
      positions an annotation appears: variable, parameter, return type
- [x] The unqualified spelling still works
- [x] A wrongly closed generic is still refused — `M.Box<int>` does not take a
      `Box<string>` (`TOAST-0125`), which is the half re-spelling could have cost
- [x] A qualified annotation still refuses an unrelated class rather than matching
      anything whose bare name resolves
- [x] Confirmed load-bearing: three of the six tests fail against the old behaviour
- [x] Full suite green — 7,576 passing, with only the four pre-existing
      `CompilerRuntimeStagingTests` failures from uncommitted compiler work

## How it was found

By **running** the repository's examples rather than parsing them. All 21 parse; 20
ran. `examples/particle.tosh` annotates a property with
`ToastLib.Math.Point2D<double>` and could not get past its own first field.

That example had a second, unrelated problem behind this one: it assigns into
`$this.Position.X`, and `Point2D` has since become immutable — every field `fixed`.
It is rewritten here to replace the value rather than mutate it, which is what the
library's design asks for and makes the example a better demonstration of it.

The remaining 20 were confirmed to run: twelve terminal programs, `cdu` through its
`scan` subcommand, `reactor-block`, `Pinwheel`, and the five GTK/SDL/OpenGL programs
under `xvfb` with software rendering, `notepad.tosh` among them.
