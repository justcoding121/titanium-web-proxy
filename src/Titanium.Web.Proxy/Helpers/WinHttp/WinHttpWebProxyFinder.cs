using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers.WinHttp;

internal sealed class WinHttpWebProxyFinder : IDisposable
{
    private readonly WinHttpHandle session;
    private bool autoDetectFailed;

    private bool disposed;
    private AutoWebProxyState state;

    public WinHttpWebProxyFinder()
    {
        session = NativeMethods.WinHttp.WinHttpOpen(null, NativeMethods.WinHttp.AccessType.NoProxy, null, null, 0);
        if (session == null || session.IsInvalid)
        {
            var lastWin32Error = GetLastWin32Error();
        }
        else
        {
            var downloadTimeout = 60 * 1000;
            if (NativeMethods.WinHttp.WinHttpSetTimeouts(session, downloadTimeout, downloadTimeout, downloadTimeout,
                    downloadTimeout))
                return;

            var lastWin32Error = GetLastWin32Error();
        }
    }

    public ICredentials? Credentials { get; set; }

    public ProxyInfo? ProxyInfo { get; internal set; }

    public bool BypassLoopback { get; internal set; }

    public bool BypassOnLocal { get; internal set; }

    public Uri? AutomaticConfigurationScript { get; internal set; }

    public bool AutomaticallyDetectSettings { get; internal set; }

    private WebProxy? Proxy { get; set; }

    public void Dispose()
    {
        if (disposed) return;

        if (session == null || session.IsInvalid) return;

        session.Close();

        disposed = true;
    }

    public bool GetAutoProxies(Uri destination, out IList<string>? proxyList)
    {
        proxyList = null;
        if (session == null || session.IsInvalid || state == AutoWebProxyState.UnrecognizedScheme) return false;

        string? proxyListString = null;
        var errorCode = NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed;
        if (AutomaticallyDetectSettings && !autoDetectFailed)
        {
            errorCode = (NativeMethods.WinHttp.ErrorCodes)GetAutoProxies(destination, null, out proxyListString);
            autoDetectFailed = IsErrorFatalForAutoDetect(errorCode);
            if (errorCode == NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme)
            {
                state = AutoWebProxyState.UnrecognizedScheme;
                return false;
            }
        }

        if (AutomaticConfigurationScript != null && IsRecoverableAutoProxyError(errorCode))
            errorCode = (NativeMethods.WinHttp.ErrorCodes)GetAutoProxies(destination, AutomaticConfigurationScript,
                out proxyListString);

        state = GetStateFromErrorCode(errorCode);
        if (state != AutoWebProxyState.Completed) return false;

        if (!string.IsNullOrEmpty(proxyListString))
        {
            proxyListString = RemoveWhitespaces(proxyListString!);
            proxyList = proxyListString.Split(';');
        }

        return true;
    }

    public IExternalProxy? GetProxy(Uri destination)
    {
        if (GetAutoProxies(destination, out var proxies))
        {
            if (proxies == null) return null;

            var proxyStr = proxies[0];
            var port = 80;
            if (proxyStr.Contains(":"))
            {
                var parts = proxyStr.Split(new[] { ':' }, 2);
                proxyStr = parts[0];
                port = int.Parse(parts[1]);
            }

            // TODO: Apply authorization
            var systemProxy = new ExternalProxy(proxyStr, port);

            return systemProxy;
        }

        if (Proxy?.IsBypassed(destination) == true) return null;

        var protocolType = ProxyInfo.ParseProtocolType(destination.Scheme);
        if (protocolType.HasValue)
        {
            HttpSystemProxyValue? value = null;
            if (ProxyInfo?.Proxies?.TryGetValue(protocolType.Value, out value) == true)
            {
                var systemProxy = new ExternalProxy(value!.HostName, value.Port);
                return systemProxy;
            }
        }

        return null;
    }

    public void LoadFromIe()
    {
        var pi = GetProxyInfo();
        ProxyInfo = pi;
        AutomaticallyDetectSettings = pi.AutoDetect == true;
        AutomaticConfigurationScript = pi.AutoConfigUrl == null ? null : new Uri(pi.AutoConfigUrl);
        BypassLoopback = pi.BypassLoopback;
        BypassOnLocal = pi.BypassOnLocal;
        Proxy = new WebProxy(new Uri("http://localhost"), BypassOnLocal, pi.BypassList);
    }

    internal void UsePacFile(Uri upstreamProxyConfigurationScript)
    {
        AutomaticallyDetectSettings = true;
        AutomaticConfigurationScript = upstreamProxyConfigurationScript;
        BypassLoopback = true;
        BypassOnLocal = false;
        Proxy = new WebProxy(new Uri("http://localhost"), BypassOnLocal);
    }

