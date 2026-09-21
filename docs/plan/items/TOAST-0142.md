---
id: TOAST-0142
title: "Type discovery needs the single-file bundle extracted, which costs 1.3 GB and a legacy SDK mode"
status: open
area: toast
priority: 2
opened: 2026-09-21
---

## Problem

The publish passes `-p:IncludeAllContentForSelfExtract=true`, and the .NET 11 SDK now warns
(`NETSDK1244`) that this is "a legacy compatibility mode". The obvious reading — remove it — is
wrong today, and the reason is worth writing down, because the warning will keep arriving and
the property will eventually go away.

TōSh resolves arbitrary framework types by name. `TypeCatalog` builds that ability from
`AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`, loading each entry with
`AssemblyLoadContext.Default.LoadFromAssemblyPath`. `DotNetTypeResolver` keys its on-disk type
index by `stat()`-ing those same paths.

**In a bundled single-file app, `TRUSTED_PLATFORM_ASSEMBLIES` is `null`.** Not "full of paths
that do not exist" — absent. So the catalog has nothing to enumerate.

Measured 2026-09-21, publishing `Tosh.Cli` both ways and running one script against each:

| | TPA | assemblies loaded | `SHA256.Create()` |
|---|---|---|---|
| extracted (flag on) | 193 entries | 193 | `Implementation` |
| bundled (flag off) | **null** | 62 | **"Unable to resolve .NET access path"** |

## What the modern replacement is, and what it is not

**Loading by *name* works in a bundle.** Verified: `Assembly.Load("System.Security.Cryptography")`
succeeds, its `Location` is empty as expected, and `GetType("…SHA256")` returns the type. So the
path-based load is the only thing the bundle actually breaks.

**Deriving the assembly name from the namespace does not work in general.** The obvious
heuristic — try the longest namespace prefix, then shorten — resolves
`System.Security.Cryptography.SHA256` and `System.Text.RegularExpressions.Regex` and fails
`System.Net.NetworkInformation.Ping`, which lives in `System.Net.Ping.dll`. .NET assembly names
are not namespaces, and no rule recovers that.

So the replacement is a **type-to-assembly index generated at build time** and resolved lazily
by name at runtime. The repository already does exactly this shape of thing for
`DiagnosticCodeManifest.g.cs` and the diagnostic chapters.

## What it buys

Startup is **not** the reason. A first comparison suggested bundling cost 2× at startup; that
was entirely a missing `PublishReadyToRun` in the test publish, not the flag. Measured fairly,
five runs each:

| | startup |
|---|---|
| extracted + R2R | 0.17–0.21 s |
| bundled + R2R | 0.14–0.21 s |

What it buys is **1.3 GB**. `~/.net/Tosh.Cli` currently holds that much extracted bundle, on a
machine where the same programme also accumulated 1.5 GB of type-index cache
([`TOAST-0094`](TOAST-0094.md)). And it removes a dependency on an SDK mode whose own warning
says it is on the way out.

## What would close this

- [ ] A build-time index maps type full names to the assembly that holds them
- [ ] Resolution is lazy and by name: loaded assemblies first, then `Assembly.Load` of the
      assembly the index names, cached both ways
- [ ] `TRUSTED_PLATFORM_ASSEMBLIES` remains a fallback where it is present, so the extracted and
      framework-dependent layouts do not regress
- [ ] `Ping` resolves — it is the case that disproves the namespace heuristic and belongs in
      the tests
- [ ] The publish drops `IncludeAllContentForSelfExtract`, and the 193-assembly eager load
      with it
- [ ] Startup is measured before and after, with `PublishReadyToRun` held constant

## Until then

Keep the flag. To silence the warning without changing behaviour, add
`-p:NoWarn=NETSDK1244` to the publish arguments — verified: the warning disappears and
`SHA256.Create()` still resolves. The flag is passed from `scripts/build/lib/Publish.tosh`,
`scripts/build/lib/Benchmark.tosh`, and the legacy `scripts/build.tosh`.
