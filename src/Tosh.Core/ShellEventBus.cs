namespace Tosh.Core;

public sealed class ShellEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ShellEventHandler>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requiredEvents = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ShellEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_handlers.TryGetValue(handler.EventName, out var list))
            {
                list = new List<ShellEventHandler>();
                _handlers[handler.EventName] = list;
            }

            list.Add(handler);
        }
    }

    public bool Remove(string eventName, string handlerName)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                return false;
            }

            var index = list.FindIndex(h => string.Equals(h.HandlerName, handlerName, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                return false;
            }

            list.RemoveAt(index);
            return true;
        }
    }

    public int RemoveAll(string eventName)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventName, out var list))
            {
                return 0;
            }

            var count = list.Count;
            list.Clear();
            return count;
        }
    }

    public void MarkRequired(string eventName)
    {
        lock (_gate)
        {
            _requiredEvents.Add(eventName);
        }
    }

    public bool IsRequired(string eventName)
    {
        lock (_gate)
        {
            return _requiredEvents.Contains(eventName);
        }
    }

    public async Task<EventRaiseResult> RaiseAsync(ShellEvent shellEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shellEvent);

        var orderedHandlers = GetOrderedHandlers(shellEvent.Name);

        if (orderedHandlers.Count == 0)
        {
            if (IsRequired(shellEvent.Name))
            {
                throw new InvalidOperationException(
                    $"Unhandled required event '{shellEvent.Name}': no handlers registered.");
            }

            return new EventRaiseResult(shellEvent.Name, Cancelled: false, HandlersInvoked: 0, Results: []);
        }

        var results = new List<object?>();
        var onceHandlersToRemove = new List<ShellEventHandler>();

        foreach (var handler in orderedHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await handler.Handler(shellEvent, cancellationToken);
            results.Add(result);

            if (handler.Once)
            {
                onceHandlersToRemove.Add(handler);
            }

            if (shellEvent.Cancelled)
            {
                break;
            }
        }

        if (onceHandlersToRemove.Count > 0)
        {
            lock (_gate)
            {
                foreach (var handler in onceHandlersToRemove)
                {
                    if (_handlers.TryGetValue(handler.EventName, out var list))
                    {
                        list.Remove(handler);
                    }
                }
            }
        }

        return new EventRaiseResult(shellEvent.Name, shellEvent.Cancelled, results.Count, results);
    }

    public IReadOnlyList<ShellEventHandler> GetHandlers(string? eventName = null)
    {
        lock (_gate)
        {
            if (eventName is not null)
            {
                return _handlers.TryGetValue(eventName, out var list)
                    ? list.ToArray()
                    : [];
            }

            return _handlers.Values
                .SelectMany(list => list)
                .OrderBy(h => h.EventName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.RegistrationOrder)
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetRegisteredEventNames()
    {
        lock (_gate)
        {
            return _handlers
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => pair.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private IReadOnlyList<ShellEventHandler> GetOrderedHandlers(string eventName)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventName, out var list) || list.Count == 0)
            {
                return [];
            }

            return list
                .OrderBy(h => h.Priority.HasValue ? 0 : 1)
                .ThenBy(h => h.Priority ?? int.MaxValue)
                .ThenBy(h => h.RegistrationOrder)
                .ToArray();
        }
    }
}
