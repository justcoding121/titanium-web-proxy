using System;
using System.Diagnostics;
using System.IO;

namespace Titanium.Tls.Tests.Support;

internal static class OpenSslOracle
{
    internal static string? Find()
    {
        string? configured = Environment.GetEnvironmentVariable("TITANIUM_OPENSSL");
        if (IsExecutable(configured))
        {
            return configured;
        }

        string[] candidates =
        [
            @"C:\Program Files\Git\usr\bin\openssl.exe",
            "/opt/homebrew/bin/openssl",
            "/usr/bin/openssl",
        ];
        foreach (string candidate in candidates)
        {
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            string built = Path.Combine(home, "opt", "openssl-3.5.9", "bin", "openssl");
            if (IsExecutable(built))
            {
                return built;
            }
        }

        return null;
    }

    internal static bool TryReadVersion(string path, out Version version)
    {
        version = new Version(0, 0);
        ProcessStartInfo start = new()
        {
            FileName = path,
            Arguments = "version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        string text = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            return false;
        }

        // "OpenSSL 3.5.9 29 Sep 2026"
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Version.TryParse(parts[1], out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return process.ExitCode == 0;
    }

    internal static bool Require(Version minimum)
    {
        string? path = Find();
        bool required = string.Equals(Environment.GetEnvironmentVariable("TITANIUM_REQUIRE_ORACLE"), "1", StringComparison.Ordinal);
        if (path is null || !TryReadVersion(path, out Version version) || version < minimum)
        {
            if (required)
            {
                throw new InvalidOperationException("OpenSSL " + minimum + " is required and was not found.");
            }

            return false;
        }

        return true;
    }

    private static bool IsExecutable(string? path)
    {
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }
}
