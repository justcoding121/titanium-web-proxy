using System;
using System.Text;

namespace Titanium.Cli.Admin;

/// <summary>
///     The one entry point for Prometheus exposition of the core meter. The admin server calls
///     <see cref="Render" />; the standalone <c>server.metrics</c> endpoint calls <see cref="Start" /> at boot.
///     <para>
///         No <see cref="MetricsListener" /> exists until <see cref="Start" /> or the first
///         <see cref="Render" />, so a host that never serves <c>/metrics</c> adds nothing to the request path.
///     </para>
/// </summary>
internal sealed class MetricsExposition : IDisposable
{
    private readonly MetricsListener _listener;
    private readonly object _gate = new();
    private Action<StringBuilder>[] _appenders = [];

    public MetricsExposition(string meterName = MetricsListener.DefaultMeterName)
    {
        _listener = new MetricsListener(meterName);
    }

    /// <summary>True once a meter listener is attached.</summary>
    public bool IsListening => _listener.IsStarted;

    /// <summary>Attaches the meter listener now. Idempotent.</summary>
    public void Start() => _listener.Start();

    /// <summary>
    ///     Adds a section appended after the meter series on every scrape (for example the Plus
    ///     destination-state series). Safe to call while scrapes run: the list is replaced, never edited.
    /// </summary>
    public void AddAppender(Action<StringBuilder> appender)
    {
        ArgumentNullException.ThrowIfNull(appender);
        lock (_gate)
        {
            var next = new Action<StringBuilder>[_appenders.Length + 1];
            Array.Copy(_appenders, next, _appenders.Length);
            next[^1] = appender;
            _appenders = next;
        }
    }

    /// <summary>Starts the listener on first use, then renders the full exposition.</summary>
    public string Render()
    {
        _listener.Start();
        var sb = new StringBuilder(4096);
        lock (_gate)
        {
            // Observable instruments report through the listener callbacks; one scrape at a time keeps that tidy.
            _listener.RecordObservableInstruments();
            PrometheusTextWriter.Write(sb, _listener);
        }

        foreach (var appender in System.Threading.Volatile.Read(ref _appenders))
        {
            try
            {
                appender(sb);
            }
            catch (Exception ex)
            {
                // A failing section must not take the core series down with it.
                sb.Append("# appender failed: ").Append(ex.GetType().Name).Append('\n');
            }
        }

        return sb.ToString();
    }

    public void Dispose() => _listener.Dispose();
}
