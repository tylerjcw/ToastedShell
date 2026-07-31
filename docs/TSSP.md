# Tosh Structured Stream Protocol (TSSP)

**Status:** Draft v1 · **Audience:** Tool authors, ToSh evaluator implementers

TSSP lets external programs emit *both* structured records (for piping
into ToSh's data model) and fully custom presentation (for direct
terminal display) on the same stdout stream. ToSh picks which to use
based on what the consumer of the stream is.

A program that speaks TSSP can give you `crumb -Ss dotnet` rendered as
its own hand-tuned coloured table when shown to a terminal, while
`crumb -Ss dotnet | where Votes > 100` automatically gets structured
records — no `from json` round-trip, no separate flag.

## 1. Negotiation

Before spawning any external command, ToSh sets the following
environment variables:

| Variable                    | Value                                          | Meaning |
|-----------------------------|------------------------------------------------|---------|
| `TOSH_STRUCTURED_STDOUT`    | `1`                                            | ToSh understands TSSP; you may emit it. Absent ⇒ emit plain text only. |
| `TOSH_TSSP_VERSION`         | `1`                                            | Highest wire-format version ToSh supports on this side. |
| `TOSH_STDOUT_CONSUMER`      | `pipe` \| `terminal` \| `capture`              | Where stdout is heading. See §6. |
| `TOSH_TERM_WIDTH`           | integer                                        | Columns. Programs that emit their own presentation should honour this. |
| `TOSH_TERM_HEIGHT`          | integer                                        | Rows. |
| `TOSH_COLOR`                | `truecolor` \| `256` \| `none`                 | Pre-resolved colour capability. Programs SHOULD use this instead of sniffing `COLORTERM`/`TERM` themselves. |
| `TOSH_STDIN_ACCEPTS`        | comma list                                     | Set on the *downstream* command, mirrored in the producer's env: indicates what shapes the consumer can ingest (`records`, `text`, schema names). See §6. |

If `TOSH_STRUCTURED_STDOUT` is unset, the program MUST emit plain
output. Programs are free to ignore TSSP entirely; ToSh will treat
their stdout as text.

## 2. Wire format

A TSSP stream is a stdout byte stream beginning with a single **magic
header line** followed by zero or more length-prefixed **frames**.

### 2.1 Header

```
\x1bTOSHSTREAM\x1e<json-header>\n
```

- `\x1b` — ESC (0x1B)
- Literal ASCII `TOSHSTREAM`
- `\x1e` — record separator (0x1E)
- `<json-header>` — a single JSON object on one line
- Terminating LF

ESC + identifier is rare enough in plain stdout that ToSh can sniff
this in a single read without parsing every command's output. If the
first bytes do not match, ToSh treats the entire stream as plain text
— no further TSSP processing occurs.

The header JSON object MUST contain:

```jsonc
{
  "v": 1,                          // protocol version (integer)
  "schema": "crumb.package",       // namespaced schema id, see §5
  "modes": ["records", "presentation"]  // any of: records, presentation
}
```

Optional header fields:

```jsonc
{
  "renderer": "crumb.search",      // namespaced ToSh renderer hint, §4 Pattern C
  "producer": "crumb/0.1",         // free-form, for diagnostics
  "encoding": "utf-8"              // default is utf-8; only utf-8 is supported in v1
}
```

### 2.2 Frames

Each frame is:

```
\x1e<kind> <length>\n<payload>
```

- `\x1e` — record separator
- `<kind>` — ASCII kind name, see §2.3
- one ASCII space
- `<length>` — decimal byte count of `<payload>`
- LF
- `<payload>` — exactly `<length>` bytes

No trailing LF after the payload; the next frame's `\x1e` follows
immediately. Length-prefixing means `pres` payloads may contain any
bytes including ESC sequences and LFs, with no escaping required.

### 2.3 Frame kinds

| Kind        | Payload                                | Purpose |
|-------------|----------------------------------------|---------|
| `rec`       | one JSON object (no trailing LF)       | A structured record. |
| `meta`      | JSON object                            | Schema/column hints. May appear multiple times; later `meta` frames are merged shallowly over earlier ones. |
| `pres`      | raw bytes (typically ANSI text)        | Pre-rendered presentation chunk. ToSh writes verbatim to the TTY when the consumer is `terminal`; discarded otherwise. |
| `pres-end`  | empty (length 0)                       | Marks the presentation stream complete. Optional but recommended. |
| `err`       | JSON `{code, message, details?}`       | Structured diagnostic. ToSh routes to stderr-equivalent regardless of consumer. |
| `progress`  | JSON `{stage, current, total?, message?}` | Advisory progress. Rendered in a status area; never appears in piped records or captured output. |

