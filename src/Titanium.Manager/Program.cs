using System.Net;
using System.Net.Sockets;
using Titanium.Manager.Api;
using Titanium.Manager.Store;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
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

static void PrintHelp()
{
    Console.WriteLine(
        """
        titanium-manager — SQLite multi-cluster config control plane (single-instance)

          --listen <url>       Listen prefix (default http://127.0.0.1:7700/)
          --db <path>          SQLite database path (default ./titanium-manager.db)
          --admin-token <tok>  Bootstrap admin bearer token (or TITANIUM_MANAGER_ADMIN_TOKEN)

        Agents dial out; the manager never pushes. OIDC admin auth is a follow-up.
        """);
}
