using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Wpf.Annotations;
using Titanium.Web.Proxy.Http;

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

        public HttpWebClient WebSession { get; set; }

        public bool IsTunnelConnect { get; set; }

        public string StatusCode
        {
            get => statusCode;
            set => SetField(ref statusCode, value);
        }

        public string Protocol
        {
            get => protocol;
            set => SetField(ref protocol, value);
        }

        public string Host
        {
            get => host;
            set => SetField(ref host, value);
        }

        public string Url
        {
            get => url;
            set => SetField(ref url, value);
        }

        public long BodySize
        {
            get => bodySize;
            set => SetField(ref bodySize, value);
        }

        public string Process
        {
            get => process;
            set => SetField(ref process, value);
        }

        public long ReceivedDataCount
        {
            get => receivedDataCount;
            set => SetField(ref receivedDataCount, value);
        }

        public long SentDataCount
        {
            get => sentDataCount;
            set => SetField(ref sentDataCount, value);
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
            var request = WebSession.Request;
            var response = WebSession.Response;
            int statusCode = response?.StatusCode ?? 0;
            StatusCode = statusCode == 0 ? "-" : statusCode.ToString();
            Protocol = request.RequestUri.Scheme;

            if (IsTunnelConnect)
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
            Process = GetProcessDescription(WebSession.ProcessId.Value);
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