    private ProxyInfo GetProxyInfo()
    {
        var proxyConfig = new NativeMethods.WinHttp.WinhttpCurrentUserIeProxyConfig();
        RuntimeHelpers.PrepareConstrainedRegions();
        try
        {
            ProxyInfo result;
            if (NativeMethods.WinHttp.WinHttpGetIEProxyConfigForCurrentUser(ref proxyConfig))
            {
                result = new ProxyInfo(
                    proxyConfig.AutoDetect,
                    Marshal.PtrToStringUni(proxyConfig.AutoConfigUrl),
                    null,
                    Marshal.PtrToStringUni(proxyConfig.Proxy),
                    Marshal.PtrToStringUni(proxyConfig.ProxyBypass));
            }
            else
            {
                if (Marshal.GetLastWin32Error() == 8) throw new OutOfMemoryException();

                result = new ProxyInfo(true, null, null, null, null);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(proxyConfig.Proxy);
            Marshal.FreeHGlobal(proxyConfig.ProxyBypass);
            Marshal.FreeHGlobal(proxyConfig.AutoConfigUrl);
        }
    }

    public void Reset()
    {
        state = AutoWebProxyState.Uninitialized;
        autoDetectFailed = false;
    }

    private int GetAutoProxies(Uri destination, Uri? scriptLocation, out string? proxyListString)
    {
        var num = 0;
        var autoProxyOptions = new NativeMethods.WinHttp.WinhttpAutoproxyOptions();
        autoProxyOptions.AutoLogonIfChallenged = false;
        if (scriptLocation == null)
        {
            autoProxyOptions.Flags = NativeMethods.WinHttp.AutoProxyFlags.AutoDetect;
            autoProxyOptions.AutoConfigUrl = null;
            autoProxyOptions.AutoDetectFlags =
                NativeMethods.WinHttp.AutoDetectType.Dhcp | NativeMethods.WinHttp.AutoDetectType.DnsA;
        }
        else
        {
            autoProxyOptions.Flags = NativeMethods.WinHttp.AutoProxyFlags.AutoProxyConfigUrl;
            autoProxyOptions.AutoConfigUrl = scriptLocation.ToString();
            autoProxyOptions.AutoDetectFlags = NativeMethods.WinHttp.AutoDetectType.None;
        }

        if (!WinHttpGetProxyForUrl(destination.ToString(), ref autoProxyOptions, out proxyListString))
        {
            num = GetLastWin32Error();

            if (num == (int)NativeMethods.WinHttp.ErrorCodes.LoginFailure && Credentials != null)
            {
                autoProxyOptions.AutoLogonIfChallenged = true;
                if (!WinHttpGetProxyForUrl(destination.ToString(), ref autoProxyOptions, out proxyListString))
                    num = GetLastWin32Error();
            }
        }

        return num;
    }

    private bool WinHttpGetProxyForUrl(string destination,
        ref NativeMethods.WinHttp.WinhttpAutoproxyOptions autoProxyOptions, out string? proxyListString)
    {
        proxyListString = null;
        bool flag;
        var proxyInfo = new NativeMethods.WinHttp.WinhttpProxyInfo();
        RuntimeHelpers.PrepareConstrainedRegions();
        try
        {
            flag = NativeMethods.WinHttp.WinHttpGetProxyForUrl(session, destination, ref autoProxyOptions,
                out proxyInfo);
            if (flag) proxyListString = Marshal.PtrToStringUni(proxyInfo.Proxy);
        }
        finally
        {
            Marshal.FreeHGlobal(proxyInfo.Proxy);
            Marshal.FreeHGlobal(proxyInfo.ProxyBypass);
        }

        return flag;
    }

    private static int GetLastWin32Error()
    {
        var lastWin32Error = Marshal.GetLastWin32Error();
        if (lastWin32Error == 8) throw new OutOfMemoryException();

        return lastWin32Error;
    }

    private static bool IsRecoverableAutoProxyError(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed:
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
            case NativeMethods.WinHttp.ErrorCodes.UnableToDownloadScript:
            case NativeMethods.WinHttp.ErrorCodes.LoginFailure:
            case NativeMethods.WinHttp.ErrorCodes.OperationCancelled:
            case NativeMethods.WinHttp.ErrorCodes.Timeout:
            case NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme:
                return true;
            default:
                return false;
        }
    }

    private static AutoWebProxyState GetStateFromErrorCode(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        if (errorCode == 0L) return AutoWebProxyState.Completed;

        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.UnableToDownloadScript:
                return AutoWebProxyState.DownloadFailure;
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.InvalidUrl:
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
                return AutoWebProxyState.Completed;
            case NativeMethods.WinHttp.ErrorCodes.AudodetectionFailed:
                return AutoWebProxyState.DiscoveryFailure;
            case NativeMethods.WinHttp.ErrorCodes.UnrecognizedScheme:
                return AutoWebProxyState.UnrecognizedScheme;
            default:
                return AutoWebProxyState.CompilationFailure;
        }
    }

    private static string RemoveWhitespaces(string value)
    {
        var stringBuilder = new StringBuilder();
        foreach (var c in value)
            if (!char.IsWhiteSpace(c))
                stringBuilder.Append(c);

        return stringBuilder.ToString();
    }

    private static bool IsErrorFatalForAutoDetect(NativeMethods.WinHttp.ErrorCodes errorCode)
    {
        switch (errorCode)
        {
            case NativeMethods.WinHttp.ErrorCodes.BadAutoProxyScript:
            case NativeMethods.WinHttp.ErrorCodes.AutoProxyServiceError:
            case NativeMethods.WinHttp.ErrorCodes.Success:
            case NativeMethods.WinHttp.ErrorCodes.InvalidUrl:
                return false;
            default:
                return true;
        }
    }

    private enum AutoWebProxyState
    {
        Uninitialized,
        DiscoveryFailure,
        DownloadFailure,
        CompilationFailure,
        UnrecognizedScheme,
        Completed
    }
}