using Tosh.Core;

namespace Tosh.Tests;

public sealed class SystemdJsonParserTests
{
    [Fact]
    public void Systemctl_json_parser_parses_unit_rows()
    {
        const string json = """
            [
              {
                "unit": "sshd.service",
                "load": "loaded",
                "active": "active",
                "sub": "running",
                "description": "OpenSSH Daemon"
              }
            ]
            """;

        var units = SystemctlJsonParser.ParseUnitList(json);
        var unit = Assert.Single(units);

        Assert.Equal("sshd.service", unit.Unit);
        Assert.Equal("loaded", unit.LoadState);
        Assert.Equal("active", unit.ActiveState);
        Assert.Equal("running", unit.SubState);
        Assert.Equal("service", unit.UnitType);
        Assert.True(unit.IsActive);
    }

    [Fact]
    public void Systemctl_json_parser_parses_unit_file_rows()
    {
        const string json = """
            [
              {
                "unit_file": "sshd.service",
                "state": "enabled",
                "preset": "disabled"
              },
              {
                "unit_file": "systemd-journald.service",
                "state": "static",
                "preset": null
              }
            ]
            """;

        var rows = SystemctlJsonParser.ParseUnitFileList(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("sshd.service", rows[0].UnitFile);
        Assert.Equal("service", rows[0].UnitType);
        Assert.True(rows[0].IsEnabled);
        Assert.False(rows[1].IsEnabled);
        Assert.True(rows[1].IsStatic);
    }

    [Fact]
    public void Systemctl_show_parser_parses_multiple_units_and_typed_values()
    {
        const string text = """
            Id=sshd.service
            Description=OpenSSH Daemon
            ActiveState=active
            SubState=running
            CanStart=yes
            MainPID=1126
            MemoryCurrent=3928064
            ActiveEnterTimestamp=Thu 2026-04-02 14:59:22 EDT
            RestartUSec=100ms
            InvocationID=65800b24718b46f89c426b7b406396e1
            
            Id=dbus-broker.service
            Description=D-Bus System Message Bus
            ActiveState=active
            SubState=running
            CanStart=yes
            MainPID=1047
            MemoryCurrent=10678272
            """;

        var sets = SystemctlJsonParser.ParseShowOutput(text);

        Assert.Equal(2, sets.Count);
        Assert.Equal("sshd.service", sets[0].Id);
        Assert.Equal(1126, sets[0].MainPid);
        Assert.Equal(StorageSize.FromBytes(3_928_064), sets[0].MemoryCurrent);
        Assert.Equal("dbus-broker.service", sets[1].Id);
        Assert.True(sets[0].CanStart);
        Assert.NotNull(sets[0].InvocationId);
        Assert.IsType<TimeSpan>(sets[0].RestartInterval);
    }

    [Fact]
    public void Journalctl_json_parser_parses_common_fields()
    {
        const string line = """
            {"__REALTIME_TIMESTAMP":"1775200101110362","__MONOTONIC_TIMESTAMP":"43749232204","PRIORITY":"5","MESSAGE":"Test message","_SYSTEMD_UNIT":"sshd.service","_PID":"1126","SYSLOG_IDENTIFIER":"sshd","_HOSTNAME":"valinor","__CURSOR":"cursor-1","_SYSTEMD_INVOCATION_ID":"65800b24718b46f89c426b7b406396e1","_BOOT_ID":"ff8ce1ae528845948a38ed84c530ba3e","_CMDLINE":"\"/usr/bin/sshd -D\"","SYSLOG_FACILITY":"4"}
            """;

        var entry = JournalctlJsonParser.ParseLine(line);

        Assert.Equal("Test message", entry.Message);
        Assert.Equal("sshd.service", entry.Unit);
        Assert.Equal(1126, entry.ProcessId);
        Assert.Equal(5, entry.Priority);
        Assert.Equal("notice", entry.PriorityName);
        Assert.Equal("valinor", entry.Hostname);
        Assert.NotNull(entry.Timestamp);
        Assert.NotNull(entry.Cursor);
        Assert.NotNull(entry.InvocationId);
        Assert.NotNull(entry.BootId);
        Assert.Equal("\"/usr/bin/sshd -D\"", entry.CommandLine);
        Assert.Equal(4, entry.Facility);
    }

    [Fact]
    public void Loginctl_json_parser_parses_session_user_and_seat_rows()
    {
        const string sessionsJson = """
            [
              {
                "session": "2",
                "uid": 1000,
                "user": "komrad",
                "seat": "seat0",
                "leader": 1656,
                "class": "user",
                "tty": null,
                "idle": false,
                "since": null
              }
            ]
            """;
        const string usersJson = """
            [
              {
                "uid": 1000,
                "user": "komrad",
                "linger": false,
                "state": "active"
              }
            ]
            """;
        const string seatsJson = """
            [
              {
                "seat": "seat0"
              }
            ]
            """;

        var session = Assert.Single(LoginctlJsonParser.ParseSessionList(sessionsJson));
        var user = Assert.Single(LoginctlJsonParser.ParseUserList(usersJson));
        var seat = Assert.Single(LoginctlJsonParser.ParseSeatList(seatsJson));

        Assert.Equal("2", session.Session);
        Assert.Equal(1000, session.UserId);
        Assert.Equal("komrad", session.User);
        Assert.Equal("active", user.State);
        Assert.Equal("seat0", seat.Seat);
    }

    [Fact]
    public void Loginctl_show_parser_parses_multiple_property_sets_and_typed_values()
    {
        const string text = """
            UID=1000
            Name=komrad
            Timestamp=Thu 2026-04-02 14:59:30 EDT
            Sessions=3 2
            IdleHint=no
            Linger=no

            UID=0
            Name=root
            State=active
            Linger=yes
            """;

        var rows = LoginctlJsonParser.ParseShowOutput(text, "UID");

        Assert.Equal(2, rows.Count);
        Assert.Equal(1000L, Convert.ToInt64(rows[0].Id, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("komrad", rows[0].Name);
        Assert.Equal("active", rows[1].State);
        Assert.Equal(new[] { "3", "2" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(rows[0].Properties["Sessions"]));
        Assert.False(Assert.IsType<bool>(rows[0].Properties["Linger"]));
        Assert.NotNull(rows[0].Timestamp);
    }

    [Fact]
    public void Hostnamectl_json_parser_parses_typed_status_fields()
    {
        const string json = """
            {
              "Hostname": "valinor",
              "StaticHostname": "valinor",
              "PrettyHostname": null,
              "KernelName": "Linux",
              "KernelRelease": "6.19.10-zen1-1-zen",
              "OperatingSystemPrettyName": "Arch Linux",
              "OperatingSystemHomeURL": "https://archlinux.org/",
              "MachineID": "0da93c22a7274eb8b347b31e060be293",
              "BootID": "ff8ce1ae528845948a38ed84c530ba3e",
              "FirmwareDate": 1753056000000000
            }
            """;

        var host = HostnamectlJsonParser.ParseStatus(json);

        Assert.Equal("valinor", host.Hostname);
        Assert.Equal("Arch Linux", host.OperatingSystem);
        Assert.Equal(new Uri("https://archlinux.org/"), host.OperatingSystemHomeUrl);
        Assert.NotNull(host.MachineId);
        Assert.NotNull(host.BootId);
        Assert.NotNull(host.FirmwareDate);
    }

    [Fact]
    public void Networkctl_list_parser_parses_link_rows()
    {
        const string text = """
            IDX LINK         TYPE     OPERATIONAL SETUP
              1 lo           loopback carrier     unmanaged
              2 eth0         ether    routable    configured
              3 virbr0       ether    no-carrier  unmanaged

            3 links listed.
            """;

        var rows = NetworkctlListParser.Parse(text);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].Index);
        Assert.Equal("lo", rows[0].Link);
        Assert.True(rows[0].HasCarrier);
        Assert.True(rows[1].IsConfigured);
        Assert.True(rows[1].IsRoutable);
        Assert.False(rows[2].HasCarrier);
    }
}
