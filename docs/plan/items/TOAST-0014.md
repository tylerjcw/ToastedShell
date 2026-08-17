---
id: TOAST-0014
title: "String interpolation renders through the display stack, so its output depends on shell configuration"
status: partial
area: toast
priority: 2
opened: 2026-08-17
---

## Problem

`$"{value}"` produces its text by calling `ObjectFormatter.Format`, and
`ObjectFormatter` is display machinery: it is constructed from a
`DisplayProfileRegistry`, consults profiles ~~in five places~~ **at one site**, and its
`FormatMany` constructs a `DisplayEngine` outright.

> **Corrected 2026-08-17 by measurement.** Inside `ObjectFormatter` profiles are consulted
> once — `TryRenderProfile`, line 239, called from `FormatValue` line 52 as the *first*
> step, before any type dispatch. The other four callers of `TryRenderProfile` live
> outside the formatter (`DisplayEngine` ×3, `Tosh.Cli` ×2). **This makes the work
> markedly cheaper than filed:** profile dependence is a single early exit, not a woven
> concern, and everything below line 52 is deterministic type dispatch.

So the string a program builds depends on how the *shell* is configured to display
things. Four sites in the language call it, and they are not one concern:

| Site | What it is |
|---|---|
| `ToshEngine.cs:4184` | string interpolation — `$"{value}"` |
| `ToshEngine.cs:3241` | value-to-text conversion |
| `ToshEngine.Diagnostics.cs:577` | a thrown value's text inside a diagnostic |
| `ToshEngine.Pipelines.cs:270` | text written to a redirect target |

The first two are **language semantics**. `$"{x}"` has to produce the same string in a
host with no shell, no profiles and no terminal, or interpolation is not a language
feature — it is a shell feature that happens to have syntax.

This also blocks `no_clr` directly: a native target cannot implement interpolation
without dragging the display stack behind it, and the display stack is the part most
tied to terminals and themes.

## What the survey added

**The specifier path is already portable.** `$"{$d:HH:mm:ss}"` does not consult profiles
and does not move when `$tosh.Config.Display.DateTime.ScalarMode` changes, while the bare
hole gives three different strings for one value. So the portable protocol does not have
to be designed from nothing — **a bare hole becomes a specifier hole with a specified
default**, which is a far smaller change than this item's framing implies.

**Decided 2026-08-17:** the default for `DateTime` is local time, with format specifiers
honoured. `TOAST-0017` covers the `Kind` handling that default must get right — the bare
hole currently shifts an `Unspecified` value by the local offset — and is fixed **with**
this item, since both change what the bare hole produces.

**What is actually entangled** is not the profiles but seven shell-specific types sitting
in the middle of the dispatch chain: `ShellCommandDescriptor`, `FormatterStatus`,
`CommandHistoryEntry`, `ObjectInspection`, `ObjectInspectionMember`, `FileSystemEntry`,
`FileSystemInfo`. Those are what a split has to relocate. `TryFormatSimple` (line 174) is
the nearest thing to a portable core that exists and already takes no profile.

**160 existing test cases** across eleven files pin today's behaviour *including* the
profile dependence. Deciding which of them assert language semantics and which assert
display is part of the work, not a side effect of it.

## Stage 1 complete — 2026-08-17

`src/Tosh.Runtime/ToastRenderer.cs` implements the contract in
`docs/plan/SPEC_DRAFT_value_rendering.md` §3–§8, with 38 cases in
`ToastRendererTests` written **from the specification**, not from current behaviour.

**Nothing calls it.** That is the point: the renderer is complete and pinned before any
call site moves, so the behaviour change lands as one reviewable flip rather than as a
rewrite argued from a diff.

It cannot consult display configuration *by construction* — static, no registry, no
preferences, no reference to `DisplayProfileRegistry`, `DisplayPreferences`, `DisplayEngine`
or `ObjectFormatter`. A test asserts the absence structurally, because a rule held by
discipline is a rule that erodes.

The `Display` trait dispatches through `IShellTypeCheckable.IsInstanceOf`, so a value
answers for itself and the renderer needs neither the engine nor a registered hook. A
method merely *named* `render` on a type that does not implement the trait is not the
extension point, and a test pins that.

Four shapes needed explicit routing, each found by a failing test rather than by reading:
`ExpandoObject` (what a `{| … |}` literal actually is — it implements
`IDictionary<string, object>` but not the non-generic `IDictionary`, so it fell into the
sequence writer), `ToshTuple` (an `IShellRecordObject` whose members are `Count`, `Item1`,
`Item2`, so the record writer produced exactly the ValueTuple leak this fixes), `ToshRange`,
and `IShellEnumValue`.

