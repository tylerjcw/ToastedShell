using System.Runtime.InteropServices;

namespace Tosh.Runtime;

public sealed record RuntimeInformationSnapshot(
    string OSDescription,
    Architecture OSArchitecture,
    Architecture ProcessArchitecture,
    string FrameworkDescription,
    string RuntimeIdentifier)
{
    public static RuntimeInformationSnapshot Capture() => new(
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture,
        RuntimeInformation.ProcessArchitecture,
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.RuntimeIdentifier);
}
