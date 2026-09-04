---
id: TOAST-0112
title: "A refinement type cannot name its base with `:`, and omitting the base reports the wrong error"
status: complete
area: toast
priority: 3
opened: 2026-09-04
---

## Problem

A refinement was declared one way only:

```tosh
type PosInt = int where _ > 0
```

The brace body already existed — `type PosInt = int { where _ > 0 }` parses, coercer and all —
but the `:` spelling did not, even though the language already uses it for exactly this shape in
`enum Level: int { … }`.

Worse, the natural first guess produced a misleading error:

```
type PosInt {
    where _ > 0
}

✖ tosh.bind.unknown_command
  Command 'type' is not a registered builtin or function declared in this source.
    did you mean 'types'?
```

`LooksLikeTypeAliasDeclaration` required an `=`, so without one the declaration was never
recognised as a declaration at all and fell through to command resolution. The reader is told the
keyword does not exist, which is the least useful true statement available.

## Fix

`:` and `=` are both accepted, inline clauses and brace bodies work under either. The base type
stays required — it is the thing being refined — and omitting it is now its own diagnostic naming
both spellings.

**The colon rides on the name token.** The lexer glues it: `type A: int` gives the bareword `A:`,
and `type A:int` gives `A:int`, while `type A : int` gives them apart. `enum` already deals with
this through `ParseTypedIdentifierToken`, so this reuses it — and the lookahead had to stop
validating the raw token as an identifier, since `A:` is not one.

All six spellings are pinned by a theory:

```tosh
type A: int { where _ > 0 }     type A = int { where _ > 0 }
type A : int { where _ > 0 }    type A = int where _ > 0
type A:int { where _ > 0 }      type A: int where _ > 0
```

## Acceptance

- [x] `:` and `=` both name the base type, with inline or braced clauses
- [x] Every spacing the lexer produces around `:` is accepted
- [x] A brace body under `:` carries `where` and `coerce`
- [x] Generic aliases are unaffected
- [x] A missing base type is one diagnostic naming both spellings, not a cascade and not
      "Command 'type' is not a builtin"
- [x] A plain alias with no refinement still works under both spellings
- [x] `§Refinement Types` documents declaring and testing one

## Notes

The base-less form the author first wrote — `type PosInt { where _ > 0 }` — is not made to work.
There is nothing to infer a base from, and a refinement over "anything" would make `_ > 0` mean
whatever the operator happens to do to the value it meets. Requiring the base and explaining the
omission is the honest version.
