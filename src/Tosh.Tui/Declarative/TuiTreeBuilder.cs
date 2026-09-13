using Tosh.Runtime;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>What a node is allowed to ask for while it is being built.</summary>
public sealed class TuiBuildContext
{
    private readonly TuiWidgetRegistry _registry;
    private readonly List<TuiBinding> _bindings = [];
    private readonly Func<IShellCallable, object?, object?>? _invoke;

    internal TuiBuildContext(TuiWidgetRegistry registry, Func<IShellCallable, object?, object?>? invoke)
    {
        _registry = registry;
        _invoke = invoke;
    }

    /// <summary>
    /// Calls a script function, for a handler attached to a widget.
    /// </summary>
    /// <remarks>
    /// Handlers run when something happens rather than when something is drawn, which is
    /// the difference between them and a pull binding. With no invoker — a tree built in
    /// a test, say — a handler is simply inert rather than an error.
    /// </remarks>
    public object? Invoke(IShellCallable callable, object? argument)
        => _invoke?.Invoke(callable, argument);

    /// <summary>Wires a handler from a node's key, if one was written there.</summary>
    public void OnHandler(TuiWidgetSpec spec, string key, Action<Func<object?, object?>> attach)
    {
        if (spec.Callable(key) is { } handler)
        {
            attach(argument => Invoke(handler, argument));
        }
    }

    /// <summary>The properties that are re-read from a script function each redraw.</summary>
    public IReadOnlyList<TuiBinding> Bindings => _bindings;

    internal void Bind(TuiWidget widget, IShellCallable source, Action<TuiWidget, object?> apply)
        => _bindings.Add(new TuiBinding(widget, source, apply));

    /// <summary>Builds a node's children.</summary>
    public IReadOnlyList<TuiWidget> BuildChildren(IReadOnlyList<object?> nodes)
        => [.. BuildChildrenWithLengths(nodes).Select(child => child.Widget)];

    /// <summary>Builds a node's children, each with the size its parent should give it.</summary>
    public IReadOnlyList<(TuiWidget Widget, TuiLength Length)> BuildChildrenWithLengths(
        IReadOnlyList<object?> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var built = new List<(TuiWidget, TuiLength)>();

        foreach (var node in nodes)
        {
            if (TuiTreeBuilder.TryBuildNode(node, _registry, this, out var widget, out var spec))
            {
                // A child says how much room it wants; a stack of things with no opinion
                // shares the space evenly, which is what a list of panes usually means.
                built.Add((widget, spec?.Length("size", TuiLength.Star()) ?? TuiLength.Star()));
            }
        }

        return built;
    }
}

/// <summary>
/// Turns a record tree into a widget tree.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the "markup" idea. A record literal already nests, already
/// carries a <see cref="TimeSpan"/> from <c>1s</c> and an <see cref="IShellCallable"/>
/// from <c>&amp;func</c>, and already reports its errors through the diagnostics a script
/// author knows. A separate file format would have been a second parser and a second
/// error reporter buying none of that (<c>TUI-0004</c>).
/// </para>
/// <para>
/// The key that names a widget carries its main argument, so a node reads as the thing
/// it makes:
/// </para>
/// <code>
/// {|
///     Title  = "Reactor Block"
///     Column = [
///         {| List  = $types, Id = "ReactorType", Display = "Name" |},
///         {| Field = "Reactors wide", Id = "Width", Value = "2" |}
///     ]
/// |}
/// </code>
/// </remarks>
public static class TuiTreeBuilder
{
    /// <summary>Builds the widget tree a record describes.</summary>
    /// <param name="node">A record, as a dictionary.</param>
    /// <param name="registry">The widgets that may be named, or the default set.</param>
    public static TuiWidget Build(object? node, TuiWidgetRegistry? registry = null)
        => Build(node, registry, null, out _);

    /// <summary>Builds the tree, and reports the properties bound to script functions.</summary>
    public static TuiWidget Build(
        object? node,
        TuiWidgetRegistry? registry,
        out IReadOnlyList<TuiBinding> bindings)
        => Build(node, registry, null, out bindings);

