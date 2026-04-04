namespace Tosh.Core;

public sealed class DisplayProfile
{
    private readonly List<TableCase> _selectableColumnCases = [];
    private readonly List<TableCase> _tableCases = [];
    private readonly Type _targetType;
    private readonly List<ValueCase> _valueCases = [];

    public DisplayProfile(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        _targetType = targetType;
    }

    public Type TargetType => _targetType;

    public static DisplayProfile For<T>() => new(typeof(T));

    public DisplayProfile AddValueCase(DisplaySurface surfaces, Func<DisplayValueContext, string> render)
    {
        return AddValueCase(surfaces, _ => true, render);
    }

    public DisplayProfile AddValueCase(
        DisplaySurface surfaces,
        Func<DisplayValueContext, bool> when,
        Func<DisplayValueContext, string> render)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(render);
        _valueCases.Add(new ValueCase(surfaces, when, render));
        return this;
    }

    public DisplayProfile AddTableCase(Func<DisplayTableContext, IReadOnlyList<DisplayTableColumn>> buildColumns)
    {
        return AddTableCase(_ => true, buildColumns);
    }

    public DisplayProfile AddTableCase(
        Func<DisplayTableContext, bool> when,
        Func<DisplayTableContext, IReadOnlyList<DisplayTableColumn>> buildColumns)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(buildColumns);
        _tableCases.Add(new TableCase(when, buildColumns));
        return this;
    }

    public DisplayProfile AddSelectableTableColumns(Func<DisplayTableContext, IReadOnlyList<DisplayTableColumn>> buildColumns)
    {
        return AddSelectableTableColumns(_ => true, buildColumns);
    }

    public DisplayProfile AddSelectableTableColumns(
        Func<DisplayTableContext, bool> when,
        Func<DisplayTableContext, IReadOnlyList<DisplayTableColumn>> buildColumns)
    {
        ArgumentNullException.ThrowIfNull(when);
        ArgumentNullException.ThrowIfNull(buildColumns);
        _selectableColumnCases.Add(new TableCase(when, buildColumns));
        return this;
    }

    internal bool AppliesTo(Type actualType) => _targetType.IsAssignableFrom(actualType);

    internal bool TryBuildTable(DisplayTableContext context, out IReadOnlyList<DisplayTableColumn> columns)
    {
        foreach (var tableCase in _tableCases)
        {
            if (!tableCase.When(context))
            {
                continue;
            }

            columns = tableCase.BuildColumns(context);
            return true;
        }

        columns = Array.Empty<DisplayTableColumn>();
        return false;
    }

    internal bool TryBuildSelectableColumns(DisplayTableContext context, out IReadOnlyList<DisplayTableColumn> columns)
    {
        foreach (var tableCase in _selectableColumnCases)
        {
            if (!tableCase.When(context))
            {
                continue;
            }

            columns = tableCase.BuildColumns(context);
            return true;
        }

        columns = Array.Empty<DisplayTableColumn>();
        return false;
    }

    internal bool TryRender(DisplayValueContext context, out string text)
    {
        foreach (var valueCase in _valueCases)
        {
            if ((valueCase.Surfaces & context.Surface) == 0 || !valueCase.When(context))
            {
                continue;
            }

            text = valueCase.Render(context);
            return true;
        }

        text = string.Empty;
        return false;
    }

    private sealed record TableCase(
        Func<DisplayTableContext, bool> When,
        Func<DisplayTableContext, IReadOnlyList<DisplayTableColumn>> BuildColumns);

    private sealed record ValueCase(
        DisplaySurface Surfaces,
        Func<DisplayValueContext, bool> When,
        Func<DisplayValueContext, string> Render);
}
