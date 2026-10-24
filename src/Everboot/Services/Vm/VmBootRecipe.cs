using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everboot.Services.Vm;

internal sealed record VmAsset(string Path, long Size, string Sha256);
internal sealed record VmBootRecipe(string Media, string? Cpu = null, string? CommandLine = null,
    VmAsset? Kernel = null, VmAsset? Initrd = null, VmAsset? Floppy = null);

// Server-owned sidecar: filenames refer only to the existing support-file folder.
// It cannot contain QEMU options, URLs, host paths, or device passthrough commands.
internal sealed record VmRecipeFile(string Media, string? Profile = null, string? Cpu = null,
    string? CommandLine = null, string? Kernel = null, string? Initrd = null, string? Floppy = null);

internal static class VmRecipes
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static VmImage Describe(IsoEntry entry, string supportDirectory)
    {
        var image = new VmImage(entry.DisplayName, "/isos/" + Uri.EscapeDataString(entry.FileName),
            entry.SizeBytes, QemuLaunch.RecommendedProfile(entry.Metadata));
        var sidecar = entry.FullPath + ".vm.json";
        if (!File.Exists(sidecar)) return image;
        try
        {
            if (new FileInfo(sidecar).Length > 16384) throw new IOException("Boot profile is too large.");
            if ((File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0) throw new IOException("Boot profiles cannot be filesystem links.");
            var spec = JsonSerializer.Deserialize<VmRecipeFile>(File.ReadAllText(sidecar), JsonOptions)
                ?? throw new IOException("Boot profile is empty.");
            if (spec.Profile is not null && !QemuLaunch.Profiles.Contains(spec.Profile))
                throw new IOException("Unknown virtual PC preset in boot profile.");
            VmAsset? Asset(string? name, long limit)
            {
                if (name is null) return null;
                if (!SafeName(name)) throw new IOException("Boot asset must be a single support filename.");
                var path = System.IO.Path.Combine(supportDirectory, name);
                if (!File.Exists(path)) throw new IOException("Missing boot asset: " + name);
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Boot assets cannot be filesystem links.");
                if (info.Length <= 0 || info.Length > limit) throw new IOException("Boot asset has an unsupported size: " + name);
                using var stream = File.OpenRead(path);
                return new("/files/" + Uri.EscapeDataString(name), info.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }
            var recipe = new VmBootRecipe(spec.Media, spec.Cpu, spec.CommandLine,
                Asset(spec.Kernel, 128L << 20), Asset(spec.Initrd, 256L << 20), Asset(spec.Floppy, 4L << 20));
            Validate(recipe);
            return image with { Profile = spec.Profile ?? image.Profile, Boot = recipe };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Keep other images usable. Never silently boot with a broken profile.
            return image with { Problem = "Boot profile needs attention. Check its fields and required support files on the server." };
        }
    }

    private static bool SafeName(string name) => name.Length is > 0 and <= 240 && name is not "." and not ".." &&
        !name.Any(c => c < ' ' || c is '/' or '\\' or ':' or '?' or '#' or '"' or '<' or '>' or '|') &&
        !name.EndsWith('.') && !name.EndsWith(' ');

    internal static void Validate(VmBootRecipe recipe)
    {
        if (recipe.Media is not ("cd" or "disk" or "floppy")) throw new ArgumentException("Unknown boot media type.");
        if (recipe.Cpu is not (null or "intel-max")) throw new ArgumentException("Unknown CPU compatibility setting.");
        if (recipe.CommandLine?.Length > 4096 || recipe.CommandLine?.Any(c => char.IsControl(c)) == true)
            throw new ArgumentException("Invalid kernel command line.");
        if (recipe.Kernel is null && (recipe.Initrd is not null || recipe.CommandLine is not null))
            throw new ArgumentException("An initrd or command line requires a kernel.");
        if (recipe.Kernel is not null && recipe.Media != "disk") throw new ArgumentException("Direct kernel boot requires disk media.");
        if (recipe.Floppy is not null && (recipe.Media != "cd" || recipe.Kernel is not null))
            throw new ArgumentException("A startup floppy must be paired with a CD.");
        Check(recipe.Kernel, 128L << 20); Check(recipe.Initrd, 256L << 20); Check(recipe.Floppy, 4L << 20);
        static void Check(VmAsset? asset, long limit)
        {
            if (asset is null) return;
            if (asset.Size <= 0 || asset.Size > limit || asset.Sha256 is null || asset.Sha256.Length != 64 ||
                !asset.Sha256.All(Uri.IsHexDigit) || asset.Path is null || !asset.Path.StartsWith("/files/", StringComparison.Ordinal) ||
                !SafeName(Uri.UnescapeDataString(asset.Path[7..]))) throw new ArgumentException("Invalid boot asset metadata.");
        }
    }

    internal static async Task<string> StageAsync(Uri server, VmAsset asset, string dataDirectory, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        ct = deadline.Token;
        var uri = new Uri(server, asset.Path);
        if (uri.Authority != server.Authority || uri.Scheme != server.Scheme || !uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal))
            throw new IOException("Boot asset must belong to the selected server.");
        var folder = System.IO.Path.Combine(dataDirectory, "boot-assets");
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, asset.Sha256.ToUpperInvariant());
        if (File.Exists(target))
        {
            using var existing = File.OpenRead(target);
            if (existing.Length == asset.Size && Convert.ToHexString(SHA256.HashData(existing)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) return target;
        }
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength != asset.Size)
                throw new IOException("Boot asset changed or could not be downloaded. Refresh the library.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                var remaining = asset.Size;
                while (remaining > 0)
                {
                    var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                    if (n == 0) throw new IOException("Incomplete boot asset download.");
                    hash.AppendData(buffer, 0, n);
                    await destination.WriteAsync(buffer.AsMemory(0, n), ct);
                    remaining -= n;
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Boot asset checksum changed. Refresh the library.");
            }
            File.Move(temp, target, true);
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
