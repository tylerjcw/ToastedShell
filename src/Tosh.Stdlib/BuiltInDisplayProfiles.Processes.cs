using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tosh.Stdlib.Shell;
using Tosh.Stdlib.Sys;
using Tosh.Runtime;
using Tosh.Stdlib.Net;

namespace Tosh.Stdlib;

public static partial class BuiltInDisplayProfiles
{
    internal static void RegisterProcessesProfiles(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        registry.Register(CreateCommandTimingInfoProfile());
        registry.Register(CreateProcessStartInfoProfile());
        registry.Register(CreateProcessModuleProfile());
        registry.Register(CreateProcessProfile());
        registry.Register(CreateProcessInfoProfile());
        registry.Register(CreateProcessTreeInfoProfile());
        registry.Register(CreateShellJobStatusProfile());
        registry.Register(CreateShellJobInfoProfile());
        registry.Register(CreateShellJobCompletionProfile());
        registry.Register(CreateJobControlResultProfile());
        registry.Register(CreateCommandResolutionKindProfile());
        registry.Register(CreateCommandResolutionProfile());
        registry.Register(CreateShellCommandDescriptorProfile());
        registry.Register(CreateCommandHistoryEntryProfile(preferences));
        registry.Register(CreateCommandResultProfile());
        registry.Register(CreateEventRaiseResultProfile());
        registry.Register(CreateShellEventHandlerProfile());
        registry.Register(CreateEventHandlerRemovalResultProfile());
        registry.Register(CreateEventClearResultProfile());
    }

