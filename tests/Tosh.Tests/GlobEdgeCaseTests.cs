using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class GlobEdgeCaseTests
{
    [Fact]
    public async Task Glob_with_no_matches_returns_empty()
    {
        using var tempDirectory = new TemporaryDirectory();

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \"*.nonexistent\"");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Glob_excludes_hidden_files_by_default()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, ".hidden"), "secret");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "visible.txt"), "hello");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \"*\"");
        var names = results.Select(r => ((FileSystemEntry)r!).Name).ToArray();

        Assert.Contains("visible.txt", names);
        Assert.DoesNotContain(".hidden", names);
    }

    [Fact]
    public async Task Glob_includes_hidden_files_with_all_flag()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, ".hidden"), "secret");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "visible.txt"), "hello");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob -a \"*\"");
        var names = results.Select(r => ((FileSystemEntry)r!).Name).ToArray();

        Assert.Contains("visible.txt", names);
        Assert.Contains(".hidden", names);
    }

    [Fact]
    public async Task Glob_hidden_pattern_matches_dotfiles_even_without_all_flag()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, ".bashrc"), "# config");
        File.WriteAllText(Path.Combine(tempDirectory.Path, ".profile"), "# profile");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "readme.txt"), "visible");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \".*\"");
        var names = results.Select(r => ((FileSystemEntry)r!).Name).ToArray();

        Assert.Contains(".bashrc", names);
        Assert.Contains(".profile", names);
        Assert.DoesNotContain("readme.txt", names);
    }

    [Fact]
    public async Task Glob_does_not_descend_into_symlinked_directories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TemporaryDirectory();
        var subdir = Path.Combine(tempDirectory.Path, "real");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "file.txt"), "content");

        // Create symlink to real/ as linked/
        var linkPath = Path.Combine(tempDirectory.Path, "linked");
        Directory.CreateSymbolicLink(linkPath, subdir);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        // ** descends into both real directories and symlinked ones
        var results = await engine.ExecuteToListAsync("glob \"**/*.txt\"");
        var paths = results.Select(r => ((FileSystemEntry)r!).FullName).ToArray();

        Assert.Contains(Path.Combine(subdir, "file.txt"), paths);

        // Non-** segment patterns skip symlinked directories during descent
        var specificResults = await engine.ExecuteToListAsync("glob \"*/file.txt\"");
        var specificPaths = specificResults.Select(r => ((FileSystemEntry)r!).FullName).ToArray();

        // real/file.txt matches, but linked/ is a symlink so linked/file.txt does not match
        Assert.Contains(Path.Combine(subdir, "file.txt"), specificPaths);
        Assert.DoesNotContain(Path.Combine(linkPath, "file.txt"), specificPaths);
    }

    [Fact]
    public async Task Glob_with_recursive_wildcard_finds_nested_files()
    {
        using var tempDirectory = new TemporaryDirectory();
        var subdir = Path.Combine(tempDirectory.Path, "a", "b");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "deep.txt"), "hello");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "top.txt"), "world");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \"**/*.txt\"");
        var names = results.Select(r => ((FileSystemEntry)r!).Name).ToArray();

        Assert.Contains("deep.txt", names);
        Assert.Contains("top.txt", names);
    }

    [Fact]
    public async Task Glob_alternation_pattern_matches_multiple_extensions()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "readme.md"), "");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "notes.txt"), "");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "photo.jpg"), "");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \"*.@(md,txt)\"");
        var names = results.Select(r => ((FileSystemEntry)r!).Name).ToArray();

        Assert.Contains("readme.md", names);
        Assert.Contains("notes.txt", names);
        Assert.DoesNotContain("photo.jpg", names);
    }

    [Fact]
    public async Task Glob_in_nonexistent_directory_returns_empty()
    {
        using var tempDirectory = new TemporaryDirectory();

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = tempDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("glob \"nonexistent-dir/*.txt\"");
        Assert.Empty(results);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-glob-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
