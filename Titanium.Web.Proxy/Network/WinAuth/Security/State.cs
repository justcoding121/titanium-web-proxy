﻿#if NET45
using System;

namespace Titanium.Web.Proxy.Network.WinAuth.Security
{
    /// <summary>
    /// Status of authenticated session
    /// </summary>
    internal class State
    {
        internal State()
        {
            Credentials = new Common.SecurityHandle(0);
            Context = new Common.SecurityHandle(0);

            LastSeen = DateTime.Now;
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

        internal void ResetHandles()
        {
            Credentials.Reset();
            Context.Reset();
        }

        internal void UpdatePresence()
        {
            LastSeen = DateTime.Now;
        }
    }
}
#endif
