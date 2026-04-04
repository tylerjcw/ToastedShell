namespace Tosh.Core;

public static class BuiltInEventNames
{
    public const string DirectoryChanged = "DirectoryChanged";
    public const string CommandStarting = "CommandStarting";
    public const string CommandCompleted = "CommandCompleted";
    public const string SessionStarted = "SessionStarted";
    public const string SessionEnding = "SessionEnding";
    public const string VariableChanged = "VariableChanged";
    public const string JobStarted = "JobStarted";
    public const string JobCompleted = "JobCompleted";
}

public sealed class DirectoryChangedEvent : ShellEvent
{
    public DirectoryChangedEvent(FileSystemEntry oldDirectory, FileSystemEntry newDirectory, ShellEventSender sender)
        : base(BuiltInEventNames.DirectoryChanged, sender)
    {
        OldDirectory = oldDirectory;
        NewDirectory = newDirectory;
    }

    public FileSystemEntry OldDirectory { get; }

    public FileSystemEntry NewDirectory { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "OldDirectory" or "oldDirectory":
                value = OldDirectory;
                return true;
            case "NewDirectory" or "newDirectory":
                value = NewDirectory;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("OldDirectory", OldDirectory),
            new("NewDirectory", NewDirectory),
        ];
    }
}

public sealed class CommandStartingEvent : ShellEvent
{
    public CommandStartingEvent(string commandName, IReadOnlyList<object?> arguments, string pipeline, ShellEventSender sender)
        : base(BuiltInEventNames.CommandStarting, sender)
    {
        CommandName = commandName;
        Arguments = arguments;
        Pipeline = pipeline;
    }

    public string CommandName { get; }

    public IReadOnlyList<object?> Arguments { get; }

    public string Pipeline { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "CommandName" or "commandName" or "Command" or "command":
                value = CommandName;
                return true;
            case "Arguments" or "arguments":
                value = Arguments;
                return true;
            case "Pipeline" or "pipeline":
                value = Pipeline;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("CommandName", CommandName),
            new("Arguments", Arguments),
            new("Pipeline", Pipeline),
        ];
    }
}

public sealed class CommandCompletedEvent : ShellEvent
{
    public CommandCompletedEvent(string commandName, int exitCode, TimeSpan duration, object? result, ShellEventSender sender)
        : base(BuiltInEventNames.CommandCompleted, sender)
    {
        CommandName = commandName;
        ExitCode = exitCode;
        Duration = duration;
        Result = result;
    }

    public string CommandName { get; }

    public int ExitCode { get; }

    public TimeSpan Duration { get; }

    public object? Result { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "CommandName" or "commandName" or "Command" or "command":
                value = CommandName;
                return true;
            case "ExitCode" or "exitCode":
                value = ExitCode;
                return true;
            case "Duration" or "duration":
                value = Duration;
                return true;
            case "Result" or "result":
                value = Result;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("CommandName", CommandName),
            new("ExitCode", ExitCode),
            new("Duration", Duration),
            new("Result", Result),
        ];
    }
}

public sealed class SessionStartedEvent : ShellEvent
{
    public SessionStartedEvent(DateTimeOffset startTime, string configDirectory, ShellEventSender sender)
        : base(BuiltInEventNames.SessionStarted, sender)
    {
        StartTime = startTime;
        ConfigDirectory = configDirectory;
    }

    public DateTimeOffset StartTime { get; }

    public string ConfigDirectory { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "StartTime" or "startTime":
                value = StartTime;
                return true;
            case "ConfigDirectory" or "configDirectory":
                value = ConfigDirectory;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("StartTime", StartTime),
            new("ConfigDirectory", ConfigDirectory),
        ];
    }
}

public sealed class SessionEndingEvent : ShellEvent
{
    public SessionEndingEvent(int exitCode, ShellEventSender sender)
        : base(BuiltInEventNames.SessionEnding, sender)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "ExitCode" or "exitCode":
                value = ExitCode;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("ExitCode", ExitCode),
        ];
    }
}

public sealed class VariableChangedEvent : ShellEvent
{
    public VariableChangedEvent(string variableName, object? oldValue, object? newValue, ShellEventSender sender)
        : base(BuiltInEventNames.VariableChanged, sender)
    {
        VariableName = variableName;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public string VariableName { get; }

    public object? OldValue { get; }

    public object? NewValue { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "VariableName" or "variableName" or "Variable" or "variable":
                value = VariableName;
                return true;
            case "OldValue" or "oldValue":
                value = OldValue;
                return true;
            case "NewValue" or "newValue":
                value = NewValue;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("VariableName", VariableName),
            new("OldValue", OldValue),
            new("NewValue", NewValue),
        ];
    }
}

public sealed class JobStartedEvent : ShellEvent
{
    public JobStartedEvent(int jobId, string commandName, ShellEventSender sender)
        : base(BuiltInEventNames.JobStarted, sender)
    {
        JobId = jobId;
        CommandName = commandName;
    }

    public int JobId { get; }

    public string CommandName { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "JobId" or "jobId":
                value = JobId;
                return true;
            case "CommandName" or "commandName" or "Command" or "command":
                value = CommandName;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("JobId", JobId),
            new("CommandName", CommandName),
        ];
    }
}

public sealed class JobCompletedEvent : ShellEvent
{
    public JobCompletedEvent(int jobId, string commandName, int exitCode, ShellEventSender sender)
        : base(BuiltInEventNames.JobCompleted, sender)
    {
        JobId = jobId;
        CommandName = commandName;
        ExitCode = exitCode;
    }

    public int JobId { get; }

    public string CommandName { get; }

    public int ExitCode { get; }

    public override bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case "JobId" or "jobId":
                value = JobId;
                return true;
            case "CommandName" or "commandName" or "Command" or "command":
                value = CommandName;
                return true;
            case "ExitCode" or "exitCode":
                value = ExitCode;
                return true;
            default:
                return base.TryGetMember(name, out value, includeHidden);
        }
    }

    public override IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var baseMembers = base.GetMembers(includeHidden);
        return
        [
            ..baseMembers,
            new("JobId", JobId),
            new("CommandName", CommandName),
            new("ExitCode", ExitCode),
        ];
    }
}
