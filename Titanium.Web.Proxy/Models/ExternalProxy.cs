namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// An upstream proxy this proxy uses if any
    /// </summary>
    public class ExternalProxy
    {
        public string HostName { get; set; }
        public int Port { get; set; }
    }
}
