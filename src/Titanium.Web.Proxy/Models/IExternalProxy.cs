namespace Titanium.Web.Proxy.Models
{
    public interface IExternalProxy
    {
        /// <summary>
        ///     Use default windows credentials?
        /// </summary>
        public bool UseDefaultCredentials { get; set; }

        /// <summary>
        ///     Bypass this proxy for connections to localhost?
        /// </summary>
        public bool BypassLocalhost { get; set; }

        /// <summary>
        ///     Username.
        /// </summary>
        public string? UserName { get; set; }

        /// <summary>
        ///     Password.
        /// </summary>
        string? Password { get; set; }

        /// <summary>
        ///     Host name.
        /// </summary>
        string HostName { get; set; }

        /// <summary>
        ///     Port.
        /// </summary>
        int Port { get; set; }

        string ToString();
    }
}
