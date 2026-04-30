namespace Tosh.Runtime;

public sealed record ShellEventSender(string? Function, string? Script, int? Line)
{
    public override string ToString()
    {
        if (Function is not null && Script is not null)
        {
            return $"{Script}:{Function}";
        }

        return Function ?? Script ?? "<unknown>";
    }
}
