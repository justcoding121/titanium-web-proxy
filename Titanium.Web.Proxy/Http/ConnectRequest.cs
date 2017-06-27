using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectRequest : Request
    {
        public ClientHelloInfo ClientHelloInfo { get; set; }
    }

    public class ClientHelloInfo
    {
        public int MajorVersion { get; set; }

        public int MinorVersion { get; set; }

        public byte[] Random { get; set; }

        public DateTime Time
        {
            get
            {
                DateTime time = DateTime.MinValue;
                if (Random.Length > 3)
                {
                    time = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(((uint)Random[3] << 24) + ((uint)Random[2] << 16) + ((uint)Random[1] << 8) + (uint)Random[0]).ToLocalTime();
                }

                return time;
            }
        }

        public byte[] SessionId { get; set; }

        private static string SslVersionToString(int major, int minor)
        {
            string str = "Unknown";
            if (major == 3 && minor == 3)
                str = "TLS/1.2";
            else if (major == 3 && minor == 2)
                str = "TLS/1.1";
            else if (major == 3 && minor == 1)
                str = "TLS/1.0";
            else if (major == 3 && minor == 0)
                str = "SSL/3.0";
            else if (major == 2 && minor == 0)
                str = "SSL/2.0";

            return $"{major}.{minor} ({str})";
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("A SSLv3-compatible ClientHello handshake was found. Titanium extracted the parameters below.");
            sb.AppendLine();
            sb.AppendLine($"Version: {SslVersionToString(MajorVersion, MinorVersion)}");
            sb.AppendLine($"Random: {string.Join(" ", Random.Select(x => x.ToString("X2")))}");
            sb.AppendLine($"\"Time\": {Time}");
            sb.AppendLine($"SessionID: {string.Join(" ", SessionId.Select(x => x.ToString("X2")))}");

            return sb.ToString();
        }
    }
}
