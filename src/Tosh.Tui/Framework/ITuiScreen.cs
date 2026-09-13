
namespace Tosh.Tui;

public interface ITuiScreen
{
    TuiFrame Render(TuiSize size);

    TuiScreenResult HandleInput(TuiInputEvent input);
}
