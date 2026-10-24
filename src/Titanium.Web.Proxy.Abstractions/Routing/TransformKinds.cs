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
                required = [];
                return true;
            default:
                required = [];
                return false;
        }
    }
}
