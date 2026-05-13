using System.Text;
using Tosh.Crumb.Models;
using Tosh.Crumb.Output;
using Tosh.Stdlib.Tssp;

namespace Tosh.Tests;

public class CrumbTsspMetaFrameTests
{
    private static Package SamplePackage() => new()
    {
        Name = "ripgrep",
        Version = "14.1.0-1",
        Repo = "extra",
        Description = "fast recursive grep",
        Installed = false,
    };

    [Fact]
    public void WriteTssp_EmitsHeaderThenMetaThenRecords()
    {
        using var mem = new MemoryStream();
        var count = PackageFormatter.WriteTssp(mem, new[] { SamplePackage(), SamplePackage() with { Name = "fd" } });

        Assert.Equal(2, count);
        var bytes = mem.ToArray();
        var asString = Encoding.UTF8.GetString(bytes);

        Assert.StartsWith("\x1bTOSHSTREAM\x1e", asString);
        var metaIdx = asString.IndexOf("\x1emeta ", StringComparison.Ordinal);
        var recIdx = asString.IndexOf("\x1erec ", StringComparison.Ordinal);
        Assert.True(metaIdx > 0, "expected a meta frame");
        Assert.True(recIdx > metaIdx, "meta frame must precede rec frames");
    }

    [Fact]
    public async Task TsspParser_RoundTripsMetaFrameWithSchemaFields()
    {
        using var mem = new MemoryStream();
        PackageFormatter.WriteTssp(mem, new[] { SamplePackage() });
        mem.Position = 0;

        var parser = new TsspParser(mem);
        var header = await parser.TryReadHeaderAsync(CancellationToken.None);
        Assert.NotNull(header);
        Assert.Equal(1, header!.Version);
        Assert.Equal("crumb.package", header.Schema);

        TsspFrame? meta = null;
        TsspFrame? rec = null;
        await foreach (var frame in parser.ReadFramesAsync())
        {
            if (frame.Kind == "meta" && meta is null) meta = frame;
            else if (frame.Kind == "rec" && rec is null) rec = frame;
        }

        Assert.NotNull(meta);
        Assert.NotNull(rec);

        // Meta payload should be a JSON object with "schema" and a "fields" block.
        using var doc = System.Text.Json.JsonDocument.Parse(meta!.Payload);
        Assert.Equal("crumb.package", doc.RootElement.GetProperty("schema").GetString());
        var fields = doc.RootElement.GetProperty("fields");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, fields.ValueKind);
        Assert.True(fields.TryGetProperty("Repo", out var repoField));
        Assert.Equal("string", repoField.GetProperty("type").GetString());
        var repoEnum = repoField.GetProperty("enum");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, repoEnum.ValueKind);
    }
}
