---
id: TOAST-0120
title: "Every file in a library repeats its module path as wrapping, two levels deep"
status: complete
area: toast
priority: 3
opened: 2026-09-10
---

## Problem

A library organised as nested modules pays for the nesting in every file. ToastLib's
geometry files each open with

```tosh
partial module ToastLib {
    partial module Math {
        partial module Geometry {
            ...
        }
    }
}
```

and close with three braces, indenting every declaration twelve columns before it says
anything. The dotted form `partial module ToastLib.Math.Geometry { … }` already worked and
removes two of the three, but the file is still a single block whose braces exist only to
say where the module ends — and a file has an end already.

## Fix

A `module` declared with no body takes the rest of the file, as a C# file-scoped namespace
does:

```tosh
partial module ToastLib.Math.Geometry

export class Rectangle(...) { ... }
```

Statements written *above* the line stay at the top level, which is what keeps `require`
and `using` usable before it. A block `module` written below nests inside, so a file can
still group. At most one bodyless declaration per file; a second one, or one inside a
brace, is refused by name rather than falling through to be read as a command — which is
what happened before, and reported `The provided value is not callable` against `partial`,
having read it as partial application.

## Notes

The body does not exist when the header is read: it is every statement the top-level loop
has yet to produce. So the header leaves a placeholder in the statement list and the loop
closes the module over everything after it once the file is parsed. The dotted-segment
nesting is shared with the block form rather than duplicated, so both produce the same
tree — the innermost module owns the body and doc comment, and the wrappers around it are
partial shells.

The lookahead recognises the bodyless form only when the name ends the line, so `module`
followed by anything else is still whatever it was.

## Acceptance

- [x] `module A.B.C` with no body scopes the rest of the file
- [x] `partial` and `export` work on it, and a doc comment attaches
- [x] Statements above the line stay at the top level
- [x] A block `module` below the line nests inside it
- [x] Two files contribute to one partial path, as with the block form
- [x] A second bodyless declaration, or one inside a block, is refused by name
- [x] The block form and every other use of the word are unchanged
