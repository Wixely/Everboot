using System;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Everboot.Logging;
using Everboot.Services;
using Everboot.Services.Iscsi;
using Everboot.Services.Smb;
using Everboot.Services.Nfs;
using Everboot.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Everboot;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        IHost? host = null;
        try
        {
            if (args.FirstOrDefault() == "--client") return await Services.Vm.VmClient.RunAsync(args);
            if (args.FirstOrDefault() is "--prepare-win98" or "--prepare-win98-pxe") return Services.Vm.Win98Preparation.Run(args);
            host = BuildHost(args);

            var lifetimeLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Everboot");
            HookProcessLevelHandlers(lifetimeLogger);

            lifetimeLogger.LogInformation("Everboot starting (pid {Pid})", Environment.ProcessId);
            await host.RunAsync().ConfigureAwait(false);
            lifetimeLogger.LogInformation("Everboot stopped cleanly");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            var logger = host?.Services.GetService<ILoggerFactory>()?.CreateLogger("Everboot");
            if (logger is not null)
            {
                logger.LogCritical(ex, "Fatal error during startup or run");
            }
            else
            {
                Console.Error.WriteLine($"FATAL: {ex}");
            }
            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static WebApplication BuildHost(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        LoggingSetup.Configure(builder.Logging, builder.Environment);

        builder.Services.AddOptions<EverbootOptions>()
            .Bind(builder.Configuration.GetSection(EverbootOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Nfs.IsValid(), "NFS requires a numeric bind address, three distinct ports (1-65535), and 1-256 connections.")
            .ValidateOnStart();

        builder.Services.AddSingleton<IsoInspector>();
        builder.Services.AddSingleton<BootCatalog>();
        builder.Services.AddSingleton<BootConfigGenerator>();
        builder.Services.AddSingleton<IscsiCatalog>();
        builder.Services.AddSingleton<SmbShareCatalog>();
        builder.Services.AddSingleton<NfsStatus>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ManagementAccess>();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddHostedService<DhcpProxyService>();
        builder.Services.AddHostedService<TftpService>();
        builder.Services.AddSingleton<HttpFileService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<HttpFileService>());
        builder.Services.AddHostedService<IscsiService>();
        builder.Services.AddHostedService<SmbService>();
        builder.Services.AddHostedService<NfsService>();

        var http = builder.Configuration.GetSection(EverbootOptions.SectionName).GetSection("Http").Get<HttpOptions>() ?? new();
        var webHost = http.BindAddress is "+" or "*" ? "0.0.0.0" : http.BindAddress;
        builder.WebHost.UseUrls($"http://{webHost}:{http.Port}");

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        static async Task ServePxe(HttpContext context) =>
            await context.RequestServices.GetRequiredService<HttpFileService>().HandleKestrelAsync(context);
        foreach (var pattern in new[] { "/boot.ipxe", "/boot.cfg", "/catalog.json", "/winpe/{**path}", "/isos/{**path}", "/iso/{**path}", "/dos-source/{**path}", "/files/{**path}" })
            app.MapMethods(pattern, ["GET", "HEAD"], ServePxe);
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return app;
    }

    private static void HookProcessLevelHandlers(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception (terminating={Terminating})", e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
