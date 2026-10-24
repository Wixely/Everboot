using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Everboot.Services;

internal sealed partial class HttpFileService
{
    private readonly string _formToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Field(string name, string label, string? value = null, string type = "text") =>
        $"<label>{H(label)}<input name='{H(name)}' type='{type}' value='{H(value)}'></label>";
    private static string Select(string name, string label, IEnumerable<(string Value, string Label)> choices, string? selected) =>
        $"<label>{H(label)}<select name='{H(name)}'>" + string.Concat(choices.Select(c =>
            $"<option value='{H(c.Value)}'{(c.Value == selected ? " selected" : "")}>{H(c.Label)}</option>")) + "</select></label>";
    private string FormStart(long revision) => $"<form method='post'><input type='hidden' name='token' value='{_formToken}'><input type='hidden' name='revision' value='{revision}'>";
    private string Credentials(HttpListenerRequest request) => IPAddress.IsLoopback(request.RemoteEndPoint!.Address)
        ? "" : Field("key", "Management key (required to save)", type: "password");

    private async Task HandleManagementAsync(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken ct)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        IsoEntry? entry = null;
        if (path.StartsWith("/images/", StringComparison.Ordinal) && !_catalog.TryGetBySlug(path[8..], out entry))
        { response.StatusCode = 404; return; }
        string? error = null;
        NameValueCollection? submitted = null;
        if (request.HttpMethod == "POST")
        {
            try
            {
                if (request.ContentLength64 is < 0 or > 32768 || request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true)
                { response.StatusCode = 413; return; }
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                submitted = System.Web.HttpUtility.ParseQueryString(await reader.ReadToEndAsync(ct));
                var origin = request.Headers["Origin"];
                if (submitted["token"] != _formToken || origin is not null && origin != request.Url!.GetLeftPart(UriPartial.Authority) ||
                    !IPAddress.IsLoopback(request.RemoteEndPoint!.Address) &&
                    (string.IsNullOrEmpty(_options.Http.ManagementKey) || !FixedEquals(submitted["key"], _options.Http.ManagementKey)))
                { response.StatusCode = 403; error = "Save denied. Use the server's local UI, or enter the configured management key. Reload if the server restarted."; }
                else
                {
                    if (!long.TryParse(submitted["revision"], out var revision)) throw new ArgumentException("Missing settings revision. Reload the page.");
                    string? Value(string key) => string.IsNullOrWhiteSpace(submitted[key]) ? null : submitted[key]!.Trim();
                    if (entry is not null)
                    {
                        var setting = new ImageBootOverride(entry.FileName, Value("display"), Value("profile") ?? "detected", Value("method") ?? "auto",
                            Value("kernel"), Value("initrd"), Value("arguments"), Value("squashfs"), submitted["nfs"] == "on");
                        _catalog.Policies.SaveImage(setting, revision, submitted["action"] == "reset");
                        _catalog.ForceRescan();
                    }
                    else
                    {
                        if (!int.TryParse(submitted["priority"], out var priority)) throw new ArgumentException("Enter a numeric priority.");
                        var rule = new DeviceBootRule(Value("id") ?? Guid.NewGuid().ToString("N"), Value("name") ?? "", priority,
                            submitted["enabled"] == "on", BootPolicies.NormalizeMac(Value("mac")), Value("ip"), Value("mode") ?? "menu",
                            submitted["all"] == "on" ? null : submitted.GetValues("images") ?? [], Value("forced"));
                        _catalog.Policies.SaveRule(rule, revision, submitted["action"] == "delete");
                    }
                    response.StatusCode = 303;
                    response.RedirectLocation = path + "?saved=1";
                    return;
                }
            }
            catch (ArgumentException ex) { response.StatusCode = 400; error = ex.Message; }
            catch (InvalidOperationException ex) { response.StatusCode = 409; error = ex.Message; }
            catch (IOException) { response.StatusCode = 500; error = "Could not save settings. Check the data directory is writable and has free space."; }
        }
        else if (request.HttpMethod is not ("GET" or "HEAD")) { response.StatusCode = 405; return; }
        var doc = _catalog.Policies.Snapshot;
        var sb = new StringBuilder("<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Everboot | Boot settings</title><style>");
        sb.Append("body{font:16px/1.6 system-ui,sans-serif;background:#f5f7fb;color:#18283d;max-width:72rem;margin:2rem auto;padding:0 1rem}h1{font-size:2.4rem;letter-spacing:-.04em}h2{font-size:1.25rem}a{color:#174ca4}nav{display:flex;gap:1.5rem;flex-wrap:wrap}section,form{background:white;border:1px solid #dbe2ed;border-radius:12px;padding:1.3rem;margin:1rem 0}form form{border:0}label{display:block;margin:.8rem 0;font-weight:600}input:not([type=checkbox]),select{display:block;width:100%;box-sizing:border-box;padding:.65rem;border:1px solid #9aabc2;border-radius:6px;font:inherit;background:white;color:inherit}input[type=checkbox]{margin-right:.6rem}button{font:inherit;padding:.6rem 1rem;border:1px solid #174ca4;border-radius:6px;background:#174ca4;color:white;cursor:pointer;margin:.5rem .5rem .5rem 0}button.secondary{background:white;color:#174ca4}:focus-visible{outline:3px solid #ca7200;outline-offset:3px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,300px),1fr));gap:1.5rem}.notice{padding:1rem;border-left:4px solid #27775b;background:#e9f6f0}.error{border-color:#b83838;background:#fff0f0}small,.muted{color:#52647d}.wrap{overflow:auto}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:.6rem;border-bottom:1px solid #dbe2ed}code,td{overflow-wrap:anywhere}details{margin:1rem 0}summary{cursor:pointer;font-weight:600}</style></head><body><nav><a href='/'>Boot library</a><a href='/devices'>Device rules</a><a href='/diagnose'>Diagnostics</a></nav>");
        if (error is not null) sb.Append($"<p role='alert' class='notice error'>{H(error)} Your changes were not saved.</p>");
        else if (request.QueryString["saved"] == "1") sb.Append("<p role='status' class='notice'>Settings saved. New PXE requests use them immediately.</p>");
        if (!IPAddress.IsLoopback(request.RemoteEndPoint!.Address)) sb.Append("<p class='muted'>Remote saves require Http:ManagementKey. Without a key, open this UI on the server to edit.</p>");
        if (entry is not null) RenderImageSettings(sb, entry, doc, request, submitted);
        else RenderDeviceSettings(sb, doc, request, submitted);
        sb.Append("<footer><p class='muted'>Settings are saved separately from your original images. Detection is a compatibility hint, not proof of a successful boot.</p></footer></body></html>");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (request.HttpMethod != "HEAD") await response.OutputStream.WriteAsync(bytes, ct);
    }
    private static bool FixedEquals(string? a, string b) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(a ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

    private void RenderImageSettings(StringBuilder sb, IsoEntry entry, BootPolicyDocument doc, HttpListenerRequest request, NameValueCollection? posted)
    {
        var setting = entry.Override ?? new(entry.FileName);
        string? V(string name, string? fallback) => posted is null ? fallback : posted[name];
        var detected = entry.DetectedMetadata;
        if (_generator.OverrideProblem(entry) is { } problem) sb.Append($"<p class='notice error'>{H(problem)}</p>");
        sb.Append($"<h1>{H(entry.DisplayName)}</h1><p class='muted'>{H(entry.FileName)} &middot; {(entry.Override is null ? "Automatic detection" : "Overrides active")}</p><section><h2>Detection and effective settings</h2><div class='wrap'><table><thead><tr><th>Property</th><th>Detected</th><th>Effective</th></tr></thead><tbody>");
        void Row(string label, string? a, string? b) => sb.Append($"<tr><th>{H(label)}</th><td>{H(a ?? "None")}</td><td>{H(b ?? "None")}</td></tr>");
        Row("Profile", detected.Profile?.DisplayName, entry.Metadata.Profile?.DisplayName);
        Row("Windows generation", detected.WindowsEra.ToString(), entry.Metadata.WindowsEra.ToString());
        Row("Volume label", detected.VolumeLabel, entry.Metadata.VolumeLabel);
        Row("Filesystem", detected.Layout.ToString(), entry.Metadata.Layout.ToString());
        Row("Live filesystem", detected.Squashfs, entry.Metadata.Squashfs);
        Row("Available recipes", string.Join(", ", (detected.AvailableRecipes ?? []).Select(r => r.DisplayName)), string.Join(", ", _generator.GetApplicableRecipes(entry).Select(r => r.DisplayName)));
        sb.Append("</tbody></table></div>");
        if (detected.Profile is { } profile)
            sb.Append($"<p><strong>Detection criteria:</strong> volume pattern <code>{H(profile.VolumeLabelPattern?.ToString() ?? "any")}</code>; marker files <code>{H(string.Join(", ", profile.MarkerFiles))}</code>.</p>");
        sb.Append($"<p><strong>Boot behaviour:</strong> {H(_generator.BootSummary(entry))}</p></section>");
        sb.Append(FormStart(doc.Revision)).Append("<h2>Override boot settings</h2><p>Start with a different boot method. For incorrect detection, choose a profile or supply kernel paths. Reset restores automatic detection.</p>");
        sb.Append(Field("display", "Display name", V("display", setting.DisplayName)));
        sb.Append(Select("profile", "Detection profile", new[] { ("detected", "Use automatic detection"), ("none", "Generic image / no OS profile") }.Concat(DistroProfiles.All.Select(p => (p.Id, p.DisplayName))), V("profile", setting.Profile)));
        var methods = new List<(string, string)> { ("auto", "Automatic (uses effective profile)"), ("sanhttp", "HTTP SAN — OS handoff support required"), ("saniscsi", "iSCSI SAN — service and OS support required"), ("memdisk", "MEMDISK — entire image in RAM, BIOS only"), ("wimboot", "wimboot — detected modern Windows only"), ("direct-custom", "Custom kernel / initrd") };
        methods.AddRange(DistroProfiles.All.SelectMany(p => p.DirectBoots).DistinctBy(r => r.Id).Select(r => ("direct-" + r.Id, r.DisplayName)));
        sb.Append(Select("method", "Preferred boot method", methods, V("method", setting.Method)));
        sb.Append("<p class='muted'>An unavailable method stops at boot failure; it does not silently choose another method.</p><details" + (setting.Kernel is not null || posted is not null ? " open" : "") + "><summary>Advanced: custom Linux boot</summary><p>Paths are relative to the ISO root. Both kernel and initrd must exist. Custom paths replace the OS profile; clear both to restore the selected profile.</p>");
        sb.Append(Field("kernel", "Kernel path", V("kernel", setting.Kernel))).Append(Field("initrd", "Initrd path", V("initrd", setting.Initrd))).Append(Field("arguments", "Kernel arguments", V("arguments", setting.Arguments))).Append(Field("squashfs", "Live filesystem path (optional)", V("squashfs", setting.Squashfs)));
        sb.Append("<p class='muted'>Argument placeholders: {server}, {slug}, {file}, {squashfs}, {nfs-server}, {nfs-options}. The kernel/initrd must supply the required network drivers.</p>");
        sb.Append($"<label><input type='checkbox' name='nfs'{((posted is null ? setting.RequiresNfs : posted["nfs"] == "on") ? " checked" : "")}>Requires Everboot NFS</label></details>");
        sb.Append(Credentials(request)).Append("<button name='action' value='save'>Save image settings</button><button class='secondary' name='action' value='reset'>Reset to detection</button></form>");
    }

    private void RenderDeviceSettings(StringBuilder sb, BootPolicyDocument doc, HttpListenerRequest request, NameValueCollection? posted)
    {
        sb.Append("<h1>Device boot rules</h1><p>Give a device its own image list, or boot one image directly without a selection screen.</p><section><h2>How matching works</h2><p>Lower priority numbers run first. The first enabled match wins. When MAC and IP are both specified, both must match. Equal priorities sort by rule ID. Unmatched devices see the full library.</p><p class='muted'>These rules control boot behaviour, not access to ISO downloads or network shares. Client-reported addresses can be spoofed.</p></section>");
        var rules = (doc.DeviceRules ?? []).OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        sb.Append("<section><h2>Rules</h2>");
        if (rules.Length == 0) sb.Append("<p>No rules yet. All devices see the full library.</p>");
        foreach (var r in rules) sb.Append($"<p><a href='/devices?edit={H(r.Id)}'>{H(r.Name)}</a> &middot; priority {r.Priority} &middot; {(r.Enabled ? "Enabled" : "Disabled")}<br><small>{H(r.Mac)} {H(r.Ip)} &rarr; {(r.Mode == "force" ? "Boot directly: " + H(r.ForcedImage) : r.Images is null ? "All images" : $"Selected images ({r.Images.Length})")}</small></p>");
        sb.Append("<a href='/devices?new=1'>Create a new rule</a></section>");
        var edit = rules.FirstOrDefault(r => r.Id == (posted?["id"] ?? request.QueryString["edit"]));
        var rule = edit ?? new DeviceBootRule(Guid.NewGuid().ToString("N"), "", 100, true, null, null, "menu");
        string? V(string name, string? fallback) => posted is null ? fallback : posted[name];
        bool Checked(string name, bool fallback) => posted is null ? fallback : posted[name] == "on";
        sb.Append(FormStart(doc.Revision)).Append($"<h2>{(edit is null ? "New rule" : "Edit rule")}</h2><input type='hidden' name='id' value='{H(V("id", rule.Id))}'>");
        sb.Append(Field("name", "Rule name", V("name", rule.Name))).Append("<div class='grid'>").Append(Field("priority", "Priority (0–100000)", V("priority", rule.Priority.ToString()), "number"));
        sb.Append(Field("mac", "MAC address (optional)", V("mac", rule.Mac))).Append(Field("ip", "IP address or CIDR subnet (optional)", V("ip", rule.Ip))).Append("</div>");
        sb.Append($"<label><input type='checkbox' name='enabled'{(Checked("enabled", rule.Enabled) ? " checked" : "")}>Rule enabled</label>");
        sb.Append(Select("mode", "Action", [("menu", "Show a menu of images"), ("force", "Boot one image directly — no menu")], V("mode", rule.Mode)));
        sb.Append("<div class='grid'><section><h2>Menu images</h2>");
        sb.Append($"<label><input type='checkbox' name='all'{(Checked("all", rule.Images is null) ? " checked" : "")}>All current and future images</label><p class='muted'>Uncheck this to use the selection below. An empty selection shows no images.</p>");
        var selected = posted is null ? rule.Images ?? [] : posted.GetValues("images") ?? [];
        var filenames = _catalog.Entries.Select(e => e.FileName).Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var filename in filenames) sb.Append($"<label><input type='checkbox' name='images' value='{H(filename)}'{(selected.Contains(filename, StringComparer.OrdinalIgnoreCase) ? " checked" : "")}>{H(filename)}{(_catalog.TryGetByFileName(filename, out _) ? "" : " (missing)")}</label>");
        sb.Append("</section><section><h2>Direct boot image</h2>");
        var forced = V("forced", rule.ForcedImage);
        sb.Append(Select("forced", "Image to boot", new[] { ("", "Choose an image") }.Concat(filenames.Concat(forced is null ? [] : new[] { forced }).Distinct().Select(f => (f, f))), forced));
        sb.Append("<p class='muted'>Used only for direct boot. If the image is missing or boot fails before OS handoff, the client stops without offering another OS.</p></section></div>");
        sb.Append(Credentials(request)).Append("<button name='action' value='save'>Save device rule</button>");
        if (edit is not null) sb.Append("<button class='secondary' name='action' value='delete'>Delete rule</button>");
        sb.Append("</form><form method='get'><h2>Preview a device</h2><p>See which rule and images a client will receive.</p>").Append(Field("mac", "Client MAC", request.QueryString["mac"])).Append(Field("ip", "Client IP", request.QueryString["ip"])).Append("<button name='preview' value='1'>Preview boot behaviour</button>");
        if (request.QueryString["preview"] == "1")
        {
            try
            {
                var decision = _catalog.Policies.Decide(_catalog.Entries, request.QueryString["mac"], request.QueryString["ip"]);
                sb.Append($"<div role='status' class='notice'><strong>{H(decision.Rule?.Name ?? "No matching rule — default library")}</strong><p>{H(decision.Error ?? (decision.Rule?.Mode == "force" ? "Direct boot: " + decision.ForcedImage?.DisplayName : $"Menu with {decision.Images.Count} images"))}</p><ul>");
                foreach (var image in decision.Images) sb.Append($"<li>{H(image.DisplayName)} — {H(_generator.BootSummary(image))}</li>");
                sb.Append("</ul></div>");
            }
            catch (ArgumentException ex) { sb.Append($"<p role='alert' class='notice error'>{H(ex.Message)}</p>"); }
        }
        sb.Append("</form>");
    }
}
