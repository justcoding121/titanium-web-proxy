using System;
using System.Globalization;
using System.IO;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Parses Linux <c>/proc/net/tcp</c> / <c>/proc/net/tcp6</c> lines and resolves socket inodes to PIDs.
/// </summary>
internal static class LinuxProcNetTcp
{
    /// <summary>
    ///     Tries to parse local port (host order) and inode from a single <c>/proc/net/tcp[6]</c> data line.
    /// </summary>
    internal static bool TryParseLocalPortAndInode(ReadOnlySpan<char> line, out int localPort, out long inode)
    {
        localPort = 0;
        inode = 0;

        line = line.Trim();
        if (line.IsEmpty || line.StartsWith("sl", StringComparison.Ordinal))
        {
            return false;
        }

        // sl local_address rem_address st ... uid timeout inode
        var rest = line;
        if (!TryTakeField(ref rest, out _))
        {
            return false;
        }

        if (!TryTakeField(ref rest, out var localAddress))
        {
            return false;
        }

        var colon = localAddress.LastIndexOf(':');
        if (colon <= 0 || colon >= localAddress.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(localAddress[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out localPort))
        {
            return false;
        }

        // rem_address, st, tx_queue:rx_queue, tr:tm->when, retrnsmt, uid, timeout, inode
        for (var i = 0; i < 7; i++)
        {
            if (!TryTakeField(ref rest, out _))
            {
                return false;
            }
        }

        if (!TryTakeField(ref rest, out var inodeField))
        {
            return false;
        }

        return long.TryParse(inodeField, NumberStyles.Integer, CultureInfo.InvariantCulture, out inode) && inode > 0;
    }

    /// <summary>
    ///     Finds the inode for <paramref name="localPort"/> in a <c>/proc/net/tcp[6]</c> file body.
    /// </summary>
    internal static bool TryFindInodeForLocalPort(string procNetTcpContents, int localPort, out long inode)
    {
        inode = 0;
        using var reader = new StringReader(procNetTcpContents);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (TryParseLocalPortAndInode(line, out var port, out var found) && port == localPort)
            {
                inode = found;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Resolves a socket inode to an owning process id by scanning <c>/proc/*/fd</c>.
    /// </summary>
    internal static int FindProcessIdByInode(long inode, string procRoot = "/proc")
    {
        if (inode <= 0)
        {
            return 0;
        }

        var needle = $"socket:[{inode}]";
        string[] pidDirs;
        try
        {
            pidDirs = Directory.GetDirectories(procRoot);
        }
        catch
        {
            return 0;
        }

        foreach (var pidDir in pidDirs)
        {
            var dirName = Path.GetFileName(pidDir);
            if (!int.TryParse(dirName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                continue;
            }

            string fdDir = Path.Combine(pidDir, "fd");
            string[] fds;
            try
            {
                fds = Directory.GetFiles(fdDir);
            }
            catch
            {
                continue;
            }

            foreach (var fdPath in fds)
            {
                try
                {
                    var target = new FileInfo(fdPath).LinkTarget;
                    if (target is not null &&
                        target.Equals(needle, StringComparison.Ordinal))
                    {
                        return pid;
                    }
                }
                catch
                {
                    // Permission or raced exit — skip.
                }
            }
        }

        return 0;
    }

    private static bool TryTakeField(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> field)
    {
        rest = rest.TrimStart();
        if (rest.IsEmpty)
        {
            field = default;
            return false;
        }

        var space = rest.IndexOfAny(' ', '\t');
        if (space < 0)
        {
            field = rest;
            rest = default;
            return true;
        }

        field = rest[..space];
        rest = rest[(space + 1)..];
        return true;
    }
}
