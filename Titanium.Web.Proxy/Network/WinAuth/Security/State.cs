namespace Titanium.Web.Proxy.Network.WinAuth.Security
{
    using System;

    /// <summary>
    /// Status of authenticated session
    /// </summary>
    internal class State
    {
        internal State()
        {
            this.Credentials = new Common.SecurityHandle(0);
            this.Context = new Common.SecurityHandle(0);

            this.LastSeen = DateTime.MinValue;
        }

        /// <summary>
        /// Credentials used to validate NTLM hashes
        /// </summary>
        internal Common.SecurityHandle Credentials;

        /// <summary>
        /// Context will be used to validate HTLM hashes
        /// </summary>
        internal Common.SecurityHandle Context;

        /// <summary>
        /// Timestamp needed to calculate validity of the authenticated session
        /// </summary>
        internal DateTime LastSeen;

        internal bool isOlder(int seconds)
        {
            return (this.LastSeen.AddSeconds(seconds) < DateTime.UtcNow) ? true : false;
        }

        internal void ResetHandles()
        {
            this.Credentials.Reset();
            this.Context.Reset();
        }

        internal void UpdatePresence()
        {
            this.LastSeen = DateTime.UtcNow;
        }
    }
}
