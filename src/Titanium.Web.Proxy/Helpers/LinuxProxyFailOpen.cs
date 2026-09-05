using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     After Inspector releases :port, a still-running Chrome with Preferences
///     <c>fixed_servers</c> keeps dialing that port. Spawn a tiny detached CONNECT/HTTP
///     tunnel so browsing does not break until Chrome is next restarted.
/// </summary>
internal static class LinuxProxyFailOpen
{
    private const string PidFileName = "fail-open-proxy.pid";
    private const string ScriptFileName = "fail-open-proxy.py";

    internal static void Stop()
    {
        try
        {
            var pidPath = PidPath();
            if (pidPath is null || !File.Exists(pidPath))
                return;
            if (int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) && pid > 1)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-TERM {pid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })?.WaitForExit(2000);
                }
                catch
                {
                    // ignore
                }
            }

            try { File.Delete(pidPath); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    internal static void Start(string hostname, int port)
    {
        Stop();
        try
        {
            var dir = ConfigDir();
            if (dir is null)
                return;
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, ScriptFileName);
            File.WriteAllText(script, FailOpenScript);
            var pidPath = Path.Combine(dir, PidFileName);
            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = Quote(script) + " " + Quote(hostname) + " " + port + " " + Quote(pidPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir,
            };
            // Detach: do not wait; child double-forks in the script.
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            // best-effort — Preferences strip still runs for the next Chrome start
        }
    }

    private static string? ConfigDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        return Path.Combine(home, ".config", "TitaniumInspector");
    }

    private static string? PidPath()
    {
        var dir = ConfigDir();
        return dir is null ? null : Path.Combine(dir, PidFileName);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // Minimal HTTP/HTTPS forwarder: CONNECT tunnels + absolute-form HTTP requests.
    private const string FailOpenScript =
        """
        #!/usr/bin/env python3
        import os, socket, select, sys, time

        host, port, pid_path = sys.argv[1], int(sys.argv[2]), sys.argv[3]

        if os.fork() > 0:
            sys.exit(0)
        os.setsid()
        if os.fork() > 0:
            sys.exit(0)
        with open(pid_path, "w") as f:
            f.write(str(os.getpid()))

        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        for _ in range(80):
            try:
                srv.bind((host, port))
                break
            except OSError:
                time.sleep(0.15)
        else:
            sys.exit(1)
        srv.listen(128)

        def pipe(a, b):
            try:
                while True:
                    r, _, _ = select.select([a, b], [], [], 120)
                    if not r:
                        break
                    for s in r:
                        data = s.recv(65536)
                        if not data:
                            return
                        (b if s is a else a).sendall(data)
            except Exception:
                pass
            finally:
                try: a.close()
                except Exception: pass
                try: b.close()
                except Exception: pass

        def handle(c):
            try:
                c.settimeout(30)
                buf = b""
                while b"\r\n\r\n" not in buf and len(buf) < 65536:
                    chunk = c.recv(4096)
                    if not chunk:
                        c.close(); return
                    buf += chunk
                head, _, rest = buf.partition(b"\r\n\r\n")
                lines = head.split(b"\r\n")
                req = lines[0].decode("latin1", "replace")
                parts = req.split(" ")
                if len(parts) < 2:
                    c.close(); return
                method, target = parts[0].upper(), parts[1]
                if method == "CONNECT":
                    hostport = target
                    if ":" in hostport:
                        h, p = hostport.rsplit(":", 1)
                        p = int(p)
                    else:
                        h, p = hostport, 443
                    up = socket.create_connection((h, p), timeout=30)
                    c.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                    if rest:
                        up.sendall(rest)
                    pipe(c, up)
                else:
                    # absolute-form: GET http://host/path HTTP/1.1
                    from urllib.parse import urlsplit
                    u = urlsplit(target)
                    h = u.hostname or ""
                    p = u.port or (443 if u.scheme == "https" else 80)
                    path = u.path or "/"
                    if u.query:
                        path += "?" + u.query
                    up = socket.create_connection((h, p), timeout=30)
                    new_req = f"{method} {path} {parts[2] if len(parts) > 2 else 'HTTP/1.1'}\r\n".encode()
                    # drop absolute Host confusion; keep remaining headers
                    hdrs = b"\r\n".join(lines[1:]) + b"\r\n\r\n"
                    up.sendall(new_req + hdrs + rest)
                    pipe(c, up)
            except Exception:
                try: c.close()
                except Exception: pass

        while True:
            try:
                c, _ = srv.accept()
            except Exception:
                break
            import threading
            threading.Thread(target=handle, args=(c,), daemon=True).start()
        """;
}
