namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>Registers a transform kind the built-in engine does not know.</summary>
public interface IRouteTransformFactory
{
    bool CanHandle(string kind);

    void Apply(TransformConfig transform, TransformRequestContext context);
}
