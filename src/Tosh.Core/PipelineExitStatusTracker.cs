namespace Tosh.Core;

public sealed class PipelineExitStatusTracker
{
    private readonly bool _pipefail;
    private readonly List<int> _exitCodes = new();

    public PipelineExitStatusTracker(bool pipefail)
    {
        _pipefail = pipefail;
    }

    public bool HasExitCodes => _exitCodes.Count > 0;

    public int ExitCodeCount => _exitCodes.Count;

    public void Record(int exitCode)
    {
        _exitCodes.Add(exitCode);
    }

    public int GetFinalExitCode()
    {
        if (_exitCodes.Count == 0)
        {
            throw new InvalidOperationException("No external exit codes were recorded for this pipeline.");
        }

        if (!_pipefail)
        {
            return _exitCodes[^1];
        }

        for (var index = _exitCodes.Count - 1; index >= 0; index--)
        {
            if (_exitCodes[index] != 0)
            {
                return _exitCodes[index];
            }
        }

        return 0;
    }
}
