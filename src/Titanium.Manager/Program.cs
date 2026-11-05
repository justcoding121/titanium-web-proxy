using System.Net;
using System.Net.Sockets;
using Titanium.Manager.Api;
using Titanium.Manager.Store;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args) // NOSONAR S3776 -- Top-level host wiring keeps healthcheck and server modes in one entry.
{
    if (args.Length > 0 && args[0].Equals("healthcheck", StringComparison.OrdinalIgnoreCase))
    {
        return await RunHealthcheckAsync(args[1..]).ConfigureAwait(false);
    }

    if (!TryParseServerArgs(args, out var listen, out var db, out var adminToken, out var exitCode))
    {
        return exitCode;
    }

    if (string.IsNullOrWhiteSpace(adminToken))
    {
        await Console.Error.WriteLineAsync(
            "titanium-manager: set --admin-token or TITANIUM_MANAGER_ADMIN_TOKEN (hashed into SQLite as role admin).")
            .ConfigureAwait(false);
        return 2;
    }

    using var store = new ManagerStore(db);
    store.EnsureBootstrapAdminToken(adminToken);

    await using var server = new ManagerHttpServer(store, listen);
    try
    {
        server.Start();
    }
    catch (HttpListenerException ex)
    {
        await Console.Error.WriteLineAsync($"titanium-manager: failed to listen on {listen}: {ex.Message}")
            .ConfigureAwait(false);
        return 1;
    }
    catch (SocketException ex)
    {
        await Console.Error.WriteLineAsync($"titanium-manager: failed to listen on {listen}: {ex.Message}")
            .ConfigureAwait(false);
        return 1;
    }

    await Console.Out.WriteLineAsync(
        $"titanium-manager listening on {server.BaseUrl} db={Path.GetFullPath(db)} (single-instance)").ConfigureAwait(false);

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try
        {
            stop.Cancel();
        }
        catch
        {
            /* ignore */
        }
    };

    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // graceful shutdown
    }

    await Console.Out.WriteLineAsync("titanium-manager shutting down").ConfigureAwait(false);
    return 0;
}

static bool TryParseServerArgs(string[] args, out string listen, out string db, out string? adminToken, out int exitCode) // NOSONAR S3776 -- CLI flag parsing keeps defaults and validation together.
{
    listen = "http://127.0.0.1:7700/";
    db = Path.Combine(Environment.CurrentDirectory, "titanium-manager.db");
    adminToken = Environment.GetEnvironmentVariable("TITANIUM_MANAGER_ADMIN_TOKEN");
    exitCode = 0;

    var i = 0;
    while (i < args.Length)
    {
        var arg = args[i];
        if (arg is "--help" or "-h")
        {
            PrintHelp();
            return false;
        }

        if (arg == "--listen")
        {
            if (i + 1 >= args.Length)
            {
                exitCode = 2;
                return false;
            }

            listen = args[i + 1];
            i += 2;
            continue;
        }

        if (arg == "--db")
        {
            if (i + 1 >= args.Length)
            {
                exitCode = 2;
                return false;
            }

            db = args[i + 1];
            i += 2;
            continue;
        }

        if (arg is "--admin-token")
        {
            if (i + 1 >= args.Length)
            {
                exitCode = 2;
                return false;
            }

            adminToken = args[i + 1];
            i += 2;
            continue;
        }

        Console.Error.WriteLine($"titanium-manager: unknown argument '{arg}'");
        PrintHelp();
        exitCode = 2;
        return false;
    }

    if (!listen.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !listen.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        listen = "http://" + listen;
    }

    if (!listen.EndsWith('/'))
    {
        listen += "/";
    }

    return true;
}

static async Task<int> RunHealthcheckAsync(string[] args) // NOSONAR S3776 -- Healthcheck flag parsing and probe share one container entrypoint.
{
    // Container HEALTHCHECK for the chiseled image (no shell/curl). Exit 0 is healthy.
    var url = DefaultHealthcheckUrl();
    var timeout = TimeSpan.FromSeconds(3);
    var i = 0;
    while (i < args.Length)
    {
        var arg = args[i];
        if (arg is "--help" or "-h")
        {
            await Console.Out.WriteLineAsync(
                """
                titanium-manager healthcheck [--url <url>] [--timeout <seconds>]

                  Probes GET /v1/healthz (default http://127.0.0.1:7700/v1/healthz). Exit 0 when HTTP 200.
                """).ConfigureAwait(false);
            return 0;
        }

        if (arg == "--url")
        {
            if (i + 1 >= args.Length)
            {
                await Console.Error.WriteLineAsync("titanium-manager healthcheck: --url requires a value.")
                    .ConfigureAwait(false);
                return 2;
            }

            url = args[i + 1];
            i += 2;
            continue;
        }

        if (arg == "--timeout")
        {
            if (i + 1 >= args.Length)
            {
                await Console.Error.WriteLineAsync("titanium-manager healthcheck: --timeout requires a value.")
                    .ConfigureAwait(false);
                return 2;
            }

            if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                seconds <= 0 || seconds > 300)
            {
                await Console.Error.WriteLineAsync(
                    "titanium-manager healthcheck: --timeout must be between 0 and 300 seconds.").ConfigureAwait(false);
                return 2;
            }

            timeout = TimeSpan.FromSeconds(seconds);
            i += 2;
            continue;
        }

        await Console.Error.WriteLineAsync($"titanium-manager healthcheck: unknown argument '{arg}'").ConfigureAwait(false);
        return 2;
    }

    try
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = new HttpClient { Timeout = timeout };
        using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return 0;
        }

        await Console.Error.WriteLineAsync($"titanium-manager healthcheck: {(int)response.StatusCode} {response.ReasonPhrase}")
            .ConfigureAwait(false);
        return 1;
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync($"titanium-manager healthcheck: {ex.Message}").ConfigureAwait(false);
        return 1;
    }
}

static string DefaultHealthcheckUrl() =>
    new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", 7700, "/v1/healthz").Uri.AbsoluteUri;

static void PrintHelp()
{
    Console.WriteLine(
        """
        titanium-manager — SQLite multi-cluster config control plane (single-instance)

          --listen <url>       Listen prefix (default http://127.0.0.1:7700/)
          --db <path>          SQLite database path (default ./titanium-manager.db)
          --admin-token <tok>  Bootstrap admin bearer token (or TITANIUM_MANAGER_ADMIN_TOKEN)

          healthcheck          Probe GET /v1/healthz (for container HEALTHCHECK)

        Agents dial out; the manager never pushes. OIDC admin auth is a follow-up.
        """);
}
