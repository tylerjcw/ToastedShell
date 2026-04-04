using Tosh.Cli;
using Tosh.Core;

namespace Tosh.Tests;

public sealed class ConsolePagerTests
{
    [Fact]
    public void Console_pager_pages_when_output_exceeds_available_height()
    {
        var config = new ToshPagingConfig();
        var rendered = string.Join(Environment.NewLine, Enumerable.Range(0, 6).Select(index => $"line-{index}"));

        var shouldPage = ConsolePager.ShouldPage(rendered, availableHeight: 4, config, isOutputRedirected: false);

        Assert.True(shouldPage);
    }

    [Fact]
    public void Console_pager_does_not_page_when_disabled()
    {
        var config = new ToshPagingConfig
        {
            Enabled = false,
        };
        var rendered = string.Join(Environment.NewLine, Enumerable.Range(0, 6).Select(index => $"line-{index}"));

        var shouldPage = ConsolePager.ShouldPage(rendered, availableHeight: 4, config, isOutputRedirected: false);

        Assert.False(shouldPage);
    }

    [Fact]
    public void Console_pager_calculates_page_size_using_reserved_lines()
    {
        var pageSize = ConsolePager.GetPageSize(availableHeight: 10, reservedLines: 2);

        Assert.Equal(7, pageSize);
    }

    [Fact]
    public void Console_pager_counts_lines_consistently()
    {
        var count = ConsolePager.CountLines("alpha\r\nbeta\ngamma");

        Assert.Equal(3, count);
    }

    [Fact]
    public void Console_pager_state_supports_backward_and_forward_navigation()
    {
        var state = new ConsolePager.PagerState(
            Enumerable.Range(1, 10).Select(index => $"line-{index}").ToArray(),
            pageSize: 3);

        state.NextPage();
        Assert.Equal(3, state.StartIndex);

        state.PreviousPage();
        Assert.Equal(0, state.StartIndex);

        state.End();
        Assert.Equal(7, state.StartIndex);

        state.PreviousLine();
        Assert.Equal(6, state.StartIndex);

        state.Home();
        Assert.Equal(0, state.StartIndex);
    }

    [Fact]
    public void Console_pager_footer_shows_progress_and_navigation_help()
    {
        var state = new ConsolePager.PagerState(["a", "b", "c", "d"], pageSize: 2);

        var footer = ConsolePager.BuildFooterText(state);

        Assert.Contains("1-2/4", footer, StringComparison.Ordinal);
        Assert.Contains("PgDn", footer, StringComparison.Ordinal);
        Assert.Contains("PgUp", footer, StringComparison.Ordinal);
        Assert.Contains("q quit", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void Console_pager_applies_navigation_keys()
    {
        var state = new ConsolePager.PagerState(
            Enumerable.Range(1, 10).Select(index => $"line-{index}").ToArray(),
            pageSize: 3);

        Assert.True(ConsolePager.TryApplyKey(state, new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)));
        Assert.Equal(3, state.StartIndex);

        Assert.True(ConsolePager.TryApplyKey(state, new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false)));
        Assert.Equal(0, state.StartIndex);

        Assert.False(ConsolePager.TryApplyKey(state, new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)));
    }

    [Fact]
    public void Console_pager_renders_reserved_blank_lines_above_footer()
    {
        var state = new ConsolePager.PagerState(["line-1", "line-2"], pageSize: 2);
        var footerStyle = new ToshTextStyleConfig(foreground: "gray", dim: true);

        var viewport = ConsolePager.RenderViewport(state, footerStyle, reservedLines: 1);

        Assert.Contains($"line-2{Environment.NewLine}{Environment.NewLine}", viewport, StringComparison.Ordinal);
        Assert.Contains("-- more --", viewport, StringComparison.Ordinal);
    }
}
