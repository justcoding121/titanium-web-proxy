using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Reflection;
using HTTPProxyServer.Utility;
using System.Linq;

namespace HTTPProxyServer
{
    public partial class ProxyServer
    {
        private static void URLPeriodFix()
        {
            MethodInfo getSyntax = typeof(UriParser).GetMethod("GetSyntax", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            FieldInfo flagsField = typeof(UriParser).GetField("m_Flags", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (getSyntax != null && flagsField != null)
            {
                foreach (string scheme in new[] { "http", "https" })
                {
                    UriParser parser = (UriParser)getSyntax.Invoke(null, new object[] { scheme });
                    if (parser != null)
                    {
                        int flagsValue = (int)flagsField.GetValue(parser);

                        if ((flagsValue & 0x1000000) != 0)
                            flagsField.SetValue(parser, flagsValue & ~0x1000000);
                    }
                }
            }

        }
        private static List<Tuple<String, String>> ProcessResponse(HttpWebResponse response)
        {
            String value = null;
            String header = null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();
            foreach (String s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }

            return returnHeaders;
        }

        private static void WriteResponseStatus(Version version, HttpStatusCode code, String description, StreamWriter myResponseWriter)
        {
            String s = String.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, (Int32)code, description);
            myResponseWriter.WriteLine(s);

        }

        private static void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<String, String>> headers)
        {
            if (headers != null)
            {
                foreach (Tuple<String, String> header in headers)
                {

                    myResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));

                } 
            }

            myResponseWriter.WriteLine();
            myResponseWriter.Flush();


        }
        private static void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<String, String>> headers, int length)
        {
            if (headers != null)
            {

                foreach (Tuple<String, String> header in headers)
                {
                    if (header.Item1.ToLower() != "content-length")
                        myResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                    else
                        myResponseWriter.WriteLine(String.Format("{0}: {1}", "content-length", length.ToString()));

                }
            }

            myResponseWriter.WriteLine();
            myResponseWriter.Flush();


        }

        //NEED to optimize this call later 
        private static string getHostName(ref Queue<string> requestLines)
        {
            String httpCmd = requestLines.Dequeue();

            while (!String.IsNullOrWhiteSpace(httpCmd))
            {
           
                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                switch (header[0].ToLower())
                {
                    case "host":
                        var hostdetail = header[1];
                        if (hostdetail.Contains(":"))
                            return hostdetail.Split(':')[0].Trim();
                        else
                            return hostdetail.Trim();
                    default:
                        break;
                }
                httpCmd = requestLines.Dequeue();
            }
            return null;
        }

        private static void ReadRequestHeaders(ref List<string> requestLines, HttpWebRequest webReq)
        {
           
            
           for(int i=1; i<requestLines.Count; i++) 
            {
                String httpCmd = requestLines[i];

                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);

                if(!String.IsNullOrEmpty(header[0].Trim()))
                switch (header[0].ToLower())
                {
                    case "accept":
                        webReq.Accept = header[1];
                        break;
                    case "accept-encoding":
                        webReq.Headers.Add(header[0], "gzip,deflate,zlib");
                        break;
                    case "cookie":
                        webReq.Headers["Cookie"] = header[1];
                        break;
                    case "connection":
                        if (header[1].ToLower() == "keep-alive")
                            webReq.KeepAlive = true;                   
                           
                        break;
                    case "content-length":
                        int contentLen;
                        int.TryParse(header[1], out contentLen);
                        if (contentLen != 0)
                            webReq.ContentLength = contentLen;
                        break;
                    case "content-type":
                        webReq.ContentType = header[1];
                        break;
                    case "expect":
                        if (header[1].ToLower() == "100-continue")
                            webReq.ServicePoint.Expect100Continue = true;
                        else
                            webReq.Expect = header[1]; 
                        break;
                    case "host":
                        webReq.Host = header[1];
                        break;
                    case "if-modified-since":
                        String[] sb = header[1].Trim().Split(semiSplit);
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webReq.IfModifiedSince = d;
                        break;
                    case "proxy-connection":
                        break;
                    case "range":
                        var startEnd = header[1].Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                        if (startEnd.Length > 1) { if (!String.IsNullOrEmpty(startEnd[1])) webReq.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1])); else webReq.AddRange(int.Parse(startEnd[0])); }
                        else
                            webReq.AddRange(int.Parse(startEnd[0]));
                        break;
                    case "referer":
                        webReq.Referer = header[1];
                        break;
                    case "user-agent":
                        webReq.UserAgent = header[1];
                        break;
                    case "transfer-encoding":
                        if (header[1].ToLower() == "chunked")
                            webReq.SendChunked = true;
                        else
                            webReq.SendChunked = false;
                        break;
                    case "upgrade":
                        if (header[1].ToLower() == "http/1.1")
                            webReq.Headers.Add(header[0], header[1]);
                        break;
                    
                    default:
                       if(header.Length>0)  
                            webReq.Headers.Add(header[0], header[1]);
                       else
                           webReq.Headers.Add(header[0], "");
                
                        break;
                }
              
               
            }
        
           
        }
    }
}
