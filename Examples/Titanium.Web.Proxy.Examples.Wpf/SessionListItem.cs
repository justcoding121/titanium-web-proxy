using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Wpf.Annotations;

namespace Titanium.Web.Proxy.Examples.Wpf
{
    public class SessionListItem : INotifyPropertyChanged
    {
        private string statusCode;
        private string protocol;
        private string host;
        private string url;
        private long bodySize;
        private string process;
        private long receivedDataCount;
        private long sentDataCount;

        public int Number { get; set; }

        public SessionEventArgs SessionArgs { get; set; }

        public string StatusCode
        {
            get { return statusCode; }
            set { SetField(ref statusCode, value);}
        }

        public string Protocol
        {
            get { return protocol; }
            set { SetField(ref protocol, value); }
        }

        public string Host
        {
            get { return host; }
            set { SetField(ref host, value); }
        }

        public string Url
        {
            get { return url; }
            set { SetField(ref url, value); }
        }

        public long BodySize
        {
            get { return bodySize; }
            set { SetField(ref bodySize, value); }
        }

        public string Process
        {
            get { return process; }
            set { SetField(ref process, value); }
        }

        public long ReceivedDataCount
        {
            get { return receivedDataCount; }
            set { SetField(ref receivedDataCount, value); }
        }

        public long SentDataCount
        {
            get { return sentDataCount; }
            set { SetField(ref sentDataCount, value); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void SetField<T>(ref T field, T value,[CallerMemberName] string propertyName = null)
        {
            field = value;
            OnPropertyChanged(propertyName);
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Update()
        {
            var request = SessionArgs.WebSession.Request;
            var response = SessionArgs.WebSession.Response;
            StatusCode = response?.ResponseStatusCode ?? "-";
            Protocol = request.RequestUri.Scheme;

            if (SessionArgs is TunnelConnectSessionEventArgs)
            {
                Host = "Tunnel to";
                Url = request.RequestUri.Host + ":" + request.RequestUri.Port;
            }
            else
            {
                Host = request.RequestUri.Host;
                Url = request.RequestUri.AbsolutePath;
            }

            BodySize = response?.ContentLength ?? -1;
            Process = GetProcessDescription(SessionArgs.WebSession.ProcessId.Value);
        }

        private string GetProcessDescription(int processId)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(processId);
                return process.ProcessName + ":" + processId;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