    /// <summary>Builds the tree, wiring handlers through <paramref name="invoke"/>.</summary>
    public static TuiWidget Build(
        object? node,
        TuiWidgetRegistry? registry,
        Func<IShellCallable, object?, object?>? invoke,
        out IReadOnlyList<TuiBinding> bindings)
    {
        var effective = registry ?? TuiWidgetRegistry.CreateDefault();
        var context = new TuiBuildContext(effective, invoke);

        if (!TryBuildNode(node, effective, context, out var widget, out _))
        {
            throw new ArgumentException(
                Describe(node, effective),
                nameof(node));
        }

        bindings = context.Bindings;
        return widget;
    }

    internal static bool TryBuildNode(
        object? node,
        TuiWidgetRegistry registry,
        TuiBuildContext context,
        out TuiWidget widget,
        out TuiWidgetSpec? spec)
    {
        spec = null;
        widget = null!;

        if (node is TuiWidget already)
        {
            // A tree may mix records with widgets built in code. Both are the same tree.
            widget = already;
            return true;
        }

        if (node is null)
        {
            return false;
        }

        if (!TryReadFields(node, out var fields))
        {
            // A bare value in a list of children is text. `Column = ["one", "two"]` means
            // what it looks like.
            widget = new TuiTextWidget(node.ToString() ?? string.Empty);
            return true;
        }

        spec = ToSpec(fields, registry);

        if (spec is null || !registry.TryCreate(spec, context, out widget))
        {
            return false;
        }

        RecordBindings(spec, widget, context);
        return true;
    }

    /// <summary>
    /// Notes every property written as a script function, so it can be re-read.
    /// </summary>
    /// <remarks>
    /// The bindable keys are the ones that carry content: a node's own name, and the
    /// value, items or title it was given. A handler like <c>OnSelect</c> is a callable
    /// too and is deliberately not bound — it is called when something happens, not read
    /// when something is drawn.
    /// </remarks>
    private static void RecordBindings(TuiWidgetSpec spec, TuiWidget widget, TuiBuildContext context)
    {
        foreach (var key in (string[])[spec.Name, "value", "items", "title", "content"])
        {
            if (spec.Callable(key) is { } source)
            {
                context.Bind(widget, source, Assign);
                return;
            }
        }
    }

    /// <summary>Puts a re-read value back into whichever widget asked for it.</summary>
    private static void Assign(TuiWidget widget, object? value)
    {
        switch (widget)
        {
            case TuiTextWidget text:
                text.Text = value?.ToString() ?? string.Empty;
                return;

            case TuiTextField field:
                field.Text = value?.ToString() ?? string.Empty;
                return;

            case TuiBorder border:
                border.Title = value?.ToString();
                return;

            case TuiList list:
                list.Items = AsItems(value);
                return;

            case TuiScroll { Child: TuiList scrolled }:
                scrolled.Items = AsItems(value);
                return;
        }

        // A labelled field is a row around an input; the input is what was meant.
        foreach (var child in widget.Children)
        {
            if (child is TuiTextField nested)
            {
                nested.Text = value?.ToString() ?? string.Empty;
                return;
            }
        }
    }

    private static IReadOnlyList<object?> AsItems(object? value)
        => value switch
        {
            null => [],
            string text => [text],
            System.Collections.IEnumerable sequence => sequence.Cast<object?>().ToArray(),
            _ => [value],
        };

    /// <summary>Finds the key that names a widget, and treats the rest as its properties.</summary>
    private static TuiWidgetSpec? ToSpec(IDictionary<string, object?> fields, TuiWidgetRegistry registry)
    {
        foreach (var entry in fields)
        {
            if (registry.Knows(entry.Key))
            {
                return new TuiWidgetSpec(entry.Key, entry.Value, fields);
            }
        }

        // A node with no widget name but with children is a column, which is what a bare
        // screen body means.
        foreach (var name in (string[])["column", "row"])
        {
            if (fields.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
            {
                return new TuiWidgetSpec(name, fields[name], fields);
            }
        }

        return null;
    }

    /// <summary>Reads a record, a dictionary, or anything else shaped like one.</summary>
    private static bool TryReadFields(object node, out IDictionary<string, object?> fields)
    {
        if (node is IDictionary<string, object?> dictionary)
        {
            fields = dictionary;
            return true;
        }

        if (ShellRecordUtilities.TryGetFields(node, out var record))
        {
            fields = record.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        fields = null!;
        return false;
    }

    private static string Describe(object? node, TuiWidgetRegistry registry)
    {
        if (node is null)
        {
            return "A screen needs a widget tree; nothing was given.";
        }

        return $"No widget was named in this node. Name one of: {string.Join(", ", registry.Names)}.";
    }
}
