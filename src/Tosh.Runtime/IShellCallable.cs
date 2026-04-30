namespace Tosh.Runtime;

public interface IShellCallable
{
    string CallableName { get; }

    int RequiredParameterCount { get; }

    int? MaximumParameterCount { get; }

    IAsyncEnumerable<object?> InvokeAsync(CommandContext context);
}
