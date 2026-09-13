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

        registry.Register("text", static (spec, _) => new TuiTextWidget(spec.Primary?.ToString() ?? string.Empty)
        {
            Style = spec.Style(),
            Wrap = spec.Flag("wrap", true),
        });

        registry.Register("list", static (spec, _) =>
        {
            var list = new TuiList(spec.PrimaryItems())
            {
                DisplayProperty = spec.Text("display"),
                MultiSelect = spec.Flag("multi"),
                Style = spec.Style(),
            };

            // A list is nearly always taller than its pane, so it arrives scrollable.
            // Asking every author to remember that is the kind of ritual this replaces.
            TuiList.Scrollable(list);

            return list.Viewport!;
        });

        registry.Register("field", static (spec, _) =>
        {
            // A labelled input, because a bare field with no label is not what anyone
            // means when they ask for one.
            var field = new TuiTextField(spec.Text("value") ?? string.Empty)
            {
                Placeholder = spec.Text("placeholder"),
                Mask = spec.Flag("password"),
                Multiline = spec.Flag("multiline"),
                Id = spec.Text("id"),
            };

            var label = spec.Primary?.ToString();

            if (string.IsNullOrEmpty(label))
            {
                return field;
            }

            return new TuiStack(TuiOrientation.Horizontal)
                .Add(new TuiTextWidget($"{label}: ") { Style = new TuiStyle(Attributes: TuiTextAttributes.Dim) }, TuiLength.Auto)
                .Add(field, TuiLength.Star());
        });

        registry.Register("button", static (spec, _) => new TuiButton(spec.Primary?.ToString() ?? "OK")
        {
            Style = spec.Style(),
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
