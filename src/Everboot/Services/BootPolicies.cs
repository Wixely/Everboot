using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Everboot.Services;

internal sealed record ImageBootOverride(string FileName, string? DisplayName = null, string Profile = "detected",
    string Method = "auto", string? Kernel = null, string? Initrd = null, string? Arguments = null,
    string? Squashfs = null, bool RequiresNfs = false);
internal sealed record DeviceBootRule(string Id, string Name, int Priority, bool Enabled, string? Mac,
    string? Ip, string Mode, string[]? Images = null, string? ForcedImage = null);
internal sealed record BootPolicyDocument(int Version = 1, long Revision = 0,
    ImageBootOverride[]? ImageOverrides = null, DeviceBootRule[]? DeviceRules = null);
internal sealed record BootDecision(DeviceBootRule? Rule, IReadOnlyList<IsoEntry> Images, IsoEntry? ForcedImage, string? Error);

// Policy lives beside isos/, never on a potentially read-only NAS library.
internal sealed class BootPolicies
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private BootPolicyDocument _document;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public BootPolicies(string directory)
    {
        _path = Path.Combine(directory, "boot-policies.json");
        _document = File.Exists(_path) ? JsonSerializer.Deserialize<BootPolicyDocument>(File.ReadAllText(_path))
            ?? throw new IOException("Boot policy file is empty.") : new();
        Validate(_document); // Invalid policy must not silently expose an unrestricted menu.
    }
    public BootPolicyDocument Snapshot { get { lock (_gate) return Clone(_document); } }
    private static BootPolicyDocument Clone(BootPolicyDocument value) =>
        value with { ImageOverrides = (value.ImageOverrides ?? []).ToArray(), DeviceRules = (value.DeviceRules ?? []).Select(r => r with { Images = r.Images?.ToArray() }).ToArray() };
    public void SaveImage(ImageBootOverride value, long revision, bool reset = false)
    {
        lock (_gate)
        {
            var images = (_document.ImageOverrides ?? []).Where(i => !i.FileName.Equals(value.FileName, StringComparison.OrdinalIgnoreCase));
            Save(_document with { ImageOverrides = reset ? images.ToArray() : [.. images, value] }, revision);
        }
    }
    public void SaveRule(DeviceBootRule value, long revision, bool delete = false)
    {
        lock (_gate)
        {
            var rules = (_document.DeviceRules ?? []).Where(r => r.Id != value.Id);
            Save(_document with { DeviceRules = delete ? rules.ToArray() : [.. rules, value] }, revision);
        }
    }
    private void Save(BootPolicyDocument next, long revision)
    {
        if (_document.Revision != revision) throw new InvalidOperationException("Settings changed in another window. Reload before saving.");
        next = Clone(next with { Revision = checked(revision + 1) });
        Validate(next);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, next, Json); file.Flush(true); }
            File.Move(temporary, _path, true);
            _document = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = value.Trim().Replace(":", "").Replace("-", "");
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit)) throw new ArgumentException("Enter a six-byte MAC address, for example 52:54:00:12:34:56.");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => compact.Substring(i * 2, 2).ToLowerInvariant()));
    }
    public static IPAddress? ParseIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!IPAddress.TryParse(value, out var ip)) throw new ArgumentException("Enter a valid IP address.");
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }
    public BootDecision Decide(IReadOnlyList<IsoEntry> entries, string? mac, string? ip)
    {
        var normalizedMac = NormalizeMac(mac);
        var address = ParseIp(ip);
        var rule = (Snapshot.DeviceRules ?? []).Where(r => r.Enabled).OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal)
            .FirstOrDefault(r => (string.IsNullOrEmpty(r.Mac) || NormalizeMac(r.Mac) == normalizedMac) &&
                (string.IsNullOrEmpty(r.Ip) || address is not null && MatchesIp(r.Ip, address)));
        if (rule is null) return new(null, entries, null, null);
        if (rule.Mode == "force")
        {
            var forced = entries.FirstOrDefault(e => e.FileName.Equals(rule.ForcedImage, StringComparison.OrdinalIgnoreCase));
            return new(rule, forced is null ? [] : [forced], forced, forced is null ? "The assigned image is unavailable. Contact the boot server administrator." : null);
        }
        var selected = rule.Images is null ? entries : entries.Where(e => rule.Images.Contains(e.FileName, StringComparer.OrdinalIgnoreCase)).ToArray();
        return new(rule, selected, null, null);
    }
    private static bool MatchesIp(string pattern, IPAddress ip) => pattern.Contains('/')
        ? IPNetwork.Parse(pattern).Contains(ip) : ParseIp(pattern)!.Equals(ip);

    public static void Validate(BootPolicyDocument document)
    {
        if (document.Version != 1 || document.Revision < 0) throw new ArgumentException("Unsupported boot policy version or revision.");
        var images = document.ImageOverrides ?? [];
        var rules = document.DeviceRules ?? [];
        if (images.Length > 2048 || rules.Length > 2048) throw new ArgumentException("Too many boot policies.");
        if (images.Select(i => i.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Length ||
            rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length) throw new ArgumentException("Duplicate policy identifier.");
        foreach (var image in images)
        {
            FileName(image.FileName); Text(image.DisplayName, 100);
            if (image.DisplayName?.IndexOfAny(['$', ';', '&', '|']) >= 0) throw new ArgumentException("Display name cannot contain iPXE variables or command separators.");
            if (image.Profile is not ("detected" or "none") && !DistroProfiles.All.Any(p => p.Id == image.Profile)) throw new ArgumentException("Unknown detection profile.");
            if (image.Method is not ("auto" or "sanhttp" or "saniscsi" or "memdisk" or "wimboot") &&
                !Regex.IsMatch(image.Method, "\\Adirect-[a-z0-9-]+\\z")) throw new ArgumentException("Unknown boot method.");
            IsoPath(image.Kernel); IsoPath(image.Initrd); IsoPath(image.Squashfs); Text(image.Arguments, 2048);
            if (string.IsNullOrEmpty(image.Kernel) != string.IsNullOrEmpty(image.Initrd)) throw new ArgumentException("Custom boot needs both a kernel and an initrd.");
            if (image.Arguments?.IndexOfAny(['$', ';', '&', '|']) >= 0) throw new ArgumentException("Kernel arguments cannot contain iPXE variables or command separators. Use the documented {server} placeholders.");
        }
        foreach (var rule in rules)
        {
            if (!Regex.IsMatch(rule.Id, "\\A[a-zA-Z0-9-]{1,64}\\z")) throw new ArgumentException("Invalid rule identifier.");
            Text(rule.Name, 100);
            if (string.IsNullOrWhiteSpace(rule.Name)) throw new ArgumentException("Give the rule a name.");
            if (rule.Priority is < 0 or > 100000) throw new ArgumentException("Priority must be 0 to 100000.");
            NormalizeMac(rule.Mac);
            if (string.IsNullOrWhiteSpace(rule.Mac) && string.IsNullOrWhiteSpace(rule.Ip)) throw new ArgumentException("Specify a MAC address or IP address/subnet.");
            if (!string.IsNullOrEmpty(rule.Ip))
            {
                if (rule.Ip.Contains('/')) { if (!IPNetwork.TryParse(rule.Ip, out _)) throw new ArgumentException("Enter a network in CIDR notation, for example 192.0.2.0/24."); }
                else ParseIp(rule.Ip);
            }
            if (rule.Mode is not ("menu" or "force")) throw new ArgumentException("Invalid device action.");
            if (rule.Mode == "force") FileName(rule.ForcedImage ?? "");
            foreach (var image in rule.Images ?? []) FileName(image);
        }
    }
    private static void FileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.IndexOfAny(['/', '\\', ':']) >= 0 || value is "." or ".." || value.Any(char.IsControl))
            throw new ArgumentException("Use an image filename, not a filesystem path.");
    }
    private static void Text(string? value, int max)
    {
        if (value is not null && (value.Length > max || value.Any(char.IsControl))) throw new ArgumentException("Text is too long or contains control characters.");
    }
    private static void IsoPath(string? value)
    {
        Text(value, 512);
        if (!string.IsNullOrEmpty(value) && (value.StartsWith('/') || value.Contains('\\') || value.Contains(':') || value.Split('/').Any(p => p is ".." or "." or "")))
            throw new ArgumentException("Use a relative path inside the ISO with forward slashes.");
    }

    public IsoEntry Apply(IsoEntry original)
    {
        var setting = (Snapshot.ImageOverrides ?? []).FirstOrDefault(i => i.FileName.Equals(original.FileName, StringComparison.OrdinalIgnoreCase));
        if (setting is null) return original;
        var metadata = original.Metadata;
        var custom = !string.IsNullOrEmpty(setting.Kernel);
        if (setting.Profile != "detected" || custom)
        {
            var profile = setting.Profile is "none" or "detected" ? null : DistroProfiles.All.Single(p => p.Id == setting.Profile);
            if (custom) profile = new("custom", "Custom kernel / initrd", DistroFamily.LinuxLive, null, [],
                [new("custom", "Custom kernel / initrd", setting.Kernel!, setting.Initrd!, setting.Arguments ?? "", setting.RequiresNfs)], null, null, null);
            IReadOnlyList<DirectBootRecipe> recipes = [];
            var squashfs = custom ? metadata.Squashfs : null;
            if (profile is not null)
            {
                try
                {
                    using var iso = File.OpenRead(original.FullPath);
                    using var fs = IsoFileSystem.Open(iso, metadata.Layout);
                    if (fs is not null)
                    {
                        recipes = profile.DirectBoots.Where(r => IsoFileResolver.Resolve(fs, r.KernelPath) is not null &&
                            IsoFileResolver.Resolve(fs, r.InitrdPath) is not null).ToArray();
                        if (!custom && profile is { SquashfsDir: not null, SquashfsPattern: not null })
                            squashfs = IsoInspector.FindLargestMatchingFile(fs, profile.SquashfsDir, profile.SquashfsPattern);
                    }
                }
                catch (IOException) { /* Incomplete/unavailable images have no usable direct recipes. */ }
                catch (NotSupportedException) { }
            }
            metadata = metadata with { Profile = profile, AvailableRecipes = recipes, WindowsEra = WindowsBootEra.None, WindowsFiles = null };
            metadata = metadata with { Squashfs = squashfs };
        }
        if (!string.IsNullOrEmpty(setting.Squashfs)) metadata = metadata with { Squashfs = setting.Squashfs };
        return original with { DisplayName = string.IsNullOrWhiteSpace(setting.DisplayName) ? original.DisplayName : setting.DisplayName,
            Metadata = metadata, DetectedMetadata = original.Metadata, Override = setting };
    }
}
