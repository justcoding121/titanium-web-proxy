using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Plus.Security.Oidc;

/// <summary>
/// Enforces the <c>oidc</c> route policy: OpenID Connect authorization-code login with PKCE, an encrypted
/// session cookie, silent refresh, and sign-out. Core calls it only for routes that list an <c>oidc</c> policy,
/// so routes without one pay nothing.
/// </summary>
public sealed class OidcPolicyHandler : IRoutePolicyHandler
{
    private readonly ConditionalWeakTable<RoutePolicyConfig, OidcRuntime> _runtimes = new();
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;

    public OidcPolicyHandler(ILogger? logger = null, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Kind => "oidc";

    /// <summary>Callbacks one client may make per minute before it is answered 429.</summary>
    internal int CallbackLimitPerMinute { get; set; } = 60;

    /// <summary>Reads environment variables for secrets. Tests replace it.</summary>
    internal Func<string, string?>? ReadEnvironment { get; set; }

    /// <summary>The provider client of a policy, once the policy has run. Null before that or when it cannot run.</summary>
    internal OidcProvider? ProviderOf(RoutePolicyConfig policy) =>
        _runtimes.TryGetValue(policy, out var runtime) && runtime.Error is null ? runtime.Provider : null;

    /// <summary>Forgets the compiled form of a policy, so its secrets are read again on the next request (tests).</summary>
    internal void Reset(RoutePolicyConfig policy) => _runtimes.Remove(policy);

    /// <inheritdoc />
    public ValueTask<RoutePolicyVerdict> EvaluateAsync(
        object session, RouteConfig route, RoutePolicyConfig policy, CancellationToken cancellationToken = default)
    {
        if (session is not SessionEventArgs args)
        {
            throw new ArgumentException("An oidc policy needs a request session.", nameof(session));
        }

        // Built on first use, once per policy object. A reload brings new policy objects, so new secrets.
        var runtime = _runtimes.GetValue(policy, p => OidcRuntime.Create(p, this, _logger, _time, ReadEnvironment));
        return runtime.Error is null
            ? runtime.EvaluateAsync(args, cancellationToken)
            : ValueTask.FromResult(OidcRuntime.Refuse(args));
    }
}

/// <summary>Registers the <c>oidc</c> policy handler with the host.</summary>
public static class OidcLogin
{
    /// <summary>
    /// Adds the handler to the host's policy handler list. It does nothing until a route lists an <c>oidc</c> policy.
    /// </summary>
    public static OidcPolicyHandler? TryStart(PlusActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PolicyHandlers is null)
        {
            PlusLog.Warn(context, "Plus OIDC: the host has no policy handler list, so oidc policies are not enforced.");
            return null;
        }

        var handler = new OidcPolicyHandler(context.Logger);
        context.PolicyHandlers.Add(handler);
        PlusLog.Info(context, "Plus OIDC: login policy handler registered.");
        return handler;
    }
}
