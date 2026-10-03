using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// The capture reader updates <see cref="SessionListCollection"/> off the test thread.
/// A direct <c>Any</c> or <c>ToArray</c> throws when that write overlaps the poll.
/// </summary>
internal static class SessionListPoll
{
    public static SessionSnapshot[] Copy(IReadOnlyList<SessionSnapshot> sessions)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return sessions.ToArray();
            }
            catch (InvalidOperationException)
            {
                // Collection changed during the copy. Retry; the wait loop still has its deadline.
            }
        }

        return [];
    }
}
