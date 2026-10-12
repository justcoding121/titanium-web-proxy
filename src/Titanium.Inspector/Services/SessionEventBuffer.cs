namespace Titanium.Inspector.Services;

/// <summary>One decision recorded for a session. <see cref="Key"/> is a localization key.</summary>
public readonly record struct SessionEventNote(double OffsetMs, string Key, bool Failure);

/// <summary>
/// Notes taken on the proxy thread and copied out once at completion.
/// Further notes after the cap are dropped. The list inside is never returned.
/// </summary>
internal sealed class SessionEventBuffer
{
    public const int Cap = 20;

    private readonly List<(DateTime At, string Key, bool Failure)> _notes = new();
    private readonly object _gate = new();

    public void Add(string key, bool failure)
    {
        lock (_gate)
        {
            if (_notes.Count >= Cap)
            {
                return;
            }

            _notes.Add((DateTime.UtcNow, key, failure));
        }
    }

    public IReadOnlyList<SessionEventNote> Freeze(DateTimeOffset started)
    {
        lock (_gate)
        {
            if (_notes.Count == 0)
            {
                return [];
            }

            var notes = new SessionEventNote[_notes.Count];
            var start = started.UtcDateTime;
            for (var i = 0; i < _notes.Count; i++)
            {
                var (at, key, failure) = _notes[i];
                var milliseconds = Math.Max(0, (at - start).TotalMilliseconds);
                notes[i] = new SessionEventNote(SessionDisplayFormat.RoundMs(milliseconds), key, failure);
            }

            return notes;
        }
    }
}
