using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

internal class ProxyInfo
{
    internal ProxyInfo(bool? autoDetect, string? autoConfigUrl, int? proxyEnable, string? proxyServer,
        string? proxyOverride)
    {
        AutoDetect = autoDetect;
        AutoConfigUrl = autoConfigUrl;
        ProxyEnable = proxyEnable;
        ProxyServer = proxyServer;
        ProxyOverride = proxyOverride;

        if (proxyServer != null) Proxies = GetSystemProxyValues(proxyServer).ToDictionary(x => x.ProtocolType);

        if (proxyOverride != null)
        {
            var overrides = proxyOverride.Split(';');
            var overrides2 = new List<string>();
            foreach (var overrideHost in overrides)
                if (overrideHost == "<-loopback>")
                    BypassLoopback = true;
                else if (overrideHost == "<local>")
                    BypassOnLocal = true;
                else
                    overrides2.Add(BypassStringEscape(overrideHost));

            if (overrides2.Count > 0) BypassList = overrides2.ToArray();

            Proxies = GetSystemProxyValues(proxyServer).ToDictionary(x => x.ProtocolType);
        }
    }

    internal bool? AutoDetect { get; }

    internal string? AutoConfigUrl { get; }

    internal int? ProxyEnable { get; }

    internal string? ProxyServer { get; }

    internal string? ProxyOverride { get; }

    internal bool BypassLoopback { get; }

    internal bool BypassOnLocal { get; }

    internal Dictionary<ProxyProtocolType, HttpSystemProxyValue>? Proxies { get; }

    internal string[]? BypassList { get; }

    private static string BypassStringEscape(string rawString)
    {
        var match =
            new Regex("^(?<scheme>.*://)?(?<host>[^:]*)(?<port>:[0-9]{1,5})?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Match(rawString);
        string empty1;
        string rawString1;
        string empty2;
        if (match.Success)
        {
            empty1 = match.Groups["scheme"].Value;
            rawString1 = match.Groups["host"].Value;
            empty2 = match.Groups["port"].Value;
        }
        else
        {
            empty1 = string.Empty;
            rawString1 = rawString;
            empty2 = string.Empty;
        }

        var str1 = ConvertRegexReservedChars(empty1);
        var str2 = ConvertRegexReservedChars(rawString1);
        var str3 = ConvertRegexReservedChars(empty2);
        if (str1 == string.Empty) str1 = "(?:.*://)?";

        if (str3 == string.Empty) str3 = "(?::[0-9]{1,5})?";

        return "^" + str1 + str2 + str3 + "$";
    }

    private static string ConvertRegexReservedChars(string rawString)
    {
        if (rawString.Length == 0) return rawString;

        var stringBuilder = new StringBuilder();
        foreach (var ch in rawString)
        {
            if ("#$()+.?[\\^{|".IndexOf(ch) != -1)
                stringBuilder.Append('\\');
            else if (ch == 42) stringBuilder.Append('.');

            stringBuilder.Append(ch);
        }

        return stringBuilder.ToString();
    }

    internal static ProxyProtocolType? ParseProtocolType(string protocolTypeStr)
    {
        if (protocolTypeStr == null) return null;

        ProxyProtocolType? protocolType = null;
        if (protocolTypeStr.Equals(Proxy.ProxyServer.UriSchemeHttp, StringComparison.InvariantCultureIgnoreCase))
            protocolType = ProxyProtocolType.Http;
        else if (protocolTypeStr.Equals(Proxy.ProxyServer.UriSchemeHttps,
                     StringComparison.InvariantCultureIgnoreCase))
            protocolType = ProxyProtocolType.Https;

        return protocolType;
    }

    /// <summary>
    ///     Parse the system proxy setting values
    /// </summary>
    /// <param name="proxyServerValues"></param>
    /// <returns></returns>
    internal static List<HttpSystemProxyValue> GetSystemProxyValues(string? proxyServerValues)
    {
        var result = new List<HttpSystemProxyValue>();

        if (string.IsNullOrWhiteSpace(proxyServerValues)) return result;

        var proxyValues = proxyServerValues!.Split(';');

        if (proxyValues.Length > 0)
        {
            foreach (var str in proxyValues)
            {
                var proxyValue = ParseProxyValue(str);
                if (proxyValue != null) result.Add(proxyValue);
            }
        }
        else
        {
            var parsedValue = ParseProxyValue(proxyServerValues);
            if (parsedValue != null) result.Add(parsedValue);
        }

        return result;
    }

    /// <summary>
    ///     Parses the system proxy setting string
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static HttpSystemProxyValue? ParseProxyValue(string value)
    {
        var tmp = Regex.Replace(value, @"\s+", " ").Trim();

        var equalsIndex = tmp.IndexOf("=", StringComparison.InvariantCulture);
        if (equalsIndex >= 0)
        {
            var protocolTypeStr = tmp.Substring(0, equalsIndex);
            var protocolType = ParseProtocolType(protocolTypeStr);

            if (protocolType.HasValue)
            {
                var endPointParts = tmp.Substring(equalsIndex + 1).Split(':');
                return new HttpSystemProxyValue(endPointParts[0], int.Parse(endPointParts[1]), protocolType.Value);
            }
        }

        return null;
    }
}