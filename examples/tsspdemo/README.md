# tsspdemo

A minimal external-program TSSP consumer built on `Tosh.Client`. About
30 lines of code total — emits 5 records under a `tsspdemo.entry`
schema, falls back to plain text outside TōSh.

## Build

```bash
dotnet build examples/tsspdemo -c Release
```

## Run

First register the program for hybrid spawn (one-time, in your
`config.tosh` or interactively):

```tosh
$tosh.Config.External.HybridConsumers.Add("tsspdemo")
```

Then:

```tosh
# Pretty native table:
./examples/tsspdemo/bin/Release/net10.0/tsspdemo

# Structured pipeline — records are the same shape ToSh sees from any
# native producer, so you can filter and sort as usual.
./examples/tsspdemo/bin/Release/net10.0/tsspdemo | where Size > 500 | sort-by Size

# Outside ToSh — same binary prints a plain-text fallback.
TOSH_STRUCTURED_STDOUT= ./examples/tsspdemo/bin/Release/net10.0/tsspdemo
```

See [docs/TSSP.md](../../docs/TSSP.md) for the full protocol spec and
the `Tosh.Client` surface reference.
