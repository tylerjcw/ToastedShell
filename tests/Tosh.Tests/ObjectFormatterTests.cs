using Tosh.Language;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Tosh.Tests;

public sealed class ObjectFormatterTests
{
    [Fact]
    public void Quantities_are_scalar_for_display_and_record_like_for_introspection()
    {
        var power = Quantity.FromLiteral(483.06, "MW");
        var flow = Quantity.FromLiteral(48 * 10.3, "L/s");
        var dimensionless = new Quantity(2, UnitExpression.Dimensionless, string.Empty);

        Assert.Equal("483.06 MW", ToshValueFormatter.Format(power));
        Assert.Equal("494.4 L/s", flow.ToString());
        Assert.Equal("483.1 MW", power.ToString("F1", CultureInfo.InvariantCulture));
        var roundTripMagnitude = flow.ToString("R", CultureInfo.InvariantCulture).Split(' ', 2)[0];
        Assert.Equal(flow.Magnitude, double.Parse(roundTripMagnitude, CultureInfo.InvariantCulture));
        Assert.Equal("2", dimensionless.ToString());

        var display = new DisplayEngine(new ObjectFormatter());
        Assert.False(display.TryBuildStreamingColumns(
            power,
            new DisplayRenderOptions(ObjectRenderStyle.Compact),
            out _));

        var mixed = StyledText.StripAnsi(display.RenderMany([1, power]));
        Assert.Contains("483.06 MW", mixed, StringComparison.Ordinal);
        Assert.DoesNotContain("base-value", mixed, StringComparison.Ordinal);

        Assert.True(ShellRecordUtilities.TryGetFields(power, out var fields));
        Assert.Contains(fields, field => field.Key == "base-value");
    }

    [Fact]
    public void Default_value_formatter_uses_canonical_scalar_and_collection_text()
    {
        Assert.Equal("null", ToshValueFormatter.Format(null));
        Assert.Equal("true", ToshValueFormatter.Format(true));
        Assert.Equal("[1, 2]", ToshValueFormatter.Format(new object?[] { 1, 2 }));
        Assert.Equal("ToastColor.Green", ToshValueFormatter.Format(ToastColor.Green));
    }

    [Theory]
    [InlineData("[1, 2]", "array<int>")]
    [InlineData("{: 1, 2 :}", "set")]
    [InlineData("(1, 2)", "tuple")]
    public async Task Shell_type_descriptors_display_as_their_shell_type_name(
        string literal,
        string expectedName)
    {
        // TS-P1-23. Giving BuiltInShellTypeDefinition a ToString fixed the paths
        // that stringify a descriptor — concatenation, the table header — but not
        // the structural ones. A descriptor exposes Name, FullName, Namespace and
        // the rest as ordinary readable properties, so the formatter's
        // record-field branch claimed it first and interpolation rendered
        // `{ Name = "array<int>", FullName = ..., ... }` instead of `array<int>`.
        //
        // Interpolation is the path most likely to be used to show a type, so it
        // is asserted alongside the nested cases rather than the root one.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var direct = await engine.ExecuteToListAsync(
            $$"""
            var v = {{literal}}
            var t = type-of $v
            echo $"{$t}"
            """);
        Assert.Equal(expectedName, Assert.Single(direct)?.ToString());