Unknown kinds MUST be ignored by the consumer (forward-compat).

### 2.4 Stream termination

A TSSP stream ends when the producer closes stdout. ToSh treats EOF
as final. No explicit terminator frame is required.

## 3. Producer constraints

> **v1 implementation note.** ToSh v1 only consumes TSSP when stdout is
> redirected (i.e. `TOSH_STDOUT_CONSUMER` is `pipe` or `capture`). When
> stdout is the terminal, ToSh leaves the child process attached to the
> TTY and does not parse its output — this preserves interactive
> programs (TUIs, pagers, prompts) that need raw TTY access. Producers
> are still informed of the consumer via the env var, so they can
> choose between TSSP and pretty output themselves. Full terminal-mode
> `pres` rendering inside ToSh is deferred to a future version.

- Programs MUST flush after each `rec` frame in long-running streams
  so consumers can process records incrementally.
- Programs MUST NOT emit `pres` frames when `TOSH_STDOUT_CONSUMER` is
  `pipe` or `capture`. This is the only "convention" enforcement —
  see §6 for why this matters.
- Programs SHOULD emit `meta` frames before the first `rec` they
  describe, but ToSh MUST handle `meta` arriving later (and apply it
  retroactively to any default-rendered table that has not yet been
  flushed).
- Programs emitting `pres` for `terminal` SHOULD honour
  `TOSH_TERM_WIDTH`, `TOSH_TERM_HEIGHT`, and `TOSH_COLOR`.

## 4. Rendering patterns

Producers pick one of these based on their needs.

### Pattern A — built-in ToSh table

Emit only `rec` frames and (optionally) a `meta` frame describing the
columns. ToSh renders its standard structured-table when the consumer
is `terminal`. The cheapest and most portable option.

```jsonc
// meta payload
{
  "columns": [
    { "name": "Name",    "width": "auto", "align": "left",  "style": "role:strong" },
    { "name": "Version", "width": 12,     "align": "right" },
    { "name": "Repo",    "width": 8,      "style": "role:accent",
      "values": {
        "core":    { "style": "role:warning" },
        "aur":     { "style": "role:info" }
      } }
  ]
}
```

`meta.columns[*]` fields:

- `name` (required) — record field to project.
- `header` (optional) — display label; defaults to `name`.
- `width` — `"auto"`, integer (chars), `"fit-content"`, or
  `{"min": N, "max": M}`.
- `align` — `"left"` | `"right"` | `"center"`.
- `style` — ToSh theme role (`"role:strong"`, `"role:accent"`,
  `"role:warning"`, …) or a raw style spec (`"bold"`, `"fg:#ff5f00"`).
- `values` — map of cell-value → per-cell `{style}` overrides.
- `formatter` — namespaced name of a built-in formatter
  (`"bytes"`, `"duration"`, `"timestamp"`, `"semver"`, …).

Tool authors get nice output for free without rendering anything
themselves.

### Pattern B — fully custom presentation

Emit `rec` frames *and* a complete pre-rendered `pres` stream. When
the consumer is `terminal`, ToSh writes the `pres` bytes verbatim to
the TTY. When the consumer is `pipe`/`capture`, `pres` is discarded
and only records flow through.

This is the "completely custom table format" path: box-drawing
characters, multi-column layouts, sparklines, embedded Sixel/Kitty
graphics — anything you can put in a terminal. Same invocation,
record stream still works for downstream consumers.

```
\x1bTOSHSTREAM\x1e{"v":1,"schema":"crumb.package","modes":["records","presentation"]}\n
\x1epres 78
extra/dotnet-host 10.0.4 [installed]
    A generic driver for the .NET Core CLI
\x1erec 142
{"Name":"dotnet-host","Version":"10.0.4","Repo":"extra","Installed":true,...}
\x1epres 12

aur/dotnet…
\x1erec …
…
\x1epres-end 0
```

Interleaving `pres` with `rec` is allowed and useful for streaming.

### Pattern C — named renderer

Emit `rec` frames and put `"renderer": "crumb.search"` in the header
(or a `meta` frame). When the consumer is `terminal`, ToSh looks up
`$tosh.Config.Renderers["crumb.search"]` — a user- or
program-installed ToSh function — and invokes it with the record
stream as `$in`.

```tosh
$tosh.Config.Renderers["crumb.search"] = { |records|
    $records | each { ... custom rendering ... }
}
```

Lets end users override third-party tools' layouts without forking
them, and lets one program ship a default renderer that gets used in
all contexts. Renderer names MUST be namespaced (`producer.usage`) to
avoid collisions.

If a renderer is named but not registered, ToSh falls back to
Pattern A (built-in table using `meta` hints).

