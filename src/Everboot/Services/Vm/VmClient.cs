using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var origin = $"http://localhost:{port}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var url = origin + "/" + token + "/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/"); listener.Start();
        using var registration = stop.Token.Register(listener.Stop);
        var running = new List<(Process Process, Task<string> Error)>();
        string? notice = null;
        Console.WriteLine("Everboot VM library: " + url);
        Console.WriteLine("Close guests normally before stopping this launcher. VM disks: " + dataDir);
        if (openBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch { Console.WriteLine("Open the library URL in your browser."); }
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                try
                {
                    var request = context.Request;
                    var response = context.Response;
                    response.Headers["Cache-Control"] = "no-store";
                    response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
                    response.Headers["X-Content-Type-Options"] = "nosniff";
                    response.Headers["Referrer-Policy"] = "same-origin";
                    if (request.Url?.Authority != new Uri(origin).Authority || request.Url.AbsolutePath != "/" + token + "/")
                    { response.StatusCode = 404; continue; }
                    if (request.HttpMethod is not ("GET" or "POST")) { response.StatusCode = 405; continue; }
                    // No shell, arbitrary command, or remote-controlled launch endpoint. Capability URL + same-origin POST.
                    if (request.HttpMethod == "POST" && request.Headers["Origin"] != origin)
                    { response.StatusCode = 403; continue; }
                    foreach (var vm in running.Where(v => v.Process.HasExited).ToArray())
                    {
                        if (vm.Process.ExitCode != 0) notice = "QEMU stopped: " + (await vm.Error)[..Math.Min((await vm.Error).Length, 2000)];
                        else notice = "Virtual machine closed. Your virtual disk is saved.";
                        vm.Process.Dispose(); running.Remove(vm);
                    }
                    VmImage[] images;
                    try
                    {
                        var json = await http.GetStringAsync(new Uri(server, "/catalog.json"), stop.Token);
                        images = JsonSerializer.Deserialize<VmImage[]>(json) ?? [];
                        if (images.Length > 2000) throw new IOException("Catalog is too large.");
                        if (images.Any(i => i is null || string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(i.Path) || i.Size < 512))
                            throw new IOException("The server returned invalid image metadata.");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
                    {
                        await Page(response, [], token, "Cannot load the server library. " + ex.Message, running.Count);
                        continue;
                    }
                    if (request.HttpMethod == "POST")
                    {
                        if (request.ContentLength64 < 0 || request.ContentLength64 > 4096) { response.StatusCode = 413; continue; }
                        using var reader = new StreamReader(request.InputStream);
                        var form = (await reader.ReadToEndAsync(stop.Token)).Split('&')
                            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => WebUtility.UrlDecode(g.Last()[1]));
                        try
                        {
                            if (running.Count > 0) throw new IOException("A virtual machine is already running. Close it before starting another.");
                            if (!form.TryGetValue("image", out var selected) || !int.TryParse(selected, out var index) || index < 0 || index >= images.Length)
                                throw new IOException("The catalog changed. Select an image again.");
                            // Bind selection to the original path, even if the catalog order changed between GET and POST.
                            if (!form.TryGetValue("path", out var path) || images[index].Path != path) throw new IOException("The catalog changed. Select an image again.");
                            var process = await QemuLaunch.StartAsync(server, images[index], form.GetValueOrDefault("profile") ?? "legacy", qemuDir, dataDir, http, stop.Token);
                            running.Add((process, process.StandardError.ReadToEndAsync()));
                            notice = "Virtual machine started. Keep Everboot and the network available. Changes are saved in its local virtual disk.";
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or HttpRequestException or OperationCanceledException)
                        { notice = ex is OperationCanceledException ? "Starting the virtual machine was cancelled or timed out. Check the server and try again." : ex.Message; }
                        response.StatusCode = 303; response.RedirectLocation = url;
                        continue;
                    }
                    await Page(response, images, token, notice, running.Count);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ArgumentException) { Console.Error.WriteLine(ex.Message); }
                finally { context.Response.Close(); }
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            Console.CancelKeyPress -= cancel;
            // Keep the streaming bridge alive until every guest closes normally.
            foreach (var vm in running)
            {
                if (!vm.Process.HasExited) Console.WriteLine("Waiting for the virtual machine to close. Keep this console open to preserve streaming.");
                await vm.Process.WaitForExitAsync(); vm.Process.Dispose();
            }
        }
        return 0;
    }

    internal static string Html(VmImage[] images, string token, string? notice, int active)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value) ?? "";
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Everboot | Run an image</title><style>");
        b.Append(HttpFileService.ThemeCss).Append(".vm-main{max-width:1180px;margin:auto;padding:2rem clamp(1rem,4vw,3rem)}.vm-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,290px),1fr));gap:1rem}.vm-card{min-width:0;padding:1rem;background:linear-gradient(145deg,var(--panel),#202320);border:1px solid var(--line)}.vm-card h2{margin:0 0 .4rem;font-size:1rem}.vm-card form{margin:.8rem 0 0;padding:0;background:transparent;border:0;box-shadow:none}.vm-card select{margin-bottom:.7rem}.vm-toolbar{display:flex;justify-content:space-between;gap:1rem;margin-bottom:1rem;padding:.7rem 1rem;background:#1b1e1c;border:1px solid var(--line);border-left:3px solid var(--amber)}</style></head><body><header class=\"topbar\"><div class=\"top-brand\">EVERBOOT / LOCAL VM</div><div class=\"top-status\"><span><i class=\"status-dot online\"></i>LAUNCHER READY</span></div></header><main class=\"vm-main\"><header class=\"page-head\"><div class=\"page-head-copy\"><div class=\"eyebrow\">Local virtual machine workspace</div><h1>Run an image</h1><p>Your image stays on the server. A virtual PC reads it as needed, including after the operating system starts.</p></div></header>");
        if (notice is not null) b.Append("<p class=\"notice\" role=\"status\">").Append(H(notice)).Append("</p>");
        b.Append("<div class=\"vm-toolbar\"><a href=\"./\">Refresh library and status</a><span>").Append(active).Append(" running</span></div><div class=\"vm-grid\">");
        for (var i = 0; i < images.Length; i++)
        {
            var image = images[i];
            if (image.Problem is not null)
            {
                b.Append("<article class=\"vm-card\"><h2>").Append(H(image.Name)).Append("</h2><p role=\"status\">").Append(H(image.Problem))
                    .Append("</p><small>Ask the server administrator to check this image's boot profile, then refresh.</small></article>");
                continue;
            }
            b.Append("<article class=\"vm-card\"><h2>").Append(H(image.Name)).Append("</h2><small>").Append((image.Size / 1048576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(" MiB on server</small><form method=\"post\" action=\"/").Append(token).Append("/\"><input type=\"hidden\" name=\"image\" value=\"").Append(i).Append("\"><input type=\"hidden\" name=\"path\" value=\"").Append(H(image.Path)).Append("\"><label for=\"profile-").Append(i).Append("\">Virtual PC</label><select name=\"profile\" id=\"profile-").Append(i).Append("\">");
            foreach (var profile in QemuLaunch.Profiles)
                b.Append("<option value=\"").Append(profile).Append('"').Append(profile == image.Profile ? " selected" : "").Append('>').Append(profile switch { "legacy" => "DOS / Windows 9x · 128 MB", "classic" => "Windows 2000 / XP · 512 MB", _ => "Newer systems · 2 GB" }).Append("</option>");
            b.Append("</select><button type=\"submit\"").Append(active > 0 ? " disabled" : "").Append(">Start virtual machine</button></form></article>");
        }
        if (images.Length == 0) b.Append("<p>No images available. Add ISO or IMG files to your Everboot server, then refresh.</p>");
        return b.Append("</div><footer class=\"page-footer\"><p>Virtual disks are saved locally. Original images stay unchanged. Guest networking is off; no physical disks or host folders are attached. Close the VM normally to save your work.</p><small>Experimental compatibility: an image still needs a bootloader and drivers for the virtual PC. Very old installers may require a boot floppy. This launcher requires QEMU on this computer.</small></footer></main></body></html>").ToString();
    }

    private static async Task Page(HttpListenerResponse response, VmImage[] images, string token, string? notice, int active)
    {
        var bytes = Encoding.UTF8.GetBytes(Html(images, token, notice, active));
        response.ContentType = "text/html; charset=utf-8"; response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
