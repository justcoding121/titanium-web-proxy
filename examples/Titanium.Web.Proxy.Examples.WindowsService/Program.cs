using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Examples.WindowsService;

var builder = Host.CreateApplicationBuilder(args);

// Registers this process as a Windows Service host when launched by the Service Control Manager
// (falls back to a normal console app when run interactively, e.g. `dotnet run`).
builder.Services.AddWindowsService(options => options.ServiceName = "ProxyService");

builder.Logging.AddEventLog(options =>
{
    options.SourceName = "ProxyService";
    options.LogName = "Application";
});

builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("ProxySettings"));
builder.Services.AddHostedService<ProxyWorker>();

var host = builder.Build();
host.Run();
