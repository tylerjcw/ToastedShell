---
id: TOAST-0033
title: "The specification does not say which of its sentences are requirements"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-21
---

## Problem

`docs/spec/toastscript-spec.tex` is 27 chapters, 163 sections and 26 noteboxes, and it
contains no **Conformance** section, no normative/informative distinction, and no
definition of "must" — a word it uses a dozen times in ordinary prose.

So it mixes three voices freely, often in one paragraph:

- **"A `str` is a sequence of UTF-16 code units."** — a requirement an implementation must
  satisfy.
- **"That convenience has a cost, and the cost is why containers do not use it."** —
  explanation of a decision.
- **"This is recorded as a defect rather than a design."** — a description of what is true
  today and is *intended to change*, which is the opposite of a requirement.

A reader implementing a backend cannot tell them apart.

## Why this is on the Phase A/B boundary rather than tidy-up

Phase A's exit is "core behaviour is specified in Tōast terms and enforced by a
backend-neutral corpus", and both halves exist. Phase B's job is making compiler-shaped
code production-ready — **against this document**.

`TOAST-0030` records five behaviours the compiled backend does not implement. Today that
reads as "fails five paragraphs". It needs to read as "fails five requirements", because
only the second is a thing a backend can be held to or excused from.

## The drift is already measurable

`§Errors and catch` says a raised diagnostic "answers to the implementation type name it
happens to have" and points at `TOAST-0029`. `TOAST-0029` closed the same day: a diagnostic
now answers `is Exception`, and the remaining half is `TOAST-0031`. The paragraph was
accurate when written and wrong within hours.

That is what a document with no marked requirements does — nothing distinguishes the
sentences that must be revisited when behaviour changes from the ones that merely read
well.

## Acceptance

- [x] A **Conformance** section states what "must", "may" and "is" mean in this document,
      and what a conforming implementation is
- [x] Normative text is distinguishable from explanation without stripping the reasoning —
      **by making normative the default** and marking the exceptions, so not one sentence
      of rationale had to be moved or removed
- [x] Every notebox describing a **current defect** is marked non-normative and names its
      item — a `defectbox` whose default title reads "Known defect --- not normative", so
      the mark cannot be applied without the reader seeing it
- [x] The eight `TOAST-0018` concerns each state their requirement identifiably —
      they are statements of behaviour, which the new rule makes normative by default, and
      a scan confirms none of those sections carries unmarked provisional language
- [x] The stale `TOAST-0029` reference is corrected, and a guard exists —
      `SpecificationConformanceTests`, with a negative control: pointing a defect box at a
      completed item fails two tests by name
- [x] `docs/spec/` still builds with zero warnings

## Resolution — 2026-08-21

**Normative by default, with the exceptions marked.** That is the choice that made this a
legend rather than a rewrite: a statement of behaviour binds, an example's stated result
binds, and the things that do not bind — rationale, notes, and boxes describing a defect —
are named as such. Not a sentence of reasoning had to be moved, which matters, because the
reasoning is most of what makes this document worth reading twice.

`defectbox` carries "Known defect --- not normative" in its title, so behaviour awaiting a
fix cannot be marked quietly. Two exist: collection shape (`TOAST-0028`) and a diagnostic
having no Tōast name (`TOAST-0031`).

### The guard, and the drift that earned it

`§Errors and catch` said a diagnostic "answers to the implementation type name it happens
to have" and pointed at `TOAST-0029` — which closed the same day, making the sentence wrong
within hours of being written. Nothing caught it but re-reading.

`SpecificationConformanceTests` now fails when a defect box names a completed item, and
when prose anywhere outside a LaTeX comment does. **Negative control: pointing one box at
`TOAST-0029` fails both tests by name.** A closed item behind a defect box means either the
behaviour was fixed and the text is stale, or the box names the wrong item — and both want
a person, which is why the failure says so rather than guessing.

### What this did not cover

Roughly eight places elsewhere in the document say "not currently" or "not yet" — indexer
access, destructuring, tail calls, unary operator overloading, one catch block per `try`.
The conformance section now gives them a defined status: they are statements of behaviour
and therefore normative, which is probably right for some and probably wrong for others.
Auditing them is a separate pass over chapters this item did not touch.

## Notes

Raised after the user confirmed the document "is _supposed_ to be a spec". It is written
with unusual care about *why* each decision was taken, and that is worth keeping — the
question is only which sentences bind an implementation.

Deliberately not a rewrite. The prose is good; what is missing is a legend.
