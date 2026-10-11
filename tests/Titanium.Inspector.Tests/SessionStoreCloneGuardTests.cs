using System.Collections;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Every public settable <see cref="SessionSnapshot"/> field must survive <c>CloneForDisk</c>.
/// A new field that is missing from the clone fails this test instead of disappearing on spill.
/// </summary>
[TestClass]
public class SessionStoreCloneGuardTests
{
    /// <summary>
    /// Live-row flags. The spill clone is the on-disk payload, so these are not copied as stored.
    /// <c>BodiesOnDisk</c> is forced false. The other two describe an in-flight or evicted row.
    /// </summary>
    private static readonly HashSet<string> IntentionallyNotCopied = new(StringComparer.Ordinal)
    {
        nameof(SessionSnapshot.BodiesOnDisk),
        nameof(SessionSnapshot.BodiesMissingFromDisk),
        nameof(SessionSnapshot.ResponseBodyStreamOpen),
    };

    [TestMethod]
    public void CloneForDisk_CopiesEverySettableProperty()
    {
        var source = new SessionSnapshot();
        var properties = typeof(SessionSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetSetMethod() is not null)
            .ToList();

        Assert.IsTrue(properties.Count > 20, "Expected the snapshot surface, not an empty type.");

        foreach (var property in properties)
        {
            property.SetValue(source, Sample(property.PropertyType, property.Name));
        }

        source.BodiesOnDisk = true;
        source.BodiesMissingFromDisk = true;
        source.ResponseBodyStreamOpen = true;

        var clone = (SessionSnapshot)typeof(SessionStore)
            .GetMethod("CloneForDisk", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source])!;

        Assert.IsFalse(clone.BodiesOnDisk);
        Assert.IsFalse(clone.BodiesMissingFromDisk);
        Assert.IsFalse(clone.ResponseBodyStreamOpen);

        var missing = new List<string>();
        foreach (var property in properties)
        {
            if (IntentionallyNotCopied.Contains(property.Name))
            {
                continue;
            }

            var left = property.GetValue(source);
            var right = property.GetValue(clone);
            if (!Same(left, right))
            {
                missing.Add(property.Name);
            }
        }

        Assert.AreEqual(0, missing.Count, "CloneForDisk dropped: " + string.Join(", ", missing));
    }

    private static object? Sample(Type type, string name)
    {
        if (type == typeof(string))
        {
            return "v-" + name;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(int))
        {
            return 7;
        }

        if (type == typeof(int?))
        {
            return 404;
        }

        if (type == typeof(long))
        {
            return 42L;
        }

        if (type == typeof(long?))
        {
            return 99L;
        }

        if (type == typeof(double?))
        {
            return 1.5d;
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        }

        if (type == typeof(byte[]))
        {
            return new byte[] { 1, 2, 3 };
        }

        if (type == typeof(BodyCaptureState))
        {
            return BodyCaptureState.Truncated;
        }

        if (type == typeof(OpaqueTunnelReason))
        {
            return OpaqueTunnelReason.UserSkipList;
        }

        if (type == typeof(IReadOnlyList<SessionEventNote>))
        {
            return new SessionEventNote[] { new(12, "stats.event.retry", false) };
        }

        if (type == typeof(IReadOnlyList<WebSocketFrameSnapshot>))
        {
            return new[] { new WebSocketFrameSnapshot() };
        }

        if (type == typeof(IReadOnlyList<SseEventSnapshot>))
        {
            return new[] { new SseEventSnapshot() };
        }

        if (type == typeof(IReadOnlyList<GrpcFrameSnapshot>))
        {
            return new[] { new GrpcFrameSnapshot() };
        }

        if (type == typeof(IReadOnlyList<MultipartPartSnapshot>))
        {
            return new[] { new MultipartPartSnapshot() };
        }

        throw new InvalidOperationException("Add a sample for " + type.Name + " (" + name + ").");
    }

    private static bool Same(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is byte[] a && right is byte[] b)
        {
            return a.AsSpan().SequenceEqual(b);
        }

        if (left is IEnumerable leftItems && right is IEnumerable rightItems && left is not string)
        {
            var l = leftItems.Cast<object>().ToList();
            var r = rightItems.Cast<object>().ToList();
            if (l.Count != r.Count)
            {
                return false;
            }

            for (var i = 0; i < l.Count; i++)
            {
                if (!Equals(l[i], r[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return Equals(left, right);
    }
}
