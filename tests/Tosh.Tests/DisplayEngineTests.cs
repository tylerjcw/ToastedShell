using Tosh.Runtime;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

namespace Tosh.Tests;

public sealed class DisplayEngineTests
{
    [Fact]
    public void Display_engine_renders_history_with_explicit_shell_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new CommandHistoryEntry(1, "help", new DateTimeOffset(2026, 3, 23, 9, 15, 0, TimeSpan.Zero)),
            new CommandHistoryEntry(2, "ls -la", new DateTimeOffset(2026, 3, 23, 9, 16, 0, TimeSpan.Zero)),
        };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("╰", text, StringComparison.Ordinal);
        Assert.Contains("│ # ", text, StringComparison.Ordinal);
        Assert.Contains("Id", text, StringComparison.Ordinal);
        Assert.Contains("Text", text, StringComparison.Ordinal);
        Assert.Contains("When", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Timestamp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_drops_low_priority_columns_when_terminal_is_narrow()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePathA = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        var filePathB = System.IO.Path.Combine(tempDirectory.Path, "beta.txt");
        File.WriteAllText(filePathA, "alpha");
        File.WriteAllText(filePathB, "beta");

        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            FileSystemEntry.From(new FileInfo(filePathA), preferLongDisplay: true),
            FileSystemEntry.From(new FileInfo(filePathB), preferLongDisplay: true),
        };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 34));

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Type", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Modified", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_structured_values_as_record_tables()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePath = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        File.WriteAllText(filePath, "alpha");

        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            FileSystemEntry.From(new FileInfo(filePath), preferLongDisplay: true),
        };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("│ Name", text, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("│ # ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("├", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_applies_transient_column_selection_without_projecting_objects()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePath = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        File.WriteAllText(filePath, "alpha");

        var runtime = ToshRuntime.CreateDefault();
        var entry = FileSystemEntry.From(new FileInfo(filePath), preferLongDisplay: false);
        runtime.RegisterDisplaySelection(entry, new DisplayColumnSelection(showColumns: ["Name", "FullName"]));

        var text = runtime.Display.RenderMany(
            [entry],
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("FullName", text, StringComparison.Ordinal);
        Assert.Contains(filePath, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Modified", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Type", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_native_filesystem_infos_as_a_shared_shell_table()
    {
        using var tempDirectory = new TemporaryDirectory();
        var filePath = System.IO.Path.Combine(tempDirectory.Path, "alpha.txt");
        var nestedPath = System.IO.Path.Combine(tempDirectory.Path, "nested");
        File.WriteAllText(filePath, "alpha");
        Directory.CreateDirectory(nestedPath);

        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new FileInfo(filePath),
            new DirectoryInfo(nestedPath),
        };

        var text = display.RenderMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Type", text, StringComparison.Ordinal);
        Assert.Contains("Modified", text, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", text, StringComparison.Ordinal);
        Assert.Contains("nested/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.FileInfo]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.DirectoryInfo]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_scalar_lists_as_indexed_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[] { "alpha", "beta" };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("│ 0 │ alpha │", text, StringComparison.Ordinal);
        Assert.Contains("│ 1 │ beta  │", text, StringComparison.Ordinal);
        Assert.DoesNotContain("├", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_table_values_with_dynamic_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> first = new System.Dynamic.ExpandoObject();
        first["Name"] = "alpha";
        first["Size"] = StorageSize.FromBytes(1024);
        IDictionary<string, object?> second = new System.Dynamic.ExpandoObject();
        second["Name"] = "beta";
        second["Size"] = StorageSize.FromBytes(2048);
        var values = new object?[] { first, second };

        var text = display.RenderMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains("2 kB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_column_summaries_with_only_requested_aggregate_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new ColumnSummary { Column = "Size", RowCount = 3, ValueCount = 3, Sum = StorageSize.FromBytes(4096), Count = 3 },
            new ColumnSummary { Column = "Used", RowCount = 3, ValueCount = 2, Sum = 7L, Count = 2 },
        };

        var text = display.RenderMany(values);

        Assert.Contains("Column", text, StringComparison.Ordinal);
        Assert.Contains("Rows", text, StringComparison.Ordinal);
        Assert.Contains("Values", text, StringComparison.Ordinal);
        Assert.Contains("Count", text, StringComparison.Ordinal);
        Assert.Contains("Sum", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Average", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Min", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Max", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_multiline_cells_in_generic_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> first = new System.Dynamic.ExpandoObject();
        first["Name"] = "alpha";
        first["Text"] = "Hello\nThere!";
        IDictionary<string, object?> second = new System.Dynamic.ExpandoObject();
        second["Name"] = "beta";
        second["Text"] = "General";

        var text = display.RenderMany([first, second]);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Text", text, StringComparison.Ordinal);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("There!", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello\nThere!", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_expando_records_with_dynamic_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> first = new System.Dynamic.ExpandoObject();
        first["Name"] = "alpha";
        first["Size"] = StorageSize.FromBytes(1024);
        IDictionary<string, object?> second = new System.Dynamic.ExpandoObject();
        second["Name"] = "beta";
        second["Size"] = StorageSize.FromBytes(2048);
        var values = new object?[]
        {
            first,
            second,
        };

        var text = display.RenderMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains("2 kB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_expando_record_as_record_table()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> person = new System.Dynamic.ExpandoObject();
        person["Name"] = "komrad";
        person["Uid"] = 1000;

        var text = display.RenderMany([person]);

        Assert.Contains("│ Name", text, StringComparison.Ordinal);
        Assert.Contains("komrad", text, StringComparison.Ordinal);
        Assert.DoesNotContain("│ # │", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Key", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_native_drive_info_as_a_record_table()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var root = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)
                   ?? throw new InvalidOperationException("Unable to determine the current drive root.");

        var text = display.RenderMany([new DriveInfo(root)]);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("DriveType", text, StringComparison.Ordinal);
        Assert.Contains(root, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_native_process_as_a_record_table()
    {
        using var process = Process.GetCurrentProcess();
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([process]);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Id", text, StringComparison.Ordinal);
        Assert.Contains(process.Id.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_nested_expando_values_inline_in_record_cells()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> name = new System.Dynamic.ExpandoObject();
        name["First"] = "Komrad";
        name["Last"] = "Toast";

        IDictionary<string, object?> person = new System.Dynamic.ExpandoObject();
        person["Name"] = name;

        var text = display.RenderMany([person]);

        Assert.Contains("First", text, StringComparison.Ordinal);
        Assert.Contains("Last", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<ExpandoObject>", text, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 2);
    }

    [Fact]
    public void Display_engine_renders_single_color_values_with_their_display_profile()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([Color.GreenYellow]);

        Assert.Contains("Sample", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Hex", text, StringComparison.Ordinal);
        Assert.Contains("GreenYellow", text, StringComparison.Ordinal);
        Assert.Contains("#ADFF2F", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;173;255;47m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_enum_values_as_titled_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([DayOfWeek.Friday]);

        Assert.Contains("System.DayOfWeek.Friday", text, StringComparison.Ordinal);
        Assert.Contains("│ 5 │ Friday", text, StringComparison.Ordinal);
        Assert.DoesNotContain("│ # │", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_top_level_scalars_as_compact_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var numberText = display.RenderMany([1317]);
        var stringText = display.RenderMany(["toast"]);
        var multilineText = display.RenderMany(["Hello\nThere!"]);

        Assert.Contains("Int32", numberText, StringComparison.Ordinal);
        Assert.Contains("1,317", numberText, StringComparison.Ordinal);
        Assert.Contains("String", stringText, StringComparison.Ordinal);
        Assert.Contains("toast", stringText, StringComparison.Ordinal);
        Assert.Contains("│ String │ Hello", multilineText, StringComparison.Ordinal);
        Assert.Contains("│        │ There!", multilineText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_vectors_as_compact_scalar_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([new ToshVector([1d, 2d, 3d])]);

        Assert.Contains("Vector", text, StringComparison.Ordinal);
        Assert.Contains("[1, 2, 3]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Magnitude", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Length", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_native_matrices_as_matrix_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([new ToshMatrix([[1d, 2d], [3d, 4d]])]);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ 1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            4 │", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("RowCount", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_complex_numbers_as_compact_scalar_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([new Complex(3d, 4d)]);

        Assert.Contains("Complex", text, StringComparison.Ordinal);
        Assert.Contains("3 + 4i", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Imaginary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Real", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_profile_backed_shell_scalars_as_compact_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var storageSizeText = display.RenderMany([StorageSize.FromBytes(1317)]);
        var timeSpanText = display.RenderMany([TimeSpan.FromHours(2)]);
        var amountText = display.RenderMany([TemporalAmount.FromTimeSpan(TimeSpan.FromHours(2))]);
        var entryTypeText = display.RenderMany([FileSystemEntryType.File]);
        var resolutionKindText = display.RenderMany([CommandResolutionKind.Function]);

        Assert.Contains("StorageSize", storageSizeText, StringComparison.Ordinal);
        Assert.Contains("1.3 kB", storageSizeText, StringComparison.Ordinal);
        Assert.Contains("TimeSpan", timeSpanText, StringComparison.Ordinal);
        Assert.Contains("2 hours", timeSpanText, StringComparison.Ordinal);
        Assert.Contains("TemporalAmount", amountText, StringComparison.Ordinal);
        Assert.Contains("2 hours", amountText, StringComparison.Ordinal);
        Assert.Contains("FileSystemEntryType", entryTypeText, StringComparison.Ordinal);
        Assert.Contains("file", entryTypeText, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemEntryType.File", entryTypeText, StringComparison.Ordinal);
        Assert.Contains("CommandResolutionKind", resolutionKindText, StringComparison.Ordinal);
        Assert.Contains("function", resolutionKindText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_biginteger_values_as_scalar_numbers()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([BigInteger.Parse("218922995834555169026", CultureInfo.InvariantCulture)]);

        Assert.Contains("BigInteger", text, StringComparison.Ordinal);
        Assert.Contains("218,922,995,834,555,169,026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEven", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_keeps_mixed_numeric_sequences_as_scalar_lists()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany(
        [
            1,
            1,
            2,
            BigInteger.Parse("218922995834555169026", CultureInfo.InvariantCulture),
            BigInteger.Parse("354224848179261915075", CultureInfo.InvariantCulture),
        ]);

        Assert.DoesNotContain("[Int32]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[BigInteger]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[number]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEven", text, StringComparison.Ordinal);
        Assert.Contains("218922995834555169026", text, StringComparison.Ordinal);
        Assert.Contains("354224848179261915075", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_detailed_single_value_records_for_inspectable_scalars()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var modeText = display.RenderMany([(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.OtherRead)]);
        var attributesText = display.RenderMany([(System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.Archive)]);
        var principalText = display.RenderMany([new FileSystemPrincipalInfo(1000, "komrad")]);
        var addressText = display.RenderMany([System.Net.IPAddress.Parse("127.0.0.1")]);

        Assert.Contains("UnixFileMode", modeText, StringComparison.Ordinal);
        Assert.Contains("Mode", modeText, StringComparison.Ordinal);
        Assert.Contains("UserRead", modeText, StringComparison.Ordinal);
        Assert.Contains("true", modeText, StringComparison.Ordinal);
        Assert.Contains("false", modeText, StringComparison.Ordinal);

        Assert.Contains("FileAttributes", attributesText, StringComparison.Ordinal);
        Assert.Contains("ReadOnly", attributesText, StringComparison.Ordinal);
        Assert.Contains("Archive", attributesText, StringComparison.Ordinal);

        Assert.Contains("FileSystemPrincipalInfo", principalText, StringComparison.Ordinal);
        Assert.Contains("DisplayName", principalText, StringComparison.Ordinal);
        Assert.Contains("komrad", principalText, StringComparison.Ordinal);

        Assert.Contains("IPAddress", addressText, StringComparison.Ordinal);
        Assert.Contains("Address", addressText, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", addressText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_additional_basic_clr_types_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var dateOnlyText = display.RenderMany([new DateOnly(2026, 3, 29)]);
        var timeOnlyText = display.RenderMany([new TimeOnly(3, 31, 42)]);
        var guidText = display.RenderMany([Guid.Parse("550e8400-e29b-41d4-a716-446655440000")]);
        var versionText = display.RenderMany([new Version(1, 2, 3, 4)]);
        var bytesText = display.RenderMany([new byte[] { 0x48, 0x69 }]);
        var uriText = display.RenderMany([new Uri("https://example.com/docs?q=1#frag")]);
        var regexText = display.RenderMany([new Regex("^alpha$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))]);
        var zoneText = display.RenderMany([TimeZoneInfo.Utc]);
        var cultureText = display.RenderMany([new CultureInfo("fr-FR")]);
        var encodingText = display.RenderMany([Encoding.UTF8]);
        var exception = new InvalidOperationException("kaboom") { Source = "tosh" };
        var exceptionText = display.RenderMany([exception]);

        Assert.Contains("DateOnly", dateOnlyText, StringComparison.Ordinal);
        Assert.Contains("Sunday, March 29, 2026", dateOnlyText, StringComparison.Ordinal);

        Assert.Contains("TimeOnly", timeOnlyText, StringComparison.Ordinal);
        Assert.Contains("3:31:42 AM", timeOnlyText, StringComparison.Ordinal);

        Assert.Contains("Guid", guidText, StringComparison.Ordinal);
        Assert.Contains("Version", guidText, StringComparison.Ordinal);
        Assert.Contains("RFC 4122", guidText, StringComparison.Ordinal);

        Assert.Contains("Version", versionText, StringComparison.Ordinal);
        Assert.Contains("Major", versionText, StringComparison.Ordinal);
        Assert.Contains("Revision", versionText, StringComparison.Ordinal);

        Assert.Contains("Byte[]", bytesText, StringComparison.Ordinal);
        Assert.Contains("Length", bytesText, StringComparison.Ordinal);
        Assert.Contains("48 69", bytesText, StringComparison.Ordinal);
        Assert.Contains("Hi", bytesText, StringComparison.Ordinal);

        Assert.Contains("Uri", uriText, StringComparison.Ordinal);
        Assert.Contains("Scheme", uriText, StringComparison.Ordinal);
        Assert.Contains("example.com", uriText, StringComparison.Ordinal);
        Assert.Contains("\x1b]8;;https://example.com/docs?q=1#frag\x1b\\", uriText, StringComparison.Ordinal);

        Assert.Contains("Regex", regexText, StringComparison.Ordinal);
        Assert.Contains("^alpha$", regexText, StringComparison.Ordinal);
        Assert.Contains("IgnoreCase", regexText, StringComparison.Ordinal);

        Assert.Contains("TimeZoneInfo", zoneText, StringComparison.Ordinal);
        Assert.Contains("BaseUtcOffset", zoneText, StringComparison.Ordinal);
        Assert.Contains("UTC", zoneText, StringComparison.Ordinal);

        Assert.Contains("CultureInfo", cultureText, StringComparison.Ordinal);
        Assert.Contains("fr-FR", cultureText, StringComparison.Ordinal);
        Assert.Contains("French (France)", cultureText, StringComparison.Ordinal);

        Assert.Contains("UTF8Encoding", encodingText, StringComparison.Ordinal);
        Assert.Contains("utf-8", encodingText, StringComparison.Ordinal);
        Assert.Contains("CodePage", encodingText, StringComparison.Ordinal);

        Assert.Contains("InvalidOperationException", exceptionText, StringComparison.Ordinal);
        Assert.Contains("Message", exceptionText, StringComparison.Ordinal);
        Assert.Contains("kaboom", exceptionText, StringComparison.Ordinal);
        Assert.Contains("Source", exceptionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_uris_as_clickable_terminal_links()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([new Uri("https://example.com/docs")]);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("\x1b]8;;https://example.com/docs\x1b\\", text, StringComparison.Ordinal);
        Assert.Contains("https://example.com/docs", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_structured_runtime_types_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var pair = new KeyValuePair<string, int>("alpha", 42);
        var tuple = (1, "beta", true);
        var index = new Index(3, fromEnd: true);
        var range = new Range(new Index(1), new Index(1, fromEnd: true));
        EndPoint ipEndPoint = new IPEndPoint(IPAddress.Loopback, 8080);
        EndPoint dnsEndPoint = new DnsEndPoint("example.com", 443);
        MethodInfo method = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        PropertyInfo property = typeof(string).GetProperty(nameof(string.Length))!;
        ConstructorInfo constructor = typeof(StringBuilder).GetConstructor([typeof(string)])!;
        var trace = new StackTrace();
        var frame = trace.GetFrame(0) ?? new StackFrame();

        var pairText = display.RenderMany([pair]);
        var tupleText = display.RenderMany([tuple]);
        var indexText = display.RenderMany([index]);
        var rangeText = display.RenderMany([range]);
        var ipEndPointText = display.RenderMany([ipEndPoint]);
        var dnsEndPointText = display.RenderMany([dnsEndPoint]);
        var methodText = display.RenderMany([method]);
        var propertyText = display.RenderMany([property]);
        var constructorText = display.RenderMany([constructor]);
        var frameText = display.RenderMany([frame]);
        var traceText = display.RenderMany([trace]);

        Assert.Contains("KeyValuePair", pairText, StringComparison.Ordinal);
        Assert.Contains("Key", pairText, StringComparison.Ordinal);
        Assert.Contains("Value", pairText, StringComparison.Ordinal);
        Assert.Contains("alpha", pairText, StringComparison.Ordinal);
        Assert.Contains("42", pairText, StringComparison.Ordinal);

        Assert.Contains("ValueTuple", tupleText, StringComparison.Ordinal);
        Assert.Contains("Item1", tupleText, StringComparison.Ordinal);
        Assert.Contains("Item2", tupleText, StringComparison.Ordinal);
        Assert.Contains("Item3", tupleText, StringComparison.Ordinal);
        Assert.Contains("beta", tupleText, StringComparison.Ordinal);

        Assert.Contains("Index", indexText, StringComparison.Ordinal);
        Assert.Contains("^3", indexText, StringComparison.Ordinal);
        Assert.Contains("FromEnd", indexText, StringComparison.Ordinal);

        Assert.Contains("Range", rangeText, StringComparison.Ordinal);
        Assert.Contains("1..^1", rangeText, StringComparison.Ordinal);
        Assert.Contains("Start", rangeText, StringComparison.Ordinal);
        Assert.Contains("End", rangeText, StringComparison.Ordinal);

        Assert.Contains("IPEndPoint", ipEndPointText, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:8080", ipEndPointText, StringComparison.Ordinal);
        Assert.Contains("Family", ipEndPointText, StringComparison.Ordinal);

        Assert.Contains("DnsEndPoint", dnsEndPointText, StringComparison.Ordinal);
        Assert.Contains("example.com:443", dnsEndPointText, StringComparison.Ordinal);
        Assert.Contains("Host", dnsEndPointText, StringComparison.Ordinal);

        Assert.Contains("MethodInfo", methodText, StringComparison.Ordinal);
        Assert.Contains("StartsWith", methodText, StringComparison.Ordinal);
        Assert.Contains("Signature", methodText, StringComparison.Ordinal);

        Assert.Contains("PropertyInfo", propertyText, StringComparison.Ordinal);
        Assert.Contains("Length", propertyText, StringComparison.Ordinal);
        Assert.Contains("PropertyType", propertyText, StringComparison.Ordinal);

        Assert.Contains("ConstructorInfo", constructorText, StringComparison.Ordinal);
        Assert.Contains("StringBuilder", constructorText, StringComparison.Ordinal);
        Assert.Contains("ParameterCount", constructorText, StringComparison.Ordinal);

        Assert.Contains("StackFrame", frameText, StringComparison.Ordinal);
        Assert.Contains("Method", frameText, StringComparison.Ordinal);

        Assert.Contains("StackTrace", traceText, StringComparison.Ordinal);
        Assert.Contains("FrameCount", traceText, StringComparison.Ordinal);
        Assert.Contains("Frames", traceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_next_batch_of_clr_types_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var entry = new System.Collections.DictionaryEntry("alpha", 42);
        var assemblyName = new AssemblyName("System.Text.Json, Version=10.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
        var typeValue = typeof(List<int>);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/docs?q=1");
        request.Headers.Add("X-Test", "alpha");
        request.Content = new StringContent("hello");
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("world"),
        };
        response.Headers.Add("X-Trace", "beta");

        var entryText = display.RenderMany([entry]);
        var assemblyText = display.RenderMany([assemblyName]);
        var typeText = display.RenderMany([typeValue]);
        var requestText = display.RenderMany([request]);
        var responseText = display.RenderMany([response]);

        Assert.Contains("DictionaryEntry", entryText, StringComparison.Ordinal);
        Assert.Contains("Key", entryText, StringComparison.Ordinal);
        Assert.Contains("Value", entryText, StringComparison.Ordinal);
        Assert.Contains("alpha", entryText, StringComparison.Ordinal);
        Assert.Contains("42", entryText, StringComparison.Ordinal);

        Assert.Contains("AssemblyName", assemblyText, StringComparison.Ordinal);
        Assert.Contains("System.Text.Json", assemblyText, StringComparison.Ordinal);
        Assert.Contains("PublicKeyToken", assemblyText, StringComparison.Ordinal);
        Assert.Contains("Version", assemblyText, StringComparison.Ordinal);

        Assert.Contains("Type", typeText, StringComparison.Ordinal);
        Assert.Contains("List", typeText, StringComparison.Ordinal);
        Assert.Contains("FullName", typeText, StringComparison.Ordinal);
        Assert.Contains("Assembly", typeText, StringComparison.Ordinal);

        Assert.Contains("HttpRequestMessage", requestText, StringComparison.Ordinal);
        Assert.Contains("Method", requestText, StringComparison.Ordinal);
        Assert.Contains("GET", requestText, StringComparison.Ordinal);
        Assert.Contains("https://example.com/docs?q=1", requestText, StringComparison.Ordinal);
        Assert.Contains("X-Test: alpha", requestText, StringComparison.Ordinal);

        Assert.Contains("HttpResponseMessage", responseText, StringComparison.Ordinal);
        Assert.Contains("Status", responseText, StringComparison.Ordinal);
        Assert.Contains("200 OK", responseText, StringComparison.Ordinal);
        Assert.Contains("X-Trace: beta", responseText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_follow_up_batch_of_runtime_types_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var assembly = typeof(string).Assembly;
        var field = typeof(DisplayReflectionDemo).GetField(nameof(DisplayReflectionDemo.Counter))!;
        var eventInfo = typeof(DisplayReflectionDemo).GetEvent(nameof(DisplayReflectionDemo.Changed))!;
        var parameter = typeof(DisplayReflectionDemo).GetMethod(nameof(DisplayReflectionDemo.Sample))!.GetParameters()[0];
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api");
        request.Headers.Add("X-Test", "alpha");
        using var content = new StringContent("hello");

        var assemblyText = display.RenderMany([assembly]);
        var fieldText = display.RenderMany([field]);
        var eventText = display.RenderMany([eventInfo]);
        var parameterText = display.RenderMany([parameter]);
        var headersText = display.RenderMany([request.Headers]);
        var contentText = display.RenderMany([content]);

        Assert.Contains("Assembly", assemblyText, StringComparison.Ordinal);
        Assert.Contains("System.Private.CoreLib", assemblyText, StringComparison.Ordinal);
        Assert.Contains("DefinedTypes", assemblyText, StringComparison.Ordinal);

        Assert.Contains("FieldInfo", fieldText, StringComparison.Ordinal);
        Assert.Contains("Counter", fieldText, StringComparison.Ordinal);
        Assert.Contains("FieldType", fieldText, StringComparison.Ordinal);

        Assert.Contains("EventInfo", eventText, StringComparison.Ordinal);
        Assert.Contains("Changed", eventText, StringComparison.Ordinal);
        Assert.Contains("HandlerType", eventText, StringComparison.Ordinal);

        Assert.Contains("ParameterInfo", parameterText, StringComparison.Ordinal);
        Assert.Contains("name", parameterText, StringComparison.Ordinal);
        Assert.Contains("ParameterType", parameterText, StringComparison.Ordinal);

        Assert.Contains("HttpRequestHeaders", headersText, StringComparison.Ordinal);
        Assert.Contains("Count", headersText, StringComparison.Ordinal);
        Assert.Contains("X-Test: alpha", headersText, StringComparison.Ordinal);

        Assert.Contains("StringContent", contentText, StringComparison.Ordinal);
        Assert.Contains("ContentLength", contentText, StringComparison.Ordinal);
        Assert.Contains("Headers", contentText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_http_request_definition_and_response_info_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var request = new HttpRequestDefinition(
            "POST",
            new Uri("https://example.com/api/items"),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = ["application/json"],
                ["X-Test"] = ["alpha"],
            },
            Encoding.UTF8.GetBytes("""{"Name":"Toast"}"""),
            "application/json; charset=utf-8",
            TimeSpan.FromSeconds(5),
            followRedirects: false,
            bodyKind: "json",
            bodyPreview: """{"Name":"Toast"}""");
        var response = new HttpResponseInfo(
            201,
            "Created",
            true,
            "POST",
            request.RequestUri,
            new Uri("https://example.com/api/items/42"),
            "1.1",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Reply"] = ["beta"],
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = ["application/json; charset=utf-8"],
            },
            "application/json; charset=utf-8",
            11,
            TimeSpan.FromMilliseconds(125),
            ShellRecordUtilities.CreateExpando([new KeyValuePair<string, object?>("ok", true)]),
            "/tmp/response.json");

        var requestText = display.RenderMany([request]);
        var responseText = display.RenderMany([response]);

        Assert.Contains("Method", requestText, StringComparison.Ordinal);
        Assert.Contains("POST", requestText, StringComparison.Ordinal);
        Assert.Contains("https://example.com/api/items", requestText, StringComparison.Ordinal);
        Assert.Contains("FollowRedirects", requestText, StringComparison.Ordinal);
        Assert.Contains("X-Test: alpha", requestText, StringComparison.Ordinal);
        Assert.Contains("BodyKind", requestText, StringComparison.Ordinal);

        Assert.Contains("201 Created", responseText, StringComparison.Ordinal);
        Assert.Contains("FinalUri", responseText, StringComparison.Ordinal);
        Assert.Contains("https://example.com/api/items/42", responseText, StringComparison.Ordinal);
        Assert.Contains("X-Reply: beta", responseText, StringComparison.Ordinal);
        Assert.Contains("/tmp/response.json", responseText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_cookie_process_and_http_header_batches_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var cookie = new Cookie("sid", "abc123", "/", "example.com")
        {
            HttpOnly = true,
            Secure = true,
        };
        var cookies = new CookieCollection();
        cookies.Add(cookie);
        var loadContext = AssemblyLoadContext.Default;
        var startInfo = new ProcessStartInfo("git", "status")
        {
            WorkingDirectory = "/tmp",
            RedirectStandardOutput = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/docs");
        request.Headers.Host = "example.com";
        request.Headers.UserAgent.ParseAdd("ToSh/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Server.ParseAdd("ToSh/1.0");
        response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
        using var content = new StringContent("hello");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline");
        content.Headers.ContentEncoding.Add("utf-8");

        var cookieText = display.RenderMany([cookie]);
        var cookiesText = display.RenderMany([cookies]);
        var loadContextText = display.RenderMany([loadContext]);
        var startInfoText = display.RenderMany([startInfo]);
        var requestHeadersText = display.RenderMany([request.Headers]);
        var responseHeadersText = display.RenderMany([response.Headers]);
        var contentHeadersText = display.RenderMany([content.Headers]);

        Assert.Contains("Cookie", cookieText, StringComparison.Ordinal);
        Assert.Contains("sid", cookieText, StringComparison.Ordinal);
        Assert.Contains("abc123", cookieText, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", cookieText, StringComparison.Ordinal);

        Assert.Contains("CookieCollection", cookiesText, StringComparison.Ordinal);
        Assert.Contains("Count", cookiesText, StringComparison.Ordinal);
        Assert.Contains("sid=abc123", cookiesText, StringComparison.Ordinal);

        Assert.Contains("AssemblyLoadContext", loadContextText, StringComparison.Ordinal);
        Assert.Contains("LoadedAssemblies", loadContextText, StringComparison.Ordinal);
        Assert.Contains("System.Private.CoreLib", loadContextText, StringComparison.Ordinal);

        Assert.Contains("ProcessStartInfo", startInfoText, StringComparison.Ordinal);
        Assert.Contains("git", startInfoText, StringComparison.Ordinal);
        Assert.Contains("Arguments", startInfoText, StringComparison.Ordinal);
        Assert.Contains("RedirectStdOut", startInfoText, StringComparison.Ordinal);

        Assert.Contains("HttpRequestHeaders", requestHeadersText, StringComparison.Ordinal);
        Assert.Contains("Host", requestHeadersText, StringComparison.Ordinal);
        Assert.Contains("ToSh/1.0", requestHeadersText, StringComparison.Ordinal);
        Assert.Contains("application/json", requestHeadersText, StringComparison.Ordinal);

        Assert.Contains("HttpResponseHeaders", responseHeadersText, StringComparison.Ordinal);
        Assert.Contains("Server", responseHeadersText, StringComparison.Ordinal);
        Assert.Contains("\"abc\"", responseHeadersText, StringComparison.Ordinal);

        Assert.Contains("HttpContentHeaders", contentHeadersText, StringComparison.Ordinal);
        Assert.Contains("ContentDisposition", contentHeadersText, StringComparison.Ordinal);
        Assert.Contains("utf-8", contentHeadersText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_next_batch_of_system_types_readably()
    {
        using var tempDirectory = new TemporaryDirectory();
        var display = new DisplayEngine(new ObjectFormatter());
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Uri("https://example.com"), new Cookie("sid", "abc123", "/", "example.com"));
        var credential = new NetworkCredential("alice", "secret", "example");
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var drive = new DriveInfo(root);
        using var process = Process.GetCurrentProcess();
        var module = process.MainModule!;
        using var watcher = new FileSystemWatcher(tempDirectory.Path)
        {
            Filter = "*.txt",
            IncludeSubdirectories = true,
        };

        var cookieContainerText = display.RenderMany([cookieContainer]);
        var credentialText = display.RenderMany([credential]);
        var driveText = display.RenderMany([drive]);
        var processText = display.RenderMany([process]);
        var moduleText = display.RenderMany([module]);
        var watcherText = display.RenderMany([watcher]);

        Assert.Contains("CookieContainer", cookieContainerText, StringComparison.Ordinal);
        Assert.Contains("Count", cookieContainerText, StringComparison.Ordinal);
        Assert.Contains("Capacity", cookieContainerText, StringComparison.Ordinal);

        Assert.Contains("NetworkCredential", credentialText, StringComparison.Ordinal);
        Assert.Contains("alice", credentialText, StringComparison.Ordinal);
        Assert.Contains("HasPassword", credentialText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", credentialText, StringComparison.Ordinal);

        Assert.Contains("DriveInfo", driveText, StringComparison.Ordinal);
        Assert.Contains("DriveType", driveText, StringComparison.Ordinal);
        Assert.Contains("IsReady", driveText, StringComparison.Ordinal);

        Assert.Contains("Process", processText, StringComparison.Ordinal);
        Assert.Contains(process.Id.ToString(CultureInfo.InvariantCulture), processText, StringComparison.Ordinal);
        Assert.Contains("Memory", processText, StringComparison.Ordinal);

        Assert.Contains("ProcessModule", moduleText, StringComparison.Ordinal);
        Assert.Contains("ModuleName", moduleText, StringComparison.Ordinal);
        Assert.Contains("MemorySize", moduleText, StringComparison.Ordinal);

        Assert.Contains("FileSystemWatcher", watcherText, StringComparison.Ordinal);
        Assert.Contains(tempDirectory.Path, watcherText, StringComparison.Ordinal);
        Assert.Contains("*.txt", watcherText, StringComparison.Ordinal);
        Assert.Contains("IncludeSubdirectories", watcherText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_network_and_version_types_readably()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var physicalAddress = PhysicalAddress.Parse("001122AABBCC");
        var hostEntry = Dns.GetHostEntry("localhost");
        var headers = new WebHeaderCollection
        {
            ["User-Agent"] = "ToSh/1.0",
            ["Accept"] = "application/json",
        };
        var versionInfo = FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule!.FileName!);
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault();

        Assert.NotNull(networkInterface);

        var physicalAddressText = display.RenderMany([physicalAddress]);
        var hostEntryText = display.RenderMany([hostEntry]);
        var headersText = display.RenderMany([headers]);
        var versionInfoText = display.RenderMany([versionInfo]);
        var networkInterfaceText = display.RenderMany([networkInterface!]);

        Assert.Contains("PhysicalAddress", physicalAddressText, StringComparison.Ordinal);
        Assert.Contains("00-11-22-AA-BB-CC", physicalAddressText, StringComparison.Ordinal);
        Assert.Contains("Length", physicalAddressText, StringComparison.Ordinal);

        Assert.Contains("IPHostEntry", hostEntryText, StringComparison.Ordinal);
        Assert.Contains("HostName", hostEntryText, StringComparison.Ordinal);
        Assert.Contains("AddressCount", hostEntryText, StringComparison.Ordinal);

        Assert.Contains("WebHeaderCollection", headersText, StringComparison.Ordinal);
        Assert.Contains("User-Agent: ToSh/1.0", headersText, StringComparison.Ordinal);
        Assert.Contains("Accept: application/json", headersText, StringComparison.Ordinal);

        Assert.Contains("FileVersionInfo", versionInfoText, StringComparison.Ordinal);
        Assert.Contains("FileName", versionInfoText, StringComparison.Ordinal);
        Assert.Contains("ProductVersion", versionInfoText, StringComparison.Ordinal);

        Assert.Contains("NetworkInterface", networkInterfaceText, StringComparison.Ordinal);
        Assert.Contains("Status", networkInterfaceText, StringComparison.Ordinal);
        Assert.Contains("PhysicalAddress", networkInterfaceText, StringComparison.Ordinal);
        Assert.Contains("IPv4", networkInterfaceText, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_keeps_nested_scalar_values_raw_inside_record_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();
        record["Count"] = 1317;
        record["Name"] = "toast";

        var text = display.RenderMany([record]);

        Assert.Contains("Count", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("1317", text, StringComparison.Ordinal);
        Assert.Contains("toast", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", text, StringComparison.Ordinal);
        Assert.DoesNotContain("String", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_multiline_scalar_values_inside_record_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> record = new System.Dynamic.ExpandoObject();
        record["Text"] = "Hello\nThere!";

        var text = display.RenderMany([record]);

        Assert.Contains("Text", text, StringComparison.Ordinal);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("There!", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello\nThere!", text, StringComparison.Ordinal);
        Assert.DoesNotContain("String", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_enum_types_as_titled_member_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([typeof(DayOfWeek)]);

        Assert.Contains("System.DayOfWeek", text, StringComparison.Ordinal);
        Assert.Contains("│ # │", text, StringComparison.Ordinal);
        Assert.Contains("Sunday", text, StringComparison.Ordinal);
        Assert.Contains("Friday", text, StringComparison.Ordinal);
        Assert.Contains("Saturday", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_groupings_with_key_and_count_columns()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new GroupingInfo("file", [1, 2]),
            new GroupingInfo("dir", [3]),
        };

        var text = display.RenderMany(values);

        Assert.Contains("Key", text, StringComparison.Ordinal);
        Assert.Contains("Count", text, StringComparison.Ordinal);
        Assert.Contains("file", text, StringComparison.Ordinal);
        Assert.Contains("dir", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_uses_configurable_table_box_style_and_header_theme()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        display.TableTheme.BoxStyle = ToshTableBoxStyle.Double;
        display.TableTheme.Header.Foreground = "bright-yellow";
        display.TableTheme.Header.Bold = true;

        var values = new object?[]
        {
            new CommandHistoryEntry(1, "help", new DateTimeOffset(2026, 3, 23, 9, 15, 0, TimeSpan.Zero)),
            new CommandHistoryEntry(2, "ls -la", new DateTimeOffset(2026, 3, 23, 9, 16, 0, TimeSpan.Zero)),
        };

        var text = display.RenderMany(values);

        Assert.Contains("╔", text, StringComparison.Ordinal);
        Assert.Contains("║", text, StringComparison.Ordinal);
        Assert.DoesNotContain("╭", text, StringComparison.Ordinal);
        Assert.Contains("\x1b[1;93m", text, StringComparison.Ordinal);
        Assert.Contains("Id", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_repeats_table_header_at_bottom_when_table_exceeds_height()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new CommandHistoryEntry(1, "help", new DateTimeOffset(2026, 3, 23, 9, 15, 0, TimeSpan.Zero)),
            new CommandHistoryEntry(2, "ls -la", new DateTimeOffset(2026, 3, 23, 9, 16, 0, TimeSpan.Zero)),
        };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxHeight: 5));

        Assert.Equal(2, text.Split("Id", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, text.Split("Text", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Display_engine_groups_mixed_type_collections_into_subtables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        IDictionary<string, object?> first = new System.Dynamic.ExpandoObject();
        first["Name"] = "alpha";
        first["Size"] = StorageSize.FromBytes(1024);
        IDictionary<string, object?> second = new System.Dynamic.ExpandoObject();
        second["Name"] = "beta";
        second["Size"] = StorageSize.FromBytes(2048);

        var values = new object?[]
        {
            first,
            second,
            new GroupingInfo("file", [1, 2]),
            new GroupingInfo("dir", [3]),
        };

        var text = display.RenderMany(values);

        Assert.Contains("[table]", text, StringComparison.Ordinal);
        Assert.Contains("[GroupingInfo]", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("Key", text, StringComparison.Ordinal);
        Assert.Contains("Count", text, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 2);
    }

    [Fact]
    public void Display_engine_renders_mixed_pretty_scalar_types_as_standalone_blocks()
    {
        var display = new DisplayEngine(new ObjectFormatter());

        var text = display.RenderMany([new DateOnly(2026, 3, 29), new TimeOnly(4, 19, 30, 492)]);

        Assert.DoesNotContain("[DateOnly]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[TimeOnly]", text, StringComparison.Ordinal);
        Assert.Contains("DateOnly", text, StringComparison.Ordinal);
        Assert.Contains("TimeOnly", text, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 2);
    }

    [Fact]
    public async Task Config_theme_values_render_as_structured_records()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        var values = await engine.ExecuteToListAsync("config get theme");
        var text = runtime.Display.RenderMany(values);

        Assert.Contains("Completion", text, StringComparison.Ordinal);
        Assert.Contains("Diagnostics", text, StringComparison.Ordinal);
        Assert.Contains("Syntax", text, StringComparison.Ordinal);
        Assert.Contains("Tables", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ToshThemeConfig {", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_config_value_renders_as_structured_record()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        var values = await engine.ExecuteToListAsync("config");
        var text = runtime.Display.RenderMany(values);

        Assert.Contains("Theme", text, StringComparison.Ordinal);
        Assert.Contains("Display", text, StringComparison.Ordinal);
        Assert.Contains("Repl", text, StringComparison.Ordinal);
        Assert.Contains("Prompt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ToshConfig {", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_clr_enum_type_names_render_as_enum_tables()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new Tosh.Language.ToshEngine(runtime);

        var values = await engine.ExecuteToListAsync("echo System.DayOfWeek");
        var text = runtime.Display.RenderMany(values);

        Assert.Contains("System.DayOfWeek", text, StringComparison.Ordinal);
        Assert.Contains("Sunday", text, StringComparison.Ordinal);
        Assert.Contains("Saturday", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_enumerable_values_as_their_contents()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[] { new[] { "alpha", "beta" } };

        var text = display.RenderMany(values);

        Assert.Contains("╭", text, StringComparison.Ordinal);
        Assert.Contains("alpha", text, StringComparison.Ordinal);
        Assert.Contains("beta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Object[]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_top_level_nested_enumerables_as_matrices()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new object?[] { 1, 2, 3 },
            new object?[] { 4, 5, 6 },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("\x1b[", text, StringComparison.Ordinal);
        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ 1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            6 │", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Object[] [", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_jagged_numeric_collections_as_matrices()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new object?[]
            {
                new object?[] { 1, 2, 3 },
                new object?[] { 4, 5, 6 },
            },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ 1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            6 │", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Object[] [", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_single_jagged_heterogeneous_collections_as_type_header_matrices()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new object?[]
            {
                new object?[] { 1, 'a', true },
                new object?[] { 2, 'b', false },
                new object?[] { 3, 'c', true },
            },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("Int32", plain, StringComparison.Ordinal);
        Assert.Contains("Char", plain, StringComparison.Ordinal);
        Assert.Contains("Boolean", plain, StringComparison.Ordinal);
        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ 1 │", plain, StringComparison.Ordinal);
        Assert.Contains(" a ", plain, StringComparison.Ordinal);
        Assert.Contains(" b ", plain, StringComparison.Ordinal);
        Assert.Contains("true", plain, StringComparison.Ordinal);
        Assert.Contains("false", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("│   │ 0 │ 1 │ 2 │", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_rectangular_two_dimensional_arrays_as_matrices()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new[,]
            {
                { 1, 2, 3 },
                { 4, 5, 6 },
            },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ 1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            1 │", plain, StringComparison.Ordinal);
        Assert.Contains("│            6 │", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_renders_three_dimensional_arrays_as_nested_matrix_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new int[,,]
            {
                { { 1, 2 }, { 3, 4 } },
                { { 5, 6 }, { 7, 8 } },
            },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("[Slice", plain, StringComparison.Ordinal);
        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ A │", plain, StringComparison.Ordinal);
        Assert.Contains("│ B │", plain, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 3);
    }

    [Fact]
    public void Display_engine_renders_four_dimensional_arrays_as_nested_matrix_tables()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new int[,,,]
            {
                { { { 1, 2 }, { 3, 4 } } },
                { { { 5, 6 }, { 7, 8 } } },
            },
        };

        var text = display.RenderMany(values);
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("[Slice", plain, StringComparison.Ordinal);
        Assert.Contains("│ 0 │", plain, StringComparison.Ordinal);
        Assert.Contains("│ A │", plain, StringComparison.Ordinal);
        Assert.Contains("│ B │", plain, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 3);
    }

    [Fact]
    public void Display_engine_falls_back_to_slice_rendering_for_higher_rank_tensors_when_width_is_too_small()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new int[,,]
            {
                { { 1, 2 }, { 3, 4 } },
                { { 5, 6 }, { 7, 8 } },
            },
        };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 20));
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("…", plain, StringComparison.Ordinal);
        Assert.True(
            plain.Contains("[Slice 0]", StringComparison.Ordinal) ||
            plain.Contains("│ I │", StringComparison.Ordinal) ||
            plain.Contains("│ II │", StringComparison.Ordinal),
            $"Expected either slice fallback labels or compact flattened tensor headers, but got:{Environment.NewLine}{text}");
    }

    [Fact]
    public void Display_engine_does_not_clip_five_dimensional_tensors_when_width_is_available()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var nextValue = 1;
        var values = new object?[] { CreateJaggedTensor(depth: 5, ref nextValue) };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 220));
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("…", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("[Slice", plain, StringComparison.Ordinal);
        Assert.Contains("│ A │", plain, StringComparison.Ordinal);
        Assert.Contains("│ B │", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_engine_does_not_clip_six_dimensional_tensors_when_width_is_available()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var nextValue = 1;
        var values = new object?[] { CreateJaggedTensor(depth: 6, ref nextValue) };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 378));
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("…", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("[Slice", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("A/I/", plain, StringComparison.Ordinal);
        Assert.Contains("│ A │ ╭", plain, StringComparison.Ordinal);
        Assert.Contains("│ B │ ╭", plain, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 7, $"Expected nested tensor cells, but got:{Environment.NewLine}{text}");
    }

    [Fact]
    public void Display_engine_keeps_seven_dimensional_tensors_nested_when_width_is_available()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var nextValue = 1;
        var values = new object?[] { CreateJaggedTensor(depth: 7, ref nextValue) };

        var text = display.RenderMany(values, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 189));
        var plain = StyledText.StripAnsi(text);

        Assert.DoesNotContain("…", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("[Slice", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("A/I/", plain, StringComparison.Ordinal);
        Assert.Contains("│ A │ ╭", plain, StringComparison.Ordinal);
        Assert.Contains("│ B │ ╭", plain, StringComparison.Ordinal);
        Assert.True(text.Count(character => character == '╭') >= 8, $"Expected a deeply nested tensor layout, but got:{Environment.NewLine}{text}");
    }

    [Fact]
    public void Display_engine_renders_external_text_lines_as_plain_text()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new ShellTextLine("alpha"),
            new ShellTextLine("beta"),
            new ShellTextLine(string.Empty),
            new ShellTextLine("gamma"),
        };

        var text = display.RenderMany(values);

        Assert.Equal($"alpha{Environment.NewLine}beta{Environment.NewLine}{Environment.NewLine}gamma", text);
    }

    [Fact]
    public void Display_engine_renders_tree_tables_for_hierarchical_block_devices()
    {
        var display = new DisplayEngine(new ObjectFormatter());
        var values = new object?[]
        {
            new BlockDeviceInfo
            {
                Name = "sda",
                Type = "disk",
                Size = StorageSize.FromBytes(4_096),
                Children =
                [
                    new BlockDeviceInfo
                    {
                        Name = "sda1",
                        Type = "part",
                        Size = StorageSize.FromBytes(1_024),
                    },
                ],
            },
        };

        var text = display.RenderMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Size", text, StringComparison.Ordinal);
        Assert.Contains("sda", text, StringComparison.Ordinal);
        Assert.Contains("sda1", text, StringComparison.Ordinal);
        Assert.Contains("└", text, StringComparison.Ordinal);
    }

    private static object? CreateJaggedTensor(int depth, ref int nextValue)
    {
        if (depth <= 0)
        {
            return nextValue++;
        }

        return new object?[]
        {
            CreateJaggedTensor(depth - 1, ref nextValue),
            CreateJaggedTensor(depth - 1, ref nextValue),
        };
    }

    private sealed class DisplayReflectionDemo
    {
        public static int Counter = 0;

        public event EventHandler? Changed;

        public void Sample(string name, int count = 1)
        {
            _ = name;
            _ = count;
        }

        public void Raise()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-display-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
