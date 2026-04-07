using System.Text;
using Tosh.Core;

namespace Tosh.Cli;

internal sealed class ReplCommandLineInsertionSink : ICommandLineInsertionSink
{
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private LineEditorBuffer? _activeBuffer;
    private int _cursorIndex;
    private (int Start, int Length)? _pendingReplacement;

    public bool TryInsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeBuffer is not null)
            {
                if (_pendingReplacement is { } replacement)
                {
                    _pendingReplacement = null;
                    return _activeBuffer.ReplaceRange(replacement.Start, replacement.Length, text);
                }

                return _activeBuffer.Insert(text);
            }

            if (_pendingReplacement is { } queuedReplacement)
            {
                _pendingReplacement = null;
                queuedReplacement.Start = Math.Clamp(queuedReplacement.Start, 0, _buffer.Length);
                queuedReplacement.Length = Math.Clamp(queuedReplacement.Length, 0, _buffer.Length - queuedReplacement.Start);
                _buffer.Remove(queuedReplacement.Start, queuedReplacement.Length);
                _buffer.Insert(queuedReplacement.Start, text);
                _cursorIndex = queuedReplacement.Start + text.Length;
                return true;
            }

            _cursorIndex = Math.Clamp(_cursorIndex, 0, _buffer.Length);
            _buffer.Insert(_cursorIndex, text);
            _cursorIndex += text.Length;
            return true;
        }
    }

    internal void ActivateBuffer(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_gate)
        {
            _activeBuffer = buffer;
        }
    }

    internal void DeactivateBuffer(LineEditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_gate)
        {
            if (ReferenceEquals(_activeBuffer, buffer))
            {
                _activeBuffer = null;
            }
        }
    }

    internal void SetPendingReplacement(int start, int length)
    {
        lock (_gate)
        {
            _pendingReplacement = (start, length);
        }
    }

    internal void ClearPendingReplacement()
    {
        lock (_gate)
        {
            _pendingReplacement = null;
        }
    }

    internal bool TryConsume(out ReplPendingCommandLine pending)
    {
        lock (_gate)
        {
            if (_buffer.Length == 0)
            {
                pending = default;
                return false;
            }

            pending = new ReplPendingCommandLine(_buffer.ToString(), _cursorIndex);
            _buffer.Clear();
            _cursorIndex = 0;
            return true;
        }
    }
}

internal readonly record struct ReplPendingCommandLine(string Text, int CursorIndex);
