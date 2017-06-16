using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    internal class ProxyInfo
    {
        public bool? AutoDetect { get; }

        public string AutoConfigUrl { get; }

        public int? ProxyEnable { get; }

        public string ProxyServer { get; }

        public string ProxyOverride { get; }

        public Dictionary<ProxyProtocolType, HttpSystemProxyValue> Proxies { get; }

        public ProxyInfo(bool? autoDetect, string autoConfigUrl, int? proxyEnable, string proxyServer, string proxyOverride)
        {
            AutoDetect = autoDetect;
            AutoConfigUrl = autoConfigUrl;
            ProxyEnable = proxyEnable;
            ProxyServer = proxyServer;
            ProxyOverride = proxyOverride;

            if (proxyServer != null)
            {
                Proxies = GetSystemProxyValues(proxyServer).ToDictionary(x=>x.ProtocolType);
            }
        }

        public static ProxyProtocolType? ParseProtocolType(string protocolTypeStr)
        {
            if (protocolTypeStr == null)
            {
                return null;
            }

            ProxyProtocolType? protocolType = null;
            if (protocolTypeStr.Equals("http", StringComparison.InvariantCultureIgnoreCase))
            {
                protocolType = ProxyProtocolType.Http;
            }
            else if (protocolTypeStr.Equals("https", StringComparison.InvariantCultureIgnoreCase))
            {
                protocolType = ProxyProtocolType.Https;
            }

            return protocolType;
        }

        /// <summary>
        /// Parse the system proxy setting values
        /// </summary>
        /// <param name="proxyServerValues"></param>
        /// <returns></returns>
        public static List<HttpSystemProxyValue> GetSystemProxyValues(string proxyServerValues)
        {
            var result = new List<HttpSystemProxyValue>();

            if (string.IsNullOrWhiteSpace(proxyServerValues))
                return result;

            var proxyValues = proxyServerValues.Split(';');

            if (proxyValues.Length > 0)
            {
                result.AddRange(proxyValues.Select(ParseProxyValue).Where(parsedValue => parsedValue != null));
            }
            else
            {
                var parsedValue = ParseProxyValue(proxyServerValues);
                if (parsedValue != null)
                    result.Add(parsedValue);
            }

            return result;
        }

        /// <summary>
        /// Parses the system proxy setting string
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static HttpSystemProxyValue ParseProxyValue(string value)
        {
            var tmp = Regex.Replace(value, @"\s+", " ").Trim();

            int equalsIndex = tmp.IndexOf("=", StringComparison.InvariantCulture);
            if (equalsIndex >= 0)
            {
                string protocolTypeStr = tmp.Substring(0, equalsIndex);
                var protocolType = ParseProtocolType(protocolTypeStr);

                if (protocolType.HasValue)
                {
                    var endPointParts = tmp.Substring(equalsIndex + 1).Split(':');
                    return new HttpSystemProxyValue
                    {
                        HostName = endPointParts[0],
                        Port = int.Parse(endPointParts[1]),
                        ProtocolType = protocolType.Value,
                    };
                }
            }

            return null;
        }
    }
}
