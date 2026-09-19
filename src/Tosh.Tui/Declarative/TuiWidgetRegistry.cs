using Tosh.Runtime;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>
/// The widgets a declarative tree can name, and how to build each one.
/// </summary>
/// <remarks>
/// <para>
/// A registry rather than an enum. <c>TuiWidgetKind</c> had six values and a 753-line
/// switch that interpreted them, which made the set of widgets a script could use closed
/// by construction: adding one meant editing the shell (<c>TUI-0002</c>).
/// </para>
/// <para>
/// Names are matched case-insensitively, because a script author writing
/// <c>{| text = "hi" |}</c> has not made a mistake worth a diagnostic.
/// </para>
/// </remarks>
public sealed class TuiWidgetRegistry
{
    private readonly Dictionary<string, Func<TuiWidgetSpec, TuiBuildContext, TuiWidget>> _factories
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a name is one this registry can build.</summary>
    public bool Knows(string name) => _factories.ContainsKey(name);

    /// <summary>The names this registry can build, for diagnostics.</summary>
    public IReadOnlyList<string> Names => [.. _factories.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>Registers a widget under a name, replacing any previous one.</summary>
    public TuiWidgetRegistry Register(string name, Func<TuiWidgetSpec, TuiBuildContext, TuiWidget> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        _factories[name] = factory;
        return this;
    }

    /// <summary>Builds the widget a node names.</summary>
    public bool TryCreate(TuiWidgetSpec spec, TuiBuildContext context, out TuiWidget widget)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_factories.TryGetValue(spec.Name, out var factory))
        {
            widget = factory(spec, context);

            // Read here rather than in each factory, because these belong to every widget.
            // A key a factory has to remember is a key half the widgets will not have.
            widget.Id = spec.Text("id");

            // Set here as well as read by a parent stack, because `Size` is a property of
            // the widget and a node that carries one means it wherever it sits. Read only
            // by the parent, a size written on a root or on a box's only child was quietly
            // ignored. A stack still overrides it, which is how a child with no opinion
            // ends up sharing the leftovers.
            if (spec.Has("size"))
            {
                widget.Size = spec.Length("size", TuiLength.Auto);
            }

            widget.Padding = spec.Thickness("padding");
            widget.Align = spec.Alignment("align");
            widget.VerticalAlign = spec.VerticalAlignment("valign");
            widget.Keys = ReadKeys(spec, context);
            widget.Feeds = ReadFeeds(spec, context);
            widget.IsVisible = spec.Flag("visible", true);
            widget.VisibleSource = spec.Callable("when");

            return true;
        }

