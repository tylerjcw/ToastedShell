---
id: TOAST-0100
title: "There is no document model, so editing a file means regenerating it and losing its comments"
status: proposed
area: toast
priority: 3
opened: 2026-08-30
---

## Problem

Every path that reads a file and writes it back goes **document → value → document**, and the
middle step drops everything that is not data: comments, trailing commas, blank lines, key
order, and whatever else the author put there on purpose.

Found by `TOAST-0092`, where it defeats an acceptance criterion outright. A hand-annotated TON
document:

```tosh
# the librarian was rebalanced in 1.21
new Villager {|
    Name = "Steve"      # placeholder name
    Level = 3
|}
```

read and written back becomes `new Villager {| Name = "Steve", Level = 3 |}`. The notation
advertises comments as a feature — the item calls their absence "the omission JSON's authors
have publicly regretted" — and then loses them the first time a program touches the file.

No amount of care in a serialiser fixes this. A value carries no comments, so there is nothing
to preserve by the time the writer runs.

## What is actually wanted

A **document → document** operation: parse, keep the tree with its trivia, change one part, and
re-emit every untouched region verbatim. That is an editing API rather than a serialiser, and it
wants its own verb so that `to ton` is not quietly failing to keep a promise it never could.

## The groundwork exists

The lexer already captures comments with their offsets and whether they stand alone on a line:

```csharp
public sealed record LineComment(
    int Position, int EndPosition, int Line, bool IsFullLine, string Text);
```

`ParseResult` carries them. What is missing is a model that holds the parse tree *alongside* its
trivia with enough fidelity to re-emit unchanged regions byte-for-byte.

## Beyond TON

The formatter wants the same thing, and for the same reason — formatting a file today means
re-deriving its text rather than adjusting it, which is why a formatter cannot preserve a
deliberate blank line or an aligned comment block. `TOAST-0014` and `TOAST-0017` are the
formatting items; this is the piece under both.

An editor integration wants it too: a refactor that renames one field should not rewrite the
whole file.

## Acceptance

- [ ] A document model retains comments, blank lines and separator style with source offsets
- [ ] Re-emitting an unmodified document reproduces its input byte-for-byte
- [ ] Changing one value leaves every other region untouched, comments included
- [ ] TON gains a document-editing verb distinct from `to ton`
- [ ] The formatter is expressed against the same model rather than regenerating text
- [ ] A round-trip corpus covers comment placement: leading, trailing, inline and between fields
