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
            widget.Id = spec.Text("id");
            return true;
        }

        widget = null!;
        return false;
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
            MinimumScale = spec.Number("scale", 0),
            Capacity = spec.Number("capacity", 0),
            PadLeft = spec.Flag("pad", true),
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

            if (spec.Callable("id") is { } identify)
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

        registry.Register("button", static (spec, context) =>
        {
            var button = new TuiButton(spec.PrimaryText() ?? "OK") { Style = spec.Style() };

            context.OnHandler(spec, "onpress", handler => button.Pressed = () => handler(null));

            return button;
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
