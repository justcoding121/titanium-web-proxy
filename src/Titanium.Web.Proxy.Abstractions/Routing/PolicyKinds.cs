namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>Policy kinds that wait on something outside the request, so they need the full session path.</summary>
internal static class PolicyKinds
{
    internal const string ExtAuth = "extAuth";

    /// <summary>Plus-supplied login policy. Core only dispatches it to an <see cref="Plugins.IRoutePolicyHandler"/>.</summary>
    internal const string Oidc = "oidc";

    /// <summary>True for a kind that Core hands to a plugin handler instead of evaluating itself.</summary>
    internal static bool IsHandled(string? kind) => string.Equals(kind, Oidc, StringComparison.Ordinal);

    /// <summary>True for a kind that awaits a service or a login. Terminate-lite cannot enforce it.</summary>
    internal static bool AwaitsService(string? kind) =>
        string.Equals(kind, ExtAuth, StringComparison.Ordinal) || IsHandled(kind);
}
