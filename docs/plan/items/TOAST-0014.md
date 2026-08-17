---
id: TOAST-0014
title: "String interpolation renders through the display stack, so its output depends on shell configuration"
status: open
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
