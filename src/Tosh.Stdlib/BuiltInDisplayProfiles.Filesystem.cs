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
    internal static void RegisterFilesystemProfiles(DisplayProfileRegistry registry, DisplayPreferences preferences)
    {
        registry.Register(CreateManagedFileHandleProfile());
        registry.Register(CreateTreeEntryProfile());
        registry.Register(CreateRemovedEntryProfile());
        registry.Register(CreateFileDescriptorProfile());
        registry.Register(CreateFileSystemWatcherProfile());
        registry.Register(CreateUnixFileModeProfile(preferences));
        registry.Register(CreateFileAttributesProfile(preferences));
        registry.Register(CreateFileSystemPrincipalProfile());
        registry.Register(CreateFileSystemEntryTypeProfile());
        registry.Register(CreateFileSystemEntryProfile(preferences));
        registry.Register(CreateFileSystemInfoProfile(preferences));
        registry.Register(CreateDriveInfoProfile());
        registry.Register(CreateFileSystemUsageInfoProfile());
        registry.Register(CreatePathUsageInfoProfile());
        registry.Register(CreateDirectoryStackEntryProfile());
        registry.Register(CreateStreamProfile());
        registry.Register(CreateStreamReaderProfile());
        registry.Register(CreateStreamWriterProfile());
        registry.Register(CreateZipArchiveProfile());
        registry.Register(CreateZipArchiveEntryProfile());
    }

    private static DisplayProfile CreateManagedFileHandleProfile()
    {
        return DisplayProfile
            .For<ManagedFileHandle>()
            .AddTableCase(
                _ => BuildManagedFileHandleColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((ManagedFileHandle)context.Value).ToString());
    }

    private static DisplayProfile CreateTreeEntryProfile()
    {
        return DisplayProfile
            .For<TreeEntryInfo>()
            .AddTableCase(_ => BuildTreeEntryDefaultColumns())
            .AddSelectableTableColumns(_ => BuildTreeEntrySelectableColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (TreeEntryInfo)context.Value;
                    return entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
                });
    }

    private static DisplayProfile CreateFileDescriptorProfile()
    {
        return DisplayProfile
            .For<FileDescriptorInfo>()
            .AddTableCase(_ => BuildFileDescriptorDefaultColumns())
            .AddSelectableTableColumns(context => BuildFileDescriptorSelectableColumns(context.Rows))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileDescriptorInfo)context.Value).ToString());
    }

    private static DisplayProfile CreateFileSystemWatcherProfile()
    {
        return DisplayProfile
            .For<FileSystemWatcher>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileSystemWatcherColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFileSystemWatcherSummary((FileSystemWatcher)context.Value));
    }

    private static DisplayProfile CreateUnixFileModeProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<UnixFileMode>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildUnixFileModeColumns(preferences))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatPermissions((UnixFileMode)context.Value, preferences.UnixFileMode.Mode));
    }

    private static DisplayProfile CreateFileAttributesProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileAttributes>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileAttributesColumns(preferences))
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => FormatFileAttributes((FileAttributes)context.Value, preferences.FileAttributes.Mode));
    }

    private static DisplayProfile CreateFileSystemPrincipalProfile()
    {
        return DisplayProfile
            .For<FileSystemPrincipalInfo>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildFileSystemPrincipalColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context => ((FileSystemPrincipalInfo)context.Value).DisplayName);
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
                    var timestamp = FormatDateTimeOffset(
                        entry.DisplayTime,
                        preferences.DateTimeOffset.TableMode,
                        preferences.DateTimeOffset.TableFormat,
                        preferences.NowProvider);
                    var owner = entry.Owner?.DisplayName ?? "-";
                    var group = entry.Group?.DisplayName ?? "-";
                    return $"{entry.GetModeDisplay(includeTypeIndicator: true)} {owner}:{group} {size,10} {timestamp} {entry.DisplayName}";
                })
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context => context.Style != ObjectRenderStyle.Detail,
                context => ((FileSystemEntry)context.Value).DisplayName)
            .AddTableCase(context => BuildFileSystemEntryColumns(context.Rows));
    }

    private static DisplayProfile CreateFileSystemInfoProfile(DisplayPreferences preferences)
    {
        return DisplayProfile
            .For<FileSystemInfo>()
            .AddValueCase(
                DisplaySurface.TableCell,
                context => GetDisplayEntry((FileSystemInfo)context.Value).DisplayName)
            .AddTableCase(context => BuildFileSystemInfoColumns(context.Rows));
    }

    private static DisplayProfile CreateDriveInfoProfile()
    {
        return DisplayProfile
            .For<DriveInfo>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var drive = (DriveInfo)context.Value;
                    return drive.IsReady
                        ? $"{drive.Name} ({drive.DriveType})"
                        : $"{drive.Name} (not ready)";
                })
            .AddTableCase(
                context => BuildDriveInfoColumns(context.Rows));
    }

    private static DisplayProfile CreateFileSystemUsageInfoProfile()
    {
        return DisplayProfile
            .For<FileSystemUsageInfo>()
            .AddTableCase(
                context =>
                {
                    var items = context.Rows.Cast<FileSystemUsageInfo>().ToArray();
                    var columns = new List<DisplayTableColumn>();

                    if (items.Any(item => !string.IsNullOrWhiteSpace(item.RequestedPath)))
                    {
                        columns.Add(new DisplayTableColumn("Path", row => ((FileSystemUsageInfo)row).RequestedPath, MinWidth: 8, MaxWidth: 40, Priority: 0));
                    }

                    columns.AddRange(
                    [
                        new DisplayTableColumn("FileSystem", row => ((FileSystemUsageInfo)row).FileSystem, MinWidth: 10, MaxWidth: 28, Priority: 10, CanHide: false),
                        new DisplayTableColumn("Type", row => ((FileSystemUsageInfo)row).Type, MinWidth: 4, MaxWidth: 14, Priority: 20),
                        new DisplayTableColumn("Size", row => ((FileSystemUsageInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                        new DisplayTableColumn("Used", row => ((FileSystemUsageInfo)row).Used, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 40),
                        new DisplayTableColumn("Available", row => ((FileSystemUsageInfo)row).Available, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 50),
                        new DisplayTableColumn("Use%", row => FormatUsePercent(((FileSystemUsageInfo)row).UsePercent), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 5, Priority: 60, SelectionKey: "UsePercent"),
                        new DisplayTableColumn("MountedOn", row => ((FileSystemUsageInfo)row).MountedOn, MinWidth: 6, MaxWidth: 28, Priority: 70, CanHide: false),
                    ]);

                    return columns;
                });
    }

    private static DisplayProfile CreatePathUsageInfoProfile()
    {
        return DisplayProfile
            .For<PathUsageInfo>()
            .AddTableCase(
                context =>
                {
                    var items = context.Rows.Cast<PathUsageInfo>().ToArray();
                    var columns = new List<DisplayTableColumn>
                    {
                        new("Name", row => ((PathUsageInfo)row).Name, MinWidth: 8, MaxWidth: 40, Priority: 0, CanHide: false),
                        new("Type", row => ((PathUsageInfo)row).Type, MinWidth: 4, MaxWidth: 8, Priority: 10),
                        new("Size", row => ((PathUsageInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                        new("Depth", row => ((PathUsageInfo)row).Depth, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 30),
                    };

                    if (items.Any(item => item.Modified is not null))
                    {
                        columns.Add(new DisplayTableColumn("Modified", row => ((PathUsageInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 40));
                    }

                    return columns;
                })
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var value = (PathUsageInfo)context.Value;
                    return $"{value.Size} {value.FullName}";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemEntryColumns(IReadOnlyList<object> rows)
    {
        var entries = rows.Cast<FileSystemEntry>().ToArray();
        var showLongMetadata = entries.Any(entry => entry.PreferLongDisplay);
        var showTarget = entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));
        var showInodeInShortDisplay = entries.Any(entry => entry.IncludeInodeInShortDisplay);
        var timeFieldEntry = entries.FirstOrDefault() ?? throw new InvalidOperationException("Expected at least one file-system entry row.");
        var timeHeader = timeFieldEntry.DisplayTimeColumnName;
        Func<object, object?> timeAccessor = row => ((FileSystemEntry)row).DisplayTime;

        if (!showLongMetadata)
        {
            var shortColumns = new List<DisplayTableColumn>();

            if (showInodeInShortDisplay)
            {
                shortColumns.Add(new DisplayTableColumn("Inode", row => ((FileSystemEntry)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 0));
            }

            shortColumns.AddRange(
            [
                new DisplayTableColumn("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 10, CanHide: false, IsTree: true),
                new DisplayTableColumn("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 20),
                new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
                new DisplayTableColumn(timeHeader, timeAccessor, MinWidth: 11, MaxWidth: 18, Priority: 40),
            ]);

            return shortColumns;
        }

        var columns = new List<DisplayTableColumn>
        {
            new("Name", row => ((FileSystemEntry)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false, IsTree: true),
            new("Type", row => ((FileSystemEntry)row).Type, MaxWidth: 8, Priority: 10),
        };

        if (showTarget)
        {
            columns.Add(new DisplayTableColumn("Target", row => ((FileSystemEntry)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 95));
        }

        columns.AddRange(
        [
            new DisplayTableColumn("Size", row => ((FileSystemEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new DisplayTableColumn(timeHeader, timeAccessor, MinWidth: 11, MaxWidth: 18, Priority: 30),
            new DisplayTableColumn("Readonly", row => ((FileSystemEntry)row).Readonly, MinWidth: 5, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Mode", row => ((FileSystemEntry)row).Mode, MinWidth: 9, MaxWidth: 18, Priority: 50),
            new DisplayTableColumn("NumLinks", row => ((FileSystemEntry)row).NumLinks, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 60),
            new DisplayTableColumn("Inode", row => ((FileSystemEntry)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Owner", row => ((FileSystemEntry)row).Owner, MinWidth: 4, MaxWidth: 16, Priority: 80),
            new DisplayTableColumn("Group", row => ((FileSystemEntry)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 90),
            new DisplayTableColumn("Created", row => ((FileSystemEntry)row).Created, MinWidth: 11, MaxWidth: 18, Priority: 100),
            new DisplayTableColumn("Accessed", row => ((FileSystemEntry)row).Accessed, MinWidth: 11, MaxWidth: 18, Priority: 110),
        ]);

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemInfoColumns(IReadOnlyList<object> rows)
    {
        if (rows.Count == 1)
        {
            return
            [
                new DisplayTableColumn("Name", row => GetDisplayEntry((FileSystemInfo)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
                new DisplayTableColumn("FullName", row => ((FileSystemInfo)row).FullName, MinWidth: 16, MaxWidth: 72, Priority: 5, CanHide: false),
                new DisplayTableColumn("Type", row => GetDisplayEntry((FileSystemInfo)row).Type, MaxWidth: 8, Priority: 10),
                new DisplayTableColumn("Size", row => GetDisplayEntry((FileSystemInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
                new DisplayTableColumn("Modified", row => GetDisplayEntry((FileSystemInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
                new DisplayTableColumn("Attributes", row => ((FileSystemInfo)row).Attributes, MinWidth: 6, MaxWidth: 28, Priority: 40),
                new DisplayTableColumn("Target", row => GetDisplayEntry((FileSystemInfo)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 50),
            ];
        }

        var entries = rows.Cast<FileSystemInfo>().Select(GetDisplayEntry).ToArray();
        var showTarget = entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Target));

        var columns = new List<DisplayTableColumn>
        {
            new("Name", row => GetDisplayEntry((FileSystemInfo)row).DisplayName, MinWidth: 12, MaxWidth: 48, Priority: 0, CanHide: false),
            new("Type", row => GetDisplayEntry((FileSystemInfo)row).Type, MaxWidth: 8, Priority: 10),
            new("Size", row => GetDisplayEntry((FileSystemInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20),
            new("Modified", row => GetDisplayEntry((FileSystemInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30),
        };

        if (showTarget)
        {
            columns.Add(new DisplayTableColumn("Target", row => GetDisplayEntry((FileSystemInfo)row).Target, MinWidth: 8, MaxWidth: 36, Priority: 40));
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildDriveInfoColumns(IReadOnlyList<object> rows)
    {
        if (rows.Count == 1)
        {
            return
            [
                new DisplayTableColumn("Name", row => ((DriveInfo)row).Name, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
                new DisplayTableColumn("DriveType", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveType), MinWidth: 4, MaxWidth: 16, Priority: 10),
                new DisplayTableColumn("DriveFormat", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveFormat), MinWidth: 4, MaxWidth: 16, Priority: 20),
                new DisplayTableColumn("VolumeLabel", row => SafeGetDriveValue((DriveInfo)row, drive => drive.VolumeLabel), MinWidth: 4, MaxWidth: 24, Priority: 30),
                new DisplayTableColumn("RootDirectory", row => ((DriveInfo)row).RootDirectory, MinWidth: 4, MaxWidth: 24, Priority: 40),
                new DisplayTableColumn("TotalSize", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalSize), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 50),
                new DisplayTableColumn("AvailableFreeSpace", row => SafeGetDriveSize((DriveInfo)row, drive => drive.AvailableFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 60),
                new DisplayTableColumn("TotalFreeSpace", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 70),
                new DisplayTableColumn("IsReady", row => ((DriveInfo)row).IsReady, MinWidth: 5, MaxWidth: 5, Priority: 80),
            ];
        }

        return
        [
            new DisplayTableColumn("Name", row => ((DriveInfo)row).Name, MinWidth: 4, MaxWidth: 16, Priority: 0, CanHide: false),
            new DisplayTableColumn("DriveType", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveType), MinWidth: 4, MaxWidth: 16, Priority: 10),
            new DisplayTableColumn("DriveFormat", row => SafeGetDriveValue((DriveInfo)row, drive => drive.DriveFormat), MinWidth: 4, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("TotalSize", row => SafeGetDriveSize((DriveInfo)row, drive => drive.TotalSize), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 30),
            new DisplayTableColumn("Available", row => SafeGetDriveSize((DriveInfo)row, drive => drive.AvailableFreeSpace), DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 40),
            new DisplayTableColumn("IsReady", row => ((DriveInfo)row).IsReady, MinWidth: 5, MaxWidth: 5, Priority: 50),
        ];
    }

    private static DisplayProfile CreateDirectoryStackEntryProfile()
    {
        return DisplayProfile
            .For<DirectoryStackEntry>()
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested,
                context =>
                {
                    var entry = (DirectoryStackEntry)context.Value;
                    var marker = entry.IsCurrent ? "*" : " ";
                    return $"{marker} {entry.Index,3}  {entry.Path}";
                })
            .AddTableCase(
                _ =>
                [
                    new DisplayTableColumn("Index", row => ((DirectoryStackEntry)row).Index, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 6, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Path", row => ((DirectoryStackEntry)row).Path, MinWidth: 16, MaxWidth: 80, Priority: 10, CanHide: false),
                    new DisplayTableColumn("Name", row => ((DirectoryStackEntry)row).Name, MinWidth: 8, MaxWidth: 32, Priority: 20),
                    new DisplayTableColumn("Current", row => ((DirectoryStackEntry)row).IsCurrent, MinWidth: 5, MaxWidth: 7, Priority: 30),
                ]);
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemWatcherColumns()
    {
        return
        [
            new DisplayTableColumn("Path", row => NullIfEmpty(((FileSystemWatcher)row).Path), MinWidth: 3, MaxWidth: 128, Priority: 0, CanHide: false),
            new DisplayTableColumn("Filter", row => NullIfEmpty(((FileSystemWatcher)row).Filter), MinWidth: 1, MaxWidth: 64, Priority: 10),
            new DisplayTableColumn("Filters", row => FormatStringCollection(((FileSystemWatcher)row).Filters), MinWidth: 3, MaxWidth: 96, Priority: 20),
            new DisplayTableColumn("NotifyFilter", row => ((FileSystemWatcher)row).NotifyFilter, MinWidth: 3, MaxWidth: 64, Priority: 30),
            new DisplayTableColumn("Enabled", row => ((FileSystemWatcher)row).EnableRaisingEvents, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("IncludeSubdirectories", row => ((FileSystemWatcher)row).IncludeSubdirectories, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("InternalBufferSize", row => StorageSize.FromBytes(((FileSystemWatcher)row).InternalBufferSize), DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 60),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildManagedFileHandleColumns()
    {
        return
        [
            new DisplayTableColumn("#", row => ((ManagedFileHandle)row).Id, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 0, CanHide: false),
            new DisplayTableColumn("Name", row => ((ManagedFileHandle)row).Name, MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("Kind", row => ((ManagedFileHandle)row).Kind, MinWidth: 4, MaxWidth: 8, Priority: 10),
            new DisplayTableColumn("Mode", row => ((ManagedFileHandle)row).Mode, MinWidth: 4, MaxWidth: 8, Priority: 20),
            new DisplayTableColumn("Open", row => ((ManagedFileHandle)row).IsOpen, MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("CanRead", row => ((ManagedFileHandle)row).CanRead, MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CanWrite", row => ((ManagedFileHandle)row).CanWrite, MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("CanSeek", row => ((ManagedFileHandle)row).CanSeek, MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Position", row => ((ManagedFileHandle)row).Position, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 70),
            new DisplayTableColumn("Length", row => ((ManagedFileHandle)row).Length, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 80),
            new DisplayTableColumn("Encoding", row => ((ManagedFileHandle)row).Encoding, MinWidth: 4, MaxWidth: 18, Priority: 90),
            new DisplayTableColumn("Path", row => ((ManagedFileHandle)row).Path, MinWidth: 12, MaxWidth: 72, Priority: 100),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileDescriptorDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Command", row => ((FileDescriptorInfo)row).Command, MinWidth: 6, MaxWidth: 24, Priority: 0, CanHide: false, SelectionKey: "COMMAND"),
            new DisplayTableColumn("Pid", row => ((FileDescriptorInfo)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 8, Priority: 10, SelectionKey: "PID"),
            new DisplayTableColumn("User", row => ((FileDescriptorInfo)row).User, MinWidth: 4, MaxWidth: 18, Priority: 20, SelectionKey: "USER"),
            new DisplayTableColumn("Assoc", row => ((FileDescriptorInfo)row).Association, MinWidth: 4, MaxWidth: 10, Priority: 30, SelectionKey: "ASSOC"),
            new DisplayTableColumn("XMode", row => ((FileDescriptorInfo)row).ExtendedMode, MinWidth: 4, MaxWidth: 10, Priority: 40, SelectionKey: "XMODE"),
            new DisplayTableColumn("Type", row => ((FileDescriptorInfo)row).Type, MinWidth: 4, MaxWidth: 16, Priority: 50, SelectionKey: "TYPE"),
            new DisplayTableColumn("Source", row => ((FileDescriptorInfo)row).Source, MinWidth: 4, MaxWidth: 28, Priority: 60, SelectionKey: "SOURCE"),
            new DisplayTableColumn("MntId", row => ((FileDescriptorInfo)row).MountId, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 10, Priority: 70, SelectionKey: "MNTID"),
            new DisplayTableColumn("Inode", row => ((FileDescriptorInfo)row).Inode, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 16, Priority: 80, SelectionKey: "INODE"),
            new DisplayTableColumn("Name", row => ((FileDescriptorInfo)row).Name, MinWidth: 6, MaxWidth: 48, Priority: 90, SelectionKey: "NAME"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileDescriptorSelectableColumns(IReadOnlyList<object> rows)
    {
        var sampleRows = rows.Cast<FileDescriptorInfo>().ToArray();
        var keys = sampleRows
            .SelectMany(row => row.GetAllFieldKeys())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var columns = new List<DisplayTableColumn>(keys.Length);
        var priority = 0;

        foreach (var key in keys)
        {
            var header = BuildReadableColumnHeader(key);
            var alignment = IsNumericFieldKey(key)
                ? DisplayTableAlignment.Right
                : DisplayTableAlignment.Left;

            columns.Add(new DisplayTableColumn(
                header,
                row => ((FileDescriptorInfo)row).GetFieldValue(key),
                alignment,
                MinWidth: 2,
                MaxWidth: key.Contains("NAME", StringComparison.OrdinalIgnoreCase) || key.Contains("PATH", StringComparison.OrdinalIgnoreCase) || key.Contains("SOURCE", StringComparison.OrdinalIgnoreCase) ? 48 : 24,
                Priority: priority,
                SelectionKey: key));
            priority += 10;
        }

        return columns;
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTreeEntryDefaultColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((TreeEntryInfo)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("Type", row => ((TreeEntryInfo)row).Type, MinWidth: 4, MaxWidth: 10, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("Size", row => ((TreeEntryInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "SIZE"),
            new DisplayTableColumn("Modified", row => ((TreeEntryInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30, SelectionKey: "MODIFIED"),
            new DisplayTableColumn("Permissions", row => ((TreeEntryInfo)row).Permissions, MinWidth: 9, MaxWidth: 12, Priority: 40, SelectionKey: "PROT"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildTreeEntrySelectableColumns()
    {
        return
        [
            new DisplayTableColumn("Name", row => ((TreeEntryInfo)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, SelectionKey: "NAME", IsTree: true),
            new DisplayTableColumn("Type", row => ((TreeEntryInfo)row).Type, MinWidth: 4, MaxWidth: 10, Priority: 10, SelectionKey: "TYPE"),
            new DisplayTableColumn("Size", row => ((TreeEntryInfo)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 20, SelectionKey: "SIZE"),
            new DisplayTableColumn("Modified", row => ((TreeEntryInfo)row).Modified, MinWidth: 11, MaxWidth: 18, Priority: 30, SelectionKey: "MODIFIED"),
            new DisplayTableColumn("Permissions", row => ((TreeEntryInfo)row).Permissions, MinWidth: 9, MaxWidth: 12, Priority: 40, SelectionKey: "PROT"),
            new DisplayTableColumn("Mode", row => ((TreeEntryInfo)row).Mode, MinWidth: 4, MaxWidth: 8, Priority: 50, SelectionKey: "MODE"),
            new DisplayTableColumn("User", row => ((TreeEntryInfo)row).User, MinWidth: 4, MaxWidth: 16, Priority: 60, SelectionKey: "USER"),
            new DisplayTableColumn("Group", row => ((TreeEntryInfo)row).Group, MinWidth: 4, MaxWidth: 16, Priority: 70, SelectionKey: "GROUP"),
            new DisplayTableColumn("Inode", row => ((TreeEntryInfo)row).Inode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 80, SelectionKey: "INODE"),
            new DisplayTableColumn("DeviceId", row => ((TreeEntryInfo)row).DeviceId, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 90, SelectionKey: "DEV"),
            new DisplayTableColumn("NumLinks", row => ((TreeEntryInfo)row).NumLinks, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 100, SelectionKey: "NLINK"),
            new DisplayTableColumn("Target", row => ((TreeEntryInfo)row).LinkTarget, MinWidth: 4, MaxWidth: 36, Priority: 110, SelectionKey: "TARGET"),
            new DisplayTableColumn("Path", row => ((TreeEntryInfo)row).FullPath, MinWidth: 8, MaxWidth: 64, Priority: 120, SelectionKey: "PATH"),
            new DisplayTableColumn("Depth", row => ((TreeEntryInfo)row).Depth, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 6, Priority: 130, SelectionKey: "DEPTH"),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildUnixFileModeColumns(DisplayPreferences preferences)
    {
        return
        [
            new DisplayTableColumn("Mode", row => FormatPermissions((UnixFileMode)row, preferences.UnixFileMode.Mode), MinWidth: 4, MaxWidth: 24, Priority: 0, CanHide: false),
            new DisplayTableColumn("UserRead", row => HasMode((UnixFileMode)row, UnixFileMode.UserRead), MinWidth: 4, MaxWidth: 5, Priority: 10),
            new DisplayTableColumn("UserWrite", row => HasMode((UnixFileMode)row, UnixFileMode.UserWrite), MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("UserExecute", row => HasMode((UnixFileMode)row, UnixFileMode.UserExecute), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("GroupRead", row => HasMode((UnixFileMode)row, UnixFileMode.GroupRead), MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("GroupWrite", row => HasMode((UnixFileMode)row, UnixFileMode.GroupWrite), MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("GroupExecute", row => HasMode((UnixFileMode)row, UnixFileMode.GroupExecute), MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("OtherRead", row => HasMode((UnixFileMode)row, UnixFileMode.OtherRead), MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("OtherWrite", row => HasMode((UnixFileMode)row, UnixFileMode.OtherWrite), MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("OtherExecute", row => HasMode((UnixFileMode)row, UnixFileMode.OtherExecute), MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("SetUser", row => HasMode((UnixFileMode)row, UnixFileMode.SetUser), MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("SetGroup", row => HasMode((UnixFileMode)row, UnixFileMode.SetGroup), MinWidth: 4, MaxWidth: 5, Priority: 110),
            new DisplayTableColumn("StickyBit", row => HasMode((UnixFileMode)row, UnixFileMode.StickyBit), MinWidth: 4, MaxWidth: 5, Priority: 120),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileAttributesColumns(DisplayPreferences preferences)
    {
        return
        [
            new DisplayTableColumn("Mode", row => FormatFileAttributes((FileAttributes)row, preferences.FileAttributes.Mode), MinWidth: 4, MaxWidth: 48, Priority: 0, CanHide: false),
            new DisplayTableColumn("Hex", row => $"0x{((int)(FileAttributes)row):X}", MinWidth: 3, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("ReadOnly", row => HasFileAttribute((FileAttributes)row, FileAttributes.ReadOnly), MinWidth: 4, MaxWidth: 5, Priority: 20),
            new DisplayTableColumn("Hidden", row => HasFileAttribute((FileAttributes)row, FileAttributes.Hidden), MinWidth: 4, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("System", row => HasFileAttribute((FileAttributes)row, FileAttributes.System), MinWidth: 4, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("Directory", row => HasFileAttribute((FileAttributes)row, FileAttributes.Directory), MinWidth: 4, MaxWidth: 5, Priority: 50),
            new DisplayTableColumn("Archive", row => HasFileAttribute((FileAttributes)row, FileAttributes.Archive), MinWidth: 4, MaxWidth: 5, Priority: 60),
            new DisplayTableColumn("Normal", row => HasFileAttribute((FileAttributes)row, FileAttributes.Normal), MinWidth: 4, MaxWidth: 5, Priority: 70),
            new DisplayTableColumn("Temporary", row => HasFileAttribute((FileAttributes)row, FileAttributes.Temporary), MinWidth: 4, MaxWidth: 5, Priority: 80),
            new DisplayTableColumn("ReparsePoint", row => HasFileAttribute((FileAttributes)row, FileAttributes.ReparsePoint), MinWidth: 4, MaxWidth: 5, Priority: 90),
            new DisplayTableColumn("Compressed", row => HasFileAttribute((FileAttributes)row, FileAttributes.Compressed), MinWidth: 4, MaxWidth: 5, Priority: 100),
            new DisplayTableColumn("Encrypted", row => HasFileAttribute((FileAttributes)row, FileAttributes.Encrypted), MinWidth: 4, MaxWidth: 5, Priority: 110),
        ];
    }

    private static IReadOnlyList<DisplayTableColumn> BuildFileSystemPrincipalColumns()
    {
        return
        [
            new DisplayTableColumn("DisplayName", row => ((FileSystemPrincipalInfo)row).DisplayName, MinWidth: 4, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Id", row => ((FileSystemPrincipalInfo)row).Id, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: 12, Priority: 10),
            new DisplayTableColumn("Name", row => ((FileSystemPrincipalInfo)row).Name, MinWidth: 1, MaxWidth: 32, Priority: 20),
        ];
    }

    private static string BuildReadableColumnHeader(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var normalized = key.Replace('.', ' ').Replace(':', ' ').Replace('-', ' ');
        var builder = new System.Text.StringBuilder(normalized.Length);
        var previousWasSpace = true;

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            if (!previousWasSpace &&
                builder.Length > 0 &&
                char.IsUpper(character) &&
                char.IsLetter(builder[^1]) &&
                char.IsLower(builder[^1]))
            {
                builder.Append(' ');
            }

            builder.Append(character);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool IsNumericFieldKey(string key)
    {
        return key.EndsWith("ID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("PORT", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("SIZE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LEN", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("POS", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("PID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("TID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("INODE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("MNTID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("FD", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("VALUE", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("RCID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LCID", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("RPORT", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("LPORT", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("PID", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("TID", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("FD", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("SIZE", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("POS", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("INODE", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("MNTID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFileAttribute(FileAttributes attributes, FileAttributes flag)
    {
        return (attributes & flag) == flag;
    }

    private static DisplayProfile CreateRemovedEntryProfile()
    {
        return DisplayProfile
            .For<RemovedEntry>()
            .AddTableCase(_ =>
            [
                new DisplayTableColumn("Name", row => ((RemovedEntry)row).ToString(), MinWidth: 3, MaxWidth: 48, Priority: 0, CanHide: false, IsTree: true),
                new DisplayTableColumn("Type", row => ((RemovedEntry)row).IsDirectory ? "dir" : "file", MinWidth: 4, MaxWidth: 10, Priority: 5),
                new DisplayTableColumn("Size", row => ((RemovedEntry)row).Size, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 12, Priority: 10),
            ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (RemovedEntry)context.Value;
                    return entry.IsDirectory ? $"{entry.Name}/" : entry.Name;
                });
    }

    // ── Streams and I/O ──────────────────────────────────────────────────

    private static DisplayProfile CreateStreamProfile()
    {
        return DisplayProfile
            .For<Stream>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ => BuildStreamColumns())
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var stream = (Stream)context.Value;
                    var type = stream.GetType().Name;
                    var len = stream.CanSeek ? $"{stream.Length} bytes" : "non-seekable";
                    var flags = string.Join("/",
                        new[] { stream.CanRead ? "R" : null, stream.CanWrite ? "W" : null, stream.CanSeek ? "S" : null }
                        .Where(f => f is not null));
                    return $"{type} ({len}, {flags})";
                });
    }

    private static IReadOnlyList<DisplayTableColumn> BuildStreamColumns()
    {
        return
        [
            new DisplayTableColumn("Type", row => row.GetType().Name, MinWidth: 6, MaxWidth: 32, Priority: 0, CanHide: false),
            new DisplayTableColumn("Length", row => ((Stream)row).CanSeek ? ((Stream)row).Length : null, DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 16, Priority: 10),
            new DisplayTableColumn("Position", row => ((Stream)row).CanSeek ? ((Stream)row).Position : null, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 16, Priority: 20),
            new DisplayTableColumn("CanRead", row => ((Stream)row).CanRead, MinWidth: 5, MaxWidth: 5, Priority: 30),
            new DisplayTableColumn("CanWrite", row => ((Stream)row).CanWrite, MinWidth: 5, MaxWidth: 5, Priority: 40),
            new DisplayTableColumn("CanSeek", row => ((Stream)row).CanSeek, MinWidth: 5, MaxWidth: 5, Priority: 50),
        ];
    }

    private static DisplayProfile CreateStreamReaderProfile()
    {
        return DisplayProfile
            .For<StreamReader>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Encoding", row => ((StreamReader)row).CurrentEncoding.WebName, MinWidth: 5, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("EndOfStream", row => ((StreamReader)row).EndOfStream, MinWidth: 5, MaxWidth: 5, Priority: 10),
                    new DisplayTableColumn("BaseStream", row => ((StreamReader)row).BaseStream.GetType().Name, MinWidth: 6, MaxWidth: 24, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var reader = (StreamReader)context.Value;
                    return $"StreamReader ({reader.CurrentEncoding.WebName}, eof={reader.EndOfStream})";
                });
    }

    private static DisplayProfile CreateStreamWriterProfile()
    {
        return DisplayProfile
            .For<StreamWriter>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Encoding", row => ((StreamWriter)row).Encoding.WebName, MinWidth: 5, MaxWidth: 20, Priority: 0, CanHide: false),
                    new DisplayTableColumn("AutoFlush", row => ((StreamWriter)row).AutoFlush, MinWidth: 5, MaxWidth: 5, Priority: 10),
                    new DisplayTableColumn("BaseStream", row => ((StreamWriter)row).BaseStream.GetType().Name, MinWidth: 6, MaxWidth: 24, Priority: 20),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var writer = (StreamWriter)context.Value;
                    return $"StreamWriter ({writer.Encoding.WebName}, autoflush={writer.AutoFlush})";
                });
    }

    private static DisplayProfile CreateZipArchiveProfile()
    {
        return DisplayProfile
            .For<ZipArchive>()
            .AddTableCase(
                context => context.Rows.Count == 1,
                _ =>
                [
                    new DisplayTableColumn("Mode", row => ((ZipArchive)row).Mode.ToString(), MinWidth: 4, MaxWidth: 10, Priority: 0, CanHide: false),
                    new DisplayTableColumn("Entries", row => ((ZipArchive)row).Entries.Count, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 10),
                ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var zip = (ZipArchive)context.Value;
                    return $"ZipArchive ({zip.Mode}, {zip.Entries.Count} entries)";
                });
    }

    private static DisplayProfile CreateZipArchiveEntryProfile()
    {
        return DisplayProfile
            .For<ZipArchiveEntry>()
            .AddTableCase(_ =>
            [
                new DisplayTableColumn("Name", row => ((ZipArchiveEntry)row).FullName, MinWidth: 8, MaxWidth: 64, Priority: 0, CanHide: false),
                new DisplayTableColumn("Size", row => new StorageSize(((ZipArchiveEntry)row).Length), DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 12, Priority: 10),
                new DisplayTableColumn("Compressed", row => new StorageSize(((ZipArchiveEntry)row).CompressedLength), DisplayTableAlignment.Right, MinWidth: 6, MaxWidth: 12, Priority: 20),
                new DisplayTableColumn("Modified", row => ((ZipArchiveEntry)row).LastWriteTime, MinWidth: 10, MaxWidth: 20, Priority: 30),
            ])
            .AddValueCase(
                DisplaySurface.Root | DisplaySurface.Nested | DisplaySurface.TableCell,
                context =>
                {
                    var entry = (ZipArchiveEntry)context.Value;
                    return $"{entry.FullName} ({new StorageSize(entry.Length)})";
                });
    }

    // ── Platform and Runtime ─────────────────────────────────────────────


}
