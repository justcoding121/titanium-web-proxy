using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Titanium.Web.Proxy.Examples.WindowsService;

// File-first logging: rolling files get Information+; Event Log is Warning+ so request volume
// does not flood Event Viewer. Console is useful when run interactively (`dotnet run`).
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);

#if DEBUG
const LogEventLevel minimumLevel = LogEventLevel.Verbose;
#else
const LogEventLevel minimumLevel = LogEventLevel.Warning;
#endif

const string timestampedTemplate =
    "{Timestamp:yyyy-MM-dd h:mm:ss.fff tt} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    // Console/file I/O on a background worker so LogInformation from a session thread never blocks.
    .WriteTo.Async(a => a.Console(outputTemplate: timestampedTemplate))
    .WriteTo.Async(a => a.File(
        path: Path.Combine(logDirectory, "proxy-service.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: 5,
        shared: true,
        outputTemplate: timestampedTemplate))
    .WriteTo.Async(a => a.EventLog(
        source: "Titanium Web Proxy",
        logName: "Application",
        manageEventSource: false,
        restrictedToMinimumLevel: LogEventLevel.Warning))
    .CreateLogger();

try
{
    try
    {
        Console.Title = "Titanium Web Proxy";
    }
    catch (IOException)
    {
        // No console when running under the Service Control Manager.
    }

    var builder = Host.CreateApplicationBuilder(args);

    // Registers this process as a Windows Service host when launched by the Service Control Manager
    // (falls back to a normal console app when run interactively, e.g. `dotnet run`).
    // Must match the service Name in install.ps1 / remove.ps1.
    builder.Services.AddWindowsService(options => options.ServiceName = "TitaniumWebProxy");

    // Serilog owns all sinks (file, Event Log Warning+, console); default Microsoft logging providers are cleared.
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();
    builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("ProxySettings"));
    builder.Services.AddHostedService<ProxyWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Proxy service terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