## 5. Schemas

The `schema` field in the header is a **namespaced identifier**
(`producer.shape`). It serves two purposes:

1. Downstream consumers can match on schema when deciding how to
   handle records (`where _.schema == "crumb.package"`).
2. ToSh can enrich completions/types when piping into builtins.

### 5.1 Schema declaration

A schema MAY be declared inline in a `meta` frame to allow lightweight
type hints:

```jsonc
{
  "schema": "crumb.package",
  "fields": {
    "Name":      { "type": "string" },
    "Version":   { "type": "string" },
    "Repo":      { "type": "string", "enum": ["core","extra","multilib","aur","local"] },
    "Votes":     { "type": "integer", "nullable": true },
    "Installed": { "type": "boolean" },
    "BuildDate": { "type": "timestamp" }
  }
}
```

Supported `type` values in v1: `string`, `integer`, `number`,
`boolean`, `timestamp`, `bytes`, `list<T>`, `record<T>`.

### 5.2 Schema registry

ToSh ships a registry of well-known schemas under
`$tosh.Config.Schemas`. Producers SHOULD register their schemas via
this registry (typically by installing a small Tosh module shipped
with the package). When a producer uses a registered schema, ToSh:

- Enables column-name completion downstream
  (`crumb -Ss x | where _.<TAB>`)
- Suggests field-type-appropriate operators
- Reuses any declared default renderer (Pattern C)

A producer that uses an *un*registered schema name still works —
records flow through unannotated.

The registry is just a dict; tooling reads it but never validates
strictly. Forward-compat over correctness.

## 6. Consumer routing

When a pipeline stage finishes, ToSh decides per-stream:

1. **Consumer is another command** (`producer | consumer`):
   - Set `TOSH_STDOUT_CONSUMER=pipe` on producer.
   - Set `TOSH_STDIN_ACCEPTS` on producer to reflect what the
     consumer declares it ingests (see §6.1).
   - Forward `rec` frames as a structured record stream.
   - Drop `pres` and `progress`.
   - Route `err` to stderr.

2. **Consumer is the terminal** (last stage of a foreground pipeline):
   - Set `TOSH_STDOUT_CONSUMER=terminal`.
   - If any `pres` frame was emitted → write `pres` payloads verbatim;
     ignore `rec` (the producer chose to render itself).
   - Else if a `renderer` was declared and is registered → invoke it
     with the record stream.
   - Else → render `rec` frames with the built-in table using `meta`
     hints (Pattern A).

3. **Consumer is variable capture** (`var x = (producer)` or `$(...)`):
   - Set `TOSH_STDOUT_CONSUMER=capture`.
   - Materialize `rec` frames as a list of records.
   - Drop `pres`/`progress`.

4. **`err` frames** always go to stderr-equivalent regardless of
   consumer routing.

### 6.1 Consumer declaration

ToSh builtins annotate their stdin acceptance in their command
metadata (`accepts: ["records", "text"]`). External commands declare
acceptance by reading `TOSH_STDIN_ACCEPTS`; an external consumer that
wants records SHOULD also speak TSSP, but ToSh will fall back to
serializing records as NDJSON on stdin if the consumer only sets
`TOSH_STDIN_ACCEPTS=ndjson`.

This is the bridge that lets external↔external pipes carry structure
without requiring both sides to be ToSh-native.

## 7. Error handling

- **Malformed header.** Treat the entire stream as plain text. No
  diagnostic. (The producer never opted in.)
- **Header parses but frame body is malformed** (bad length, invalid
  JSON, unknown required field): ToSh terminates structured parsing
  for that stream, flushes any records already accepted, and emits
  a recoverable diagnostic:
  `tosh.tssp.frame_error: <producer>: <reason>`. Subsequent bytes are
  *discarded* (not promoted to plain text) so a half-rendered stream
  doesn't corrupt the terminal.
- **Unknown kinds.** Ignore, advance past the payload using the
  declared length.
- **Unknown `meta` fields.** Ignore.
- **Version mismatch.** If header `v` is higher than ToSh's
  `TOSH_TSSP_VERSION`, ToSh falls back to plain text mode and emits
  a recoverable diagnostic suggesting an upgrade. Producers SHOULD
  check `TOSH_TSSP_VERSION` and emit a compatible version.

## 8. Examples

### 8.1 Crumb search, terminal consumer

Invocation: `crumb -Ss dotnet` (terminal is the consumer)

Crumb sees `TOSH_STDOUT_CONSUMER=terminal`, so:

