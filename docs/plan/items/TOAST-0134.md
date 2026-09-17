---
id: TOAST-0134
title: "The persisted type cache answers a miss authoritatively, so a type added since it was written does not exist"
status: complete
area: toast
priority: 1
opened: 2026-09-17
closed: 2026-09-17
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

- [x] A rebuilt assembly invalidates the cache that indexed it
- [x] A cache miss is not authoritative for an assembly whose contents the cache cannot vouch for
- [x] The cache directory is bounded
- [x] A test builds an index, changes what is loaded, and asserts the new type resolves
- [x] The start-up win `TOAST-0064` measured is still there, measured rather than assumed

## What was tried and does not work

Checking coverage **by assembly name**. The obvious fix is to treat a miss as authoritative
only when every loaded assembly is named in the cache's `.asm` record — but the record
holds `Tosh.Tui, Version=26.8.50.10`, and a rebuild produces an assembly with exactly that
name and version. The check passes and the stale answer stands. It was implemented, built
and tested against the real stale file before that was discovered; the record is here so
nobody spends the afternoon again.

Whatever the fix keys on has to change when the code changes: a module version id, or the
file's size and modification time. The name does not.

## What landed

The first of the two candidates below, which turned out to subsume the second.

**The fingerprint stamps the build.** `ApplicationStamp` adds the name, size and write time
of every trusted-platform assembly that sits beside the executable. A rebuild changes the
stamp, the stamp changes the fingerprint, and the next process reads a different file and
builds a fresh index rather than trusting an old one.

Only the application's own assemblies are stamped. The framework's do not change under a
running installation and there are two hundred of them; stamping those would pay two hundred
file reads at every start-up to learn nothing. The list is sorted before hashing, because
the trusted-platform order is not something to depend on and a stamp that varied with it
would mint files for no reason. A published single-file binary has no such paths and stamps
nothing, which is right rather than a gap: its assemblies cannot change without the file
changing.

That also settles the second box without a format change. A miss is authoritative for the
assemblies the cache covers — and the cache can now only be *read* by a process whose
assemblies are byte-for-byte the ones it was built from.

**The version is 4.** Every file written before this may be indexing source that no longer
exists, and a miss in one is answered as fact. They are unreadable now rather than merely
wrong.

**The directory is capped at twelve pairs**, pruned after a write — which is already off the
critical path, since a process that has just paid to build an index will not notice a
directory listing. A pair is treated as being as recent as its *newer* half, so a pair being
written right now is not mistaken for an old one because its first file has an older
timestamp.

### Measured

Restoring the exact stale file from 31 August and running the same script that failed
against it:

```
both spellings constructed
```

The file is still on disk. It is simply never read, because the fingerprint no longer
matches. Adding a comment to `TuiGauge.cs` and rebuilding mints new fingerprints, which is
the first box end to end.

The directory went from **7,478 files and 2.9 GB** to **24 files and 11 MB** on the first
write.

Start-up, to check `TOAST-0064`'s win survived rather than assuming it:

| | Mean of three |
|---|---:|
| warm, cache present | 0.33 s |
| cold, no cache | 0.42 s |

Still about 90 ms, which is what it was for.

### What the tests hold

`ApplicationStamp` and `CacheFilesToPrune` are pure functions over their inputs, so the
tests drive them directly rather than through a directory: a rebuild changes the stamp, an
unchanged build does not, list order does not, the framework's assemblies are never stat-ed,
a file that cannot be read is skipped, both halves of a pair are pruned together, and a pair
is as recent as its newer half.

## What was considered

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

## The directory was also unbounded

Found on the way and worth its own line: `~/.cache/tosh` held **7,478 files, 2.9 GB**.

The fingerprint varies with the trusted-platform *paths*, so every build directory, every
test that runs from a temp directory and every publish mints a new ~800 KB pair, and nothing
ever removes one. Whatever the fix above does about staleness, the directory needs a cap —
prune on write by age or by count.

## Workaround, before the fix

`rm -rf ~/.cache/tosh` fixed both symptoms and cost one index rebuild. Anyone on a build
from before this does not need it any more: the version bump makes those files unreadable
and the first write prunes them.
