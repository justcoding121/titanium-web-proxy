using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using Titanium;
using Titanium.HTTPProxyServer;



namespace Titanium.HTTPProxyServer.Test
{
    public partial class ProxyTestController
    {
        private List<string> _URLList = new List<string>();
        private string _lastURL = string.Empty;

        public int ListeningPort { get; set; }

        public void StartProxy()
        {

            if(Visited!=null)
            {
                ProxyServer.BeforeRequest += OnRequest;
                ProxyServer.BeforeResponse += OnResponse;
            }
          
            ProxyServer.Start();


            SystemProxyUtility.EnableProxyHTTP("localhost", ProxyServer.ListeningPort);
            FireFoxUtility.AddFirefox();

            ListeningPort = ProxyServer.ListeningPort;
 
            Console.WriteLine(String.Format("Proxy listening on local machine port: {0} ",  ProxyServer.ListeningPort));

        }
        public void Stop()
        {
            if (Visited!=null)
            {
                ProxyServer.BeforeRequest -= OnRequest;
                ProxyServer.BeforeResponse -= OnResponse;
            }
            ProxyServer.Stop(); 
        }



        public delegate void SiteVisitedEventHandler(VisitedEventArgs e);
        public event SiteVisitedEventHandler Visited;


        // Invoke the Changed event; called whenever list changes
        protected virtual void OnChanged(VisitedEventArgs e)
        {
            if (Visited != null)
                Visited(e);
        }
        //Test On Request, intecept requests
        //Read browser URL send back to proxy by the injection script in OnResponse event
        public void OnRequest(object sender, SessionEventArgs e)
        {
            string Random = e.requestURL.Substring(e.requestURL.LastIndexOf(@"/") + 1);
            int index = _URLList.IndexOf(Random);
            if (index >= 0)
            {

                string URL = e.decode();

                if (_lastURL != URL)
                {
                    OnChanged(new VisitedEventArgs() { hostname = e.hostName, URL = URL, remoteIP = e.ipAddress, remotePort = e.port });

                }

                e.Ok();
                _lastURL = URL;
            }
        }

        //Test script injection
        //Insert script to read the Browser URL and send it back to proxy
        public void OnResponse(object sender, SessionEventArgs e)
        {
            try
            {


                if (e.proxyRequest.Method == "GET" || e.proxyRequest.Method == "POST")
                {
                    if (e.serverResponse.StatusCode == HttpStatusCode.OK)
                    {
                        if (e.serverResponse.ContentType.Trim().ToLower().Contains("text/html"))
                        {
                            string c = e.serverResponse.GetResponseHeader("X-Requested-With");
                            if (e.serverResponse.GetResponseHeader("X-Requested-With") == "")
                            {
                                e.getResponseBody();

                                string functioname = "fr" + RandomString(10);
                                string VisitedURL = RandomString(5);

                                string RequestVariable = "c" + RandomString(5);
                                string RandomURLEnding = RandomString(25);
                                string RandomLastRequest = RandomString(10);
                                string LocalRequest;

                                if (e.isSecure)
                                    LocalRequest = "https://" + e.hostName + "/" + RandomURLEnding;
                                else
                                    LocalRequest = "http://" + e.hostName + "/" + RandomURLEnding;

                                string script = "var " + RandomLastRequest + " = null;" +
                                 "if(window.top==self) { " + "\n" +
                                  " " + functioname + "();" +
                                 "setInterval(" + functioname + ",500); " + "\n" + "}" +
                                 "function " + functioname + "(){ " + "\n" +
                                 "var " + RequestVariable + " = new XMLHttpRequest(); " + "\n" +
                                 "var " + VisitedURL + " = null;" + "\n" +
                                 "if(window.top.location.href!=null) " + "\n" +
                                 "" + VisitedURL + " = window.top.location.href; else " + "\n" +
                                "" + VisitedURL + " = document.referrer; " +
                                "if(" + RandomLastRequest + "!= " + VisitedURL + ") {" +
                                 RequestVariable + ".open(\"POST\",\"" + LocalRequest + "\", true); " + "\n" +
                                 RequestVariable + ".send(" + VisitedURL + ");} " + RandomLastRequest + " = " + VisitedURL + "}";

                                string response = e.responseString;
                                Regex RE = new Regex("</body>", RegexOptions.RightToLeft | RegexOptions.IgnoreCase | RegexOptions.Multiline);

                                string replaced = RE.Replace(response, "<script type =\"text/javascript\">" + script + "</script></body>", 1);
                                if (replaced.Length != response.Length)
                                {
                                    e.responseString = replaced;
                                    _URLList.Add(RandomURLEnding);

                                }

                            }
                        }
                    }
                }
            }
            catch { }


        }


        private Random random = new Random((int)DateTime.Now.Ticks);
        private string RandomString(int size)
        {
            StringBuilder builder = new StringBuilder();
            char ch;
            for (int i = 0; i < size; i++)
            {
                ch = Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65)));
                builder.Append(ch);
            }

            return builder.ToString();
        }

    }
    public class VisitedEventArgs : EventArgs
    {
        public string URL;
        public string hostname;

        public IPAddress remoteIP { get; set; }
        public int remotePort { get; set; }
    }
}
