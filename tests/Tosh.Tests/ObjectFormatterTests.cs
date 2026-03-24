using Tosh.Core;

namespace Tosh.Tests;

public sealed class ObjectFormatterTests
{
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

        Assert.Equal("String[] [ \"alpha\", \"beta\" ]", text);
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
    public void Formatter_uses_preferences_for_datetime_and_storage_size()
    {
        var preferences = new DisplayPreferences
        {
            NowProvider = () => new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero),
        };
        preferences.DateTime.ScalarMode = TemporalDisplayMode.Custom;
        preferences.DateTime.ScalarFormat = "yyyy/MM/dd";
        preferences.StorageSize.Mode = StorageSizeDisplayMode.Bytes;

        var formatter = new ObjectFormatter(DisplayProfileRegistry.CreateDefault(preferences));

        Assert.Equal("2026/03/21", formatter.Format(new DateTime(2026, 3, 21, 8, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("1536 B", formatter.Format(StorageSize.FromBytes(1536)));
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
    public void Format_many_renders_help_search_results_as_a_table()
    {
        var formatter = new ObjectFormatter();
        var values = new object?[]
        {
            new HelpSearchResult("from-json", 98.2, HelpSubjectKind.BuiltIn, "Data", "Parses JSON text into CLR objects.", "from-json", Array.Empty<string>()),
            new HelpSearchResult("to-json", 82.5, HelpSubjectKind.BuiltIn, "Data", "Serializes CLR objects to JSON text.", "to-json", Array.Empty<string>()),
        };

        var text = formatter.FormatMany(values);

        Assert.Contains("Score", text, StringComparison.Ordinal);
        Assert.Contains("Name", text, StringComparison.Ordinal);
        Assert.Contains("from-json", text, StringComparison.Ordinal);
        Assert.Contains("to-json", text, StringComparison.Ordinal);
    }

    private sealed record DemoObject(string Name, int Count);

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
