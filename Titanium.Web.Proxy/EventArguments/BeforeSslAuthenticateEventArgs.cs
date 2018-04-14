using System;
using System.Threading;

namespace Titanium.Web.Proxy.EventArguments
{
    public class BeforeSslAuthenticateEventArgs : EventArgs
    {
        internal CancellationTokenSource TaskCancellationSource;

        internal BeforeSslAuthenticateEventArgs(CancellationTokenSource taskCancellationSource)
        {
            this.TaskCancellationSource = taskCancellationSource;
        }

        public string SniHostName { get; internal set; }

        public bool DecryptSsl { get; set; } = true;

        public void TerminateSession()
        {
            TaskCancellationSource.Cancel();
        }
    }
}
