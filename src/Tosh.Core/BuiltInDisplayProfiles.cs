using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace Tosh.Core;

public static class BuiltInDisplayProfiles
{
    public static void RegisterDefaults(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(preferences);

        registry.Register(CreateDateTimeProfile(preferences));
        registry.Register(CreateDateTimeOffsetProfile(preferences));
        registry.Register(CreateStorageSizeProfile(preferences));
        registry.Register(CreateShellTextLineProfile());
        registry.Register(CreateIpAddressProfile());
        registry.Register(CreateUnixFileModeProfile());
        registry.Register(CreateFileSystemPrincipalProfile());
        registry.Register(CreateProjectedObjectProfile());
        registry.Register(CreateFileSystemEntryTypeProfile());
        registry.Register(CreateFileSystemEntryProfile(preferences));
        registry.Register(CreateUnameInfoProfile());
        registry.Register(CreateUserIdentityInfoProfile());
        registry.Register(CreateFileSystemUsageInfoProfile());
        registry.Register(CreatePingReplyInfoProfile());
        registry.Register(CreateProcessInfoProfile());
        registry.Register(CreateGroupingInfoProfile());
        registry.Register(CreateEnvironmentVariableEntryProfile());
        registry.Register(CreateHelpSubjectKindProfile());
        registry.Register(CreateHelpSummaryProfile());
        registry.Register(CreateHelpSearchResultProfile());
        registry.Register(CreateHelpTopicProfile());
        registry.Register(CreateHelpCategoryInfoProfile());
        registry.Register(CreateCommandResolutionKindProfile());
        registry.Register(CreateCommandResolutionProfile());
        registry.Register(CreateShellCommandDescriptorProfile());
        registry.Register(CreateCommandHistoryEntryProfile(preferences));
        registry.Register(CreateFormatterStatusProfile());
        registry.Register(CreateXDocumentProfile());
        registry.Register(CreateXElementProfile());
    }

