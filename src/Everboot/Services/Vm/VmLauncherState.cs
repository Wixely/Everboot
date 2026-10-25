using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Everboot.Services.Vm;

internal sealed class VmLauncherState : IAsyncDisposable
{
    private readonly Uri _server;
    private readonly string? _qemuDirectory;
    private readonly string _dataDirectory;
    private readonly CancellationToken _stopping;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<(Process Process, Task<string> Error)> _running = [];

    public VmLauncherState(Uri server, string? qemuDirectory, string dataDirectory, CancellationToken stopping)
    { _server = server; _qemuDirectory = qemuDirectory; _dataDirectory = dataDirectory; _stopping = stopping; }

    public VmImage[] Images { get; private set; } = [];
    public string? Notice { get; private set; }
    public bool NoticeIsError { get; private set; }
    public int Active => _running.Count;

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync(_stopping);
        try
        {
            await PollCoreAsync();
            var json = await _http.GetStringAsync(new Uri(_server, "/catalog.json"), _stopping);
            var images = JsonSerializer.Deserialize<VmImage[]>(json) ?? [];
            if (images.Length > 2000 || images.Any(i => i is null || string.IsNullOrEmpty(i.Name) || string.IsNullOrEmpty(i.Path) || i.Size < 512))
                throw new IOException("The server returned invalid image metadata.");
            Images = images;
            if (NoticeIsError && Notice?.StartsWith("Cannot load", StringComparison.Ordinal) == true) { Notice = null; NoticeIsError = false; }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException)
        { Images = []; Notice = "Cannot load the server library. " + ex.Message; NoticeIsError = true; }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(string path, string profile)
    {
        await _gate.WaitAsync(_stopping);
        try
        {
            await PollCoreAsync();
            if (_running.Count > 0) throw new IOException("A virtual machine is already running. Close it before starting another.");
            if (!QemuLaunch.Profiles.Contains(profile)) throw new ArgumentException("Unknown virtual PC profile.");
            var image = Images.SingleOrDefault(i => i.Path == path) ?? throw new IOException("The catalog changed. Refresh and select the image again.");
            if (image.Problem is not null) throw new IOException(image.Problem);
            var process = await QemuLaunch.StartAsync(_server, image, profile, _qemuDirectory, _dataDirectory, _http, _stopping);
            _running.Add((process, process.StandardError.ReadToEndAsync()));
            Notice = "Virtual machine started. Keep Everboot and the network available. Changes are saved in its local virtual disk."; NoticeIsError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or HttpRequestException or OperationCanceledException)
        { Notice = ex is OperationCanceledException ? "Starting the virtual machine was cancelled or timed out. Check the server and try again." : ex.Message; NoticeIsError = true; }
        finally { _gate.Release(); }
    }

    public async Task PollAsync()
    {
        await _gate.WaitAsync();
        try { await PollCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task PollCoreAsync()
    {
        foreach (var vm in _running.Where(v => v.Process.HasExited).ToArray())
        {
            var error = await vm.Error;
            Notice = vm.Process.ExitCode == 0 ? "Virtual machine closed. Your virtual disk is saved." : "QEMU stopped: " + error[..Math.Min(error.Length, 2000)];
            NoticeIsError = vm.Process.ExitCode != 0;
            vm.Process.Dispose(); _running.Remove(vm);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var vm in _running)
        {
            if (!vm.Process.HasExited) Console.WriteLine("Waiting for the virtual machine to close. Keep this console open to preserve streaming.");
            await vm.Process.WaitForExitAsync(); vm.Process.Dispose();
        }
        _http.Dispose(); _gate.Dispose();
    }
}
