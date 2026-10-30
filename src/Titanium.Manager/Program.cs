using System.Net;
using System.Net.Sockets;
using Titanium.Manager.Api;
using Titanium.Manager.Store;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length > 0 && args[0].Equals("healthcheck", StringComparison.OrdinalIgnoreCase))
    {
        return await RunHealthcheckAsync(args[1..]).ConfigureAwait(false);
    }

    string listen = "http://127.0.0.1:7700/";
    string db = Path.Combine(Environment.CurrentDirectory, "titanium-manager.db");
    string? adminToken = Environment.GetEnvironmentVariable("TITANIUM_MANAGER_ADMIN_TOKEN");

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        if (arg == "--listen" && i + 1 < args.Length)
        {
            listen = args[++i];
            continue;
        }

        if (arg == "--db" && i + 1 < args.Length)
        {
            db = args[++i];
            continue;
        }

        if (arg is "--admin-token" && i + 1 < args.Length)
        {
            adminToken = args[++i];
            continue;
        }

        Console.Error.WriteLine($"titanium-manager: unknown argument '{arg}'");
        PrintHelp();
        return 2;
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

    if (string.IsNullOrWhiteSpace(adminToken))
    {
        Console.Error.WriteLine(
            "titanium-manager: set --admin-token or TITANIUM_MANAGER_ADMIN_TOKEN (hashed into SQLite as role admin).");
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
        Console.Error.WriteLine($"titanium-manager: failed to listen on {listen}: {ex.Message}");
        return 1;
    }
    catch (SocketException ex)
    {
        Console.Error.WriteLine($"titanium-manager: failed to listen on {listen}: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"titanium-manager listening on {server.BaseUrl} db={Path.GetFullPath(db)} (single-instance)");

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

    Console.WriteLine("titanium-manager shutting down");
    return 0;
}

static async Task<int> RunHealthcheckAsync(string[] args)
{
    // Container HEALTHCHECK for the chiseled image (no shell/curl). Exit 0 is healthy.
    var url = "http://127.0.0.1:7700/v1/healthz";
    var timeout = TimeSpan.FromSeconds(3);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg is "--help" or "-h")
        {
            Console.WriteLine(
                """
                titanium-manager healthcheck [--url <url>] [--timeout <seconds>]

                  Probes GET /v1/healthz (default http://127.0.0.1:7700/v1/healthz). Exit 0 when HTTP 200.
                """);
            return 0;
        }

        if (arg == "--url" && i + 1 < args.Length)
        {
            url = args[++i];
            continue;
        }

        if (arg == "--timeout" && i + 1 < args.Length)
        {
            if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
                seconds <= 0 || seconds > 300)
            {
                Console.Error.WriteLine("titanium-manager healthcheck: --timeout must be between 0 and 300 seconds.");
                return 2;
            }

            timeout = TimeSpan.FromSeconds(seconds);
            continue;
        }

        Console.Error.WriteLine($"titanium-manager healthcheck: unknown argument '{arg}'");
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

        Console.Error.WriteLine($"titanium-manager healthcheck: {(int)response.StatusCode} {response.ReasonPhrase}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"titanium-manager healthcheck: {ex.Message}");
        return 1;
    }
}

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
