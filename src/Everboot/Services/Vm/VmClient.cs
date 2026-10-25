using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everboot.Services.Vm;

internal static class VmClient
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var server) ||
            server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0)
        {
            Console.Error.WriteLine("Usage: Everboot --client http://server:8080 [--qemu-dir <folder>] [--vm-data <folder>] [--no-browser]");
            return 2;
        }
        string? qemuDir = null;
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everboot", "machines");
        var openBrowser = true;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--no-browser") { openBrowser = false; continue; }
            if (i + 1 >= args.Length || args[i] is not ("--qemu-dir" or "--vm-data")) throw new ArgumentException("Unknown or incomplete client option: " + args[i]);
            if (args[i] == "--qemu-dir") qemuDir = Path.GetFullPath(args[++i]);
            else dataDir = Path.GetFullPath(args[++i]);
        }

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var state = new VmLauncherState(server, qemuDir, dataDir, stop.Token);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(state);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        await using var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<Web.Vm.VmApp>().AddInteractiveServerRenderMode();
        try
        {
            await app.StartAsync(stop.Token);
            var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var url = origin.TrimEnd('/') + "/";
            Console.WriteLine("Everboot VM library: " + url);
            Console.WriteLine("Close guests normally before stopping this launcher. VM disks: " + dataDir);
            if (openBrowser)
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
                catch { Console.WriteLine("Open the library URL in your browser."); }
            }
            try { await app.WaitForShutdownAsync(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            await app.StopAsync();
            await state.DisposeAsync();
        }
        return 0;
    }
}