**Recorded gap:** `IShellTypeDescriptor.ShellIsClass` is `true` for records *and* classes,
so it cannot tell them apart. The renderer discriminates on whether the value carries
methods (`IShellInvocableObject`), which is a proxy for the question rather than the
question. Worth a real discriminator on the descriptor.

## Stage 2 complete — 2026-08-17

All four language call sites now render through `ToastRenderer`. **Six tests failed**, not
the 160-case triage the survey projected — the pinned formatting tests mostly exercise
`ObjectFormatter` directly, which stage 2 does not touch.

Four of the six were gaps in the renderer, each found by a failing test rather than by
reading: `Quantity` (record-shaped, so `$"{$power}"` gave `{| value = 483.06, unit = "MW" |}`
instead of `483.06 MW`), `IShellNamedType` and `Type` (`TS-P1-23`, a descriptor exposes
`Name`/`FullName` as readable properties and the record walk claimed it), and a **`shy`
`ToString`**.

That last one moved a design decision. A hidden `ToString` is visible to a probe but
refused by invocation, so the renderer inspecting the receiver was the wrong shape: whether
a declaration counts is a question only the receiver can answer. `IShellInvocableObject`
gained `TryGetOwnRendering`, and `ToshClassInstance` answers it by invoking `Display` or
`ToString` with the class itself as accessor — rendering is a type describing itself, not
an outside caller reaching in.

The remaining two were intended: an unhonourable format clause now raises, and
`InterpolationFormatTests.An_inapplicable_format_falls_back_to_plain_text` is renamed and
inverted with its old reasoning recorded.

### A second defect, found in the flip

The clause path used `CultureInfo.CurrentCulture`, so `$"{3.14159:F2}"` was `3.14` here and
`3,14` on a German machine. The survey's claim that "the specifier path is already
portable" was true only of display configuration. Rendering is invariant, and both paths
now are.

### Verified end to end

| | Before | After |
|---|---|---|
| `$"{$d}"` after a config change | `1786982400` | `2026-08-17 12:00:00` |
| `$"{$d}"` vs `$"{$d:HH:mm:ss}"` | `08:00:00` vs `12:00:00` | agree |
| `$"{3.14159:F2}"` under `de_DE` | `3,14` | `3.14` |
| `echo Color.Red out> f` | 7 lines of `ToshEnumValue` | `Red` |
| `$"{[[1, 2], [3]]}"` | `Int32[] [⏎ 1⏎ …` | `[[1, 2], [3]]` |

### Stages remaining
- [ ] **Stage 3** — `ObjectFormatter` delegates to the renderer, so display and rendering
      cannot disagree; this also fixes the table cells
- [ ] **Stage 4** — `Formatter` leaves the language's required set, `ToshValueFormatter`
      points at the renderer, and a differential test pins interpreted against compiled

## Acceptance

- [ ] A portable value-to-text protocol exists in the language, independent of display profiles
- [ ] Interpolation and value-to-text conversion use it; their output does not vary with shell display configuration
- [ ] `ObjectFormatter` keeps display concerns and calls the same protocol underneath, so the two do not drift into disagreeing about what a value looks like
- [ ] The protocol is specified, not merely implemented — the conformance corpus pins scalars, collections, records, `null`, NaN and signed zero, and Unicode
- [ ] A test proves interpolation is unaffected by display-profile changes, since that is precisely what silently varies today

## Notes

**This belongs to Phase A of `SELF_HOSTING_RFC.md`, not to the assembly separation.**
Phase A is "Specify portable semantics", and its task list already names *formatting*
alongside equality, hashing, ordering, nullability, overflow and Unicode. Filed from
`TOAST-0006` stage 2e, which found it while moving rendering out of the language and
stopped rather than fixing it in passing.

The reason for stopping is the discipline the split has been run on throughout: deciding
what `$"{x}"` produces is a **language-semantics decision**, and making it as a side
effect of relocating code is how a behavioural change ends up inside a mechanical diff.
It also deserves the conformance corpus Phase A implies, which is more work than a
separation step should carry.

Note that `ToshEngine.Pipelines.cs:270` is a different question and may resolve
elsewhere: it formats a value on its way to a redirect target, which is closer to "how
does a value serialise to a stream" than to interpolation.
