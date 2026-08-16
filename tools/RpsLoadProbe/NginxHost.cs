using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
/// Starts a stock-ish nginx reverse proxy against the shared Kestrel origin.
/// Uses a temp prefix directory so the user's install tree is never modified.
/// </summary>
internal sealed class NginxHost : IDisposable
{
    private readonly Process process;
    private readonly string prefixDir;

    public int Port { get; }
    public string ListenUrl => $"http://127.0.0.1:{Port}/";
    public string Version { get; }

    private NginxHost(Process process, string prefixDir, int port, string version)
    {
        this.process = process;
        this.prefixDir = prefixDir;
        Port = port;
        Version = version;
    }

    public static NginxHost? TryStart(int originHttpPort, string? nginxPath)
    {
        var exe = ResolveNginxExecutable(nginxPath);
        if (exe == null)
            return null;

        var version = ReadVersion(exe);
        var port = GetFreeTcpPort();
        var prefixDir = Path.Combine(Path.GetTempPath(), "twp-rps-nginx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefixDir);
        Directory.CreateDirectory(Path.Combine(prefixDir, "logs"));
        Directory.CreateDirectory(Path.Combine(prefixDir, "temp"));
        Directory.CreateDirectory(Path.Combine(prefixDir, "conf"));

        var confPath = Path.Combine(prefixDir, "conf", "nginx.conf");
        var conf = $$"""
            worker_processes auto;
            daemon off;
            error_log logs/error.log error;
            pid nginx.pid;
            events {
                worker_connections 4096;
            }
            http {
                access_log off;
                sendfile on;
                keepalive_timeout 65;
                upstream origin {
                    server 127.0.0.1:{{originHttpPort}};
                    keepalive 32;
                }
                server {
                    listen 127.0.0.1:{{port}};
                    location / {
                        proxy_http_version 1.1;
                        proxy_set_header Connection "";
                        proxy_set_header Host $host;
                        proxy_pass http://origin;
                    }
                }
            }
            """;
        File.WriteAllText(confPath, conf, Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-p \"{prefixDir}\" -c conf/nginx.conf",
            WorkingDirectory = prefixDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start nginx.");

        // nginx master may daemonize on Linux; give the listen socket a moment.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen(port))
                return new NginxHost(process, prefixDir, port, version);
            if (process.HasExited)
            {
                var err = process.StandardError.ReadToEnd();
                TryDeleteDir(prefixDir);
                throw new InvalidOperationException(
                    $"nginx exited early (code {process.ExitCode}). stderr: {err}");
            }

            Thread.Sleep(50);
        }

        TryStop(process, exe, prefixDir);
        TryDeleteDir(prefixDir);
        throw new TimeoutException($"nginx did not open port {port} in time.");
    }

    public void Dispose()
    {
        var exe = process.StartInfo.FileName;
        TryStop(process, exe, prefixDir);
        TryDeleteDir(prefixDir);
    }

    public static string? ResolveNginxExecutable(string? nginxPath)
    {
        if (!string.IsNullOrWhiteSpace(nginxPath))
        {
            if (File.Exists(nginxPath))
                return Path.GetFullPath(nginxPath);
            return null;
        }

        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "nginx.exe", "nginx" }
            : new[] { "nginx" };

        foreach (var name in names)
        {
            var fromPath = FindOnPath(name);
            if (fromPath != null)
                return fromPath;
        }

        return null;
    }

    public static string NginxMissingMessage() =>
        """
        nginx was not found on PATH (and no --nginx-path was given).
        TWP arms will still run. To enable the same-machine nginx control arm:
          Windows: download the official Windows zip from https://nginx.org/en/docs/windows.html
                   or run: scoop install nginx   /   choco install nginx
          Linux:   sudo apt-get install -y nginx
        Then re-run with nginx on PATH, or pass --nginx-path <path-to-nginx[.exe]>.
        """;

    private static string ReadVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-v",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (p == null) return "unknown";
            var err = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(err) ? stdout : err;
            return text.Trim().Replace('\n', ' ');
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryStop(Process process, string exe, string prefixDir)
    {
        try
        {
            using var quit = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-p \"{prefixDir}\" -c conf/nginx.conf -s quit",
                WorkingDirectory = prefixDir,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            quit?.WaitForExit(5000);
        }
        catch
        {
            // fall through to kill
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // best effort
        }

        process.Dispose();
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore bad PATH entries
            }
        }

        return null;
    }

    private static bool IsPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(IPAddress.Loopback, port, null, null);
            var ok = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
            if (!ok) return false;
            client.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp leftovers are fine
        }
    }
}
