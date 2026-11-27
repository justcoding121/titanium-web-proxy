namespace Titanium.Tls;

/// <summary>
/// Receives NSS key-log lines. Opt in only for a debug session: the lines are the traffic secrets.
/// </summary>
public interface ITlsKeyLogSink
{
    /// <summary>Accepts one key-log line without a trailing line break.</summary>
    /// <param name="line">The NSS key-log line.</param>
    void Write(string line);
}
