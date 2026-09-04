---
id: TOAST-0102
title: "A capitalised command name stops parsing as a command when its first argument is parenthesised"
status: complete
area: toast
priority: 2
opened: 2026-08-30
---

## Problem

Two functions differing only in the case of their first letter parse differently at the call
site. In `a.tosh`:

```tosh
export partial module T {
    export func lower(v, a, b, c) { return $v }
    export func Upper(v, a, b, c) { return $v }
}
```

and in `b.tosh`, required after it:

```tosh
export partial module T {
    export func GoLower(s) { return (lower ($s.Points[0].X) 1 2 3) }   # parses
    export func GoUpper(s) { return (Upper ($s.Points[0].X) 1 2 3) }   # does not
}
```

```
✖  tosh.parser.missing_pipeline_separator
│  Expression pipeline stages must be separated by '|'.
│  3 │     export func GoUpper(s) { return (Upper ($s.Points[0].X) 1 2 3) }
│                                                                  ╰─▶ insert '|' before the next command
```

The parser reads `Upper ($s.Points[0].X)` as a complete expression and then finds `1` where it
expects a pipe. `lower (...)` is read as the command it is.

## The rule appears to be name knowledge, not case alone

The same call parses when the callee is defined **earlier in the same file** — which is why the
capitalised form works in isolation and fails once the definition moves to another file. A prior
`require` of the defining file does not help: the parser's view is per-file, so a name defined in
an already-loaded sibling is still unknown while the caller is parsed.

So the defect is the *interaction*: an unknown capitalised identifier followed by a space and an
open parenthesis is taken for something other than a command, and an unknown lowercase one is
not.

## Why it matters

It makes case a load-bearing part of the syntax in a way nothing documents, and it penalises
.NET-style naming specifically. Found while converting `ToastLib.Plot` from `series-of` to
`SeriesOf`: a pure rename, no logic touched, broke the parse of a file that had been fine —
`(scale ($s.Points[0].X) $ax $left $right)` parses, `(Scale ($s.Points[0].X) $ax $left $right)`
does not. Splitting a library across files makes it near-certain to be hit, because that is
exactly when callees stop being defined above their callers.

The workaround is to write the call in comma form — `Scale(($s.Points[0].X), $ax, $left, $right)`
— which is what the library now does in 56 places.

## Acceptance

- [x] `(Upper ($x.Y) 1 2 3)` and `(lower ($x.Y) 1 2 3)` parse the same way
- [x] Case does not change how an identifier in command position is parsed
- [x] A callee defined in another file parses like one defined above the call
- [~] If the ambiguity is genuine, the diagnostic names it rather than reporting a missing pipe
      — **moot.** Whitespace decides it outright, so there is no residual ambiguity to report.
- [x] Corpus covers: unknown callee, capitalised, parenthesised first argument, in a module

## Fix — 2026-09-04: whitespace, not capitalisation

A space between an **unqualified** name and its parenthesis says command invocation; no space says
call. `Upper ($x) 1 2 3` is the first, `Upper($x, 1, 2, 3)` the second.

The rule this replaces was `char.IsUpper(text[0])` in `LooksLikePotentialClrTypeName`, reached
from `LooksLikeStaticMethodCallExpression`. Two things were wrong with it, and the second is the
one that made this an item rather than a curiosity:

1. It made case load-bearing in a way nothing documented.
2. **It already had a carve-out for names the file declares** — `_userFunctionNames` — so the
   decision depended on *where* the callee was declared. The same call parsed above its
   definition and failed below it, or in another file. That is why splitting a library across
   files makes it near-certain to be hit.

Whitespace needs no knowledge of what the name is, which is the whole point: the parser's view is
per-file, it cannot see another file's declarations, and every rule that pretends otherwise fails
at a file boundary. A name-knowledge fix would have had to make the parser aware of already-
required files — a much larger change that would still be wrong for a name defined later.

A qualified name never reaches the test: it returns true at `IsQualifiedDotNetAccess` several
checks earlier, so `System.Math.Max (3, 9)` is a static call with or without the space. And an
unqualified bareword followed by `(` is never a static call in the first place — a static call
needs a type and a member, so it is always dotted.

### Verified

Full suite green with no changes: 7,135 passing. The author's `Plot` library, which carries the
comma workaround in 56 places, still loads — and the space form it was working around now parses,
so the workaround can be reverted at leisure rather than needing to be.
