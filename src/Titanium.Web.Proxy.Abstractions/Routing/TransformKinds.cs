namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>Known request and response transform kind names.</summary>
public static class TransformKinds
{
    public const string PathRemovePrefix = "PathRemovePrefix";
    public const string PathPrefix = "PathPrefix";
    public const string RequestHeaderSet = "RequestHeaderSet";
    public const string RequestHeaderRemove = "RequestHeaderRemove";
    public const string QueryValueSet = "QueryValueSet";
    public const string RequestHostUseDestination = "RequestHostUseDestination";
    public const string ResponseHeaderSet = "ResponseHeaderSet";
    public const string ResponseHeaderRemove = "ResponseHeaderRemove";
    public const string PathSet = "PathSet";
    public const string PathPattern = "PathPattern";
    public const string PathRegexReplace = "PathRegexReplace";
    public const string HttpMethodChange = "HttpMethodChange";
    public const string QueryValueAppend = "QueryValueAppend";
    public const string QueryRemove = "QueryRemove";
    public const string QueryRouteParameter = "QueryRouteParameter";
    public const string RequestHeaderAppend = "RequestHeaderAppend";
    public const string RequestHeadersAllowed = "RequestHeadersAllowed";
    public const string RequestHeaderOriginalHost = "RequestHeaderOriginalHost";
    public const string ResponseHeaderAppend = "ResponseHeaderAppend";
    public const string ResponseHeadersAllowed = "ResponseHeadersAllowed";
    public const string ClientCertHeader = "ClientCertHeader";

    public static bool TryGetRequiredParameters(string kind, out IReadOnlyList<string> required)
    {
        switch (kind)
        {
            case PathRemovePrefix:
            case PathPrefix:
                required = ["prefix"];
                return true;
            case RequestHeaderSet:
            case ResponseHeaderSet:
            case QueryValueSet:
                required = ["name", "value"];
                return true;
            case RequestHeaderRemove:
            case ResponseHeaderRemove:
                required = ["name"];
                return true;
            case RequestHostUseDestination:
            case RequestHeaderOriginalHost:
                required = [];
                return true;
            case PathSet:
                required = ["path"];
                return true;
            case PathPattern:
                required = ["pattern"];
                return true;
            case PathRegexReplace:
                required = ["pattern", "replacement"];
                return true;
            case HttpMethodChange:
                required = ["to"];
                return true;
            case QueryValueAppend:
            case RequestHeaderAppend:
            case ResponseHeaderAppend:
                required = ["name", "value"];
                return true;
            case QueryRemove:
                required = ["name"];
                return true;
            case QueryRouteParameter:
                required = ["name", "routeValue"];
                return true;
            case RequestHeadersAllowed:
            case ResponseHeadersAllowed:
                required = ["headers"];
                return true;
            case ClientCertHeader:
                required = ["name"];
                return true;
            default:
                required = [];
                return false;
        }
    }
}
