namespace Tosh.Runtime;

public abstract class ShellControlFlowException : Exception
{
    protected ShellControlFlowException(TextSpan span)
    {
        Span = span;
    }

    public TextSpan Span { get; }
}

public sealed class ReturnSignalException : ShellControlFlowException
{
    public ReturnSignalException(TextSpan span, IReadOnlyList<object?> values)
        : base(span)
    {
        Values = values;
    }

    public IReadOnlyList<object?> Values { get; }
}

public sealed class BreakSignalException : ShellControlFlowException
{
    public BreakSignalException(TextSpan span)
        : base(span)
    {
    }
}

public sealed class ContinueSignalException : ShellControlFlowException
{
    public ContinueSignalException(TextSpan span)
        : base(span)
    {
    }
}

public sealed class ThrowSignalException : Exception
{
    public ThrowSignalException(TextSpan span, object? value)
        : base(value?.ToString() ?? "An error was thrown.")
    {
        Span = span;
        Value = value;
    }

    public TextSpan Span { get; }

    public object? Value { get; }
}
