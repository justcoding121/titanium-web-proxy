using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.DirectoryServices.AccountManagement;
using System.DirectoryServices.ActiveDirectory;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Helpers;



namespace Titanium.Web.Proxy.Test
{
    public partial class ProxyTestController
    {

        public int ListeningPort { get; set; }
        public bool EnableSSL { get; set; }
        public bool SetAsSystemProxy { get; set; }

        public void StartProxy()
        {


            ProxyServer.BeforeRequest += OnRequest;
            ProxyServer.BeforeResponse += OnResponse;


            ProxyServer.EnableSSL = EnableSSL;

            ProxyServer.SetAsSystemProxy = SetAsSystemProxy;


            ProxyServer.Start();


            ListeningPort = ProxyServer.ListeningPort;

            Console.WriteLine(String.Format("Proxy listening on local machine port: {0} ", ProxyServer.ListeningPort));
           
        }
        public void Stop()
        {
            ProxyServer.BeforeRequest -= OnRequest;
            ProxyServer.BeforeResponse -= OnResponse;

            ProxyServer.Stop();
        }



        //Test On Request, intecept requests
        //Read browser URL send back to proxy by the injection script in OnResponse event
        public void OnRequest(object sender, SessionEventArgs e)
        {

            Console.WriteLine(e.RequestURL);

        }

        //Test script injection
        //Insert script to read the Browser URL and send it back to proxy
        public void OnResponse(object sender, SessionEventArgs e)
        {



        }

    }

}