    private static DisplayProfile CreateDateTimeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<DateTime>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatDateTime(
                    (DateTime)context.Value,
                    preferences.DateTime.TableMode,
                    preferences.DateTime.TableFormat,
                    preferences.NowProvider))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatDateTime(
                    (DateTime)context.Value,
                    preferences.DateTime.ScalarMode,
                    preferences.DateTime.ScalarFormat,
                    preferences.NowProvider));
    }

    private static DisplayProfile CreateDateTimeOffsetProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<DateTimeOffset>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => FormatDateTimeOffset(
                    (DateTimeOffset)context.Value,
                    preferences.DateTimeOffset.TableMode,
                    preferences.DateTimeOffset.TableFormat,
                    preferences.NowProvider))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => FormatDateTimeOffset(
                    (DateTimeOffset)context.Value,
                    preferences.DateTimeOffset.ScalarMode,
                    preferences.DateTimeOffset.ScalarFormat,
                    preferences.NowProvider));
    }

    private static DisplayProfile CreateStorageSizeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<StorageSize>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatStorageSize((StorageSize)context.Value, preferences.StorageSize.Mode));
    }

    private static DisplayProfile CreateShellTextLineProfile()
    {
        return DisplayProfile
            .For<ShellTextLine>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ShellTextLine)context.Value).Text);
    }

    private static DisplayProfile CreateIpAddressProfile()
    {
        return DisplayProfile
            .For<IPAddress>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((IPAddress)context.Value).ToString());
    }

    private static DisplayProfile CreateUnixFileModeProfile()
    {
        return DisplayProfile
            .For<UnixFileMode>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatPermissions((UnixFileMode)context.Value));
    }

    private static DisplayProfile CreateFileSystemPrincipalProfile()
    {
        return DisplayProfile
            .For<FileSystemPrincipalInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileSystemPrincipalInfo)context.Value).DisplayName);
    }

    private static DisplayProfile CreateProjectedObjectProfile()
    {
        return DisplayProfile
            .For<ProjectedObject>()
            .AddTableCase(
                context =>
                {
                    var rows = context.Rows.Cast<ProjectedObject>().ToArray();
                    var fieldNames = rows
                        .SelectMany(row => row.Fields.Select(field => field.Name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return fieldNames
                        .Select((fieldName, index) => new DisplayTableColumn(
                            fieldName,
                            row => ((ProjectedObject)row).TryGetValue(fieldName, out var value) ? value : null,
                            Alignment: ShouldRightAlignProjectedField(rows, fieldName) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                            Priority: index,
                            CanHide: index > 0))
                        .ToArray();
                });
    }

    private static DisplayProfile CreateFileSystemEntryTypeProfile()
    {
        return DisplayProfile
            .For<FileSystemEntryType>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileSystemEntryType)context.Value) switch
                {
                    FileSystemEntryType.Dir => "dir",
                    FileSystemEntryType.File => "file",
                    _ => context.Value.ToString() ?? context.Value.GetType().Name,
                });
    }

    private static DisplayProfile CreateFileSystemEntryProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileSystemEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => ((FileSystemEntry)context.Value).PreferLongDisplay,
                context =>
                {
                    var entry = (FileSystemEntry)context.Value;
                    var size = entry.Size is StorageSize storageSize
                        ? FormatStorageSize(storageSize, preferences.StorageSize.Mode)
                        : "-";
                    var timestamp = FormatDateTime(
                        entry.Modified,
                        preferences.DateTime.TableMode,
                        preferences.DateTime.TableFormat,
                        preferences.NowProvider);
                    var owner = entry.Owner?.DisplayName ?? "-";
                    var group = entry.Group?.DisplayName ?? "-";
                    return $"{entry.GetModeDisplay(includeTypeIndicator: true)} {owner}:{group} {size,10} {timestamp} {entry.DisplayName}";
                })
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => context.Style != ObjectRenderStyle.Detail,
                context => ((FileSystemEntry)context.Value).DisplayName)
            .AddTableCase(
                context =>
                {
                    var rows = context.Rows.Cast<FileSystemEntry>().ToArray();
                    var showLongMetadata = rows.Any(entry => entry.PreferLongDisplay);
                    var showTarget = rows.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));

                    if (!showLongMetadata)
                    {
                        return
                        [
                            new DisplayTableColumn("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
                            new DisplayTableColumn("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 10),
                            new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                            new DisplayTableColumn("Modified", row => ((FileSystemEntry)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
                        ];
                    }

                    var columns = new List<DisplayTableColumn>
                    {
                        new("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
                        new("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 10),
                    };

                    if (showTarget)
                    {
                        columns.Add(new DisplayTableColumn("Target", row => ((FileSystemEntry)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 95));
                    }

                    columns.AddRange(
                    [
                        new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                        new DisplayTableColumn("Modified", row => ((FileSystemEntry)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
                        new DisplayTableColumn("Readonly", row => ((FileSystemEntry)row).Readonly, MinWidth: 5, MaxWidth: 5, Priority: 40),
                        new DisplayTableColumn("Mode", row => ((FileSystemEntry)row).GetModeDisplay(includeTypeIndicator: false), MinWidth: 9, MaxWidth: 10, Priority: 50),
                        new DisplayTableColumn("NumLinks", row => ((FileSystemEntry)row).NumLinks, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 60),
                        new DisplayTableColumn("Inode", row => ((FileSystemEntry)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
                        new DisplayTableColumn("Owner", row => ((FileSystemEntry)row).Owner, MinWidth: 4, MaxWidth: 16, Priority: 80),
                        new DisplayTableColumn("Group", row => ((FileSystemEntry)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 90),
                        new DisplayTableColumn("Created", row => ((FileSystemEntry)row).Created, MinWidth: 11, MaxWidth: 18, Priority: 100),
                        new DisplayTableColumn("Accessed", row => ((FileSystemEntry)row).Accessed, MinWidth: 11, MaxWidth: 18, Priority: 110),
                    ]);

                    return columns;
                });
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

    private static DisplayProfile CreateUnameInfoProfile()
    {
        return DisplayProfile
            .For<UnameInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("SystemName", row => ((UnameInfo)row).SystemName, MinWidth: 6, MaxWidth: 16, Priority: 0, CanHide: false),
                    new DisplayTableColumn("NodeName", row => ((UnameInfo)row).NodeName, MinWidth: 6, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Release", row => ((UnameInfo)row).Release, MinWidth: 6, MaxWidth: 24, Priority: 20),
                    new DisplayTableColumn("Version", row => ((UnameInfo)row).Version, MinWidth: 8, MaxWidth: 48, Priority: 30),
                    new DisplayTableColumn("Machine", row => ((UnameInfo)row).Machine, MinWidth: 6, MaxWidth: 16, Priority: 40),
                    new DisplayTableColumn("OperatingSystem", row => ((UnameInfo)row).OperatingSystem, MinWidth: 8, MaxWidth: 24, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateUserIdentityInfoProfile()
    {
        return DisplayProfile
            .For<UserIdentityInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("User", row => ((UserIdentityInfo)row).User, MinWidth: 6, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Uid", row => ((UserIdentityInfo)row).Uid, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Group", row => ((UserIdentityInfo)row).Group, MinWidth: 6, MaxWidth: 20, Priority: 20),
                    new DisplayTableColumn("Gid", row => ((UserIdentityInfo)row).Gid, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 10, Priority: 30),
                    new DisplayTableColumn("Euid", row => ((UserIdentityInfo)row).Euid, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 40),
                    new DisplayTableColumn("Egid", row => ((UserIdentityInfo)row).Egid, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 10, Priority: 50),
                    new DisplayTableColumn("Groups", row => string.Join(", ", ((UserIdentityInfo)row).Groups.Select(group => group.DisplayName)), MinWidth: 8, MaxWidth: 48, Priority: 60),
                ]);
    }

    private static DisplayProfile CreateFileSystemUsageInfoProfile()
    {
        return DisplayProfile
            .For<FileSystemUsageInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("FileSystem", row => ((FileSystemUsageInfo)row).FileSystem, MinWidth: 10, MaxWidth: 28, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Type", row => ((FileSystemUsageInfo)row).Type, MinWidth: 4, MaxWidth: 14, Priority: 10),
                    new DisplayTableColumn("Size", row => ((FileSystemUsageInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                    new DisplayTableColumn("Used", row => ((FileSystemUsageInfo)row).Used, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("Available", row => ((FileSystemUsageInfo)row).Available, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 40),
                    new DisplayTableColumn("Use%", row => FormatUsePercent(((FileSystemUsageInfo)row).UsePercent), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 5, Priority: 50),
                    new DisplayTableColumn("MountedOn", row => ((FileSystemUsageInfo)row).MountedOn, MinWidth: 6, MaxWidth: 28, Priority: 60, CanHide: false),
                ]);
    }

    private static DisplayProfile CreatePingReplyInfoProfile()
    {
        return DisplayProfile
            .For<PingReplyInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Sequence", row => ((PingReplyInfo)row).Sequence, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Address", row => ((PingReplyInfo)row).Address, MinWidth: 7, MaxWidth: 24, Priority: 10),
                    new DisplayTableColumn("Status", row => ((PingReplyInfo)row).Status, MinWidth: 7, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Time", row => FormatPingDuration(((PingReplyInfo)row).RoundtripTime), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                    new DisplayTableColumn("Ttl", row => ((PingReplyInfo)row).Ttl, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 5, Priority: 40),
                    new DisplayTableColumn("Bytes", row => ((PingReplyInfo)row).Bytes, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 50),
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

    private static DisplayProfile CreateGroupingInfoProfile()
    {
        return DisplayProfile
            .For<GroupingInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var grouping = (GroupingInfo)context.Value;
                    var key = grouping.Key?.ToString() ?? "<null>";
                    return $"{key} ({grouping.Count})";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Key", row => ((GroupingInfo)row).Key, MinWidth: 3, MaxWidth: 32, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Count", row => ((GroupingInfo)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 10, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateHelpSubjectKindProfile()
    {
        return DisplayProfile
            .For<HelpSubjectKind>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((HelpSubjectKind)context.Value) switch
                {
                    HelpSubjectKind.BuiltIn => "built-in",
                    HelpSubjectKind.Alias => "alias",
                    HelpSubjectKind.Function => "function",
                    HelpSubjectKind.External => "external",
                    HelpSubjectKind.Language => "language",
                    HelpSubjectKind.Type => "type",
                    _ => context.Value.ToString() ?? string.Empty,
                });
    }

    private static DisplayProfile CreateHelpSummaryProfile()
    {
        return DisplayProfile
            .For<HelpSummary>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((HelpSummary)row).Name, MinWidth: 8, MaxWidth: 18, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpSummary)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Category", row => ((HelpSummary)row).Category, MinWidth: 8, MaxWidth: 14, Priority: 20),
                    new DisplayTableColumn("Description", row => ((HelpSummary)row).Description, MinWidth: 20, MaxWidth: 56, Priority: 30, CanHide: false),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpSummary)row).Aliases), MinWidth: 6, MaxWidth: 24, Priority: 40),
                    new DisplayTableColumn("Usage", row => ((HelpSummary)row).Usage, MinWidth: 16, MaxWidth: 48, Priority: 50),
                ]);
    }

    private static DisplayProfile CreateHelpSearchResultProfile()
    {
        return DisplayProfile
            .For<HelpSearchResult>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Score", row => ((HelpSearchResult)row).Score.ToString("0.0", CultureInfo.InvariantCulture), MinWidth: 5, MaxWidth: 7, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Name", row => ((HelpSearchResult)row).Name, MinWidth: 8, MaxWidth: 18, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpSearchResult)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 20),
                    new DisplayTableColumn("Category", row => ((HelpSearchResult)row).Category, MinWidth: 8, MaxWidth: 14, Priority: 30),
                    new DisplayTableColumn("Description", row => ((HelpSearchResult)row).Description, MinWidth: 20, MaxWidth: 56, Priority: 40, CanHide: false),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpSearchResult)row).Aliases), MinWidth: 6, MaxWidth: 24, Priority: 50),
                    new DisplayTableColumn("Usage", row => ((HelpSearchResult)row).Usage, MinWidth: 16, MaxWidth: 48, Priority: 60),
                ]);
    }

    private static DisplayProfile CreateHelpTopicProfile()
    {
        return DisplayProfile
            .For<HelpTopic>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((HelpTopic)row).Name, MinWidth: 8, MaxWidth: 22, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Kind", row => ((HelpTopic)row).Kind, MinWidth: 8, MaxWidth: 10, Priority: 10),
                    new DisplayTableColumn("Category", row => ((HelpTopic)row).Category, MinWidth: 8, MaxWidth: 16, Priority: 20),
                    new DisplayTableColumn("Description", row => ((HelpTopic)row).Description, MinWidth: 20, MaxWidth: 64, Priority: 30, CanHide: false),
                    new DisplayTableColumn("Usage", row => ((HelpTopic)row).Usage, MinWidth: 16, MaxWidth: 56, Priority: 40),
                    new DisplayTableColumn("Aliases", row => string.Join(", ", ((HelpTopic)row).Aliases), MinWidth: 6, MaxWidth: 28, Priority: 50),
                    new DisplayTableColumn("Related", row => string.Join(", ", ((HelpTopic)row).Related), MinWidth: 8, MaxWidth: 36, Priority: 60),
                    new DisplayTableColumn("Examples", row => string.Join(" | ", ((HelpTopic)row).Examples), MinWidth: 12, MaxWidth: 72, Priority: 70),
                    new DisplayTableColumn("Path", row => ((HelpTopic)row).Path, MinWidth: 8, MaxWidth: 48, Priority: 80),
                    new DisplayTableColumn("Notes", row => ((HelpTopic)row).Notes, MinWidth: 12, MaxWidth: 72, Priority: 90),
                ]);
    }

    private static DisplayProfile CreateHelpCategoryInfoProfile()
    {
        return DisplayProfile
            .For<HelpCategoryInfo>()
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Category", row => ((HelpCategoryInfo)row).Category, MinWidth: 10, MaxWidth: 18, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Count", row => ((HelpCategoryInfo)row).Count, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 8, Priority: 10, CanHide: false),
                ]);
    }

    private static DisplayProfile CreateEnvironmentVariableEntryProfile()
    {
        return DisplayProfile
            .For<EnvironmentVariableEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var variable = (EnvironmentVariableEntry)context.Value;
                    return variable.IsSet
                        ? $"{variable.Name}={variable.Value}"
                        : $"{variable.Name}=<unset>";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Name", row => ((EnvironmentVariableEntry)row).Name, MinWidth: 10, MaxWidth: 24, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Value", row => ((EnvironmentVariableEntry)row).Value, MinWidth: 12, MaxWidth: 64, Priority: 10),
                    new DisplayTableColumn("Set", row => ((EnvironmentVariableEntry)row).IsSet, MinWidth: 3, MaxWidth: 5, Priority: 20),
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
                    return $"{entry.Index,4}  {timestamp}  {entry.Text}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Index", row => ((CommandHistoryEntry)row).Index, DisplayTableAlignment.Right, MinWidth: 5, MaxWidth: 5, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Text", row => ((CommandHistoryEntry)row).Text, MinWidth: 12, MaxWidth: 64, Priority: 10, CanHide: false),
                    new DisplayTableColumn("When", row => ((CommandHistoryEntry)row).When, MinWidth: 11, MaxWidth: 18, Priority: 20),
                ]);
    }

    private static DisplayProfile CreateFormatterStatusProfile()
    {
        return DisplayProfile
            .For<FormatterStatus>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => $"view: {((FormatterStatus)context.Value).Style.ToString().ToLowerInvariant()}");
    }

    private static DisplayProfile CreateXDocumentProfile()
    {
        return DisplayProfile
            .For<XDocument>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var document = (XDocument)context.Value;
                    return document.Root?.ToString(SaveOptions.DisableFormatting) ?? "<empty-xml />";
                });
    }

    private static DisplayProfile CreateXElementProfile()
    {
        return DisplayProfile
            .For<XElement>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((XElement)context.Value).ToString(SaveOptions.DisableFormatting));
    }

    private static string FormatDateTime(
        DateTime value,
        TemporalDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            TemporalDisplayMode.Iso => value.ToString("O", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Local => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(ToDisplayInstant(value), nowProvider()),
            TemporalDisplayMode.Unix => ToDisplayInstant(value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            TemporalDisplayMode.Custom => value.ToString(format ?? "O", CultureInfo.InvariantCulture),
            _ => value.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatDateTimeOffset(
        DateTimeOffset value,
        TemporalDisplayMode mode,
        string? format,
        Func<DateTimeOffset> nowProvider)
    {
        return mode switch
        {
            TemporalDisplayMode.Iso => value.ToString("O", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Local => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TemporalDisplayMode.Relative => FormatRelativeTime(value, nowProvider()),
            TemporalDisplayMode.Unix => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            TemporalDisplayMode.Custom => value.ToString(format ?? "O", CultureInfo.InvariantCulture),
            _ => value.ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static DateTimeOffset ToDisplayInstant(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
        }

        return new DateTimeOffset(value);
    }

    private static string FormatRelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = value - now;
        var isFuture = delta >= TimeSpan.Zero;
        var elapsed = isFuture ? delta : -delta;

        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return isFuture ? "in a few seconds" : "just now";
        }

        if (elapsed < TimeSpan.FromMinutes(90))
        {
            var minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(minutes, "minute", isFuture);
        }

        if (elapsed < TimeSpan.FromHours(36))
        {
            var hours = Math.Max(1, (int)Math.Round(elapsed.TotalHours, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(hours, "hour", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            var days = Math.Max(1, (int)Math.Round(elapsed.TotalDays, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(days, "day", isFuture);
        }

        if (elapsed < TimeSpan.FromDays(365))
        {
            var months = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 30d, MidpointRounding.AwayFromZero));
            return FormatRelativeUnit(months, "month", isFuture);
        }

        var years = Math.Max(1, (int)Math.Round(elapsed.TotalDays / 365d, MidpointRounding.AwayFromZero));
        return FormatRelativeUnit(years, "year", isFuture);
    }

    private static string FormatRelativeUnit(int value, string unit, bool isFuture)
    {
        var suffix = value == 1 ? unit : $"{unit}s";
        return isFuture ? $"in {value} {suffix}" : $"{value} {suffix} ago";
    }

    private static string FormatStorageSize(StorageSize size, StorageSizeDisplayMode mode)
    {
        if (mode == StorageSizeDisplayMode.Bytes)
        {
            return $"{size.Bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var absoluteBytes = Math.Abs((decimal)size.Bytes);
        var sign = size.Bytes < 0 ? "-" : string.Empty;
        var units = new[] { "B", "kB", "MB", "GB", "TB", "PB" };
        var unitIndex = 0;
        var scaled = absoluteBytes;

        while (scaled >= 1000m && unitIndex < units.Length - 1)
        {
            scaled /= 1000m;
            unitIndex++;
        }

        var format = unitIndex == 0 || scaled >= 100m ? "0" : "0.#";
        return $"{sign}{scaled.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static string FormatUsePercent(int? percent)
    {
        return percent is null
            ? string.Empty
            : $"{percent.Value.ToString(CultureInfo.InvariantCulture)}%";
    }

    private static string FormatPingDuration(TimeSpan? duration)
    {
        return duration is null
            ? string.Empty
            : $"{duration.Value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
    }

    private static string FormatPermissions(UnixFileMode mode)
    {
        Span<char> characters = stackalloc char[9];
        characters[0] = HasMode(mode, UnixFileMode.UserRead) ? 'r' : '-';
        characters[1] = HasMode(mode, UnixFileMode.UserWrite) ? 'w' : '-';
        characters[2] = GetExecuteCharacter(mode, UnixFileMode.UserExecute, UnixFileMode.SetUser, 's', 'S');
        characters[3] = HasMode(mode, UnixFileMode.GroupRead) ? 'r' : '-';
        characters[4] = HasMode(mode, UnixFileMode.GroupWrite) ? 'w' : '-';
        characters[5] = GetExecuteCharacter(mode, UnixFileMode.GroupExecute, UnixFileMode.SetGroup, 's', 'S');
        characters[6] = HasMode(mode, UnixFileMode.OtherRead) ? 'r' : '-';
        characters[7] = HasMode(mode, UnixFileMode.OtherWrite) ? 'w' : '-';
        characters[8] = GetExecuteCharacter(mode, UnixFileMode.OtherExecute, UnixFileMode.StickyBit, 't', 'T');
        return new string(characters);
    }

    private static bool ShouldRightAlignProjectedField(IReadOnlyList<ProjectedObject> rows, string fieldName)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldName, out var value) || value is null)
            {
                continue;
            }

            var effectiveType = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();

            return effectiveType == typeof(byte) ||
                   effectiveType == typeof(short) ||
                   effectiveType == typeof(int) ||
                   effectiveType == typeof(long) ||
                   effectiveType == typeof(StorageSize) ||
                   effectiveType == typeof(float) ||
                   effectiveType == typeof(double) ||
                   effectiveType == typeof(decimal);
        }

        return false;
    }

    private static bool HasMode(UnixFileMode value, UnixFileMode flag) => (value & flag) == flag;

    private static char GetExecuteCharacter(
        UnixFileMode mode,
        UnixFileMode executeFlag,
        UnixFileMode specialFlag,
        char specialWhenExecute,
        char specialWhenNotExecute)
    {
        var hasExecute = HasMode(mode, executeFlag);
        var hasSpecial = HasMode(mode, specialFlag);

        if (hasSpecial)
        {
            return hasExecute ? specialWhenExecute : specialWhenNotExecute;
        }

        return hasExecute ? 'x' : '-';
    }
}
