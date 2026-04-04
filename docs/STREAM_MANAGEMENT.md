# Stream Management

ToSh should treat managed `.NET` I/O as a first-class shell capability rather than a bag of ad-hoc file helpers.

This subsystem is meant to be one of ToSh's defining strengths:

- shell-friendly path and pipeline ergonomics
- explicit managed file/stream access when needed
- strong reuse of native `.NET` types and APIs
- clear separation between text and binary workflows

## Goals

- Make common file reads and writes easy from the shell.
- Make stream lifetime explicit instead of hiding resource allocation behind properties.
- Keep the public surface readable and shell-oriented.
- Build a foundation that can expand from files into memory, process, compression, network, and archive streams.

## Core Principles

1. Path-first convenience comes first.
   Common workflows should not require opening a long-lived handle.

2. Explicit handles come next.
   Opening a stream has side effects and lifetime concerns, so it should stay method- or command-driven.

3. Text and binary should stay distinct.
   `read-file` and `write-file` are not the same thing as `read-bytes` and `write-bytes`.

4. `.NET` should do the heavy lifting.
   Use `File`, `FileStream`, `StreamReader`, `StreamWriter`, and related types instead of inventing fake replacements.

5. `FileSystemEntry` should remain path-first.
   Safe convenience methods belong on the object. Resource-owning stream access should be method-based, never property-based.

## Public Layers

### 1. Path-Level Convenience

These commands operate directly on files and are the main everyday surface:

- `read-file`
- `read-lines`
- `write-file`
- `append-file`
- `read-bytes`
- `write-bytes`

These commands should accept normal path-like values such as:

- strings
- `FileSystemEntry`
- `FileInfo`
- `DirectoryInfo` where appropriate

### 2. File Entry Convenience Methods

Safe whole-file helpers belong directly on `FileSystemEntry`:

- `ReadAllText()`
- `ReadAllLines()`
- `ReadAllBytes()`

This lets shell values stay object-first without exposing resource-owning state accidentally.

### 3. Explicit Handle / Stream Layer

This is the next major phase after the path-level slice:

- `open-file`
- `close`
- `read-from`
- `read-line-from`
- `read-to-end`
- `write-to`
- `write-line-to`
- `flush`
- `seek`
- `copy-to`

This layer should eventually use shell-friendly handle objects backed by real `.NET` streams, readers, and writers.

Active handles should be visible through the runtime namespace:

- `$tosh.Session.OpenHandles`
- `$tosh.Session.OpenHandleCount`

## Naming

Canonical names should be readable and shell-friendly:

- `read-file`
- `read-lines`
- `write-file`
- `append-file`
- `read-bytes`
- `write-bytes`

C-style aliases such as `fopen`, `fread`, and `fwrite` can come later if they still feel useful after the handle layer exists.

## FileSystemEntry Policy

Do not add a `.Stream` property.

Why:

- opening a stream has side effects
- it can fail
- it creates something that must be closed
- repeated property access would be ambiguous

Use explicit methods instead.

For the first slice, that means safe whole-file methods. Later it means explicit open methods such as:

- `OpenRead()`
- `OpenWrite()`
- `OpenAppend()`
- `OpenText()`

## Text Semantics

`write-file` and `append-file` should use ToSh's plain-text serialization rules, not rich table rendering.

That means they should behave more like:

- `write`
- `writeline`
- `raw`

than the interactive display engine.

## Binary Semantics

`read-bytes` should yield real byte arrays.

`write-bytes` should accept:

- `byte[]`
- memory-backed byte values
- byte-like collections
- byte-convertible scalar items

This keeps the first slice practical without needing the full stream-handle model yet.

## Display

The path-level commands should produce familiar values:

- `read-file` -> `string`
- `read-lines` -> `ShellTextLine`
- `read-bytes` -> `byte[]`
- `write-file` / `append-file` / `write-bytes` -> `FileSystemEntry`

Later, open handles should get dedicated display profiles.

## First Implementation Slice

1. Add the convenience commands:
   - `read-file`
   - `read-lines`
   - `write-file`
   - `append-file`
   - `read-bytes`
   - `write-bytes`

2. Add safe `FileSystemEntry` whole-file helpers:
   - `ReadAllText()`
   - `ReadAllLines()`
   - `ReadAllBytes()`

3. Keep output intentionally typed and pipeline-friendly.

4. Defer long-lived stream handles and session tracking to the next slice.

## Follow-Up Phases

### Phase 2

- `open-file`
- `close`
- `read-line-from`
- `read-to-end`
- `write-to`
- `write-line-to`
- `flush`

### Phase 3

- `seek`
- `position`
- `length`
- `copy-to`
- stream/session tracking under `$tosh.Session`

### Phase 4

- memory streams
- process stdio stream adapters
- compression streams
- archive-entry streams
- network streams

## Why This Matters

If this subsystem is done well, ToSh gets something most shells never really have:

- file I/O that is easy in shell code
- stream I/O that is explicit and composable
- direct leverage of the `.NET` runtime without forcing users to live in raw CLR ceremony
