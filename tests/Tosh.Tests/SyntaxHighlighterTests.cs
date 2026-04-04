using Tosh.Cli;
using Tosh.Core;

namespace Tosh.Tests;

public sealed class SyntaxHighlighterTests
{
    [Fact]
    public void Highlights_valid_commands_as_bold_green()
    {
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight("echo hello", runtime);

        Assert.Contains("\x1b[1;32mecho\x1b[0m", highlighted);
        Assert.Contains("\x1b[32mhello\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_invalid_commands_as_red()
    {
        var runtime = ToshRuntime.CreateDefault();

        var highlighted = SyntaxHighlighter.Highlight("no_such_command", runtime);

        Assert.Contains("\x1b[31mno_such_command\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_existing_directory_arguments_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("cd examples", runtime);

            Assert.Contains("\x1b[4;32mexamples\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Highlights_existing_directory_paths_at_command_position_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-dir-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("./examples/", runtime);

            Assert.Contains("\x1b[4;32m./examples/\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Highlights_existing_file_paths_at_command_position_as_underlined_green()
    {
        var tempRoot = Directory.CreateTempSubdirectory("tosh-highlight-file-");

        try
        {
            var childDirectory = Path.Combine(tempRoot.FullName, "examples");
            Directory.CreateDirectory(childDirectory);
            File.WriteAllText(Path.Combine(childDirectory, "interop_demo.tosh"), "# demo");

            var runtime = ToshRuntime.CreateDefault();
            runtime.CurrentDirectory = tempRoot.FullName;

            var highlighted = SyntaxHighlighter.Highlight("./examples/interop_demo.tosh", runtime);

            Assert.Contains("\x1b[4;32m./examples/interop_demo.tosh\x1b[0m", highlighted);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Uses_runtime_theme_for_command_highlighting()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.ValidCommand.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("echo hello", runtime);

        Assert.Contains("\x1b[1;95mecho\x1b[0m", highlighted);
    }

    [Fact]
    public void Highlights_intrinsic_literals_as_constants()
    {
        var runtime = ToshRuntime.CreateDefault();
        runtime.Config.Theme.Syntax.Constant.Foreground = "bright-magenta";

        var highlighted = SyntaxHighlighter.Highlight("echo 2d 2026-03-27 127.0.0.1 ::1", runtime);

        Assert.Contains("\x1b[95m2d\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m2026-03-27\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m127.0.0.1\x1b[0m", highlighted);
        Assert.Contains("\x1b[95m::1\x1b[0m", highlighted);
    }
}
