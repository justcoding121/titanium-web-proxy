using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.RpsLoadProbe;

// Framework-dependent macOS: make app-local libmsquic visible to QuicListener
// (same reexec as CLI). Without this, H3 arms skip on Darwin ("QuicListener is not supported").
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
var blockedOpenSsl = Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(args);
if (blockedOpenSsl.Count > 0)
{
    Console.Error.WriteLine(Http3NativeBootstrap.FormatBlockedOpenSslMessage(blockedOpenSsl));
    return 1;
}

return Cli.Run(args);
