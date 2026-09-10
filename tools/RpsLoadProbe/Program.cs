using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.RpsLoadProbe;

// Framework-dependent macOS: make app-local libmsquic visible to QuicListener
// (same reexec as CLI). Without this, H3 arms skip on Darwin ("QuicListener is not supported").
Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(args);
return Cli.Run(args);
