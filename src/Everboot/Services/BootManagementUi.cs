using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Everboot.Services;

internal sealed partial class HttpFileService
{
    private const string ClassicMenuCss = """
        :root{color-scheme:dark;--desktop:#102a73;--desktop2:#081846;--panel:#55a6d9;--panel2:#2d7fbd;--ink:#06143d;--bright:#d8f4ff;--line:#07194d;--accent:#fff36b;--focus:#ff71ce}
        *{box-sizing:border-box}html{min-height:100%;background:#081846}body{min-height:100vh;max-width:72rem;margin:0 auto;padding:1.2rem;overflow-x:hidden;color:var(--bright);font:14px/1.55 "Lucida Console",Consolas,"Courier New",monospace;background:repeating-linear-gradient(0deg,rgba(255,255,255,.025) 0 1px,transparent 1px 4px),linear-gradient(135deg,var(--desktop),var(--desktop2));background-attachment:fixed}
        a{color:#fff36b;text-decoration:none;text-shadow:1px 1px #06143d}a:hover{text-decoration:underline;color:#fff}a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible,summary:focus-visible{outline:3px solid var(--focus);outline-offset:2px}
        nav,.toolbar{display:flex;gap:.55rem;flex-wrap:wrap;align-items:center;margin:0 0 1rem;padding:.45rem;background:#07194d;border:2px solid;border-color:#75d1ff #020b2c #020b2c #75d1ff;box-shadow:2px 2px 0 #000}
        nav a,.toolbar>a{display:inline-block;padding:.35rem .65rem;color:white;background:#174b9a;border:2px solid;border-color:#7ce1ff #07194d #07194d #7ce1ff;text-shadow:1px 1px #000}.toolbar form{display:inline;margin:0;padding:0;background:none;border:0;box-shadow:none}
        header,section,form,.image{min-width:0;color:var(--ink);background:linear-gradient(135deg,var(--panel),var(--panel2));border:3px solid;border-color:#bdefff #07194d #07194d #bdefff;box-shadow:4px 4px 0 rgba(0,0,0,.65);padding:1rem;margin:0 0 1rem}
        header{position:relative;padding-top:3.1rem}header:before{content:"EVERBOOT CONTROL CENTER";position:absolute;inset:.35rem .35rem auto;padding:.32rem .65rem;color:white;font-weight:700;letter-spacing:.08em;text-align:center;background:repeating-linear-gradient(90deg,#163989 0 8px,#214ca3 8px 16px);border:2px solid;border-color:#7ce1ff #07194d #07194d #7ce1ff;text-shadow:2px 2px #000}
        h1{margin:.2rem 0 .55rem;color:white;font-size:clamp(1.65rem,5vw,2.45rem);line-height:1.1;letter-spacing:.03em;text-shadow:2px 2px #07194d}h2{margin:-.35rem -.35rem .8rem;padding:.3rem .55rem;color:white;font-size:1rem;letter-spacing:.04em;background:repeating-linear-gradient(90deg,#153a88 0 7px,#214da3 7px 14px);border:2px solid;border-color:#76d4ff #07194d #07194d #76d4ff;text-shadow:1px 1px #000}
        p{margin:.65rem 0}.muted,small{color:#102b60}.eyebrow{color:#07194d;font-size:.78rem;font-weight:700;letter-spacing:.12em;text-transform:uppercase}.grid,.library{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,285px),1fr));gap:1rem}.image{margin:0}.image h2{overflow-wrap:anywhere}.badge{display:inline-block;padding:.25rem .45rem;color:#07194d;background:#b8eaff;border:2px solid;border-color:#effcff #174b7c #174b7c #effcff}
        label{display:block;min-width:0;margin:.75rem 0;font-weight:700}input:not([type=checkbox]),select{display:block;min-width:0;max-width:100%;width:100%;margin-top:.25rem;padding:.55rem;color:#07194d;background:#d8f4ff;border:3px solid;border-color:#07194d #bdefff #bdefff #07194d;border-radius:0;font:inherit}input[type=checkbox]{width:1.05rem;height:1.05rem;margin:0 .5rem 0 0;accent-color:#153a88;vertical-align:-.12rem}
        button{padding:.45rem .8rem;color:white;background:#174b9a;border:3px solid;border-color:#85e5ff #06143d #06143d #85e5ff;border-radius:0;box-shadow:2px 2px 0 rgba(0,0,0,.5);font:inherit;font-weight:700;cursor:pointer;text-shadow:1px 1px #000}button:hover{background:#2465bd}button:active{transform:translate(2px,2px);box-shadow:none;border-color:#06143d #85e5ff #85e5ff #06143d}button.secondary{color:#06143d;background:#92d4ef;text-shadow:none}
        .notice{padding:.7rem;color:#06143d;background:#bfffd2;border:3px solid;border-color:#edfff2 #175e36 #175e36 #edfff2;box-shadow:3px 3px 0 rgba(0,0,0,.5)}.error{background:#ff9da7;border-color:#ffe5e8 #7d1420 #7d1420 #ffe5e8}.wrap{overflow:auto;border:2px solid #07194d}table{width:100%;border-collapse:collapse;color:#06143d;background:#a8daf0}th,td{padding:.55rem;text-align:left;vertical-align:top;border:1px solid #397eae}thead th{color:white;background:#153a88;text-shadow:1px 1px #000}tbody tr:nth-child(even){background:#8fcbe8}code{padding:.1rem .25rem;color:#03113b;background:#bdeaff;border:1px solid #397eae;overflow-wrap:anywhere}details{margin:1rem 0;padding:.65rem;background:rgba(216,244,255,.42);border:2px solid #174b7c}summary{cursor:pointer;font-weight:700}footer{margin:1.4rem 0;padding:.7rem;color:#b9dfff;text-align:center}.yes{color:#064d1e;font-weight:700}.no{color:#314f70}.warn{color:#713600;font-weight:700}
        @media(max-width:600px){body{padding:.55rem}header,section,form,.image{padding:.75rem;box-shadow:2px 2px 0 rgba(0,0,0,.65)}nav,.toolbar{gap:.35rem}nav a,.toolbar>a,button{width:100%;text-align:center}.toolbar form,.toolbar form button{width:100%}h2{margin:-.15rem -.15rem .7rem}th,td{min-width:9rem}}
        @media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important}}
        """;
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
        sb.Append(ClassicMenuCss).Append("</style></head><body><nav aria-label='Primary'><a href='/'>Boot library</a><a href='/devices'>Device rules</a><a href='/diagnose'>Diagnostics</a></nav>");
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
