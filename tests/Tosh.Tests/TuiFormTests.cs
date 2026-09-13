using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A form: what accepting and abandoning one mean, and when its handlers run.
/// </summary>
/// <remarks>
/// What this replaces is a screen handing back a dictionary the caller then read by string
/// key, having just built the widgets it was asking about.
/// </remarks>
public sealed class TuiFormTests
{
    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static TuiDeclarativeScreen Screen(TuiWidget root)
    {
        var screen = new TuiDeclarativeScreen(root, []);

        // A screen is measured before it is typed into; focus and hit testing need bounds.
        screen.Render(new TuiSize(40, 10));

        return screen;
    }

    [Fact]
    public void Enter_submits_and_escape_cancels()
    {
        var submitted = new TuiForm(new TuiField("Width", "2"));
        Assert.Equal(TuiScreenResult.Exit, Screen(submitted).HandleInput(Key(ConsoleKey.Enter)));
        Assert.True(submitted.WasSubmitted);

        var cancelled = new TuiForm(new TuiField("Width", "2"));
        Assert.Equal(TuiScreenResult.Exit, Screen(cancelled).HandleInput(Key(ConsoleKey.Escape)));
        Assert.True(cancelled.WasCancelled);
    }

    [Fact]
    public void A_handler_runs_while_the_widgets_can_still_be_read()
    {
        var field = new TuiField("Width", "24") { Id = "Width" };
        var form = new TuiForm(field);
        string? readInHandler = null;

        form.Submitted = _ => readInHandler = field.Text;

        Screen(form).HandleInput(Key(ConsoleKey.Enter));

        Assert.Equal("24", readInHandler);
    }

    [Fact]
    public void A_handler_is_handed_the_form_so_markup_can_read_it_too()
    {
        // A tree written as markup holds no variables. The ids are the only handle it has,
        // and the form is what carries them.
        var form = new TuiForm(new TuiStack(TuiOrientation.Vertical)
            .Add(new TuiField("Width", "3") { Id = "Width" })
            .Add(new TuiField("Height", "4") { Id = "Height" }));

        TuiForm? sender = null;
        form.Submitted = received => sender = received;

        Screen(form).HandleInput(Key(ConsoleKey.Enter));

        Assert.Same(form, sender);
        Assert.Equal("3", sender!.ValueOf("Width"));
        Assert.Equal("4", sender.ValueOf("Height"));
    }

    [Fact]
    public void A_form_ends_once_and_stays_ended()
    {
        var closes = 0;
        var form = new TuiForm(new TuiTextWidget("x"));

        form.Submitted = _ => closes += 1;
        form.Cancelled = _ => closes += 1;

        form.Submit();
        form.Submit();
        form.Cancel();

        Assert.Equal(1, closes);
        Assert.True(form.WasSubmitted);
    }

    [Fact]
    public void A_button_can_close_the_form_the_screen_is_showing()
    {
        // Nothing about this arrives as a key the screen recognises, so the screen has to
        // notice the form closed rather than deciding it did.
        var form = new TuiForm();
        var button = new TuiButton("Save", form.Submit) { IsSelected = true };
        form.Content = button;

        var screen = Screen(form);

        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Key(ConsoleKey.Enter)));
        Assert.True(form.WasSubmitted);
        Assert.False(screen.Outcome!.Cancelled);
    }

    [Fact]
    public void A_widget_that_wanted_the_key_keeps_it()
    {
        // Enter travels outwards from the focused widget, so a field listening for it
        // submits itself rather than the form around it.
        var field = new TuiTextField("hello");
        var submissions = 0;
        field.Submitted = _ => submissions += 1;

        var form = new TuiForm(field);

        Assert.Equal(TuiScreenResult.Continue, Screen(form).HandleInput(Key(ConsoleKey.Enter)));
        Assert.Equal(1, submissions);
        Assert.Equal(TuiFormResult.Open, form.Result);
    }

    [Fact]
    public void A_list_with_nothing_listening_lets_enter_reach_the_form()
    {
        var form = new TuiForm(new TuiList(["a", "b"]));

        Assert.Equal(TuiScreenResult.Exit, Screen(form).HandleInput(Key(ConsoleKey.Enter)));
        Assert.True(form.WasSubmitted);
    }

    [Fact]
    public void A_screen_with_no_form_behaves_as_it_did()
    {
        var screen = Screen(new TuiField("Width", "2") { Id = "Width" });

        Assert.Equal(TuiScreenResult.Exit, screen.HandleInput(Key(ConsoleKey.Enter)));
        Assert.Equal("2", screen.Outcome!.Values["Width"]);
    }

    [Fact]
    public void A_form_names_its_own_window()
    {
        var form = new TuiForm(new TuiTextWidget("body")) { Title = "Reactor Block" };
        var frame = new TuiDeclarativeScreen(form, []).Render(new TuiSize(30, 3));

        Assert.Contains("Reactor Block", frame.Buffer.RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_title_wins_over_the_form_s_own()
    {
        var form = new TuiForm(new TuiTextWidget("body")) { Title = "Reactor Block" };
        var frame = new TuiDeclarativeScreen(form, [], title: "Told otherwise").Render(new TuiSize(30, 3));

        Assert.Contains("Told otherwise", frame.Buffer.RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_can_write_a_form_with_handlers()
    {
        var registry = TuiWidgetRegistry.CreateDefault();

        Assert.True(registry.Knows("form"));
        Assert.Contains("form", registry.Names);
    }
}
