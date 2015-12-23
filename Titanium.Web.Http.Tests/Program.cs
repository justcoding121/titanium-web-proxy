using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Http.Tests
{
    class Program
    {
        static void Main(string[] args)
        {
            var t = Task.Factory.StartNew(Test);
            t.Wait();
            Console.Read();
        }

        public static async void Test()
        {
            var s = new HttpClient
            {
                Method = "GET",
                Uri = new Uri("https://google.com"),
                Version = "HTTP/1.1"
            };

            s.RequestHeaders.Add(new HttpHeader("Host", s.Uri.Host));

            await s.SendRequest();
            await s.ReceiveResponse();
        }
    }
}
