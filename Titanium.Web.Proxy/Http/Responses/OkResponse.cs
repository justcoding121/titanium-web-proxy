namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// 200 Ok response
    /// </summary>
    public sealed class OkResponse : Response
    {
        public OkResponse()
        {
            ResponseStatusCode = "200";
            ResponseStatusDescription = "Ok";
        }
    }
}
