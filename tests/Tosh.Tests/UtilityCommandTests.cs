using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Stdlib.Net;

namespace Tosh.Tests;

public sealed class UtilityCommandTests
{
    [Fact]
    public async Task Seq_generates_numeric_sequences()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("seq 3");

        Assert.Equal([1L, 2L, 3L], results.Cast<long>().ToArray());
    }

    [Fact]
    public async Task Dirname_and_basename_split_paths()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var dirname = await engine.ExecuteToListAsync("dirname /usr/bin/bash");
        var basename = await engine.ExecuteToListAsync("basename /usr/bin/bash");

        Assert.Equal("/usr/bin", Assert.Single(dirname));
        Assert.Equal("bash", Assert.Single(basename));
    }

    [Fact]
    public async Task Head_tail_wc_uniq_cut_tr_and_grep_work_on_text_pipelines()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var head = await engine.ExecuteToListAsync("echo one two three | head -n 2");
        var tail = await engine.ExecuteToListAsync("echo one two three | tail -n 2");
        var wc = await engine.ExecuteToListAsync("echo one two three | wc");
        var uniq = await engine.ExecuteToListAsync("echo a a b | uniq");
        var uniqCount = await engine.ExecuteToListAsync("echo a a b | uniq -c");
        var cut = await engine.ExecuteToListAsync("echo \"alpha,beta,gamma\" | cut -d \",\" -f 2");
        var translated = await engine.ExecuteToListAsync("echo abc | tr a-z A-Z");
        var grep = await engine.ExecuteToListAsync("echo one two three | grep tw");

        Assert.Equal(["one", "two"], head.Cast<string>().ToArray());
        Assert.Equal(["two", "three"], tail.Cast<string>().ToArray());

        var stats = Assert.IsType<TextStatistics>(Assert.Single(wc));
        Assert.Equal(3, stats.Lines);
        Assert.Equal(3, stats.Words);

        Assert.Equal(["a", "b"], uniq.Cast<string>().ToArray());

        var countProjection = Assert.IsAssignableFrom<IDictionary<string, object?>>(uniqCount[0]);
        Assert.True(countProjection.TryGetValue("Count", out var countValue));
        Assert.Equal(2, Assert.IsType<int>(countValue));

        Assert.Equal("beta", Assert.IsType<ShellTextLine>(Assert.Single(cut)).Text);
        Assert.Equal("ABC", Assert.IsType<ShellTextLine>(Assert.Single(translated)).Text);

        var match = Assert.IsType<GrepMatchInfo>(Assert.Single(grep));
        Assert.Equal("two", match.Text);
        Assert.Equal(2, match.LineNumber);
    }

    [Fact]
    public async Task Grep_supports_shared_regex_flags_and_regex_objects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var ignoreCaseResults = await engine.ExecuteToListAsync("echo Alpha beta | grep -i \"^alpha$\"");
        var multilineResults = await engine.ExecuteToListAsync("echo \"first\nsecond\" | grep -m \"^second$\"");
        var regexObjectResults = await engine.ExecuteToListAsync("echo Alpha beta | grep (new regex(\"^alpha$\", System.Text.RegularExpressions.RegexOptions.IgnoreCase))");

        Assert.Equal(["Alpha"], ignoreCaseResults.Cast<GrepMatchInfo>().Select(item => item.Text).ToArray());
        Assert.Equal(["second"], multilineResults.Cast<GrepMatchInfo>().Select(item => item.Text).ToArray());
        Assert.Equal(["Alpha"], regexObjectResults.Cast<GrepMatchInfo>().Select(item => item.Text).ToArray());
    }

    [Fact]
    public async Task Cat_can_read_pipeline_text_pipeline_paths_and_numbered_lines()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(temporaryDirectory.Path, "alpha.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\n");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var textResults = await engine.ExecuteToListAsync("echo alpha beta | cat");
        var fileResults = await engine.ExecuteToListAsync("ls alpha.txt | cat");
        var numberedResults = await engine.ExecuteToListAsync("echo \"alpha\n\nbeta\" | cat -b -s");

        Assert.Equal(["alpha", "beta"], textResults.Cast<ShellTextLine>().Select(item => item.Text).ToArray());
        Assert.Equal(["alpha", "beta"], fileResults.Cast<ShellTextLine>().Select(item => item.Text).ToArray());

        var numbered = numberedResults.Cast<IDictionary<string, object?>>().ToArray();
        Assert.Equal(3, numbered.Length);
        Assert.Equal(1L, numbered[0]["Number"]);
        Assert.Null(numbered[1]["Number"]);
        Assert.Equal(2L, numbered[2]["Number"]);
    }

    [Fact]
    public async Task Wc_supports_selector_flags_and_total_rows()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var alphaPath = Path.Combine(temporaryDirectory.Path, "alpha.txt");
        var betaPath = Path.Combine(temporaryDirectory.Path, "beta.txt");
        await File.WriteAllTextAsync(alphaPath, "alpha\nbeta\n");
        await File.WriteAllTextAsync(betaPath, "gamma\n");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("wc -l -w -L alpha.txt beta.txt");

        Assert.Equal(3, results.Count);

        var alpha = Assert.IsType<TextStatistics>(results[0]);
        var beta = Assert.IsType<TextStatistics>(results[1]);
        var total = Assert.IsType<TextStatistics>(results[2]);

        Assert.Equal(2, alpha.Lines);
        Assert.Equal(2, alpha.Words);
        Assert.Equal(5, alpha.LongestLine);
        Assert.Equal(1, beta.Lines);
        Assert.True(total.IsTotal);
        Assert.Equal(3, total.Lines);
        Assert.Equal(3, total.Words);
        Assert.Equal(5, total.LongestLine);
    }

    [Fact]
    public async Task Xargs_invokes_nested_commands()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo alpha beta | xargs echo prefix");

        Assert.Equal(["prefix", "alpha", "beta"], results.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Prompt_segment_commands_return_styled_segments()
    {
        // prompt-* commands are [ShellOnly]; mark interactive to bypass the guard.
        var engine = new ToshEngine(ToshRuntime.CreateDefault()) { IsInteractiveSession = true };

        var timeResults = await engine.ExecuteToListAsync("prompt-time --format HH --dim");
        var userHostResults = await engine.ExecuteToListAsync("prompt-userhost");
        var historyResults = await engine.ExecuteToListAsync("prompt-history 432 --bold");
        var jobsResults = await engine.ExecuteToListAsync("prompt-jobs 3 --bold");
        var durationResults = await engine.ExecuteToListAsync("prompt-duration 2.5s --threshold-ms 250");
        var zeroExitResults = await engine.ExecuteToListAsync("prompt-exit 0");
        var failureExitResults = await engine.ExecuteToListAsync("prompt-exit 7 --bold");

        var timeSegment = Assert.IsType<StyledText>(Assert.Single(timeResults));
        Assert.Matches(@"^\d{2}$", timeSegment.Text);
        Assert.True(timeSegment.Dim);

        var userHostSegment = Assert.IsType<StyledText>(Assert.Single(userHostResults));
        Assert.Contains("@", userHostSegment.Text, StringComparison.Ordinal);

        var historySegment = Assert.IsType<StyledText>(Assert.Single(historyResults));
        Assert.Equal("!432", historySegment.Text);
        Assert.True(historySegment.Bold);

        var jobsSegment = Assert.IsType<StyledText>(Assert.Single(jobsResults));
        Assert.Equal("jobs:3", jobsSegment.Text);
        Assert.True(jobsSegment.Bold);

        var durationSegment = Assert.IsType<StyledText>(Assert.Single(durationResults));
        Assert.Equal("2.5s", durationSegment.Text);

        Assert.Empty(zeroExitResults);

        var exitSegment = Assert.IsType<StyledText>(Assert.Single(failureExitResults));
        Assert.Equal(TerminalEnvironmentTestSupport.ExitCodeText(7), exitSegment.Text);
        Assert.True(exitSegment.Bold);
    }

    [Fact]
    public async Task Prompt_dispatcher_matches_legacy_per_segment_commands()
    {
        // The consolidated `prompt <segment>` form must produce identical
        // output to the legacy `prompt-<segment>` form for every segment.
        var engine = new ToshEngine(ToshRuntime.CreateDefault()) { IsInteractiveSession = true };

        var pairs = new (string Legacy, string Dispatched)[]
        {
            ("prompt-userhost",                            "prompt userhost"),
            ("prompt-history 432 --bold",                  "prompt history 432 --bold"),
            ("prompt-jobs 3 --bold",                       "prompt jobs 3 --bold"),
            ("prompt-duration 2.5s --threshold-ms 250",    "prompt duration 2.5s --threshold-ms 250"),
            ("prompt-exit 7 --bold",                       "prompt exit 7 --bold"),
            ("prompt-text \" » \" --fg gray",              "prompt text \" » \" --fg gray"),
            ("prompt-newline",                             "prompt newline"),
            ("prompt-dir --depth 1",                       "prompt dir --depth 1"),
        };

        foreach (var (legacy, dispatched) in pairs)
        {
            var legacyResults = await engine.ExecuteToListAsync(legacy);
            var dispatchedResults = await engine.ExecuteToListAsync(dispatched);

            Assert.Equal(legacyResults.Count, dispatchedResults.Count);
            for (var i = 0; i < legacyResults.Count; i++)
            {
                // StyledText is a value type-ish record — equality compares
                // text + styling, so a direct Equal call covers both axes.
                Assert.Equal(legacyResults[i], dispatchedResults[i]);
            }
        }
    }

    [Fact]
    public async Task Prompt_dispatcher_reports_missing_subcommand()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault()) { IsInteractiveSession = true };

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("prompt"));

        Assert.Contains(ex.Diagnostics, d => d.Code == "tosh.command.missing_subcommand");
    }

    [Fact]
    public async Task Prompt_dispatcher_reports_unknown_subcommand()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault()) { IsInteractiveSession = true };

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("prompt frobnicate"));

        Assert.Contains(ex.Diagnostics, d => d.Code == "tosh.command.unknown_subcommand");
    }

    [Fact]
    public async Task Guid_command_supports_creation_parsing_formatting_and_info()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            guid | type-of
            guid new v7 | type-of
            echo "550e8400-e29b-41d4-a716-446655440000" | guid parse | type-of
            guid format n "550e8400-e29b-41d4-a716-446655440000"
            guid info "550e8400-e29b-41d4-a716-446655440000" | get Version
            guid info "550e8400-e29b-41d4-a716-446655440000" | get Variant
            guid empty | type-of
            """);

        Assert.Equal(typeof(Guid), results[0]);
        Assert.Equal(typeof(Guid), results[1]);
        Assert.Equal(typeof(Guid), results[2]);
        Assert.Equal("550e8400e29b41d4a716446655440000", results[3]);
        Assert.Equal(4, results[4]);
        Assert.Equal("RFC 4122", results[5]);
        Assert.Equal(typeof(Guid), results[6]);
    }

    [Fact]
    public async Task Free_and_uptime_return_system_info_objects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var free = await engine.ExecuteToListAsync("free");
        var uptime = await engine.ExecuteToListAsync("uptime");

        Assert.NotEmpty(free);
        Assert.All(free, item => Assert.IsType<MemoryUsageInfo>(item));
        Assert.IsType<SystemUptimeInfo>(Assert.Single(uptime));
    }

    [Fact]
    public async Task Env_supports_temporary_assignments_and_unsets_for_nested_commands()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var variableName = $"TOSH_TEST_TEMP_ENV_{Guid.NewGuid():N}";

        Environment.SetEnvironmentVariable(variableName, "original");

        try
        {
            var assigned = await engine.ExecuteToListAsync($"env {variableName}=toast env {variableName} | get Value");
            var unset = await engine.ExecuteToListAsync($"env -u {variableName} env {variableName} | get IsSet");
            var unsetThenAssign = await engine.ExecuteToListAsync($"env -u {variableName} {variableName}=toast env {variableName} | get Value");
            var commandOnly = await engine.ExecuteToListAsync($"env -- env {variableName} | get Name");

            Assert.Collection(assigned, item => Assert.Equal("toast", item));
            Assert.Collection(unset, item => Assert.Equal(false, item));
            Assert.Collection(unsetThenAssign, item => Assert.Equal("toast", item));
            Assert.Collection(commandOnly, item => Assert.Equal(variableName, item));
            Assert.Equal("original", Environment.GetEnvironmentVariable(variableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task Ip_addr_returns_typed_interface_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var ipLookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "ip");

        if (ipLookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("ip addr");

        Assert.NotEmpty(results);

        var firstInterface = Assert.IsType<IpInterfaceInfo>(results[0]);
        Assert.False(string.IsNullOrWhiteSpace(firstInterface.Name));
        Assert.All(firstInterface.Addresses, address => Assert.IsType<IPAddress>(address.Address));
    }

    [Fact]
    public async Task Ip_link_and_route_return_typed_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var ipLookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "ip");

        if (ipLookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var linkResults = await engine.ExecuteToListAsync("ip link");
        var routeResults = await engine.ExecuteToListAsync("ip route");

        Assert.NotEmpty(linkResults);
        Assert.All(linkResults, item => Assert.IsType<IpInterfaceInfo>(item));

        if (routeResults.Count == 0)
        {
            return;
        }

        var firstRoute = Assert.IsType<IpRouteInfo>(routeResults[0]);
        Assert.False(string.IsNullOrWhiteSpace(firstRoute.Destination));
    }

    [Fact]
    public async Task Systemctl_returns_structured_unit_rows_and_show_sets()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("systemctl", "list-units", "--output=json", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var units = await engine.ExecuteToListAsync("systemctl --type service | first 5");

        Assert.NotEmpty(units);
        Assert.All(units, item => Assert.IsType<SystemdUnitInfo>(item));

        var firstUnit = Assert.IsType<SystemdUnitInfo>(units[0]);
        var showResults = await engine.ExecuteToListAsync($"systemctl show {Quote(firstUnit.Unit)}");
        var show = Assert.IsType<SystemdUnitPropertySet>(Assert.Single(showResults));

        Assert.Equal(firstUnit.Unit, show.Id);
        Assert.False(string.IsNullOrWhiteSpace(show.ActiveState));
    }

    [Fact]
    public async Task Systemctl_list_unit_files_returns_structured_rows()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("systemctl", "list-unit-files", "--output=json", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var rows = await engine.ExecuteToListAsync("systemctl list-unit-files --type service | first 5");

        Assert.NotEmpty(rows);
        Assert.All(rows, item => Assert.IsType<SystemdUnitFileInfo>(item));
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<SystemdUnitFileInfo>(rows[0]).UnitFile));
    }

    [Fact]
    public async Task Systemctl_status_returns_structured_unit_property_sets()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("systemctl", "list-units", "--output=json", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var units = await engine.ExecuteToListAsync("systemctl --type service | first 5");

        if (units.Count == 0)
        {
            return;
        }

        var firstUnit = Assert.IsType<SystemdUnitInfo>(units[0]);
        var statusResults = await engine.ExecuteToListAsync($"systemctl status {Quote(firstUnit.Unit)}");
        var status = Assert.IsType<SystemdUnitPropertySet>(Assert.Single(statusResults));

        Assert.Equal(firstUnit.Unit, status.Id);
        Assert.False(string.IsNullOrWhiteSpace(status.ActiveState));
    }

    [Fact]
    public async Task Journalctl_returns_structured_entries()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("journalctl", "-n", "1", "-o", "json", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("journalctl -n 2");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<SystemdJournalEntry>(item));
        Assert.NotNull(Assert.IsType<SystemdJournalEntry>(results[0]).Message);
    }

    [Fact]
    public async Task Loginctl_returns_structured_rows_and_show_sets()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("loginctl", "list-users", "--json=short", "--no-legend", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var users = await engine.ExecuteToListAsync("loginctl list-users");

        if (users.Count == 0)
        {
            return;
        }

        Assert.All(users, item => Assert.IsType<SystemdLoginUserInfo>(item));

        var firstUser = Assert.IsType<SystemdLoginUserInfo>(users[0]);
        var showResults = await engine.ExecuteToListAsync($"loginctl show-user {firstUser.UserId}");
        var show = Assert.IsType<SystemdPropertySet>(Assert.Single(showResults));

        Assert.Equal(firstUser.UserId, Convert.ToInt32(show.Id, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(string.IsNullOrWhiteSpace(show.Name));
    }

    [Fact]
    public async Task Hostnamectl_returns_structured_host_status()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("hostnamectl", "status", "--json=short"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var result = Assert.IsType<SystemdHostInfo>(Assert.Single(await engine.ExecuteToListAsync("hostnamectl")));

        Assert.False(string.IsNullOrWhiteSpace(result.DisplayHostname));
        Assert.False(string.IsNullOrWhiteSpace(result.KernelRelease));
    }

    [Fact]
    public async Task Networkctl_returns_structured_link_rows()
    {
        if (!OperatingSystem.IsLinux() ||
            !CanExecuteExternalCommand("networkctl", "list", "--no-legend", "--no-pager"))
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("networkctl");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<SystemdNetworkLinkInfo>(item));

        var first = Assert.IsType<SystemdNetworkLinkInfo>(results[0]);
        Assert.False(string.IsNullOrWhiteSpace(first.Link));
        Assert.False(string.IsNullOrWhiteSpace(first.SetupState));
    }

    [Fact]
    public async Task Http_get_can_decode_json_responses()
    {
        using var server = await TestHttpServer.StartAsync(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";

            var payload = Encoding.UTF8.GetBytes("""{"Name":"Toast","Count":2}""");
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"http get {server.Url} --as json");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(results));
        Assert.Equal("Toast", record["Name"]);
        Assert.Equal(2L, record["Count"]);
    }

    [Fact]
    public async Task Http_post_can_send_json_and_return_structured_response()
    {
        string? requestBody = null;
        string? requestHeader = null;

        using var server = await TestHttpServer.StartAsync(async context =>
        {
            requestHeader = context.Request.Headers["X-Test"];

            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            requestBody = await reader.ReadToEndAsync();

            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["X-Reply"] = "beta";

            var payload = Encoding.UTF8.GetBytes("""{"ok":true}""");
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"http post {server.Url} --json ({{| Name = \"Toast\" |}}) --header X-Test alpha --as response");

        var response = Assert.IsType<HttpResponseInfo>(Assert.Single(results));
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("POST", response.Method);
        Assert.True(response.IsSuccess);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal("alpha", requestHeader);
        Assert.Equal("""{"Name":"Toast"}""", requestBody);

        var body = Assert.IsAssignableFrom<IDictionary<string, object?>>(response.Body);
        Assert.Equal(true, body["ok"]);
        Assert.Equal("beta", Assert.Single(response.Headers["X-Reply"]));
    }

    [Fact]
    public async Task Http_request_and_send_support_request_objects_and_out_files()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputPath = Path.Combine(temporaryDirectory.Path, "response.txt");

        using var server = await TestHttpServer.StartAsync(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain; charset=utf-8";

            var payload = Encoding.UTF8.GetBytes("hello from http");
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var requestResults = await engine.ExecuteToListAsync($"http request GET {server.Url} --header Accept text/plain");
        var request = Assert.IsType<HttpRequestDefinition>(Assert.Single(requestResults));
        Assert.Equal("GET", request.Method);
        Assert.Equal(server.Url, request.RequestUri);
        Assert.Equal("text/plain", Assert.Single(request.Headers["Accept"]));

        var sendResults = await engine.ExecuteToListAsync($"""http request GET {server.Url} --header Accept text/plain | http send --as text --out {Quote(outputPath)}""");
        var responseText = Assert.IsType<ShellTextLine>(Assert.Single(sendResults));

        Assert.Equal("hello from http", responseText.Text);
        Assert.Equal("hello from http", await File.ReadAllTextAsync(outputPath));
    }

    [Fact]
    public async Task Http_serve_can_host_static_files_and_be_stopped()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(temporaryDirectory.Path, "hello.txt");
        await File.WriteAllTextAsync(filePath, "hello from server");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"""http serve {Quote(temporaryDirectory.Path)} --browse""");
        var handle = Assert.IsType<HttpFileServerHandle>(Assert.Single(results));

        try
        {
            using var client = new HttpClient();
            var fileText = await client.GetStringAsync(new Uri(handle.Url, "hello.txt"));
            var directoryText = await client.GetStringAsync(handle.Url);

            Assert.Equal("hello from server", fileText);
            Assert.Contains("hello.txt", directoryText, StringComparison.Ordinal);
            Assert.True(handle.IsOpen);
            Assert.True(handle.RequestCount >= 2);

            var serverResults = await engine.ExecuteToListAsync("http servers");
            Assert.Contains(serverResults, item => item is HttpFileServerHandle server && server.Id == handle.Id);

            var stopResults = await engine.ExecuteToListAsync($"http stop {handle.Id}");
            var stopped = Assert.IsType<HttpFileServerHandle>(Assert.Single(stopResults));
            Assert.Equal(handle.Id, stopped.Id);
            Assert.False(stopped.IsOpen);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Http_serve_can_accept_uploads_and_auto_stop_after_one_request()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"""http serve {Quote(temporaryDirectory.Path)} --upload --once""");
        var handle = Assert.IsType<HttpFileServerHandle>(Assert.Single(results));

        try
        {
            using var client = new HttpClient();
            using var content = new StringContent("uploaded from test", Encoding.UTF8, "text/plain");
            using var response = await client.PutAsync(new Uri(handle.Url, "nested/upload.txt"), content);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("uploaded from test", await File.ReadAllTextAsync(Path.Combine(temporaryDirectory.Path, "nested", "upload.txt")));

            for (var attempt = 0; attempt < 20 && handle.IsOpen; attempt++)
            {
                await Task.Delay(25);
            }

            Assert.False(handle.IsOpen);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Http_serve_can_require_tokens_and_expose_share_urls()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var filePath = Path.Combine(temporaryDirectory.Path, "hello.txt");
        await File.WriteAllTextAsync(filePath, "protected hello");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"""http serve {Quote(temporaryDirectory.Path)} --browse --token secret-token""");
        var handle = Assert.IsType<HttpFileServerHandle>(Assert.Single(results));

        try
        {
            Assert.True(handle.RequiresToken);
            Assert.Equal("secret-token", handle.AccessToken);
            Assert.Contains("token=secret-token", handle.ShareUrl.ToString(), StringComparison.Ordinal);

            using var unauthorizedClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using var unauthorizedResponse = await unauthorizedClient.GetAsync(handle.Url);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
            var unauthorizedText = await unauthorizedResponse.Content.ReadAsStringAsync();
            Assert.Contains("Authorization required", unauthorizedText, StringComparison.Ordinal);

            using var authorizedClient = new HttpClient();
            var directoryHtml = await authorizedClient.GetStringAsync(handle.ShareUrl);
            authorizedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "SECRETTOKEN");
            var fileText = await authorizedClient.GetStringAsync(new Uri(handle.Url, "hello.txt"));

            Assert.Contains("secret-token", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("Hyphens and casing are ignored", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("Share URL", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("Capabilities", directoryHtml, StringComparison.Ordinal);
            Assert.Equal("protected hello", fileText);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Http_serve_can_render_browser_upload_page_and_accept_multipart_uploads()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "existing.txt"), "already here");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"""http serve {Quote(temporaryDirectory.Path)} --upload --generate-token""");
        var handle = Assert.IsType<HttpFileServerHandle>(Assert.Single(results));

        try
        {
            Assert.True(handle.UploadEnabled);
            Assert.True(handle.RequiresToken);
            Assert.NotNull(handle.AccessToken);
            Assert.Matches("^[a-z]+-[a-z]+$", handle.AccessToken!);

            using var client = new HttpClient();
            var directoryHtml = await client.GetStringAsync(handle.ShareUrl);

            Assert.Contains("Upload files", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("multipart/form-data", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("curl -T ./file.txt", directoryHtml, StringComparison.Ordinal);
            Assert.Contains("existing.txt", directoryHtml, StringComparison.Ordinal);

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("browser upload one")), "file", "browser-one.txt");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("browser upload two")), "file", "browser-two.txt");

            using var response = await client.PostAsync(handle.ShareUrl, form);
            Assert.True(response.IsSuccessStatusCode);

            var savedNames = Directory.GetFiles(temporaryDirectory.Path)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Contains("browser-one.txt", savedNames);
            Assert.Contains("browser-two.txt", savedNames);
            Assert.Equal("browser upload one", await File.ReadAllTextAsync(Path.Combine(temporaryDirectory.Path, "browser-one.txt")));
            Assert.Equal("browser upload two", await File.ReadAllTextAsync(Path.Combine(temporaryDirectory.Path, "browser-two.txt")));

            var refreshedHtml = await client.GetStringAsync(handle.ShareUrl);
            Assert.Contains("browser-one.txt", refreshedHtml, StringComparison.Ordinal);
            Assert.Contains("browser-two.txt", refreshedHtml, StringComparison.Ordinal);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Http_serve_lan_binding_keeps_local_url_and_exposes_share_urls()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "hello.txt"), "lan hello");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync($"""http serve {Quote(temporaryDirectory.Path)} --lan --browse""");
        var handle = Assert.IsType<HttpFileServerHandle>(Assert.Single(results));

        try
        {
            Assert.Equal("0.0.0.0", handle.BindAddress);
            Assert.Equal("localhost", handle.Url.Host);
            Assert.NotEmpty(handle.ShareUrls);
            Assert.All(handle.ShareUrls, uri =>
            {
                Assert.Equal("http", uri.Scheme);
                Assert.Equal(handle.Port, uri.Port);
            });

            using var client = new HttpClient();
            var fileText = await client.GetStringAsync(new Uri(handle.Url, "hello.txt"));
            Assert.Equal("lan hello", fileText);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Lsblk_returns_typed_block_device_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lsblkLookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "lsblk");

        if (lsblkLookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("lsblk");

        Assert.NotEmpty(results);

        var firstDevice = Assert.IsType<BlockDeviceInfo>(results[0]);
        Assert.False(string.IsNullOrWhiteSpace(firstDevice.Name));
    }

    [Fact]
    public async Task Lsblk_o_changes_rendering_without_changing_output_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lsblkLookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "lsblk");

        if (lsblkLookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("lsblk -o NAME,PATH");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<BlockDeviceInfo>(item));

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Path", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("FsType", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findmnt_returns_typed_mount_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "findmnt");

        if (lookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("findmnt");

        Assert.NotEmpty(results);

        var firstMount = Assert.IsType<MountInfo>(results[0]);
        Assert.False(string.IsNullOrWhiteSpace(firstMount.Target));
    }

    [Fact]
    public async Task Findmnt_o_changes_rendering_without_changing_output_objects()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "findmnt");

        if (lookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("findmnt -o TARGET,SOURCE,FSTYPE");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<MountInfo>(item));

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Target", rendered, StringComparison.Ordinal);
        Assert.Contains("Source", rendered, StringComparison.Ordinal);
        Assert.Contains("FsType", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Options", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lscpu_returns_structured_cpu_summary_or_rows()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "lscpu");

        if (lookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var summaryResults = await engine.ExecuteToListAsync("lscpu");
        var extendedResults = await engine.ExecuteToListAsync("lscpu -e | first 2");
        var cacheResults = await engine.ExecuteToListAsync("lscpu -C | first 2");

        Assert.IsType<CpuInfo>(Assert.Single(summaryResults));
        Assert.NotEmpty(extendedResults);
        Assert.All(extendedResults, item => Assert.IsType<CpuTopologyInfo>(item));
        Assert.NotEmpty(cacheResults);
        Assert.All(cacheResults, item => Assert.IsType<CpuCacheInfo>(item));
    }

    [Fact]
    public async Task Lsfd_returns_structured_descriptor_and_summary_rows()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "lsfd");

        if (lookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var rows = await engine.ExecuteToListAsync("lsfd | first 3");
        var summary = await engine.ExecuteToListAsync("lsfd --summary=only | first 3");

        Assert.NotEmpty(rows);
        Assert.All(rows, item => Assert.IsType<FileDescriptorInfo>(item));
        Assert.NotEmpty(summary);
        Assert.All(summary, item => Assert.IsType<SystemCounterInfo>(item));
    }

    [Fact]
    public async Task Lsipc_returns_structured_ipc_rows()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, "lsipc");

        if (lookup.Status != ExternalCommandLookupStatus.Found)
        {
            return;
        }

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var summaryRows = await engine.ExecuteToListAsync("lsipc | first 3");

        Assert.NotEmpty(summaryRows);
        Assert.All(summaryRows, item => Assert.IsAssignableFrom<IDictionary<string, object?>>(item));

        var sharedMemoryRows = await engine.ExecuteToListAsync("lsipc -m | first 2");
        Assert.NotEmpty(sharedMemoryRows);
        Assert.All(sharedMemoryRows, item => Assert.IsAssignableFrom<IDictionary<string, object?>>(item));
    }

    [Fact]
    public async Task Du_find_and_stat_operate_on_file_system_entries()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "b.txt"), "world");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var du = await engine.ExecuteToListAsync($"du -s {Quote(temporaryDirectory.Path)}");
        var find = await engine.ExecuteToListAsync($"find {Quote(temporaryDirectory.Path)} -maxdepth 1 -type d | get Name");
        var stat = await engine.ExecuteToListAsync($"stat {Quote(Path.Combine(temporaryDirectory.Path, "b.txt"))}");

        var usage = Assert.IsType<PathUsageInfo>(Assert.Single(du));
        Assert.True(usage.Size.Bytes >= 10);
        Assert.Contains(Path.GetFileName(temporaryDirectory.Path), find.Cast<string>());
        Assert.IsType<FileSystemEntry>(Assert.Single(stat));
    }

    [Fact]
    public async Task Touch_supports_no_create_reference_and_grouped_short_flags()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var referencePath = Path.Combine(temporaryDirectory.Path, "reference.txt");
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        var missingPath = Path.Combine(temporaryDirectory.Path, "missing.txt");
        await File.WriteAllTextAsync(referencePath, "reference");
        await File.WriteAllTextAsync(targetPath, "target");

        var referenceInstant = new DateTimeOffset(2026, 03, 28, 12, 00, 00, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(referencePath, referenceInstant.UtcDateTime);

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("touch -c missing.txt");
        await engine.ExecuteToListAsync("touch -r reference.txt target.txt");
        Assert.Equal(File.GetLastWriteTimeUtc(referencePath), File.GetLastWriteTimeUtc(targetPath));
        await engine.ExecuteToListAsync("touch -amd 2026-03-29T01:02:03Z target.txt");

        Assert.False(File.Exists(missingPath));
        var expectedTouchedTime = new DateTime(2026, 03, 29, 01, 02, 03, DateTimeKind.Utc);
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(targetPath) - expectedTouchedTime).TotalSeconds) < 1.0d);
        Assert.True(Math.Abs((File.GetLastAccessTimeUtc(targetPath) - expectedTouchedTime).TotalSeconds) < 1.0d);
    }

    [Fact]
    public async Task Mv_supports_default_overwrite_no_clobber_update_and_target_directory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archivePath = Path.Combine(temporaryDirectory.Path, "archive");
        Directory.CreateDirectory(archivePath);

        var overwriteSource = Path.Combine(temporaryDirectory.Path, "overwrite-source.txt");
        var overwriteTarget = Path.Combine(temporaryDirectory.Path, "overwrite-target.txt");
        await File.WriteAllTextAsync(overwriteSource, "new");
        await File.WriteAllTextAsync(overwriteTarget, "old");

        var skipSource = Path.Combine(temporaryDirectory.Path, "skip-source.txt");
        var skipTarget = Path.Combine(temporaryDirectory.Path, "skip-target.txt");
        await File.WriteAllTextAsync(skipSource, "source");
        await File.WriteAllTextAsync(skipTarget, "target");

        var olderSource = Path.Combine(temporaryDirectory.Path, "older-source.txt");
        var newerTarget = Path.Combine(temporaryDirectory.Path, "newer-target.txt");
        await File.WriteAllTextAsync(olderSource, "older");
        await File.WriteAllTextAsync(newerTarget, "newer");
        File.SetLastWriteTimeUtc(olderSource, new DateTime(2026, 03, 28, 00, 00, 00, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerTarget, new DateTime(2026, 03, 29, 00, 00, 00, DateTimeKind.Utc));

        var alphaPath = Path.Combine(temporaryDirectory.Path, "alpha.txt");
        var betaPath = Path.Combine(temporaryDirectory.Path, "beta.txt");
        await File.WriteAllTextAsync(alphaPath, "alpha");
        await File.WriteAllTextAsync(betaPath, "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        await engine.ExecuteToListAsync("mv overwrite-source.txt overwrite-target.txt");
        await engine.ExecuteToListAsync("mv -n skip-source.txt skip-target.txt");
        await engine.ExecuteToListAsync("mv -u older-source.txt newer-target.txt");
        await engine.ExecuteToListAsync("mv -t archive alpha.txt beta.txt");

        Assert.False(File.Exists(overwriteSource));
        Assert.Equal("new", await File.ReadAllTextAsync(overwriteTarget));
        Assert.True(File.Exists(skipSource));
        Assert.Equal("target", await File.ReadAllTextAsync(skipTarget));
        Assert.True(File.Exists(olderSource));
        Assert.Equal("newer", await File.ReadAllTextAsync(newerTarget));
        Assert.True(File.Exists(Path.Combine(archivePath, "alpha.txt")));
        Assert.True(File.Exists(Path.Combine(archivePath, "beta.txt")));
    }

    [Fact]
    public async Task Find_supports_regex_path_filters()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "alpha.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "README.MD"), "readme");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var regexResults = await engine.ExecuteToListAsync($"find {Quote(temporaryDirectory.Path)} -regex \".*\\\\.txt$\" | get Name");
        var ignoreCaseResults = await engine.ExecuteToListAsync($"find {Quote(temporaryDirectory.Path)} -iregex \".*readme\\\\.md$\" | get Name");

        Assert.Equal(["alpha.txt"], regexResults.Cast<string>().OrderBy(static item => item, StringComparer.Ordinal).ToArray());
        Assert.Equal(["README.MD"], ignoreCaseResults.Cast<string>().ToArray());
    }

    [Fact]
    public async Task Df_du_and_find_accept_pipeline_path_input()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "a.txt"), "hello");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var df = await engine.ExecuteToListAsync($"echo {Quote(temporaryDirectory.Path)} | df");
        var du = await engine.ExecuteToListAsync($"echo {Quote(temporaryDirectory.Path)} | du -s");
        var find = await engine.ExecuteToListAsync($"echo {Quote(temporaryDirectory.Path)} | find -maxdepth 0 -type d | get FullName");

        Assert.IsType<FileSystemUsageInfo>(Assert.Single(df));
        Assert.IsType<PathUsageInfo>(Assert.Single(du));
        Assert.Collection(find, item => Assert.Equal(temporaryDirectory.Path, item));
    }

    [Fact]
    public async Task Ls_show_columns_changes_rendering_without_changing_output_objects()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "beta.txt"), "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls --show Name,FullName");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<FileSystemEntry>(item));

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("FullName", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Modified", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Type", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ps_o_changes_rendering_without_changing_output_objects()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ps -o Name,Id");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<ProcessInfo>(item));

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Name", rendered, StringComparison.Ordinal);
        Assert.Contains("Id", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Cpu", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Df_output_changes_rendering_without_changing_output_objects()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("df --output FileSystem,UsePercent /");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.IsType<FileSystemUsageInfo>(item));

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("FileSystem", rendered, StringComparison.Ordinal);
        Assert.Contains("Use%", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("MountedOn", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Available", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Df_total_appends_total_row()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync($"df --total {Quote(Directory.GetCurrentDirectory())}");

        var total = Assert.IsType<FileSystemUsageInfo>(results[^1]);
        Assert.True(total.IsTotal);
        Assert.Equal("total", total.FileSystem);
    }

    [Fact]
    public async Task Df_type_filters_and_local_flag_work()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var current = Assert.IsType<FileSystemUsageInfo>(Assert.Single(await engine.ExecuteToListAsync($"df {Quote(Directory.GetCurrentDirectory())}")));
        var localOnly = await engine.ExecuteToListAsync("df -l");

        Assert.All(localOnly, item => Assert.True(Assert.IsType<FileSystemUsageInfo>(item).IsLocal));

        if (string.IsNullOrWhiteSpace(current.Type))
        {
            return;
        }

        var include = await engine.ExecuteToListAsync($"df -t {current.Type}");
        var exclude = await engine.ExecuteToListAsync($"df -x {current.Type}");

        Assert.NotEmpty(include);
        Assert.All(include, item => Assert.Equal(current.Type, Assert.IsType<FileSystemUsageInfo>(item).Type));
        Assert.All(exclude, item => Assert.NotEqual(current.Type, Assert.IsType<FileSystemUsageInfo>(item).Type));
    }

    [Fact]
    public void Df_default_visible_entries_skip_duplicate_subroot_mounts_but_keep_real_tmpfs_rows()
    {
        var entries = new[]
        {
            new FileSystemUsageInfo("/dev/sdb2", "/data", "fuseblk", StorageSize.FromBytes(2_000_000_000_000), StorageSize.FromBytes(100), StorageSize.FromBytes(1_999_999_999_900), 0, null, true, "/"),
            new FileSystemUsageInfo("/dev/sdb2", "/opt", "fuseblk", StorageSize.FromBytes(2_000_000_000_000), StorageSize.FromBytes(100), StorageSize.FromBytes(1_999_999_999_900), 0, null, true, "/opt"),
            new FileSystemUsageInfo("none", "/run/credentials/a", "tmpfs", StorageSize.FromBytes(1_048_576), StorageSize.FromBytes(0), StorageSize.FromBytes(1_048_576), 0, null, true, "/"),
            new FileSystemUsageInfo("none", "/run/credentials/b", "tmpfs", StorageSize.FromBytes(1_048_576), StorageSize.FromBytes(0), StorageSize.FromBytes(1_048_576), 0, null, true, "/"),
            new FileSystemUsageInfo("proc", "/proc", "proc", StorageSize.FromBytes(0), StorageSize.FromBytes(0), StorageSize.FromBytes(0), 0, null, true, "/"),
        };

        var visible = FileSystemUsageUtilities.GetDefaultVisibleEntries(entries);
        var mounts = visible.Select(item => item.MountedOn).ToArray();

        Assert.Contains("/data", mounts);
        Assert.DoesNotContain("/opt", mounts);
        Assert.Contains("/run/credentials/a", mounts);
        Assert.Contains("/run/credentials/b", mounts);
        Assert.DoesNotContain("/proc", mounts);
    }

    [Fact]
    public async Task Ls_d_lists_directory_itself()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync($"ls -d {Quote(temporaryDirectory.Path)}");

        var entry = Assert.IsType<FileSystemEntry>(Assert.Single(results));
        Assert.True(entry.IsDirectory);
        Assert.Equal(temporaryDirectory.Path, entry.FullName);
    }

    [Fact]
    public async Task Ls_R_includes_descendants()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedDirectory = Path.Combine(temporaryDirectory.Path, "alpha", "beta");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "child.txt"), "child");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -R");
        var names = results.Cast<FileSystemEntry>().Select(item => item.Name).ToArray();

        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
        Assert.Contains("child.txt", names);
    }

    [Fact]
    public async Task Ls_S_sorts_files_by_size_descending()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "small.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "large.txt"), "abcdef");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "medium.txt"), "abc");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -S");
        var names = results.Cast<FileSystemEntry>().Select(item => item.Name).ToArray();

        Assert.Equal(["large.txt", "medium.txt", "small.txt"], names);
    }

    [Fact]
    public async Task Ls_r_reverses_name_sort()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "beta.txt"), "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -r");
        var names = results.Cast<FileSystemEntry>().Select(item => item.Name).ToArray();

        Assert.Equal(["beta.txt", "alpha.txt"], names);
    }

    [Fact]
    public async Task Ls_group_directories_first_groups_directories_ahead_of_files()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "a-file.txt"), "file");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "z-dir"));

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls --group-directories-first");
        var names = results.Cast<FileSystemEntry>().Select(item => item.Name).ToArray();

        Assert.Equal(["z-dir", "a-file.txt"], names);
    }

    [Fact]
    public async Task Ls_F_classifies_executable_files()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var scriptPath = Path.Combine(temporaryDirectory.Path, "tool.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho hi\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);
        }

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -F");

        var entry = Assert.IsType<FileSystemEntry>(Assert.Single(results));
        Assert.Equal("tool.sh*", entry.DisplayName);
    }

    [Fact]
    public async Task Ls_i_renders_inode_column()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -i");

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Inode", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ls_long_time_access_uses_accessed_column()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ls -l --time access");

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Accessed", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Modified", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ps_p_filters_by_process_id()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var currentId = Process.GetCurrentProcess().Id;

        var results = await engine.ExecuteToListAsync($"ps -p {currentId}");

        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.Equal(currentId, Assert.IsType<ProcessInfo>(item).Id));
    }

    [Fact]
    public async Task Ps_u_filters_by_current_user_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var currentUser = UnixSystemServices.GetCurrentIdentity().User.DisplayName;

        var results = await engine.ExecuteToListAsync($"ps -u {currentUser}");

        Assert.NotEmpty(results);
        Assert.Contains(
            results.Cast<ProcessInfo>(),
            item => string.Equals(item.User?.DisplayName, currentUser, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ps_u_filters_by_current_user_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var currentUser = UnixSystemServices.GetCurrentIdentity().User.DisplayName;

        var results = await engine.ExecuteToListAsync($"ps -u {currentUser}");

        Assert.NotEmpty(results);
        Assert.Contains(
            results.Cast<ProcessInfo>(),
            item => string.Equals(item.User?.DisplayName, currentUser, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ps_sort_descending_id_orders_highest_ids_first()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("ps --sort -Id");
        var ids = results.Cast<ProcessInfo>().Select(item => item.Id).Take(5).ToArray();

        Assert.True(ids.Length > 0);
        Assert.Equal(ids.OrderByDescending(static item => item).ToArray(), ids);
    }

    [Fact]
    public async Task Du_total_and_time_extend_typed_usage_output()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "beta.txt"), "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("du -a -c --time");

        Assert.NotEmpty(results);
        Assert.Contains(results.Cast<PathUsageInfo>(), item => item.Modified is not null && !item.IsTotal);

        var total = Assert.IsType<PathUsageInfo>(results[^1]);
        Assert.True(total.IsTotal);
        Assert.Equal("total", total.Name);

        var rendered = runtime.Display.RenderMany(
            results,
            new DisplayRenderOptions(runtime.Display.Style, ColumnSelectionResolver: runtime.GetDisplaySelection));

        Assert.Contains("Modified", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stat_supports_dereference_and_filesystem_mode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        var linkPath = Path.Combine(temporaryDirectory.Path, "link.txt");
        await File.WriteAllTextAsync(targetPath, "toast");
        File.CreateSymbolicLink(linkPath, targetPath);

        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var linked = Assert.IsType<FileSystemEntry>(Assert.Single(await engine.ExecuteToListAsync($"stat {Quote(linkPath)}")));
        var dereferenced = Assert.IsType<FileSystemEntry>(Assert.Single(await engine.ExecuteToListAsync($"stat -L {Quote(linkPath)}")));
        var filesystem = Assert.IsType<FileSystemUsageInfo>(Assert.Single(await engine.ExecuteToListAsync($"stat -f {Quote(linkPath)}")));

        Assert.Equal("link.txt", linked.Name);
        Assert.Equal("target.txt", dereferenced.Name);
        Assert.Equal(linkPath, filesystem.RequestedPath);
    }

    [Fact]
    public async Task Readlink_realpath_ln_and_chmod_work_for_local_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        var linkPath = Path.Combine(temporaryDirectory.Path, "link.txt");
        await File.WriteAllTextAsync(targetPath, "toast");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var linkResults = await engine.ExecuteToListAsync($"ln -s {Quote(targetPath)} {Quote(linkPath)}");
        var readlinkResults = await engine.ExecuteToListAsync($"readlink {Quote(linkPath)}");
        var realpathResults = await engine.ExecuteToListAsync($"realpath {Quote(linkPath)}");
        await engine.ExecuteToListAsync($"chmod 600 {Quote(targetPath)}");

        Assert.IsType<FileSystemEntry>(Assert.Single(linkResults));
        Assert.Equal(targetPath, Assert.Single(readlinkResults));
        Assert.Equal(targetPath, Assert.Single(realpathResults));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(targetPath));
    }

    [Fact]
    public async Task Chmod_and_chown_work_for_local_files_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        await File.WriteAllTextAsync(targetPath, "toast");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var currentUser = UnixSystemServices.GetCurrentIdentity().User.DisplayName;

        await engine.ExecuteToListAsync($"chmod 444 {Quote(targetPath)}");
        Assert.True(File.GetAttributes(targetPath).HasFlag(FileAttributes.ReadOnly));

        await engine.ExecuteToListAsync($"chmod 644 {Quote(targetPath)}");
        Assert.False(File.GetAttributes(targetPath).HasFlag(FileAttributes.ReadOnly));

        var chownResults = await engine.ExecuteToListAsync($"chown {Quote(currentUser)} {Quote(targetPath)}");
        var changed = Assert.IsType<FileSystemEntry>(Assert.Single(chownResults));

        Assert.Equal(targetPath, changed.FullName);
        Assert.Equal(currentUser, changed.Owner?.DisplayName, ignoreCase: true);
    }

    [Fact]
    public async Task Ln_can_create_hard_links_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var targetPath = Path.Combine(temporaryDirectory.Path, "target.txt");
        var linkPath = Path.Combine(temporaryDirectory.Path, "link.txt");
        await File.WriteAllTextAsync(targetPath, "toast");

        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var linkResults = await engine.ExecuteToListAsync($"ln {Quote(targetPath)} {Quote(linkPath)}");
        var created = Assert.IsType<FileSystemEntry>(Assert.Single(linkResults));

        Assert.Equal(linkPath, created.FullName);
        Assert.Equal("toast", await File.ReadAllTextAsync(linkPath));

        await File.WriteAllTextAsync(linkPath, "jam");
        Assert.Equal("jam", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Glob_command_supports_recursive_patterns_alternation_and_hidden_control()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "beta.md"), "beta");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "child"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "child", "gamma.txt"), "gamma");
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, ".hidden"));
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, ".hidden", "delta.txt"), "delta");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, ".secret.txt"), "secret");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var alternationResults = await engine.ExecuteToListAsync("glob **/*.@(txt,md)");
        var alternationNames = alternationResults
            .Cast<FileSystemEntry>()
            .Select(item => item.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var defaultResults = await engine.ExecuteToListAsync("glob **/*.txt");
        var defaultNames = defaultResults
            .Cast<FileSystemEntry>()
            .Select(item => item.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var hiddenResults = await engine.ExecuteToListAsync("glob -a **/*.txt");
        var hiddenNames = hiddenResults
            .Cast<FileSystemEntry>()
            .Select(item => item.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["alpha.txt", "beta.md", "gamma.txt"], alternationNames);
        Assert.Equal(["alpha.txt", "gamma.txt"], defaultNames);
        Assert.Equal([".secret.txt", "alpha.txt", "delta.txt", "gamma.txt"], hiddenNames);
    }

    [Fact]
    public async Task Builtin_commands_expand_bareword_globs_and_preserve_literals_when_quoted_or_unmatched()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "beta.txt"), "beta");

        var runtime = ToshRuntime.CreateDefault();
        runtime.CurrentDirectory = temporaryDirectory.Path;
        var engine = new ToshEngine(runtime);

        var echoExpanded = await engine.ExecuteToListAsync("echo *.txt");
        var echoQuoted = await engine.ExecuteToListAsync("echo \"*.txt\"");
        var echoUnmatched = await engine.ExecuteToListAsync("echo *.missing");
        var listed = await engine.ExecuteToListAsync("ls *.txt | get Name");

        Assert.Equal(["alpha.txt", "beta.txt"], echoExpanded.Cast<string>().ToArray());
        Assert.Equal(["*.txt"], echoQuoted.Cast<string>().ToArray());
        Assert.Equal(["*.missing"], echoUnmatched.Cast<string>().ToArray());
        Assert.Equal(["alpha.txt", "beta.txt"], listed.Cast<string>().ToArray());
    }

    private static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool CanExecuteExternalCommand(string name, params string[] arguments)
    {
        var lookup = ExternalCommandResolver.Resolve(Environment.CurrentDirectory, name);

        if (lookup.Status != ExternalCommandLookupStatus.Found ||
            lookup.ResolvedPath is null)
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = lookup.ResolvedPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tosh-utility-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _backgroundTask;

        private TestHttpServer(HttpListener listener, Uri url, Func<HttpListenerContext, Task> handler)
        {
            _listener = listener;
            Url = url;
            _backgroundTask = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;

                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    await handler(context);
                }
            });
        }

        public Uri Url { get; }

        public static Task<TestHttpServer> StartAsync(Func<HttpListenerContext, Task> handler)
        {
            var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            return Task.FromResult(new TestHttpServer(listener, new Uri(prefix), handler));
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();

            try
            {
                _backgroundTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }
}
