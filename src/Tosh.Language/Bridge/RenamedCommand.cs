using Tosh.Runtime;

namespace Tosh.Language.Bridge;

/// <summary>
/// The same command under another name — <c>require { Clamp as pin } from …</c>.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper has to be as capable as what it wraps. This one used to implement only
/// <see cref="IShellCommand"/>, which is enough to run it where a command is expected and not
/// enough where a value is: <c>Pin 15 0 10</c> worked and <c>echo (Pin(15))</c> reported
/// "Unable to resolve .NET access path 'Pin'" for a name that <c>which</c> could find.
/// The un-aliased import worked because nothing wrapped it.
/// </para>
/// <para>
/// So <see cref="Create"/> picks a wrapper that keeps whatever the inner command could do.
/// It never claims more: a command that cannot run itself is wrapped plainly rather than
/// promoted, because claiming <see cref="ISelfHostedCallable"/> falsely would turn a
/// conversion that ought to decline into a delegate that throws when someone calls it.
/// </para>
/// </remarks>
internal static class RenamedCommand
{
    /// <summary>Wraps <paramref name="inner"/> under <paramref name="name"/>.</summary>
    public static IShellCommand Create(string name, IShellCommand inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(inner);

        return inner is IShellCallable and ISelfHostedCallable
            ? new RenamedCallableCommand(name, inner)
            : new RenamedPlainCommand(name, inner);
    }
}

/// <summary>A renamed command that is only ever run as a command.</summary>
internal sealed class RenamedPlainCommand(string name, IShellCommand inner)
    : IShellCommand, ICommandResolutionMetadata
{
    private readonly ICommandResolutionMetadata? _metadata = inner as ICommandResolutionMetadata;

    public string Name { get; } = name;

    public string Description => inner.Description;

    public string Usage => inner.Usage;

    public CommandResolutionKind ResolutionKind => _metadata?.ResolutionKind ?? CommandResolutionKind.Function;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context) => inner.ExecuteAsync(context);
}

/// <summary>
/// A renamed command that can also be called as a value.
/// </summary>
/// <remarks>
/// Which is what makes <c>echo (pin(15))</c> and handing the name to a CLR delegate work
/// under the alias exactly as they do under the original.
/// </remarks>
internal sealed class RenamedCallableCommand : IShellCommand, ICommandResolutionMetadata, IShellCallable, ISelfHostedCallable
{
    private readonly IShellCommand _inner;
    private readonly IShellCallable _callable;
    private readonly ISelfHostedCallable _selfHosted;
    private readonly ICommandResolutionMetadata? _metadata;

    public RenamedCallableCommand(string name, IShellCommand inner)
    {
        Name = name;
        _inner = inner;
        _callable = (IShellCallable)inner;
        _selfHosted = (ISelfHostedCallable)inner;
        _metadata = inner as ICommandResolutionMetadata;
    }

    public string Name { get; }

    public string Description => _inner.Description;

    public string Usage => _inner.Usage;

    public CommandResolutionKind ResolutionKind => _metadata?.ResolutionKind ?? CommandResolutionKind.Function;

    /// <summary>The name the caller wrote, so a diagnostic says what they typed.</summary>
    public string CallableName => Name;

    public int RequiredParameterCount => _callable.RequiredParameterCount;

    public int? MaximumParameterCount => _callable.MaximumParameterCount;

    public IAsyncEnumerable<object?> ExecuteAsync(CommandContext context) => _inner.ExecuteAsync(context);

    public IAsyncEnumerable<object?> InvokeAsync(CommandContext context) => _callable.InvokeAsync(context);

    public object? InvokeWithoutContext(IReadOnlyList<object?> arguments) =>
        _selfHosted.InvokeWithoutContext(arguments);
}
