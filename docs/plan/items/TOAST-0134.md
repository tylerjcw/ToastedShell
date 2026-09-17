---
id: TOAST-0134
title: "The persisted type cache answers a miss authoritatively, so a type added since it was written does not exist"
status: proposed
area: toast
priority: 1
opened: 2026-09-17
---

## Problem

`~/.cache/tosh/platform-types-<fingerprint>.idx` is a persisted CLR type index, and
`DotNetTypeResolver.TryResolveCore` treats a **miss** in it as authoritative:

```csharp
if (PlatformTypeCache.Value is { } cached)
{
    if (!cached.TryGet(name, out var entry) || entry is null)
    {
        type = null;
        return false;      // never consults the live index
    }
```

The comment above it says so on purpose — answering "`ToastLib.Shell.ZClearScreen` is not a
CLR type" without loading every assembly on the trusted-platform list was worth 150 ms of
start-up (`TOAST-0064`).

The trouble is what keys the file. `PlatformSetFingerprint()` hashes the cache version, the
framework description, and `TRUSTED_PLATFORM_ASSEMBLIES` — which is a list of assembly
**paths**. Rebuilding the project does not change a single path, so a rebuilt binary reads a
cache built from the source as it was whenever the file was first written. Nothing rewrites
it: `WritePlatformTypeCache` returns early if the file exists.

So **every public type added since that file was written does not exist**, for the life of
the file, in every process that shares the fingerprint.

### How it showed up

`new Tui.TuiGauge()` in `examples/system-monitor.tosh` failed with "Unable to resolve type"
against the Debug build and worked against the installed binary. That reads as a TUI bug,
or as an OO-versus-markup bug, and is neither.

The cache file was dated **31 August** — seventeen days stale. It lists `Tosh.Tui` among its
contributing assemblies and contains **not one** `Tui*` type, because none of the widgets
existed when it was written. Deleting that one file fixes it completely, with no source
change:

```
$ dotnet src/Tosh.Cli/bin/Debug/net10.0/Tosh.Cli.dll --no-profile oo.tosh
Unable to resolve type 'Tui.TuiGauge'.

$ rm ~/.cache/tosh/platform-types-b03f7cfa03883719.{idx,asm}
$ dotnet src/Tosh.Cli/bin/Debug/net10.0/Tosh.Cli.dll --no-profile oo.tosh
both spellings constructed
```

The installed binary was never affected only because a published layout has a different
trusted-platform list, so a different fingerprint, so a file that happened to be written
more recently.

**This is not about the TUI.** Any public type added to any project assembly is invisible
the same way. The TUI is simply where most new public types have been added lately, and
constructing one from a script is the one place a script names a type directly.

## Acceptance

- [ ] A rebuilt assembly invalidates the cache that indexed it
- [ ] A cache miss is not authoritative for an assembly whose contents the cache cannot vouch for
- [ ] The cache directory is bounded
- [ ] A test builds an index, changes what is loaded, and asserts the new type resolves
- [ ] The start-up win `TOAST-0064` measured is still there, measured rather than assumed

## What was tried and does not work

Checking coverage **by assembly name**. The obvious fix is to treat a miss as authoritative
only when every loaded assembly is named in the cache's `.asm` record — but the record
holds `Tosh.Tui, Version=26.8.50.10`, and a rebuild produces an assembly with exactly that
name and version. The check passes and the stale answer stands. It was implemented, built
and tested against the real stale file before that was discovered; the record is here so
nobody spends the afternoon again.

Whatever the fix keys on has to change when the code changes: a module version id, or the
file's size and modification time. The name does not.

## Proposed

Two candidates, and they are not exclusive.

**Fingerprint the build, not the paths.** Add each non-framework assembly's MVID — or its
file size and mtime — to `PlatformSetFingerprint`. A rebuild then reads a different file and
builds a fresh index. Correct, and cheap to compute. It writes one more cache file per
build, which makes the directory bound below mandatory rather than optional.

**Vouch by identity, not by name.** Record each contributing assembly's MVID beside its name,
and let a miss be authoritative only while every loaded assembly's MVID is one the cache
recorded. A rebuilt assembly then falls through to the live index instead of being answered
from stale data. No extra files, but the fall-through costs a live scan in exactly the
processes a developer runs, so it wants measuring against `TOAST-0064`'s 150 ms.

## The directory is also unbounded

Found on the way and worth its own line: `~/.cache/tosh` holds **7,478 files, 2.9 GB**.

The fingerprint varies with the trusted-platform *paths*, so every build directory, every
test that runs from a temp directory and every publish mints a new ~800 KB pair, and nothing
ever removes one. Whatever the fix above does about staleness, the directory needs a cap —
prune on write by age or by count.

## Workaround

`rm -rf ~/.cache/tosh` fixes both symptoms today and costs one index rebuild.