```
\x1bTOSHSTREAM\x1e{"v":1,"schema":"crumb.package","modes":["records","presentation"],"renderer":"crumb.search"}\n
\x1epres 64
extra/dotnet-host 10.0.4 [installed]
    A generic driver for the .NET Core CLI
\x1erec 138
{"Name":"dotnet-host","Version":"10.0.4","Repo":"extra","Description":"A generic driver for the .NET Core CLI","Installed":true,…}
\x1epres 0
\x1epres-end 0
```

ToSh writes the `pres` bytes to the terminal; `rec` frames are
buffered for any potential `$(...)` capture but otherwise unused.

### 8.2 Crumb search, piped consumer

Invocation: `crumb -Ss dotnet | where Votes > 50 | sort-by Popularity desc`

Crumb sees `TOSH_STDOUT_CONSUMER=pipe`, so it emits **only** `rec` and
`meta` frames — no `pres`. ToSh forwards records to `where`, which
operates on the structured stream directly. No `from json`.

### 8.3 Crumb search, captured

Invocation: `var pkgs = (crumb -Ss dotnet)`

Crumb sees `TOSH_STDOUT_CONSUMER=capture`. Same as §8.2: only `rec`
frames. ToSh materializes them as `list<record>` and binds `$pkgs`.

### 8.4 Custom table (Pattern B)

A network tool wants a layout ToSh's table renderer can't produce —
say, a per-row inline sparkline. It emits `pres` chunks containing the
sparkline ANSI alongside `rec` frames with the raw datapoints. Terminal
gets the sparkline view; pipes get the datapoints.

## 9. Versioning

- Wire format version is integer `v` in the header.
- Additive changes (new optional frame kinds, new `meta` fields, new
  `type` values) do not bump `v`; consumers ignore unknowns.
- Breaking changes bump `v`. ToSh advertises the highest version it
  supports via `TOSH_TSSP_VERSION`; producers MUST emit a version
  ≤ that value.

## 10. Open items (post-v1)

- `stdin` TSSP for external→external structured piping (likely v1.1).
- A `pres-region` frame kind for terminal screen-region updates
  (overdraw without clearing).
- Binary record encoding (CBOR/MessagePack) for hot pipelines.
- Cross-producer schema imports
  (`$tosh.Config.Schemas.import("crumb")`).

## 11. Producing TSSP from .NET: `Tosh.Client`

`Tosh.Client` is a zero-dependency NuGet package (`net10.0`) that wraps
all of the above behind a small, ergonomic surface. External programs
written against it never touch the wire format directly.

```csharp
using Tosh.Client;

var host = ToshHost.Current;

// Plain stdout when not inside ToSh — never crashes outside hybrid mode.
if (!host.IsToshConsumer)
{
    Console.WriteLine("plain text fallback");
    return;
}

using var w = host.OpenFrameWriter(schema: "myapp.thing");
w.WriteMeta("""{"schema":"myapp.thing","fields":{"Name":{"type":"string"}}}""");
foreach (var item in items)
{
    w.WriteRecord(new { item.Name, item.Size });
}
```

Surface highlights:

| API                                       | Purpose |
|-------------------------------------------|---------|
| `ToshHost.Current.Info`                   | Cached envvar snapshot: `StdoutConsumer`, `StdioMode`, `TermWidth/Height`, `Color`, `ControllingTty`, `StdinAccepts`. |
| `ToshHost.Current.IsToshConsumer`         | `true` iff `TOSH_STRUCTURED_STDOUT=1` is set. |
| `ToshHost.Current.OpenFrameWriter(...)`   | Writes the TSSP header to stdout, returns a thread-safe `ToshFrameWriter`. |
| `ToshHost.Current.Status.WriteLine(...)`  | Status text routed to `/dev/tty` (falls back to stderr). Won't pollute a downstream pipe. |
| `ToshHost.Current.Prompt.YesNo(q, def)`   | Interactive yes/no, read directly from `/dev/tty`. |
| `Tosh.Client.ChildTtyScope.Acquire()`     | Wrap a child-process spawn that needs to drive the terminal directly (sudo, editors, builders). Restores fds on dispose. |

`ToshFrameWriter` is thread-safe (single internal lock) and disposable;
disposing it flushes pending writes and (by default) leaves the
underlying stream open so callers can interleave their own writes if
they really want to.

### Hybrid spawn opt-in

ToSh only parses TSSP from external programs that have explicitly opted
into **hybrid spawn mode** by name:

```tosh
$tosh.Config.External.HybridConsumers.Add("myapp")
```

First-party TōSh tooling (`crumb`) is seeded by default. Bare program
name only — no path, no extension. Unknown programs spawn in
**passthrough** mode (all fds inherit the tty), which is the right
choice for anything that isn't TSSP-aware.
