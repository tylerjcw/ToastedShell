using System.Collections;

namespace Tosh.Runtime;

/// <summary>
/// A command's arguments, together with which of them were written in the source as unquoted
/// words — the only arguments that may be read as options (<c>TOSH-0013</c>).
/// </summary>
/// <remarks>
/// <para>
/// Builtins find their options by comparing argument values with option spellings, and by the
/// time a command sees its arguments they are only values. A value that happened to start with a
/// dash — a file name from <c>ls</c> or a glob, a loop variable, user input — was read as an
/// option: <c>var name = "-r"; rm $name victim</c> removed <c>victim/</c> recursively and left
/// the file called <c>-r</c> alone. The engine knows where each argument came from, and records
/// it here.
/// </para>
/// <para>
/// A word is an unquoted bare word, or an unquoted literal such as <c>-9</c>. Everything else is
/// a value: a variable, a subexpression, an interpolation, a quoted string, a splat, a pipe-forward
/// value, a glob match.
/// </para>
/// <para>
/// The record travels with the list object. A command-wrapper function forwards its caller's list,
/// so after <c>func rmf => rm -rf</c> the <c>-v</c> in <c>rmf -v x</c> is still an option. A list
/// a command builds for itself is not one of these, and <see cref="CommandContext.MayBeOption"/>
/// then answers no — deliberately: a lost option is an error message, while a value taken for an
/// option can be a recursive delete. <see cref="Slice"/> keeps the record for a command that
/// hands the rest of its arguments on.
/// </para>
/// </remarks>
public sealed class CommandArgumentList : IReadOnlyList<object?>
{
    private readonly object?[] _values;
    private readonly bool[] _words;

    public CommandArgumentList(IReadOnlyList<object?> values, IReadOnlyList<bool> writtenAsWords)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(writtenAsWords);

        if (values.Count != writtenAsWords.Count)
        {
            throw new ArgumentException(
                $"Expected an origin for each of the {values.Count} arguments, but got {writtenAsWords.Count}.",
                nameof(writtenAsWords));
        }

        _values = values.ToArray();
        _words = writtenAsWords.ToArray();
    }

    private CommandArgumentList(object?[] values, bool[] words)
    {
        _values = values;
        _words = words;
    }

    public int Count => _values.Length;

    public object? this[int index] => _values[index];

    /// <summary>Whether the argument at <paramref name="index"/> was written as an unquoted word.</summary>
    public bool IsWord(int index) => _words[index];

    /// <summary>Arguments that are all values: none of them may be read as an option.</summary>
    public static CommandArgumentList Values(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new CommandArgumentList(values.ToArray(), new bool[values.Count]);
    }

    /// <summary>
    /// <paramref name="arguments"/> with its record when it has one, and as values when it does not.
    /// </summary>
    public static CommandArgumentList From(IReadOnlyList<object?> arguments)
        => arguments as CommandArgumentList ?? Values(arguments);

    /// <summary>The arguments from <paramref name="start"/> on, each keeping its origin.</summary>
    public CommandArgumentList Slice(int start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);

        if (start >= _values.Length)
        {
            return new CommandArgumentList([], []);
        }

        return new CommandArgumentList(_values[start..], _words[start..]);
    }

    /// <summary>This list followed by <paramref name="other"/>, each argument keeping its origin.</summary>
    public CommandArgumentList Concat(CommandArgumentList other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CommandArgumentList([.. _values, .. other._values], [.. _words, .. other._words]);
    }

    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
