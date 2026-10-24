namespace Titanium.Web.Proxy.Abstractions.Diagnostics;

/// <summary>Why a proxied request failed. <see cref="None"/> means the request completed normally.</summary>
public enum ForwarderError
{
    None = 0,
    NoAvailableDestination = 1,
    RequestTimedOut = 2,
    RequestCanceled = 3,
    RequestBodyClient = 4,
    RequestBodyDestination = 5,
    ResponseHeaders = 6,
    ResponseBodyClient = 7,
    ResponseBodyDestination = 8,
    UpgradeFailed = 9,
    Other = 10,
    PolicyDenied = 11,
}
