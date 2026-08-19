---
id: TOAST-0003
title: "Documentation disagrees with the implementation in twelve recorded places"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Twelve mismatches between what the documentation says and what the code does,
collected on the old stabilization board under "Documentation Drift to Resolve" and
carried unfiled ever since. They were listed as things to "repair alongside their
owning work item", which meant several had no owner at all.

They are gathered into one item because they are one kind of work — reconcile a
statement with the behaviour — and because a checklist is the honest shape for
something that is finished only when the last one is done.

## Acceptance

- [ ] The specification says a failed `as` returns `null`; runtime help and the implementation throw
- [ ] Storage-size suffixes are documented as literals but behave as strings when both operands are suffix forms
- [ ] Storage-size suffixes are worse than that in value position — treated as an unknown command, and comparisons silently return wrong booleans (`TS-P2-14`)
- [ ] The operator-precedence table disagrees with the implementation four ways: ternary versus `??`, the folded comparison/type-test/membership levels, range binding (`TS-P2-03`), and `**` versus unary minus (`TS-P2-02`) — regenerate it from the `TS-P2-10` surface registry rather than editing it
- [ ] The equality cascade omits the `TypeConversion` coercion (`1 == "1"` is true) and the case-insensitive `ToString` fallback for mixed types (`TS-P1-14`)
- [ ] The comprehension chapter pipes `$myDict \| entries`, and no `entries` command exists — implement it or fix the example
- [ ] Operator help and MCP metadata misstate case sensitivity and which operators are supported
- [ ] The LaTeX build depends on an absolute personal path for the cover image (`/data/pic/Colby Family/Colby-Crest.png`) — ship the asset under `docs/spec/` or guard it with `\IfFileExists`
- [ ] CLI help omits the compilation and metadata-export modes *(shell-side)*
- [ ] Compile output is documented as requiring `-o` though it can be derived *(shell-side)*
- [ ] Startup documentation disagrees with itself about whether `--no-profile` skips autoload *(shell-side)*
- [ ] A guard exists so the precedence table cannot drift again — it is generated, not written

## Notes

The last box is the one that matters. Four of these are precedence-table entries, and
a table maintained by hand beside a parser will disagree with it again; the surface
registry from `TS-P2-10` exists precisely so it can be generated. Fixing the other
eleven without that one buys a year.

Three boxes are marked shell-side. They can be split into a `TOSH-` item if the
Tōast/TōSh separation makes that natural — the split is the point at which each of
these acquires an obvious owner.

Several name an owning item (`TS-P2-14`, `TS-P2-03`, `TS-P2-02`, `TS-P1-14`,
`TS-P2-10`); check those first, since some may already be resolved and only the prose
left behind.

## Note — 2026-08-17

None of the twelve above are closed by this, but two neighbouring things were:

**The specification gained a `Value Rendering` section**, and the format-clause paragraph
was corrected. It had said "every .NET format string works" and that "a clause a value
cannot honour leaves it rendered plainly rather than failing" — both true when this item was
filed and both falsified by `TOAST-0014`, which made rendering invariant and made an
unhonourable clause an error. Quoting inside a word, what an interpolation hole is, and a
trait's declared member types are documented too. **Every example in the new text was run
against the binary before it was written down**, which is the discipline the twelve above
exist because of.

**`buildtosh spec` recovers from a poisoned build.** A LaTeX run that fails leaves a
truncated `.out` or `.aux`, and the next run dies with "File ended while scanning use of
\BKM@entry" at `\begin{document}` — naming nothing about the edit that caused it. One
genuine error therefore made *every* later build fail until someone cleaned by hand. That is
a documentation-drift cause in its own right: a specification that is painful to rebuild is
a specification people stop rebuilding.

Measured rather than assumed, over several wrong attempts: it happens with and without
`-halt-on-error`, so dropping that flag does not fix it, and deleting the `.out` first is
worse — latexmk then cannot tell a rerun is needed and the *first* build after any edit
fails. What fixes it is recovering: clean and retry once, and report a second failure as
real. All three controls pass — a settled build does not warn, a genuine LaTeX error still
fails, and the build recovers on the run after.

Box 8 above — the absolute path to the cover image — was attempted and **reverted**. A
guard on the bare filename silently failed, because `\IfFileExists` does not search
`\graphicspath`, and the cover lost its crest while still building. It remains open.
