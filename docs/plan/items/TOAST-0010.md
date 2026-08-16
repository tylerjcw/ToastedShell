---
id: TOAST-0010
title: "Separate the specification into a language document and a shell document"
status: proposed
area: toast
priority: 3
opened: 2026-08-16
---

## Problem

`docs/spec/toastscript-spec.tex` is **7,010 lines across 27 chapters** and documents
the language and the shell together. Separating it means deciding chapter by chapter
which document each belongs to — the same judgement as the code split, applied to the
user-facing contract.

It gets its own item because it is not a find-and-replace and cannot lag behind the
code: it is the definition of Tōast.

## Acceptance

- [ ] Every chapter assigned to the language document or the shell document, with the borderline calls argued
- [ ] Both documents build, and `buildtosh spec` produces both
- [ ] The precedence table is generated from the surface registry rather than maintained by hand — shared with `TOAST-0003`
- [ ] `SpecWorkedExampleTests` still runs every worked example, across both documents
- [ ] The cover-image path no longer depends on an absolute personal directory

## Notes

Budget as real work. The chapter assignment is the same boundary decision as
`TOAST-0006`, so doing it alongside that item means making each call once.

The rendered PDFs are no longer tracked — `docs/spec/*.tex` is the source and the PDFs
ship as release artefacts. They accounted for 66 MB of repository history across 24
revisions before that changed.
