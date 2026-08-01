using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Wpf.Annotations;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Examples.Wpf
{
    public class SessionListItem : INotifyPropertyChanged
    {
        private long? bodySize;
        private long clientConnectionId;
        private Exception exception;
        private string host;
        private int processId;
        private string protocol;
        private long receivedDataCount;
        private long sentDataCount;
        private long serverConnectionId;
        private string statusCode;
        private string url;

        public int Number { get; set; }

        public long ClientConnectionId
        {
            get => clientConnectionId;
            set => SetField(ref clientConnectionId, value);
        }

        public long ServerConnectionId
        {
            get => serverConnectionId;
            set => SetField(ref serverConnectionId, value);
        }

        public HttpWebClient HttpClient { get; set; }

        public IPEndPoint ClientLocalEndPoint { get; set; }

        public IPEndPoint ClientRemoteEndPoint { get; set; }

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

        public long? BodySize
        {
            get => bodySize;
            set => SetField(ref bodySize, value);
        }

        public int ProcessId
        {
            get => processId;
            set
            {
                if (SetField(ref processId, value)) OnPropertyChanged(nameof(Process));
            }
        }

        public string Process
        {
            get
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

        public Exception Exception
        {
            get => exception;
            set => SetField(ref exception, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        ///     Brief client↔proxy | proxy↔server label (e.g. "HTTP/2 ↔ HTTP/3").
        /// </summary>
        private static string FormatClientServerProtocol(Version clientVersion, Version serverVersion)
        {
            var client = FormatHttpProtocol(clientVersion);
            var server = FormatHttpProtocol(serverVersion);
            if (server == "unknown")
                return client;

            return client + " ↔ " + server;
        }

        private static string FormatHttpProtocol(Version version)
        {
            if (version == null || version.Major == 0)
                return "unknown";

            if (version.Major >= 2)
                return "HTTP/" + version.Major;

            return "HTTP/" + version.Major + "." + version.Minor;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            return false;
        }

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Update(SessionEventArgsBase args)
        {
            var request = HttpClient.Request;
            var response = HttpClient.Response;
            var statusCode = response?.StatusCode ?? 0;
            StatusCode = statusCode == 0 ? "-" : statusCode.ToString();
            // e.g. "HTTP/2 ↔ HTTP/3" (client↔proxy | proxy↔server).
            Protocol = FormatClientServerProtocol(request.HttpVersion, response?.HttpVersion);
            ClientConnectionId = args.ClientConnectionId;
            ServerConnectionId = args.ServerConnectionId;

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

            if (!IsTunnelConnect)
            {
                long responseSize = -1;
                if (response != null)
                {
                    if (response.ContentLength != -1)
                        responseSize = response.ContentLength;
                    else if (response.IsBodyRead && response.Body != null) responseSize = response.Body.Length;
                }

                BodySize = responseSize;
            }

            ProcessId = HttpClient.ProcessId.Value;
        }
    }
}