        widget = null!;
        return false;
    }

    /// <summary>
    /// Reads a menu: its name, and the commands under it.
    /// </summary>
    /// <remarks>
    /// <code>
    /// {| Menu = "File", Items = [
    ///        {| Item = "Open",  Key = "Ctrl+O", Do = &amp;Open |},
    ///        {| Separator = true |},
    ///        {| Item = "Quit",  Key = "q",      Do = &amp;Quit |}
    ///    ] |}
    /// </code>
    /// <c>Key</c> is shown, not registered: the key belongs to whatever screen the menu is
    /// on, and one registered here would answer only while the menu was open.
    /// </remarks>
    private static TuiMenu BuildMenu(TuiWidgetSpec spec, TuiBuildContext context)
    {
        var menu = new TuiMenu(spec.PrimaryText() ?? string.Empty) { Style = spec.Style() };

        if (!spec.TryGet("items", out var declared) || declared is not IEnumerable<object?> nodes)
        {
            return menu;
        }

        foreach (var node in nodes)
        {
            if (ShellRecordUtilities.TryGetValue(node, "Separator", out var line) && line is true)
            {
                menu.AddSeparator();
                continue;
            }

            if (!ShellRecordUtilities.TryGetValue(node, "Item", out var label) || label is null)
            {
                continue;
            }

            var handler = ShellRecordUtilities.TryGetValue(node, "Do", out var action)
                ? action as IShellCallable
                : null;

            menu.Add(new TuiMenuItem(
                label.ToString() ?? string.Empty,
                ShellRecordUtilities.TryGetValue(node, "Key", out var key) ? key?.ToString() ?? string.Empty : string.Empty,
                handler is null ? null : () => context.Invoke(handler, null),
                !(ShellRecordUtilities.TryGetValue(node, "Enabled", out var enabled) && enabled is false)));
        }

        return menu;
    }

    /// <summary>Registers a group of keys under a condition, and says whether any landed.</summary>
    private static bool Conditionally(
        TuiShortcuts keys,
        IShellCallable applies,
        TuiBuildContext context,
        Func<bool> register)
    {
        var registered = false;

        keys.When(
            () => context.Invoke(applies, null) is not (null or false or 0),
            () => registered = register());

        return registered;
    }

    /// <summary>
    /// Registers one key, whichever of the five things it turned out to be.
    /// </summary>
    /// <returns>Whether it was any of them.</returns>
    private static bool Register(
        TuiShortcuts keys,
        object? binding,
        string text,
        string label,
        string describes,
        bool exits,
        IShellCallable? handler,
        TuiBuildContext context)
    {
        // A key can aim at part of the screen instead of at a function: `Focus` moves the
        // keyboard to a widget by id, `Press` does what Enter on it would do, and `Back`
        // puts the keyboard where it was. The screen resolves all three, because the focus
        // manager is its and should stay its.
        if (Aimed(binding, "Focus") is { } focused)
        {
            keys.Focus(text, label, describes, focused);
            return true;
        }

        if (Aimed(binding, "Press") is { } pressed)
        {
            keys.Press(text, label, describes, pressed);
            return true;
        }

        if (ShellRecordUtilities.TryGetValue(binding, "Back", out var back) && back is true)
        {
            keys.Back(text, label, describes);
            return true;
        }

        if (exits)
        {
            if (handler is null)
            {
                keys.Exit(text, label, describes);
            }
            else
            {
                keys.Exit(text, label, describes, () => context.Invoke(handler, null));
            }

            return true;
        }

        if (handler is null)
        {
            return false;
        }

        keys.On(text, label, describes, () => context.Invoke(handler, null));
        return true;
    }

    /// <summary>The widget id a key aims at, when it names one.</summary>
    private static string? Aimed(object? binding, string key)
        => ShellRecordUtilities.TryGetValue(binding, key, out var id) && id?.ToString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>
    /// Reads the sources a node is fed by.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Feed = [ {| Source = $lines, Do = &amp;Append, OnEnd = &amp;Finished |} ]
    /// </code>
    /// A channel, an async sequence, a task or a plain sequence. Each is read on a task of
    /// its own and each value is handed to <c>Do</c> on the loop's thread, so the handler
    /// is written exactly like one answering a keystroke: no lock, no thread to think
    /// about, and a redraw when it returns.
    /// </remarks>
    private static TuiFeeds? ReadFeeds(TuiWidgetSpec spec, TuiBuildContext context)
    {
        if (!spec.TryGet("feed", out var declared))
        {
            return null;
        }

        // One source is written as one record rather than a list of one, because that is
        // what a reader with one source writes.
        var nodes = declared is IEnumerable<object?> many ? many : [declared];

        var feeds = new TuiFeeds();
        var any = false;

        foreach (var node in nodes)
        {
            if (node is null)
            {
                continue;
            }

            var source = ShellRecordUtilities.TryGetValue(node, "Source", out var from) ? from : null;
            var handler = ShellRecordUtilities.TryGetValue(node, "Do", out var action)
                ? action as IShellCallable
                : null;

            if (handler is null)
            {
                continue;
            }

            var ended = ShellRecordUtilities.TryGetValue(node, "OnEnd", out var finished)
                ? finished as IShellCallable
                : null;

            feeds.From(
                source,
                value => context.Invoke(handler, value),
                ended is null ? null : () => context.Invoke(ended, null));

            any = true;
        }

        return any ? feeds : null;
    }

    /// <summary>
    /// Reads the keys a node registers.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Keys = [
    ///     {| Key = "ctrl+s", Does = "save",  Do = &amp;Save |},
    ///     {| Key = "q",      Does = "quit",  Exit = true  |}
    /// ]
    /// </code>
    /// On the node they belong to rather than on the screen, because where a binding is
    /// written is where it applies: keys on a dialog answer while the dialog is up.
    /// </remarks>
    private static TuiShortcuts? ReadKeys(TuiWidgetSpec spec, TuiBuildContext context)
    {
        if (!spec.TryGet("keys", out var declared) || declared is not IEnumerable<object?> bindings)
        {
            return null;
        }

        var keys = new TuiShortcuts();
        var any = false;

        foreach (var binding in bindings)
        {
            if (!ShellRecordUtilities.TryGetValue(binding, "Key", out var chord) || chord is null)
            {
                continue;
            }

            var text = chord.ToString() ?? string.Empty;
            var label = ShellRecordUtilities.TryGetValue(binding, "Label", out var written) && written is not null
                ? written.ToString() ?? text
                : text;
            var describes = ShellRecordUtilities.TryGetValue(binding, "Does", out var does) && does is not null
                ? does.ToString() ?? string.Empty
                : string.Empty;

            var exits = ShellRecordUtilities.TryGetValue(binding, "Exit", out var exit) &&
                exit is bool flag && flag;

            var handler = ShellRecordUtilities.TryGetValue(binding, "Do", out var action)
                ? action as IShellCallable
                : null;

            // `When` on a key is the condition it applies under — the same word a node uses
            // for whether it is drawn at all. A key that does not apply does not fire and is
            // not described, so a footer cannot offer one the screen would ignore.
            var applies = ShellRecordUtilities.TryGetValue(binding, "When", out var condition)
                ? condition as IShellCallable
                : null;

            var registered = applies is null
                ? Register(keys, binding, text, label, describes, exits, handler, context)
                : Conditionally(keys, applies, context, () =>
                    Register(keys, binding, text, label, describes, exits, handler, context));

            any |= registered;
        }

        return any ? keys : null;
    }

    /// <summary>The built-in widget set.</summary>
    public static TuiWidgetRegistry CreateDefault()
    {
        var registry = new TuiWidgetRegistry();

        registry.Register("text", static (spec, _) => new TuiTextWidget(spec.PrimaryText() ?? string.Empty)
        {
            Style = spec.Style(),
            Wrap = spec.Flag("wrap", true),
        });

        registry.Register("list", static (spec, context) =>
        {
            var list = new TuiList(spec.PrimaryItems())
            {
                DisplayProperty = spec.Text("display"),
                MultiSelect = spec.Flag("multi"),
                Style = spec.Style(),
            };

            context.OnHandler(spec, "onselect", handler => list.Activated = item => handler(item));
            context.OnHandler(spec, "onchange", handler => list.SelectionChanged = _ => handler(list.SelectedItem));

            return list;
        });

        registry.Register("field", static (spec, context) =>
        {
            var widget = new TuiField(spec.PrimaryText() ?? string.Empty, spec.Text("value") ?? string.Empty)
            {
                Placeholder = spec.Text("placeholder"),
                Password = spec.Flag("password"),
                Multiline = spec.Flag("multiline"),
            };

            context.OnHandler(spec, "onchange", handler => widget.Changed = text => handler(text));
            context.OnHandler(spec, "onsubmit", handler => widget.Submitted = text => handler(text));

            return widget;
        });

        registry.Register("lines", static (spec, _) =>
        {
            // A document rather than a paragraph: the lines a caller wrote, kept as written,
            // scrolling themselves. `Text` wraps; this does not.
            var lines = new TuiLines
            {
                Scrollbar = spec.Flag("scrollbar"),
            };

            lines.Lines = [.. spec.PrimaryItems().Select(item => item switch
            {
                TuiSpanLine already => already,
                _ => new TuiSpanLine([new TuiSpan(item?.ToString() ?? string.Empty, spec.Style())]),
            })];

            return lines;
        });

        registry.Register("dock", static (spec, context) =>
        {
            // `Side` beside whatever names the widget. Order decides the corners, so the
            // order written is the order docked — which is the whole of the semantics.
            var dock = new TuiDock();

            foreach (var item in spec.PrimaryItems())
            {
                var built = context.BuildChildren([item]);

                if (built.Count == 0)
                {
                    continue;
                }

                var side = ShellRecordUtilities.TryGetValue(item, "Side", out var written)
                    ? written?.ToString()?.ToLowerInvariant()
                    : null;

                dock.Add(built[0], side switch
                {
                    "top" => TuiDockSide.Top,
                    "bottom" => TuiDockSide.Bottom,
                    "left" => TuiDockSide.Left,
                    "right" => TuiDockSide.Right,
                    _ => TuiDockSide.Fill,
                });
            }

            return dock;
        });

        registry.Register("grid", static (spec, context) =>
        {
            // A list of rows, each a list of cells:
            //
            //     {| Grid = [ [ {| Text = "Name"  |}, {| Text = "tosh"  |} ],
            //                 [ {| Text = "Cache" |}, {| Text = "11 MB" |} ] ],
            //        Columns = "auto, *" |}
            //
            // Position rather than `Row = 0, Column = 1` keys, for a reason worth writing
            // down: `Row` and `Column` are already the names of the two stack widgets, and
            // markup picks the widget out of a node's keys — so `Row = 0` built a horizontal
            // stack containing the number nought. Keys and widget names share one namespace
            // here, and a placement key can only be one that no widget answers to.
            var grid = new TuiGrid(spec.Text("columns"), spec.Text("rows"))
            {
                ColumnGap = spec.Number("columngap", 1),
                RowGap = spec.Number("rowgap", 0),
            };

            var row = 0;

            foreach (var line in spec.PrimaryItems())
            {
                var column = 0;

                // A row written as a bare node rather than a list of them is one cell, which
                // is what a single-column grid looks like when somebody writes it.
                foreach (var cell in Cells(line))
                {
                    var built = context.BuildChildren([cell]);

                    if (built.Count == 0)
                    {
                        column += 1;
                        continue;
                    }

                    grid.Add(
                        built[0],
                        row,
                        column,
                        Span(cell, "RowSpan"),
                        Span(cell, "ColumnSpan"));

                    column += 1;
                }

                row += 1;
            }

            return grid;

            static IEnumerable<object?> Cells(object? line)
                => line is System.Collections.IEnumerable items and not string &&
                   !ShellRecordUtilities.IsRecordLike(line)
                    ? items.Cast<object?>()
                    : [line];

            static int Span(object? cell, string key)
                => ShellRecordUtilities.TryGetValue(cell, key, out var value) &&
                   TypeConversion.TryConvert(value, typeof(int), out var number)
                    ? Math.Max(1, (int)number!)
                    : 1;
        });

        registry.Register("tabs", static (spec, context) =>
        {
            // `{| Tabs = [ {| Label = "Log", Log = [...] |}, ... ] |}` — each item is a
            // node like any other, with `Label` beside whatever names the widget. Writing
            // the content under a `Content` key as well would mean two spellings for the
            // same nesting, and the one already in the language is the node itself.
            var tabs = new List<TuiTab>();

            foreach (var item in spec.PrimaryItems())
            {
                var built = context.BuildChildren([item]);

                if (built.Count == 0)
                {
                    continue;
                }

                var label = ShellRecordUtilities.TryGetValue(item, "Label", out var written)
                    ? written?.ToString()
                    : null;

                tabs.Add(new TuiTab(label ?? $"{tabs.Count + 1}", built[0]));
            }

            var widget = new TuiTabs(tabs)
            {
                Gap = spec.Number("gap", 2),
                Underline = spec.Flag("underline", true),
                Title = spec.Text("title"),
                Style = spec.Style(),
            };

            widget.Selected = spec.Number("selected", 0);

            context.OnHandler(spec, "onchange", handler => widget.Changed = index => handler(index));

            return widget;
        });

        registry.Register("scroll", static (spec, context) =>
        {
            // For a child that draws itself at full height and has no scrolling of its own.
            // A list, a table, a tree and a document all scroll themselves; this is for
            // everything else.
            var children = context.BuildChildren(spec.PrimaryItems());

            return new TuiScroll(children.Count == 1 ? children[0] : Wrap(children, TuiOrientation.Vertical));
        });

        registry.Register("spark", static (spec, _) => new TuiSparkline(spec.Numbers())
        {
            Style = spec.Style(),
            MinimumScale = spec.Number("scale", 0d),
            Capacity = spec.Number("capacity", 0),
            PadLeft = spec.Flag("pad", true),
        });

        registry.Register("chart", static (spec, _) => new TuiChart(spec.Numbers())
        {
            Style = spec.Style(),
            MinimumScale = spec.Number("scale", 0d),
            Capacity = spec.Number("capacity", 0),
            Minimum = spec.Has("min") ? spec.Number("min", 0d) : null,
            Maximum = spec.Has("max") ? spec.Number("max", 0d) : null,
            Stretch = spec.Flag("stretch", true),
            ShowAxis = spec.Flag("axis", true),
            ShowHorizontalAxis = spec.Flag("xaxis", true),
            Marks = spec.Text("marks")?.ToLowerInvariant() switch
            {
                "braille" or "line" or "dots" => TuiChartMarks.Braille,
                "ascii" => TuiChartMarks.Ascii,
                _ => TuiChartMarks.Blocks,
            },
        });

        registry.Register("bars", static (spec, _) =>
        {
            // `{| Label = …, Value = … |}` per row, or a record whose fields are the rows:
            // `{| Bars = {% "disk" => 40, "swap" => 5 %} |}` reads better for a fixed set.
            var bars = new List<TuiBar>();

            foreach (var item in spec.PrimaryItems())
            {
                if (ShellRecordUtilities.TryGetValue(item, "Label", out var label) &&
                    ShellRecordUtilities.TryGetValue(item, "Value", out var value) &&
                    TypeConversion.TryConvert(value, typeof(double), out var amount))
                {
                    bars.Add(new TuiBar(label?.ToString() ?? string.Empty, (double)amount!));
                }
            }

            if (bars.Count == 0 && ShellRecordUtilities.TryGetFields(spec.Primary, out var rows))
            {
                // Written as a loop rather than a query because `out _` inside one of these
                // factories does not discard: the factory's second parameter is named `_`,
                // so the discard binds to it and the call fails to compile with a message
                // about a build context nobody wrote.
                foreach (var field in rows)
                {
                    if (TypeConversion.TryConvert(field.Value, typeof(double), out var amount))
                    {
                        bars.Add(new TuiBar(field.Key, (double)amount!));
                    }
                }
            }

            return new TuiBars(bars)
            {
                MinimumScale = spec.Number("scale", 0),
                ShowValues = spec.Flag("values", true),
                FilledStyle = spec.Style(),
                Gap = spec.Number("gap", 1),

                // `Vertical = true` rather than an `Orientation` word, because a row and a
                // column are what `Row` and `Column` already mean and reusing those here
                // would read as a container.
                Orientation = spec.Flag("vertical")
                    ? TuiBarsOrientation.Vertical
                    : TuiBarsOrientation.Horizontal,
            };
        });

        registry.Register("gauge", static (spec, _) => new TuiGauge(spec.Number("value", 0))
        {
            Minimum = spec.Number("min", 0),
            Maximum = spec.Number("max", 100),
            Label = spec.Text("label"),
            FilledStyle = spec.Style(),
        });

        registry.Register("tree", static (spec, context) =>
        {
            var tree = new TuiTree(spec.Primary)
            {
                ShowRoot = spec.Flag("root", true),
                Scrollbar = spec.Flag("scrollbar"),
            };

            // `Children = &Kids` is a question asked of a node, not a pull binding on the
            // screen's values — so it is wired here rather than through TuiBindings.
            if (spec.Callable("children") is { } children)
            {
                tree.ChildrenOf = node => context.Invoke(children, node) as IEnumerable<object?> ?? [];
            }

            if (spec.Callable("display") is { } display)
            {
                tree.Display = node => context.Invoke(display, node)?.ToString() ?? string.Empty;
            }

            // `Key`, not `Id`: every widget's `Id` is the name its value is reported under,
            // and a tree needs a second, different identity — the one expansion is
            // remembered against. Two meanings on one key is a key nobody can read.
            if (spec.Callable("key") is { } identify)
            {
                tree.Identify = node => context.Invoke(identify, node)?.ToString() ?? string.Empty;
            }

            context.OnHandler(spec, "onselect", handler => tree.Activated = node => handler(node));
            context.OnHandler(spec, "onchange", handler => tree.SelectionChanged = node => handler(node));

            return tree;
        });

        registry.Register("table", static (spec, context) =>
        {
            var table = new TuiTable(spec.PrimaryItems())
            {
                ShowHeader = spec.Flag("header", true),
                Scrollbar = spec.Flag("scrollbar"),
            };

            // `Borders = "all"`, `"header"` or `"none"` — and a bare `true` means the grid,
            // because that is what someone writing `Borders = true` is asking for.
            if (spec.TryGet("borders", out var borders) && borders is not null)
            {
                table.Borders = borders switch
                {
                    bool on => on ? TuiTableBorders.All : TuiTableBorders.None,
                    TuiTableBorders already => already,
                    _ => Enum.TryParse<TuiTableBorders>(borders.ToString(), ignoreCase: true, out var parsed)
                        ? parsed
                        : TuiTableBorders.None,
                };
            }

            // `Columns = ["Name", "Length"]` names them; a record per column says more.
            if (spec.TryGet("columns", out var declared) && declared is IEnumerable<object?> columns)
            {
                table.Columns = [.. columns.Select(ToColumn).Where(column => column is not null)!];
            }

            context.OnHandler(spec, "onselect", handler => table.Activated = row => handler(row));
            context.OnHandler(spec, "onchange", handler => table.SelectionChanged = _ => handler(table.SelectedRow));

            return table;
        });

        registry.Register("check", static (spec, context) =>
        {
            var widget = new TuiCheck(spec.PrimaryText() ?? string.Empty, spec.Flag("value"))
            {
                Style = spec.Style()
            };

            context.OnHandler(spec, "onchange", handler => widget.Toggled = val => handler(val));

            return widget;
        });

        registry.Register("button", static (spec, context) =>
        {
            var button = new TuiButton(spec.PrimaryText() ?? "OK") { Style = spec.Style() };

            context.OnHandler(spec, "onpress", handler => button.Pressed = () => handler(null));

            // `Exit = true`, spelled the same way a key is. A markup screen has no handle
            // on its own form, so without this a dialog could raise itself and never let
            // the reader out of one.
            if (spec.Flag("exit"))
            {
                var pressed = button.Pressed;

                button.Pressed = () =>
                {
                    pressed?.Invoke();
                    button.ClosesScreen = true;
                };
            }

            return button;
        });

        registry.Register("image", static (spec, _) => new TuiImage
        {
            Path = spec.PrimaryText(),
            Fit = spec.Text("fit")?.ToLowerInvariant() switch
            {
                "crop" => TuiImageFit.Crop,
                "stretch" => TuiImageFit.Stretch,
                _ => TuiImageFit.Letterbox,
            },
            Placeholder = spec.Text("placeholder") ?? string.Empty,
            Loading = spec.Text("loading") ?? "\u2026",
            Style = spec.Style(),
        });

        registry.Register("spinner", static (spec, context) =>
        {
            var spinner = new TuiSpinner(spec.PrimaryText() ?? string.Empty)
            {
                Style = TuiSpinnerStyle.Named(spec.Text("frames")),
                Idle = spec.Text("idle") ?? " ",
                IsSpinning = spec.Flag("spinning", true),
                TextStyle = spec.Style(),
            };

            // `Spinning = &Busy` rather than a fixed true: whether something is still
            // happening is exactly the thing that changes while the screen is up.
            if (spec.Callable("spinning") is { } busy)
            {
                spinner.SpinningWhen = () => context.Invoke(busy, null) is not (null or false or 0);
            }

            return spinner;
        });

        registry.Register("combo", static (spec, context) =>
        {
            var combo = new TuiCombo(spec.PrimaryItems())
            {
                DisplayProperty = spec.Text("display"),
                Placeholder = spec.Text("placeholder") ?? "\u2014",
                Style = spec.Style(),
            };

            context.OnHandler(spec, "onchange", handler => combo.Changed = item => handler(item));

            return combo;
        });

        registry.Register("help", static (spec, context) =>
        {
            var help = new TuiHelp
            {
                Full = spec.Flag("full"),
                Separator = spec.Text("separator") ?? "   ",
                Style = spec.Style(),
            };

            // `Full = &Everything` rather than a fixed true or false: whether the reader
            // wants the list is something a key toggles, so the widget asks.
            if (spec.Callable("full") is { } everything)
            {
                help.FullWhen = () => context.Invoke(everything, null) is not (null or false or 0);
            }

            return help;
        });

        registry.Register("menu", static (spec, context) => BuildMenu(spec, context));

        registry.Register("menubar", static (spec, context) =>
        {
            var bar = new TuiMenuBar { Gap = spec.Number("gap", 0), Style = spec.Style() };

            // Each child is a menu node, built through the registry rather than by hand, so
            // a bar of menus and a menu on its own are the same thing.
            foreach (var child in context.BuildChildren(spec.PrimaryItems()))
            {
                if (child is TuiMenu menu)
                {
                    bar.Add(menu);
                }
            }

            return bar;
        });

        registry.Register("form", static (spec, context) =>
        {
            var children = context.BuildChildren(spec.PrimaryItems());

            var form = new TuiForm(children.Count == 1 ? children[0] : Wrap(children, TuiOrientation.Vertical))
            {
                Title = spec.Text("title"),
            };

            context.OnHandler(spec, "onsubmit", handler => form.Submitted = sender => handler(sender));
            context.OnHandler(spec, "oncancel", handler => form.Cancelled = sender => handler(sender));

            return form;
        });

        registry.Register("overlay", static (spec, context) =>
        {
            var children = context.BuildChildren(spec.PrimaryItems());

            // The first child is the page and the second is what sits on top of it, which
            // is the order they are written in and the order they are drawn in.
            return new TuiOverlay(
                children.Count > 0 ? children[0] : null,
                children.Count > 1 ? children[1] : null)
            {
                Margin = spec.Number("margin", 2),
            };
        });

        registry.Register("row", static (spec, context) => Stack(spec, context, TuiOrientation.Horizontal));
        registry.Register("column", static (spec, context) => Stack(spec, context, TuiOrientation.Vertical));

        registry.Register("box", static (spec, context) =>
        {
            var children = context.BuildChildren(spec.PrimaryItems());

            return new TuiBorder(children.Count == 1 ? children[0] : Wrap(children, TuiOrientation.Vertical))
            {
                Title = spec.Text("title"),
                Style = spec.Style(),
            };
        });

        return registry;
    }

    /// <summary>Reads one column of a table, written as a name or as a record.</summary>
    private static TuiColumn? ToColumn(object? value)
    {
        if (value is TuiColumn already)
        {
            return already;
        }

        if (value is string name)
        {
            return new TuiColumn(name);
        }

        if (!ShellRecordUtilities.TryGetValue(value, "Header", out var header) &&
            !ShellRecordUtilities.TryGetValue(value, "Name", out header))
        {
            return null;
        }

        var column = new TuiColumn(header?.ToString() ?? string.Empty);

        if (ShellRecordUtilities.TryGetValue(value, "Property", out var property) && property is not null)
        {
            column.Property = property.ToString();
        }

        if (ShellRecordUtilities.TryGetValue(value, "Width", out var width) && width is not null)
        {
            column.Width = width is int cells ? TuiLength.Fixed(cells) : TuiLength.Parse(width.ToString());
        }

        if (ShellRecordUtilities.TryGetValue(value, "Align", out var align) &&
            Enum.TryParse<TuiAlignment>(align?.ToString(), ignoreCase: true, out var alignment))
        {
            column.Align = alignment;
        }

        return column;
    }

    private static TuiWidget Stack(TuiWidgetSpec spec, TuiBuildContext context, TuiOrientation orientation)
    {
        var stack = new TuiStack(orientation) { Gap = spec.Number("gap", 0) };

        foreach (var child in context.BuildChildrenWithLengths(spec.PrimaryItems()))
        {
            stack.Add(child.Widget, child.Length);
        }

        return stack;
    }

    private static TuiWidget Wrap(IReadOnlyList<TuiWidget> children, TuiOrientation orientation)
    {
        var stack = new TuiStack(orientation);

        foreach (var child in children)
        {
            stack.Add(child);
        }

        return stack;
    }
}