    private static DisplayProfile CreateCommandTimingInfoProfile()
    {
        return DisplayProfile
            .For<CommandTimingInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildCommandTimingInfoColumns());
    }

    private static DisplayProfile CreateProcessStartInfoProfile()
    {
        return DisplayProfile
            .For<ProcessStartInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildProcessStartInfoColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatProcessStartInfoSummary((ProcessStartInfo)context.Value));
    }

    private static DisplayProfile CreateProcessModuleProfile()
    {
        return DisplayProfile
            .For<ProcessModule>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildProcessModuleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatProcessModuleSummary((ProcessModule)context.Value));
    }

    private static DisplayProfile CreateProcessProfile()
    {
        return DisplayProfile
            .For<Process>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var process = ProcessInfo.From((Process)context.Value);
                    return $"{process.Id,6} {process.Name}";
                })
            .AddTableCase(
                context => BuildProcessColumns(context.Rows));
    }

    private static DisplayProfile CreateShellCommandDescriptorProfile()
    {
        return DisplayProfile
            .For<ShellCommandDescriptor>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var descriptor = (ShellCommandDescriptor)context.Value;
                    return $"{descriptor.Name.PadRight(12)} {descriptor.Description} ({descriptor.Usage})";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ShellCommandDescriptor)row).Name, MinWidth: 8, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Description", row => ((ShellCommandDescriptor)row).Description, MinWidth: 18, MaxWidth: 48, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Usage", row => ((ShellCommandDescriptor)row).Usage, MinWidth: 18, MaxWidth: 48, Priority: 20),
                ]);
    }

    private static DisplayProfile CreateProcessInfoProfile()
    {
        return DisplayProfile
            .For<ProcessInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var process = (ProcessInfo)context.Value;
                    return $"{process.Id,6} {process.Name}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ProcessInfo)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Id", row => ((ProcessInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Memory", row => ((ProcessInfo)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Cpu", row => ((ProcessInfo)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("Started", row => ((ProcessInfo)row).Started, MinWidth: 11, MaxWidth: 18, Priority: 40),
                    new DisplayTableColumn("Path", row => ((ProcessInfo)row).Path, MinWidth: 16, MaxWidth: 48, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateProcessTreeInfoProfile()
    {
        return DisplayProfile
            .For<ProcessTreeInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((ProcessTreeInfo)row).Name, MinWidth: 10, MaxWidth: 40, Priority: 0, CanHide: false, IsTree: true),
                    new DisplayTableColumn("Id", row => ((ProcessTreeInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Memory", row => ((ProcessTreeInfo)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Cpu", row => ((ProcessTreeInfo)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("User", row => ((ProcessTreeInfo)row).UserName, MinWidth: 4, MaxWidth: 16, Priority: 40),
                ]);
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessColumns(IReadOnlyList<object> rows)
    {
        return
        [
            new DisplayTableColumn("Name", row => ProcessInfo.From((Process)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Id", row => ProcessInfo.From((Process)row).Id, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("Memory", row => ProcessInfo.From((Process)row).Memory, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("Cpu", row => ProcessInfo.From((Process)row).Cpu, MinWidth: 6, MaxWidth: 12, Priority: 30),
            new DisplayTableColumn("Started", row => ProcessInfo.From((Process)row).Started, MinWidth: 11, MaxWidth: 18, Priority: 40),
            new DisplayTableColumn("Path", row => ProcessInfo.From((Process)row).Path, MinWidth: 16, MaxWidth: 48, Priority: 50),
        ];
    }

    private static DisplayProfile CreateShellJobStatusProfile()
    {
        return DisplayProfile
            .For<ShellJobStatus>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ShellJobStatus)context.Value).ToString().ToLowerInvariant());
    }

    private static DisplayProfile CreateShellJobInfoProfile()
    {
        return DisplayProfile
            .For<ShellJobInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var job = (ShellJobInfo)context.Value;
                    var pid = job.ProcessId is int processId ? $" pid={processId}" : string.Empty;
                    return $"[{job.Id}] {job.Status.ToString().ToLowerInvariant()}{pid} {job.Command}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((ShellJobInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Status", row => ((ShellJobInfo)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Pid", row => ((ShellJobInfo)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("ExitCode", row => ((ShellJobInfo)row).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 30),
                    new DisplayTableColumn("Started", row => ((ShellJobInfo)row).StartedAt, MinWidth: 11, MaxWidth: 18, Priority: 40),
                    new DisplayTableColumn("Duration", row => ((ShellJobInfo)row).Duration, MinWidth: 6, MaxWidth: 14, Priority: 50),
                    new DisplayTableColumn("Command", row => ((ShellJobInfo)row).Command, MinWidth: 12, MaxWidth: 64, Priority: 60, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateShellJobCompletionProfile()
    {
        return DisplayProfile
            .For<ShellJobCompletion>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var job = (ShellJobCompletion)context.Value;
                    var exitCode = job.ExitCode is int code ? $" exit={code}" : string.Empty;
                    return $"[{job.Id}] {job.Status.ToString().ToLowerInvariant()}{exitCode} {job.Command}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((ShellJobCompletion)row).Id, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Status", row => ((ShellJobCompletion)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Pid", row => ((ShellJobCompletion)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("ExitCode", row => ((ShellJobCompletion)row).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 30),
                    new DisplayTableColumn("Duration", row => ((ShellJobCompletion)row).Duration, MinWidth: 6, MaxWidth: 14, Priority: 40),
                    new DisplayTableColumn("OutputCount", row => ((ShellJobCompletion)row).OutputCount, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 50),
                    new DisplayTableColumn("ErrorCount", row => ((ShellJobCompletion)row).ErrorCount, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 60),
                    new DisplayTableColumn("Command", row => ((ShellJobCompletion)row).Command, MinWidth: 12, MaxWidth: 64, Priority: 70, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateJobControlResultProfile()
    {
        return DisplayProfile
            .For<JobControlResult>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var result = (JobControlResult)context.Value;
                    var scope = result.JobId is int jobId
                        ? $"[{jobId}]"
                        : result.ProcessId is int processId
                            ? $"pid={processId}"
                            : string.Empty;
                    return string.IsNullOrWhiteSpace(scope)
                        ? $"{result.Action}: {result.Message}"
                        : $"{result.Action} {scope}: {result.Message}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Action", row => ((JobControlResult)row).Action, MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("JobId", row => ((JobControlResult)row).JobId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 10),
                    new DisplayTableColumn("Pid", row => ((JobControlResult)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                    new DisplayTableColumn("Status", row => ((JobControlResult)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 30),
                    new DisplayTableColumn("Success", row => ((JobControlResult)row).IsSuccess, MinWidth: 5, MaxWidth: 5, Priority: 40),
                    new DisplayTableColumn("Message", row => ((JobControlResult)row).Message, MinWidth: 12, MaxWidth: 64, Priority: 50, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateCommandResolutionKindProfile()
    {
        return DisplayProfile
            .For<CommandResolutionKind>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((CommandResolutionKind)context.Value) switch
                {
                    CommandResolutionKind.BuiltIn => "builtin",
                    CommandResolutionKind.Alias => "alias",
                    CommandResolutionKind.Function => "function",
                    CommandResolutionKind.External => "external",
                    _ => context.Value.ToString() ?? context.Value.GetType().Name,
                });
    }

    private static DisplayProfile CreateCommandResolutionProfile()
    {
        return DisplayProfile
            .For<CommandResolution>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var resolution = (CommandResolution)context.Value;
                    return resolution.Path is not null
                        ? $"{resolution.Kind}: {resolution.Path}"
                        : $"{resolution.Kind}: {resolution.Name}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((CommandResolution)row).Name, MinWidth: 8, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((CommandResolution)row).Kind, MinWidth: 7, MaxWidth: 8, Priority: 10),
                    new DisplayTableColumn("Path", row => ((CommandResolution)row).Path, MinWidth: 12, MaxWidth: 64, Priority: 20),
                    new DisplayTableColumn("Usage", row => ((CommandResolution)row).Usage, MinWidth: 12, MaxWidth: 32, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateCommandHistoryEntryProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<CommandHistoryEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var entry = (CommandHistoryEntry)context.Value;
                    var timestamp = FormatDateTimeOffset(
                        entry.Timestamp,
                        preferences.DateTimeOffset.ScalarMode,
                        preferences.DateTimeOffset.ScalarFormat,
                        preferences.NowProvider);
                    return $"{entry.Id,4}  {timestamp}  {entry.Text}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Id", row => ((CommandHistoryEntry)row).Id, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Text", row => ((CommandHistoryEntry)row).Text, MinWidth: 12, MaxWidth: 64, Priority: 10, CanHide: false),
                    new DisplayTableColumn("When", row => ((CommandHistoryEntry)row).When, MinWidth: 11, MaxWidth: 18, Priority: 20),
                ]);
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessStartInfoColumns()
    {
        return
        [
            new DisplayTableColumn("FileName", row => ((ProcessStartInfo)row).FileName, MinWidth: 3, MaxWidth: 96, Priority: 0, CanHide: false),
            new DisplayTableColumn("Arguments", row => NullIfEmpty(((ProcessStartInfo)row).Arguments), MinWidth: 3, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("WorkingDirectory", row => NullIfEmpty(((ProcessStartInfo)row).WorkingDirectory), MinWidth: 3, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("Verb", row => NullIfEmpty(((ProcessStartInfo)row).Verb), MinWidth: 3, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("UseShellExecute", row => ((ProcessStartInfo)row).UseShellExecute, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CreateNoWindow", row => ((ProcessStartInfo)row).CreateNoWindow, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("RedirectStdIn", row => ((ProcessStartInfo)row).RedirectStandardInput, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("RedirectStdOut", row => ((ProcessStartInfo)row).RedirectStandardOutput, MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("RedirectStdErr", row => ((ProcessStartInfo)row).RedirectStandardError, MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("Environment", row => ((ProcessStartInfo)row).Environment.Count, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 90),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildProcessModuleColumns()
    {
        return
        [
            new DisplayTableColumn("ModuleName", row => NullIfEmpty(((ProcessModule)row).ModuleName), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("FileName", row => NullIfEmpty(((ProcessModule)row).FileName), MinWidth: 3, MaxWidth: 128, Priority: 10),
            new DisplayTableColumn("MemorySize", row => StorageSize.FromBytes(((ProcessModule)row).ModuleMemorySize), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn("BaseAddress", row => FormatPointer(((ProcessModule)row).BaseAddress), MinWidth: 3, MaxWidth: 24, Priority: 30),
            new DisplayTableColumn("EntryPoint", row => FormatPointer(((ProcessModule)row).EntryPointAddress), MinWidth: 3, MaxWidth: 24, Priority: 40),
            new DisplayTableColumn("FileVersion", row => SafeGetProcessModuleFileVersion((ProcessModule)row), MinWidth: 3, MaxWidth: 48, Priority: 50),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildCommandTimingInfoColumns()
    {
        return
        [
            new DisplayTableColumn("Elapsed", row => ((CommandTimingInfo)row).Elapsed, MinWidth: 8, MaxWidth: 20, Priority: 0, CanHide: false),
            new DisplayTableColumn("User CPU", row => ((CommandTimingInfo)row).UserCpuTime, MinWidth: 8, MaxWidth: 20, Priority: 10, CanHide: false),
            new DisplayTableColumn("System CPU", row => ((CommandTimingInfo)row).SystemCpuTime, MinWidth: 8, MaxWidth: 20, Priority: 20, CanHide: false),
            new DisplayTableColumn("CPU %", row => ((CommandTimingInfo)row).CpuPercent, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 10, Priority: 30, CanHide: false),
            new DisplayTableColumn("Peak Memory", row => ((CommandTimingInfo)row).PeakWorkingSet, MinWidth: 8, MaxWidth: 16, Priority: 40),
            new DisplayTableColumn("Memory Δ", row => ((CommandTimingInfo)row).WorkingSetDelta, MinWidth: 8, MaxWidth: 16, Priority: 50),
            new DisplayTableColumn("Allocations", row => ((CommandTimingInfo)row).ThreadAllocations, MinWidth: 8, MaxWidth: 16, Priority: 60),
            new DisplayTableColumn("Minor Faults", row => ((CommandTimingInfo)row).MinorPageFaults, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Major Faults", row => ((CommandTimingInfo)row).MajorPageFaults, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 80),
        ];
    }

    private static string? SafeGetProcessModuleFileVersion(ProcessModule module)
    {
        try
        {
            return NullIfEmpty(module.FileVersionInfo?.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatPointer(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        return $"0x{value.ToInt64().ToString("x", CultureInfo.InvariantCulture)}";
    }

    private static DisplayProfile CreateCommandResultProfile()
    {
        return DisplayProfile
            .For<CommandSuccess>()
            .AddValueCase(
                DisplaySurface.Any,
                context => ((CommandSuccess)context.Value).Message);
    }

    private static DisplayProfile CreateEventRaiseResultProfile()
    {
        return DisplayProfile
            .For<EventRaiseResult>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((EventRaiseResult)context.Value).ToString())
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Event", row => ((EventRaiseResult)row).EventName, MinWidth: 10, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Handled", row => ((EventRaiseResult)row).Handled ? "yes" : "no", MinWidth: 7, MaxWidth: 7, Priority: 10),
                    new DisplayTableColumn("Handlers", row => ((EventRaiseResult)row).HandlersInvoked, DisplayTableAlignment.Right, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Cancelled", row => ((EventRaiseResult)row).Cancelled ? "yes" : "no", MinWidth: 9, MaxWidth: 9, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateShellEventHandlerProfile()
    {
        return DisplayProfile
            .For<ShellEventHandler>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((ShellEventHandler)context.Value).ToString())
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Event", row => ((ShellEventHandler)row).EventName, MinWidth: 10, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Handler", row => ((ShellEventHandler)row).HandlerName, MinWidth: 10, MaxWidth: 32, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Priority", row => ((ShellEventHandler)row).Priority?.ToString(CultureInfo.InvariantCulture) ?? "—", DisplayTableAlignment.Right, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Once", row => ((ShellEventHandler)row).Once ? "yes" : "no", MinWidth: 4, MaxWidth: 5, Priority: 30),
                ]);
    }

    private static DisplayProfile CreateEventHandlerRemovalResultProfile()
    {
        return DisplayProfile
            .For<Shell.EventHandlerRemovalResult>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var result = (Shell.EventHandlerRemovalResult)context.Value;
                    return $"Removed handler '{result.HandlerName}' from event '{result.EventName}'.";
                });
    }

    private static DisplayProfile CreateEventClearResultProfile()
    {
        return DisplayProfile
            .For<Shell.EventClearResult>()
            .AddValueCase(
                DisplaySurface.Any,
                context =>
                {
                    var result = (Shell.EventClearResult)context.Value;
                    return $"Cleared {result.HandlersRemoved} handler(s) from event '{result.EventName}'.";
                });
    }


}
