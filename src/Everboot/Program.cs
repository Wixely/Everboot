using System;
using System.Threading;
using System.Threading.Tasks;
using Everboot.Configuration;
using Everboot.Logging;
using Everboot.Services;
using Everboot.Services.Iscsi;
using Everboot.Services.Smb;
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

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        LoggingSetup.Configure(builder.Logging, builder.Environment);

        builder.Services.AddOptions<EverbootOptions>()
            .Bind(builder.Configuration.GetSection(EverbootOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IsoInspector>();
        builder.Services.AddSingleton<BootCatalog>();
        builder.Services.AddSingleton<BootConfigGenerator>();
        builder.Services.AddSingleton<IscsiCatalog>();
        builder.Services.AddSingleton<SmbShareCatalog>();

        builder.Services.AddHostedService<DhcpProxyService>();
        builder.Services.AddHostedService<TftpService>();
        builder.Services.AddHostedService<HttpFileService>();
        builder.Services.AddHostedService<IscsiService>();
        builder.Services.AddHostedService<SmbService>();

        return builder.Build();
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
