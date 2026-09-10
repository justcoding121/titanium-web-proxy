namespace Titanium.Web.Proxy;

/// <summary>
///     Outcome of enabling or disabling OS system proxy. Callers must treat failure as
///     non-fatal: log <see cref="Message"/> and continue (do not crash the process).
/// </summary>
public readonly struct SystemProxyChangeResult
{
    public SystemProxyChangeResult(bool succeeded, string message)
    {
        Succeeded = succeeded;
        Message = message ?? string.Empty;
    }

    public bool Succeeded { get; }

    public string Message { get; }

    public static SystemProxyChangeResult Ok(string message) => new(true, message);

    public static SystemProxyChangeResult Fail(string message) => new(false, message);
}
