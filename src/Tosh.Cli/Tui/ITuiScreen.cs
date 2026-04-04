namespace Tosh.Cli.Tui;

internal interface ITuiScreen
{
    TuiFrame Render(TuiSize size);

    TuiScreenResult HandleKey(ConsoleKeyInfo key);
}
