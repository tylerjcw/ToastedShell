// tsspdemo — minimal Tosh.Client consumer.
//
// Run inside ToSh, after registering the program in the hybrid list:
//
//   $tosh.Config.External.HybridConsumers.Add("tsspdemo")
//   /path/to/tsspdemo
//   /path/to/tsspdemo | where Size > 500 | sort-by Size
//
// Outside ToSh (plain shell, no TOSH_STRUCTURED_STDOUT in env) it falls
// back to a human-readable table so the same binary stays useful.

using Tosh.Client;

var rows = new[]
{
    new Row("README.md",       1842, "doc"),
    new Row("LICENSE",         1071, "doc"),
    new Row("src/Tosh.Client",  512, "code"),
    new Row("docs/TSSP.md",   18324, "doc"),
    new Row("Tosh.slnx",       6201, "config"),
};

var host = ToshHost.Current;

// Status messages always route through /dev/tty when available — they
// never land in a downstream pipe's data stream.
host.Status.InfoLine($"tsspdemo: emitting {rows.Length} rows (consumer={host.Info.StdoutConsumer})");

if (!host.IsToshConsumer)
{
    // Plain-text fallback for non-ToSh shells.
    Console.WriteLine($"{"Name",-22} {"Size",6}  Kind");
    foreach (var r in rows) Console.WriteLine($"{r.Name,-22} {r.Size,6}  {r.Kind}");
    return 0;
}

using var w = host.OpenFrameWriter(schema: "tsspdemo.entry");
w.WriteMeta("""
{"schema":"tsspdemo.entry","title":"{Name}","fields":{"Name":{"type":"string"},"Size":{"type":"integer"},"Kind":{"type":"string","enum":["code","doc","config"]}}}
""");

// Phase 1: progress-only updates overwrite in place (single status line on /dev/tty).
for (var i = 0; i < rows.Length; i++)
{
    w.WriteProgress(message: $"scanning {rows[i].Name}", current: i + 1, total: rows.Length);
    Thread.Sleep(40);
}

// Phase 2: emit the records. ToSh will finalize the progress line with a newline
// before the first record renders.
foreach (var r in rows) w.WriteRecord(r);

return 0;

internal sealed record Row(string Name, int Size, string Kind);
