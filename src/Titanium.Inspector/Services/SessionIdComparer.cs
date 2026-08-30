using System.Collections;

namespace Titanium.Inspector.Services;

/// <summary>
/// Numeric Id sort for the sessions grid. DataGridTextColumn can otherwise
/// compare display strings ("10" before "2").
/// </summary>
public sealed class SessionIdComparer : IComparer
{
    public static SessionIdComparer Instance { get; } = new();

    public int Compare(object? x, object? y)
    {
        var left = x as SessionSnapshot;
        var right = y as SessionSnapshot;
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return left.Id.CompareTo(right.Id);
    }
}
