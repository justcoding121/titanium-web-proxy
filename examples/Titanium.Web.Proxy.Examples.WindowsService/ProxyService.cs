using System;
using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Models;
using WindowsServiceExample.Properties;

namespace WindowsServiceExample
{
    internal partial class ProxyService : ServiceBase
    {
        private static ProxyServer _proxyServerInstance;

        public ProxyService()
        {
            InitializeComponent();
            AppDomain.CurrentDomain.UnhandledException += UnhandledDomainException;
        }

        protected override void OnStart(string[] args)
        {
            // we do all this in here so we can reload settings with a simple restart

            _proxyServerInstance = new ProxyServer(false);

            if (Settings.Default.ListeningPort <= 0 ||
                Settings.Default.ListeningPort > 65535)
                throw new Exception("Invalid listening port");

            _proxyServerInstance.CheckCertificateRevocation = Settings.Default.CheckCertificateRevocation;
            _proxyServerInstance.ConnectionTimeOutSeconds = Settings.Default.ConnectionTimeOutSeconds;
            _proxyServerInstance.Enable100ContinueBehaviour = Settings.Default.Enable100ContinueBehaviour;
            _proxyServerInstance.EnableConnectionPool = Settings.Default.EnableConnectionPool;
            _proxyServerInstance.EnableTcpServerConnectionPrefetch = Settings.Default.EnableTcpServerConnectionPrefetch;
            _proxyServerInstance.EnableWinAuth = Settings.Default.EnableWinAuth;
            _proxyServerInstance.ForwardToUpstreamGateway = Settings.Default.ForwardToUpstreamGateway;
            _proxyServerInstance.MaxCachedConnections = Settings.Default.MaxCachedConnections;
            _proxyServerInstance.ReuseSocket = Settings.Default.ReuseSocket;
            _proxyServerInstance.TcpTimeWaitSeconds = Settings.Default.TcpTimeWaitSeconds;
            _proxyServerInstance.CertificateManager.SaveFakeCertificates = Settings.Default.SaveFakeCertificates;
            _proxyServerInstance.EnableHttp2 = Settings.Default.EnableHttp2;
            _proxyServerInstance.NoDelay = Settings.Default.NoDelay;

            if (Settings.Default.ThreadPoolWorkerThreads < 0)
                _proxyServerInstance.ThreadPoolWorkerThread = Environment.ProcessorCount;
            else
                _proxyServerInstance.ThreadPoolWorkerThread = Settings.Default.ThreadPoolWorkerThreads;

            if (Settings.Default.ThreadPoolWorkerThreads < Environment.ProcessorCount)
                ProxyServiceEventLog.WriteEntry(
                    $"Worker thread count of {Settings.Default.ThreadPoolWorkerThreads} is below the " +
                    $"processor count of {Environment.ProcessorCount}. This may be on purpose.",
                    EventLogEntryType.Warning);

            var explicitEndPointV4 = new ExplicitProxyEndPoint(IPAddress.Any, Settings.Default.ListeningPort,
                Settings.Default.DecryptSsl);

            _proxyServerInstance.AddEndPoint(explicitEndPointV4);

            if (Settings.Default.EnableIpV6)
            {
                var explicitEndPointV6 = new ExplicitProxyEndPoint(IPAddress.IPv6Any, Settings.Default.ListeningPort,
                    Settings.Default.DecryptSsl);

                _proxyServerInstance.AddEndPoint(explicitEndPointV6);
            }

            if (Settings.Default.LogErrors)
                _proxyServerInstance.ExceptionFunc = ProxyException;

            _proxyServerInstance.Start();

            ProxyServiceEventLog.WriteEntry($"Service Listening on port {Settings.Default.ListeningPort}",
                EventLogEntryType.Information);
        }

        protected override void OnStop()
        {
            _proxyServerInstance.Stop();

            // clean up here since we make a new instance when starting
            _proxyServerInstance.Dispose();
        }

        private void ProxyException(Exception exception)
        {
            string message;
            if (exception is ProxyHttpException pEx)
                message =
                    $"Unhandled Proxy Exception in ProxyServer, UserData = {pEx.Session?.UserData}, URL = {pEx.Session?.HttpClient.Request.RequestUri} Exception = {pEx}";
            else
                message = $"Unhandled Exception in ProxyServer, Exception = {exception}";

            ProxyServiceEventLog.WriteEntry(message, EventLogEntryType.Error);
        }

        private void UnhandledDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            ProxyServiceEventLog.WriteEntry($"Unhandled Exception in AppDomain, Exception = {e}",
                EventLogEntryType.Error);
        }
    }
}