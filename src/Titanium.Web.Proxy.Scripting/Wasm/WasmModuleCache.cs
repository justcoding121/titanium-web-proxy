using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// A disk cache of compiled modules, so a restart or reload skips compiling a module it has seen. An entry is keyed by
/// the module's SHA-256 and the engine fingerprint (Wasmtime version, architecture, every engine setting), so a module
/// compiled under other settings is never reused.
/// </summary>
/// <remarks>
/// <para>A cache entry is machine code that runs in the proxy, so whoever can write the directory can run code. The cache
/// is used only when the directory passes a permission check: on Unix it must not be writable by group or others (it is
/// created 0700); on Windows its ACL must not let Everyone, Users or Authenticated Users write. A directory that fails is
/// ignored with a warning and modules compile as usual.</para>
/// <para>The directory is <c>TITANIUM_WASM_CACHE_DIR</c>, or <c>titanium/wasm-cache</c> under the user's local
/// application data. <c>TITANIUM_WASM_CACHE_DIR=off</c> turns the cache off.</para>
/// </remarks>
internal sealed class WasmModuleCache
{
    public const string DirectoryVariable = "TITANIUM_WASM_CACHE_DIR";

    private static readonly Lazy<WasmModuleCache?> DefaultCache = new(CreateDefault);

    public WasmModuleCache(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    /// <summary>The process cache from the environment, or null when it is off.</summary>
    public static WasmModuleCache? Default => DefaultCache.Value;

    public string PathFor(string moduleHash, string fingerprint)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(moduleHash + "\n" + fingerprint)));
        return Path.Combine(Directory, key + ".cwasm");
    }

    /// <summary>Reads an entry. Returns null when there is none or the directory or file fails the permission check.</summary>
    public byte[]? TryRead(string moduleHash, string fingerprint, out string? warning)
    {
        warning = null;
        var path = PathFor(moduleHash, fingerprint);
        if (!File.Exists(path))
        {
            return null;
        }

        if (!IsTrusted(Directory, out warning) || !IsTrusted(path, out warning))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"could not read cached module {path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Writes an entry atomically (temporary file, then rename). Returns false with a reason when it cannot.</summary>
    public bool TryWrite(string moduleHash, string fingerprint, ReadOnlySpan<byte> compiled, out string? warning)
    {
        warning = null;
        try
        {
            EnsureDirectory();
            if (!IsTrusted(Directory, out warning))
            {
                return false;
            }

            var path = PathFor(moduleHash, fingerprint);
            var temporary = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                stream.Write(compiled);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"could not write the module cache in {Directory}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Deletes an entry that Wasmtime refused (corrupt, or from another build), so it is rewritten.</summary>
    public void Discard(string moduleHash, string fingerprint)
    {
        try
        {
            File.Delete(PathFor(moduleHash, fingerprint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next write replaces it.
        }
    }

    /// <summary>Whether only the current user (and administrators) can write <paramref name="path"/>.</summary>
    public static bool IsTrusted(string path, out string? reason)
    {
        reason = null;
        if (OperatingSystem.IsWindows())
        {
            return IsTrustedWindows(path, out reason);
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            reason = $"{path} is writable by group or others; the WASM module cache is not used";
            return false;
        }

        return true;
    }

    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedWindows(string path, out string? reason)
    {
        reason = null;
        FileSystemSecurity security = System.IO.Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var broad = new[]
        {
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        };
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateFiles |
                                       FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 &&
                broad.Contains(rule.IdentityReference as SecurityIdentifier))
            {
                reason = $"{path} lets {rule.IdentityReference.Value} write; the WASM module cache is not used";
                return false;
            }
        }

        return true;
    }

    private static WasmModuleCache? CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new WasmModuleCache(configured);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(root) ? null : new WasmModuleCache(Path.Combine(root, "titanium", "wasm-cache"));
    }
}
