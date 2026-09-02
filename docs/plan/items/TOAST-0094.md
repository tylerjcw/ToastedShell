---
id: TOAST-0094
title: "The platform type index cache grows without bound — 1,327 files, 1.5 GB"
status: proposed
area: toast
priority: 3
opened: 2026-08-28
---

## Problem

`TOAST-0064` records the platform type index on disk so the next process need not rebuild it.
The cache is keyed by a hash of the trusted-platform assembly set, and nothing ever evicts an
entry. Every distinct assembly set — every published binary, every test host variation, every
build that changes a referenced assembly — writes a new pair of files and leaves the old pair
in place.

On this machine, after ordinary development:

```
$ ls ~/.cache/tosh | wc -l
1327
$ du -sh ~/.cache/tosh
1.5G
```

Individual `.idx` files run from ~800 KB to ~2.7 MB. The oldest entries are months stale and
can never be read again, because no future process will present the assembly set that produced
their key.

## Why it matters

It is a cache, so nothing breaks and deleting it is safe. But 1.5 GB accumulated silently in
`~/.cache` on a developer machine, and a shell that ships to users would do the same at a
slower rate. It also made the `TOAST-0093` investigation harder to read: cache presence and
content varied between runs, which was a plausible-looking suspect for the non-determinism
there before it was measured and ruled out.

## Suggested fix

Evict on write. When recording a fresh index, drop entries not read for some interval, or cap
the directory at a small number of the most recently used pairs — two or three covers the real
case, which is a shell binary and the test host. The read path already tolerates a missing
cache, so eviction needs no new failure mode.

Worth deciding at the same time whether the cache should live under `~/.cache/tosh` at all
given that it is a language-layer artefact, or whether the key should be coarse enough that
ordinary rebuilds reuse one entry rather than minting a new one.
