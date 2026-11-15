using System.Globalization;

namespace Titanium.Inspector.Tests;

/// <summary>Sets <see cref="CultureInfo.CurrentCulture"/> for one test and puts the previous culture back.</summary>
internal static class CulturePin
{
    public static IDisposable Use(string name)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly CultureInfo _previous;

        public Restore(CultureInfo previous) => _previous = previous;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