        var nested = await engine.ExecuteToListAsync(
            $$"""
            var v = {{literal}}
            var r = {| kind = (type-of $v) |}
            echo $"{$r}"
            """);
        // Record rendering uses the literal's own delimiters so output
        // round-trips as source (TS-P2-25).
        Assert.Equal($"{{| kind = {expectedName} |}}", Assert.Single(nested)?.ToString());
    }

    [Fact]
    public async Task Clr_type_values_are_unaffected_by_the_shell_descriptor_rule()
    {
        // The other half of the TS-P1-23 acceptance: a CLR value still reports its
        // CLR type, so the descriptor rule must not capture System.Type.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            echo $"{(type-of 5)}"
            echo $"{(type-of "s")}"
            """);

        Assert.Equal(
            ["System.Int32", "System.String"],
            results.Select(value => value?.ToString()));
    }

    [Fact]
    public void Formatter_renders_shell_command_descriptors_readably()
    {
        var formatter = new ObjectFormatter();

        var text = formatter.Format(new ShellCommandDescriptor("ls", "Lists entries.", "ls [-a] [-l] [path]"));

        Assert.Contains("ls", text, StringComparison.Ordinal);
        Assert.Contains("Lists entries.", text, StringComparison.Ordinal);
        Assert.Contains("ls [-a] [-l] [path]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_renders_compact_collections_and_nested_strings_consistently()
    {
        var formatter = new ObjectFormatter();

        var text = formatter.Format(new[] { "alpha", "beta" });

        // `TOAST-0014`: no CLR type header, and no padding inside the brackets. The
        // rendering is a language value's text now, so it reads as the literal that
        // produced it.
        Assert.Equal("[\"alpha\", \"beta\"]", text);
    }

    [Fact]
    public void Formatter_renders_detail_mode_as_multiline_object_output()
    {
        var formatter = new ObjectFormatter { Style = ObjectRenderStyle.Detail };

        var text = formatter.Format(new DemoObject("toaster", 2));

        Assert.Contains("DemoObject {", text, StringComparison.Ordinal);
        Assert.Contains("Count = 2", text, StringComparison.Ordinal);
        Assert.Contains("Name = \"toaster\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_renders_object_inspection_as_readable_diagnostic_output()
    {
        var formatter = new ObjectFormatter();
        var inspection = new ObjectInspection(
            Index: 1,
            TypeName: "System.Text.StringBuilder",
            AssemblyName: "System.Private.CoreLib",
            BaseTypeName: "System.Object",
            Display: "StringBuilder { Length = 5 }",
            IsEnumerable: false,
            ItemCount: null,
            Interfaces: Array.Empty<string>(),
            Members: new[]
            {
                new ObjectInspectionMember("Length", "property", "System.Int32", "5"),
            },
            ItemsPreview: Array.Empty<string>(),
            HasMoreItems: false);

        var text = formatter.Format(inspection);

        Assert.Contains("inspect 1: System.Text.StringBuilder", text, StringComparison.Ordinal);
        Assert.Contains("display: StringBuilder { Length = 5 }", text, StringComparison.Ordinal);
        Assert.Contains("property Length : System.Int32 = 5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_renders_typed_ls_values_through_display_profiles()
    {
        var formatter = new ObjectFormatter();

        var typeText = formatter.Format(FileSystemEntryType.File);
        var modeText = formatter.Format(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal("file", typeText);
        Assert.Equal("rw-r--r--", modeText);
    }

    [Fact]
    public void Formatter_uses_preferences_for_datetime_timespan_and_storage_size()
    {
        var preferences = new DisplayPreferences
        {
            NowProvider = () => new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
        };
        preferences.DateTime.ScalarMode = TemporalDisplayMode.Custom;
        preferences.DateTime.ScalarFormat = "yyyy/MM/dd";
        preferences.TimeSpan.ScalarMode = DurationDisplayMode.TotalSeconds;
        preferences.StorageSize.Mode = StorageSizeDisplayMode.Bytes;

        var formatter = new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences));

        Assert.Equal("2026/03/21", formatter.Format(new DateTime(2026, 3, 21, 8, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("90", formatter.Format(TimeSpan.FromSeconds(90)));
        Assert.Equal("90", formatter.Format(TemporalAmount.FromTimeSpan(TimeSpan.FromSeconds(90))));
        Assert.Equal("1536 B", formatter.Format(StorageSize.FromBytes(1536)));
    }

    [Fact]
    public void Formatter_uses_preferences_for_dateonly_and_timeonly()
    {
        var preferences = new DisplayPreferences
        {
            NowProvider = () => new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
        };
        preferences.DateOnly.ScalarMode = DateOnlyDisplayMode.Relative;
        preferences.TimeOnly.ScalarMode = TimeOnlyDisplayMode.TwentyFourHour;

        var formatter = new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences));

        Assert.Equal("tomorrow", formatter.Format(new DateOnly(2026, 3, 24)));
        Assert.Equal("15:31:42", formatter.Format(new TimeOnly(15, 31, 42)));
    }

    [Fact]
    public void Formatter_uses_preferences_for_permissions_and_file_attributes()
    {
        var preferences = new DisplayPreferences();
        preferences.UnixFileMode.Mode = UnixFileModeDisplayMode.Both;
        preferences.FileAttributes.Mode = FileAttributesDisplayMode.Hex;

        var formatter = new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences));

        var permissions = formatter.Format(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var attributes = formatter.Format(FileAttributes.ReadOnly | FileAttributes.Hidden);

        Assert.Equal("rw-r--r-- (0644)", permissions);
        Assert.Equal($"0x{((int)(FileAttributes.ReadOnly | FileAttributes.Hidden)):X}", attributes);
    }

    [Fact]
    public void Formatter_renders_additional_basic_clr_values_through_profiles()
    {
        var formatter = new ObjectFormatter();

        Assert.Equal("Sunday, March 29, 2026", formatter.Format(new DateOnly(2026, 3, 29)));
        Assert.Equal("3:31:42 AM", formatter.Format(new TimeOnly(3, 31, 42)));
        Assert.Equal("fr-FR", formatter.Format(new CultureInfo("fr-FR")));
        Assert.Equal("utf-8", formatter.Format(Encoding.UTF8));
        Assert.Contains("2 bytes", formatter.Format(new byte[] { 0x48, 0x69 }), StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_renders_plain_enums_as_typed_values()
    {
        var formatter = new ObjectFormatter();

        var rootText = formatter.Format(DayOfWeek.Friday);

        Assert.Equal("System.DayOfWeek.Friday", rootText);
    }

    [Fact]
    public void Formatter_renders_vectors_compactly()
    {
        var formatter = new ObjectFormatter();

        var text = formatter.Format(new ToshVector([1d, 2d, 3d]));

        Assert.Equal("[1, 2, 3]", text);
    }

    [Fact]
    public void Formatter_renders_matrices_compactly()
    {
        var formatter = new ObjectFormatter();

        var text = formatter.Format(new ToshMatrix([[1d, 2d], [3d, 4d]]));

        Assert.Equal("[[1, 2], [3, 4]]", text);
    }

    [Fact]
    public void Formatter_renders_complex_numbers_compactly()
    {
        var formatter = new ObjectFormatter();

        var text = formatter.Format(new Complex(3d, 4d));

        Assert.Equal("3 + 4i", text);
    }

    [Fact]
    public void Inspector_surfaces_enum_numeric_value_and_names()
    {
        var formatter = new ObjectFormatter();
        var inspector = new ObjectInspector(formatter);

        var inspection = inspector.Inspect(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, 1);

        Assert.Contains(inspection.Members, member => member.Name == "NumericValue");
        Assert.Contains(inspection.Members, member => member.Name == "Names" && member.Display.Contains("UserRead", StringComparison.Ordinal));
        Assert.DoesNotContain(inspection.Members, member => member.Name == "value__");
    }

    [Fact]
    public void Inspector_uses_adapted_storage_size_members_for_drive_info()
    {
        var formatter = new ObjectFormatter();
        var inspector = new ObjectInspector(formatter);
        var driveRoot = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)
                        ?? throw new InvalidOperationException("Unable to determine the current drive root.");

        var inspection = inspector.Inspect(new DriveInfo(driveRoot), 1);

        Assert.Contains(
            inspection.Members,
            member => member.Name == "TotalSize" &&
                      member.TypeName == typeof(StorageSize).FullName);
        Assert.Contains(
            inspection.Members,
            member => member.Name == "AvailableFreeSpace" &&
                      member.TypeName == typeof(StorageSize).FullName);
    }

    [Fact]
    public void Format_many_renders_command_descriptors_as_a_table()
    {
        var formatter = new ObjectFormatter();
        var values = new object?[]
        {
            new ShellCommandDescriptor("help", "Shows commands.", "help [command]"),
            new ShellCommandDescriptor("ls", "Lists entries.", "ls [-a] [-l] [path]"),
        };

        var text = formatter.FormatMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Description", text, StringComparison.Ordinal);
        Assert.Contains("Usage", text, StringComparison.Ordinal);
        Assert.Contains("help", text, StringComparison.Ordinal);
        Assert.Contains("ls", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_many_renders_filesystem_entries_as_an_ls_style_table()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(tempDirectory.Path, "alpha.txt"), "alpha");
        Directory.CreateDirectory(System.IO.Path.Combine(tempDirectory.Path, "nested"));

        var values = new object?[]
        {
            FileSystemEntry.From(new FileInfo(System.IO.Path.Combine(tempDirectory.Path, "alpha.txt"))),
            FileSystemEntry.From(new DirectoryInfo(System.IO.Path.Combine(tempDirectory.Path, "nested"))),
        };

        var formatter = new ObjectFormatter();
        var text = formatter.FormatMany(values);

        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("Type", text, StringComparison.Ordinal);
        Assert.Contains("Modified", text, StringComparison.Ordinal);
        Assert.Contains("alpha.txt", text, StringComparison.Ordinal);
        Assert.Contains("nested/", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_renders_long_symlink_entries_with_unix_link_indicator()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var targetPath = System.IO.Path.Combine(tempDirectory.Path, "target.txt");
        var linkPath = System.IO.Path.Combine(tempDirectory.Path, "target-link.txt");
        File.WriteAllText(targetPath, "alpha");
        File.CreateSymbolicLink(linkPath, targetPath);

        var formatter = new ObjectFormatter();
        var text = formatter.Format(FileSystemEntry.From(new FileInfo(linkPath), preferLongDisplay: true));

        Assert.StartsWith("l", text, StringComparison.Ordinal);
        Assert.Contains("target-link.txt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_many_renders_help_search_results_as_a_table()
    {
        var formatter = new ObjectFormatter();
        var values = new object?[]
        {
            new HelpSearchResult("from", 98.2, HelpSubjectKind.BuiltIn, "Data", "Parses structured text into objects.", "from <format> [options] [text]", Array.Empty<string>()),
            new HelpSearchResult("to", 82.5, HelpSubjectKind.BuiltIn, "Data", "Serializes objects into structured text.", "to <format> [options]", Array.Empty<string>()),
        };

        var text = formatter.FormatMany(values);

        Assert.Contains("Score", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("from", text, StringComparison.Ordinal);
        Assert.Contains("to", text, StringComparison.Ordinal);
    }

    private sealed record DemoObject(string Name, int Count);

    [ToshType("enum", 0, 0)]
    private enum ToastColor
    {
        Green,
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-render-tests-{Guid.NewGuid():N}");
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
