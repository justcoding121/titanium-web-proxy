using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Xds.Tests;

/// <summary>Wait helpers that name the phase and how long it ran when a test times out.</summary>
internal static class AdsPhase
{
    public static async Task Wait(Task task, TimeSpan budget, string phase)
    {
        var started = DateTime.UtcNow;
        try
        {
            await task.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            var elapsed = DateTime.UtcNow - started;
            throw new AssertFailedException(
                $"{phase} exceeded {budget.TotalSeconds:0.#}s (ran {elapsed.TotalSeconds:0.0}s).",
                ex);
        }
    }
}
