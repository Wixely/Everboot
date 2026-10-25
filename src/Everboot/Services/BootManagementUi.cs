using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Everboot.Services;

internal sealed partial class HttpFileService
{
    internal const string ThemeCss = """
        :root{color-scheme:dark;--bg:#121513;--rail:#191c1a;--panel:#242725;--panel2:#2c2e2b;--field:#37332f;--text:#eadfb9;--muted:#9d9580;--line:#48483f;--amber:#dfa31d;--amber2:#f0bd47;--olive:#a8b21b;--cyan:#68aaa4;--rose:#c76e91;--danger:#df7384;--focus:#f0bd47}
        *{box-sizing:border-box}html,body{min-height:100%;margin:0;background:var(--bg)}body{overflow-x:hidden;color:var(--text);font:14px/1.55 "Lucida Console",Consolas,"Courier New",monospace;background:radial-gradient(circle at 78% 8%,rgba(223,163,29,.07),transparent 28rem),repeating-linear-gradient(90deg,rgba(255,255,255,.012) 0 1px,transparent 1px 5px),#121513}
        a{color:var(--amber2);text-decoration:none}a:hover{color:#ffe8a7;text-decoration:underline}a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible,summary:focus-visible{outline:2px solid var(--focus);outline-offset:2px}
        .topbar{position:sticky;z-index:10;top:0;display:flex;align-items:center;min-height:34px;color:#171817;background:var(--amber);border-bottom:1px solid #71520d;box-shadow:0 5px 18px rgba(0,0,0,.28)}.top-brand{padding:.45rem 1rem;font-weight:900;letter-spacing:.08em}.workspace-tabs{display:flex;align-self:stretch}.workspace-tabs span{display:grid;min-width:36px;place-items:center;border-left:1px solid rgba(0,0,0,.18)}.workspace-tabs .selected{color:var(--text);background:#242725}.top-status{display:flex;gap:1.2rem;margin-left:auto;padding:.35rem 1rem;font-size:.82rem}.status-dot{display:inline-block;width:.55rem;height:.55rem;margin-right:.4rem;border-radius:50%;background:#546052}.status-dot.online{background:#687300;box-shadow:0 0 0 2px rgba(255,255,255,.2)}
        .app-shell{display:grid;grid-template-columns:230px minmax(0,1fr);min-height:calc(100vh - 34px);max-width:1500px;margin:auto;border-inline:1px solid #272a27}.sidebar{display:flex;flex-direction:column;gap:1rem;padding:1.25rem 1rem;background:rgba(25,28,26,.92);border-right:1px solid var(--line)}.brand-lockup{display:flex;align-items:center;gap:.75rem;padding:.3rem}.brand-mark{display:grid;width:42px;height:42px;place-items:center;color:#171817;background:var(--amber);font-size:1rem;font-weight:900}.brand-copy strong{display:block;font-size:1.05rem;letter-spacing:.08em}.brand-copy small{color:var(--muted)}.side-nav{display:grid;gap:.25rem;margin:.6rem 0}.side-nav a{display:flex;align-items:center;gap:.7rem;padding:.65rem .75rem;color:var(--text);border-left:3px solid transparent}.side-nav a:hover{background:#222522;text-decoration:none}.side-nav a.active{color:#171817;background:var(--amber);border-left-color:var(--amber2);font-weight:800}.nav-glyph{width:1.3rem;color:var(--cyan);text-align:center}.side-nav a.active .nav-glyph{color:#171817}.side-panel{margin-top:auto;padding:.8rem;background:#202320;border:1px solid var(--line)}.side-panel-title{margin-bottom:.55rem;color:var(--olive);font-size:.72rem;font-weight:800;letter-spacing:.12em;text-transform:uppercase}.side-kv{display:flex;justify-content:space-between;gap:.6rem;padding:.22rem 0;color:var(--muted);font-size:.78rem}.side-kv strong{color:var(--text);font-weight:500}.side-path{display:block;margin-top:.6rem;padding:.45rem;color:#cfc66a;background:#151716;border:1px solid #383a35;overflow-wrap:anywhere;font-size:.7rem}
        main{min-width:0;padding:2rem clamp(1rem,3vw,2.6rem) 3rem}.page-head{display:flex;align-items:flex-end;justify-content:space-between;gap:1.5rem;margin-bottom:1.5rem}.page-head-copy{max-width:52rem}.eyebrow{margin-bottom:.35rem;color:var(--olive);font-size:.75rem;font-weight:800;letter-spacing:.13em;text-transform:uppercase}.page-head h1{margin:0;color:var(--text);font-size:clamp(1.85rem,4vw,3rem);font-weight:500;line-height:1.08}.page-head h1:before{content:"~ >> ";color:var(--rose);font-size:.48em;vertical-align:.2em}.page-head p{max-width:65ch;margin:.6rem 0 0;color:var(--muted)}.head-actions{display:flex;gap:.5rem;flex-wrap:wrap;justify-content:flex-end}
        .button,button{display:inline-block;margin:0;padding:.58rem .85rem;color:#171817;background:var(--amber);border:1px solid var(--amber2);border-radius:0;font:inherit;font-weight:800;line-height:1.3;cursor:pointer;text-decoration:none}.button:hover,button:hover{color:#171817;background:var(--amber2);text-decoration:none}.button.secondary,button.secondary{color:var(--text);background:#30322f;border-color:#55554c}.button.ghost{color:var(--amber2);background:transparent;border-color:#5a5037}button:disabled{color:#777164;background:#343533;border-color:#484640;cursor:default}
        .stats-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:.75rem;margin-bottom:1rem}.stat-card{position:relative;min-width:0;padding:1rem;background:linear-gradient(145deg,var(--panel),#202320);border:1px solid var(--line);overflow:hidden}.stat-card:before{content:"";position:absolute;inset:0 auto 0 0;width:3px;background:var(--amber)}.stat-label{color:var(--muted);font-size:.72rem;letter-spacing:.1em;text-transform:uppercase}.stat-value{display:block;margin:.15rem 0;color:var(--text);font-size:1.65rem;line-height:1.2}.stat-note{color:var(--cyan);font-size:.75rem}
        .window{min-width:0;margin-bottom:1rem;background:linear-gradient(145deg,var(--panel),#202320);border:1px solid var(--line);box-shadow:0 14px 30px rgba(0,0,0,.28)}.window-bar{display:flex;align-items:center;justify-content:space-between;gap:1rem;min-height:37px;padding:.45rem .7rem;background:#1b1e1c;border-bottom:1px solid var(--line);border-left:3px solid var(--amber)}.window-title{font-weight:800}.window-meta{color:var(--muted);font-size:.76rem}.window-body{padding:1rem}.section-intro{display:flex;justify-content:space-between;gap:1rem;align-items:center;margin-bottom:.9rem}.section-intro p{margin:0;color:var(--muted)}
        .image-list{display:grid}.image-row{display:grid;grid-template-columns:48px minmax(14rem,1fr) minmax(10rem,.45fr) minmax(12rem,.5fr);gap:1rem;align-items:center;padding:.85rem .2rem;border-bottom:1px solid #3b3d38}.image-row:last-child{border-bottom:0}.image-row:hover{background:rgba(255,255,255,.018)}.media-icon{display:grid;width:42px;height:42px;place-items:center;color:var(--amber2);background:#171a18;border:1px solid #555149;font-size:.7rem;font-weight:900;text-transform:uppercase}.image-name{display:block;color:var(--text);font-size:1rem;font-weight:800;overflow-wrap:anywhere}.image-sub{margin-top:.2rem;color:var(--muted);font-size:.77rem}.boot-state{display:inline-flex;align-items:center;gap:.45rem;color:var(--cyan);font-size:.78rem}.boot-state:before{content:"";width:.5rem;height:.5rem;background:var(--cyan)}.row-actions{display:flex;gap:.75rem;justify-content:flex-end;flex-wrap:wrap;font-size:.78rem}.empty-state{padding:3rem 1rem;text-align:center}.empty-state strong{display:block;margin-bottom:.5rem;font-size:1.05rem}.empty-state p{color:var(--muted)}
        .settings-layout{display:grid;grid-template-columns:minmax(0,1.1fr) minmax(0,1fr);gap:1rem;align-items:start}.settings-layout table{table-layout:fixed}.settings-layout th,.settings-layout td{overflow-wrap:anywhere}.device-layout{display:grid;grid-template-columns:minmax(15rem,.65fr) minmax(0,1.35fr);gap:1rem;align-items:start}.rule-list{position:sticky;top:50px}.rule-item{display:block;padding:.75rem;border-bottom:1px solid #3b3d38}.rule-item:hover{background:#292c29;text-decoration:none}.rule-item strong{display:block;color:var(--text)}.rule-item small{color:var(--muted)}.rule-item.active{border-left:3px solid var(--amber);background:#292c29}.choice-grid,.field-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1rem}.field-grid.three{grid-template-columns:repeat(3,minmax(0,1fr))}.choice-panel{padding:.8rem;background:#1c1f1d;border:1px solid #3e403a}.form-actions{display:flex;gap:.5rem;flex-wrap:wrap;margin-top:1rem}
        label{display:block;min-width:0;margin:.75rem 0;color:#d7cba5;font-weight:700}input:not([type=checkbox]),select{display:block;min-width:0;max-width:100%;width:100%;margin-top:.25rem;padding:.62rem;color:var(--text);background:var(--field);border:1px solid #625d52;border-bottom-color:#817764;border-radius:0;font:inherit}input:not([type=checkbox]):hover,select:hover{border-color:var(--amber)}input[type=checkbox]{width:1.05rem;height:1.05rem;margin:0 .5rem 0 0;accent-color:var(--amber);vertical-align:-.12rem}.form-help,.muted,small{color:var(--muted)}
        .notice{margin-bottom:1rem;padding:.75rem 1rem;color:#dbe6b0;background:#24281f;border:1px solid #4e5728;border-left:3px solid var(--olive)}.notice.error{color:#f2c0d2;background:#2d2025;border-color:#6a344b;border-left-color:var(--rose)}.badge{display:inline-block;padding:.28rem .5rem;color:var(--cyan);background:#1b1d1c;border:1px solid var(--line);border-left:3px solid var(--cyan)}.wrap{overflow:auto}table{width:100%;border-collapse:collapse;color:var(--text);background:#202320}th,td{padding:.65rem;text-align:left;vertical-align:top;border-bottom:1px solid #3d3f3a}thead th{color:#171817;background:var(--amber)}tbody tr:nth-child(even){background:#292c29}tbody tr:hover{background:#313430}code{padding:.1rem .25rem;color:#d9d46a;background:#151716;border:1px solid #3c3e38;overflow-wrap:anywhere}details{margin:1rem 0;padding:.7rem;background:#1b1e1c;border:1px solid var(--line);border-left:3px solid var(--cyan)}summary{cursor:pointer;color:var(--cyan);font-weight:700}.yes{color:var(--olive);font-weight:700}.no{color:var(--muted)}.warn{color:var(--amber2);font-weight:700}.page-footer{margin-top:1.6rem;padding-top:1rem;color:var(--muted);border-top:1px solid var(--line);font-size:.75rem}
        @media(max-width:980px){.app-shell{grid-template-columns:1fr}.sidebar{position:static;display:block;padding:.65rem 1rem;border-right:0;border-bottom:1px solid var(--line)}.brand-lockup,.side-panel{display:none}.side-nav{display:flex;overflow:auto;margin:0}.side-nav a{white-space:nowrap}.stats-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.settings-layout,.device-layout{grid-template-columns:1fr}.rule-list{position:static}.image-row{grid-template-columns:44px minmax(0,1fr);gap:.75rem}.image-method,.row-actions{grid-column:2}.row-actions{justify-content:flex-start}.top-status span:not(:first-child){display:none}}
        @media(max-width:600px){.workspace-tabs{display:none}.top-status{gap:.4rem}.app-shell{display:block}.side-nav a{padding:.55rem}.nav-glyph{display:none}main{padding:1rem .65rem 2rem}.page-head{display:block}.head-actions{justify-content:flex-start;margin-top:1rem}.head-actions .button{flex:1;text-align:center}.stats-grid{grid-template-columns:1fr 1fr}.stat-value{font-size:1.3rem}.choice-grid,.field-grid,.field-grid.three{grid-template-columns:1fr}.window-body{padding:.75rem}.image-row{grid-template-columns:40px minmax(0,1fr)}.media-icon{width:36px;height:36px}.row-actions{gap:.45rem}.form-actions button{width:100%;text-align:left}th,td{min-width:9rem}}
        @media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important}}
        """;
    private readonly string _formToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Field(string name, string label, string? value = null, string type = "text") =>
        $"<label>{H(label)}<input name='{H(name)}' type='{type}' value='{H(value)}'></label>";
    private static string Select(string name, string label, IEnumerable<(string Value, string Label)> choices, string? selected) =>
        $"<label>{H(label)}<select name='{H(name)}'>" + string.Concat(choices.Select(c =>
            $"<option value='{H(c.Value)}'{(c.Value == selected ? " selected" : "")}>{H(c.Label)}</option>")) + "</select></label>";
    private string FormStart(long revision) => $"<form method='post' class='window'><input type='hidden' name='token' value='{_formToken}'><input type='hidden' name='revision' value='{revision}'>";
    private string Credentials(HttpListenerRequest request) => IPAddress.IsLoopback(request.RemoteEndPoint!.Address)
        ? "" : Field("key", "Management key (required to save)", type: "password");

    private void AppendShellStart(StringBuilder sb, string active, string eyebrow, string title, string description, string? actions = null)
    {
        var entries = _catalog.Entries;
        var policies = _catalog.Policies.Snapshot;
        var ruleCount = policies.DeviceRules?.Length ?? 0;
        var nfsState = !_options.Nfs.Enabled ? "disabled" : _nfsStatus.Running ? "online" : "starting";
        string Nav(string target, string href, string glyph, string label) =>
            $"<a href='{href}' class='{(active == target ? "active" : "")}'{(active == target ? " aria-current='page'" : "")}><span class='nav-glyph'>{glyph}</span>{label}</a>";

        sb.Append("<body><header class='topbar'><div class='workspace-tabs' aria-hidden='true'><span class='selected'>1</span><span>2</span><span>3</span></div><div class='top-brand'>EVERBOOT</div><div class='top-status'><span><i class='status-dot online'></i>SERVER ONLINE</span><span>")
          .Append(entries.Count).Append(" IMAGES</span><span>").Append(ruleCount).Append(" RULES</span></div></header><div class='app-shell'><aside class='sidebar'><div class='brand-lockup'><div class='brand-mark'>EB</div><div class='brand-copy'><strong>Everboot</strong><small>PXE control plane</small></div></div><nav class='side-nav' aria-label='Primary'>")
          .Append(Nav("library", "/", "▤", "Boot library"))
          .Append(Nav("devices", "/devices", "⌁", "Device policies"))
          .Append(Nav("diagnostics", "/diagnose", "›_", "Diagnostics"))
          .Append("</nav><div class='side-panel'><div class='side-panel-title'>Runtime</div><div class='side-kv'><span>HTTP</span><strong>:")
          .Append(_options.Http.Port).Append("</strong></div><div class='side-kv'><span>NFS</span><strong>").Append(H(nfsState)).Append("</strong></div><div class='side-kv'><span>Policies</span><strong>").Append(ruleCount).Append("</strong></div><code class='side-path'>")
          .Append(H(_catalog.IsoDirectory)).Append("</code></div></aside><main><header class='page-head'><div class='page-head-copy'><div class='eyebrow'>")
          .Append(H(eyebrow)).Append("</div><h1>").Append(H(title)).Append("</h1><p>").Append(H(description)).Append("</p></div>");
        if (!string.IsNullOrEmpty(actions)) sb.Append("<div class='head-actions'>").Append(actions).Append("</div>");
        sb.Append("</header>");
    }

    private static void AppendShellEnd(StringBuilder sb) => sb.Append("<footer class='page-footer'>Everboot serves original images without modifying them. Detection describes compatibility; validate important deployments on representative hardware.</footer></main></div></body></html>");

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
        sb.Append(ThemeCss).Append("</style></head>");
        if (entry is null)
            AppendShellStart(sb, "devices", "Policy workspace", "Device policies", "Control which images each machine can see, or assign an operating system that boots without a menu.");
        else
            AppendShellStart(sb, "library", "Image configuration", entry.DisplayName, $"Review automatic detection and tune the boot path for {entry.FileName}.");
        if (error is not null) sb.Append($"<p role='alert' class='notice error'>{H(error)} Your changes were not saved.</p>");
        else if (request.QueryString["saved"] == "1") sb.Append("<p role='status' class='notice'>Settings saved. New PXE requests use them immediately.</p>");
        if (!IPAddress.IsLoopback(request.RemoteEndPoint!.Address)) sb.Append("<p class='muted'>Remote saves require Http:ManagementKey. Without a key, open this UI on the server to edit.</p>");
        if (entry is not null) RenderImageSettings(sb, entry, doc, request, submitted);
        else RenderDeviceSettings(sb, doc, request, submitted);
        AppendShellEnd(sb);
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
        sb.Append("<div class='settings-layout'><section class='window'><div class='window-bar'><span class='window-title'>Detection and effective settings</span><span class='window-meta'>")
          .Append(entry.Override is null ? "AUTOMATIC" : "OVERRIDDEN").Append("</span></div><div class='window-body'><div class='wrap'><table><thead><tr><th>Property</th><th>Detected</th><th>Effective</th></tr></thead><tbody>");
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
        sb.Append($"<p><strong>Current boot path:</strong> <span class='badge'>{H(_generator.BootSummary(entry))}</span></p></div></section>");
        sb.Append(FormStart(doc.Revision)).Append("<div class='window-bar'><span class='window-title'>Boot configuration</span><span class='window-meta'>PERSISTED POLICY</span></div><div class='window-body'><p class='form-help'>Override incorrect detection or choose a proven method for this image. Reset returns every field to automatic detection.</p>");
        sb.Append(Field("display", "Display name", V("display", setting.DisplayName)));
        sb.Append(Select("profile", "Detection profile", new[] { ("detected", "Use automatic detection"), ("none", "Generic image / no OS profile") }.Concat(DistroProfiles.All.Select(p => (p.Id, p.DisplayName))), V("profile", setting.Profile)));
        var methods = new List<(string, string)> { ("auto", "Automatic (uses effective profile)"), ("sanhttp", "HTTP SAN — OS handoff support required"), ("saniscsi", "iSCSI SAN — service and OS support required"), ("memdisk", "MEMDISK — entire image in RAM, BIOS only"), ("wimboot", "wimboot — detected modern Windows only"), ("direct-custom", "Custom kernel / initrd") };
        methods.AddRange(DistroProfiles.All.SelectMany(p => p.DirectBoots).DistinctBy(r => r.Id).Select(r => ("direct-" + r.Id, r.DisplayName)));
        sb.Append(Select("method", "Preferred boot method", methods, V("method", setting.Method)));
        sb.Append("<p class='form-help'>An unavailable method stops at boot failure; it does not silently choose another method.</p><details" + (setting.Kernel is not null || posted is not null ? " open" : "") + "><summary>Custom Linux boot</summary><p>Paths are relative to the ISO root. Both kernel and initrd must exist. Custom paths replace the OS profile; clear both to restore the selected profile.</p><div class='field-grid'>");
        sb.Append(Field("kernel", "Kernel path", V("kernel", setting.Kernel))).Append(Field("initrd", "Initrd path", V("initrd", setting.Initrd))).Append(Field("arguments", "Kernel arguments", V("arguments", setting.Arguments))).Append(Field("squashfs", "Live filesystem path (optional)", V("squashfs", setting.Squashfs)));
        sb.Append("</div><p class='form-help'>Argument placeholders: {server}, {slug}, {file}, {squashfs}, {nfs-server}, {nfs-options}. The kernel/initrd must supply the required network drivers.</p>");
        sb.Append($"<label><input type='checkbox' name='nfs'{((posted is null ? setting.RequiresNfs : posted["nfs"] == "on") ? " checked" : "")}>Requires Everboot NFS</label></details>");
        sb.Append(Credentials(request)).Append("<div class='form-actions'><button name='action' value='save'>Save configuration</button><button class='secondary' name='action' value='reset'>Reset to detection</button></div></div></form></div>");
    }

    private void RenderDeviceSettings(StringBuilder sb, BootPolicyDocument doc, HttpListenerRequest request, NameValueCollection? posted)
    {
        var rules = (doc.DeviceRules ?? []).OrderBy(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var edit = rules.FirstOrDefault(r => r.Id == (posted?["id"] ?? request.QueryString["edit"]));
        var rule = edit ?? new DeviceBootRule(Guid.NewGuid().ToString("N"), "", 100, true, null, null, "menu");
        string? V(string name, string? fallback) => posted is null ? fallback : posted[name];
        bool Checked(string name, bool fallback) => posted is null ? fallback : posted[name] == "on";
        sb.Append("<div class='notice'><strong>First match wins.</strong> Lower priority numbers run first. When MAC and IP are both set, both must match. These policies control boot behaviour, not access control.</div><div class='device-layout'><section class='window rule-list'><div class='window-bar'><span class='window-title'>Policy stack</span><span class='window-meta'>")
          .Append(rules.Length).Append(" RULES</span></div><div class='window-body'>");
        if (rules.Length == 0) sb.Append("<div class='empty-state'><strong>No policies configured</strong><p>Every device currently sees the full image library.</p></div>");
        foreach (var r in rules)
            sb.Append($"<a class='rule-item {(edit?.Id == r.Id ? "active" : "")}' href='/devices?edit={H(r.Id)}'><strong>{H(r.Name)}</strong><small>Priority {r.Priority} · {(r.Enabled ? "enabled" : "disabled")} · {H(r.Mode == "force" ? "direct boot " + r.ForcedImage : r.Images is null ? "full library" : $"{r.Images.Length} selected images")}</small></a>");
        sb.Append("<div class='form-actions'><a class='button ghost' href='/devices?new=1'>+ New policy</a></div></div></section>");
        sb.Append(FormStart(doc.Revision)).Append($"<div class='window-bar'><span class='window-title'>{(edit is null ? "Create policy" : "Edit policy")}</span><span class='window-meta'>{(edit is null ? "NEW" : H(edit.Id))}</span></div><div class='window-body'><input type='hidden' name='id' value='{H(V("id", rule.Id))}'>");
        sb.Append(Field("name", "Policy name", V("name", rule.Name))).Append("<div class='field-grid three'>").Append(Field("priority", "Priority (0-100000)", V("priority", rule.Priority.ToString()), "number"));
        sb.Append(Field("mac", "MAC address (optional)", V("mac", rule.Mac))).Append(Field("ip", "IP address or CIDR subnet (optional)", V("ip", rule.Ip))).Append("</div>");
        sb.Append($"<label><input type='checkbox' name='enabled'{(Checked("enabled", rule.Enabled) ? " checked" : "")}>Rule enabled</label>");
        sb.Append(Select("mode", "Action", [("menu", "Show a menu of images"), ("force", "Boot one image directly — no menu")], V("mode", rule.Mode)));
        sb.Append("<div class='choice-grid'><section class='choice-panel'><strong>Menu image set</strong>");
        sb.Append($"<label><input type='checkbox' name='all'{(Checked("all", rule.Images is null) ? " checked" : "")}>All current and future images</label><p class='muted'>Uncheck this to use the selection below. An empty selection shows no images.</p>");
        var selected = posted is null ? rule.Images ?? [] : posted.GetValues("images") ?? [];
        var filenames = _catalog.Entries.Select(e => e.FileName).Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var filename in filenames) sb.Append($"<label><input type='checkbox' name='images' value='{H(filename)}'{(selected.Contains(filename, StringComparer.OrdinalIgnoreCase) ? " checked" : "")}>{H(filename)}{(_catalog.TryGetByFileName(filename, out _) ? "" : " (missing)")}</label>");
        sb.Append("</section><section class='choice-panel'><strong>Direct boot assignment</strong>");
        var forced = V("forced", rule.ForcedImage);
        sb.Append(Select("forced", "Image to boot", new[] { ("", "Choose an image") }.Concat(filenames.Concat(forced is null ? [] : new[] { forced }).Distinct().Select(f => (f, f))), forced));
        sb.Append("<p class='muted'>Used only for direct boot. If the image is missing or boot fails before OS handoff, the client stops without offering another OS.</p></section></div>");
        sb.Append(Credentials(request)).Append("<div class='form-actions'><button name='action' value='save'>Save policy</button>");
        if (edit is not null) sb.Append("<button class='secondary' name='action' value='delete'>Delete policy</button>");
        sb.Append("</div></div></form></div><form method='get' class='window'><div class='window-bar'><span class='window-title'>Device preview</span><span class='window-meta'>DRY RUN</span></div><div class='window-body'><p class='form-help'>Enter a client identity to see its winning policy and final boot choices before restarting it.</p><div class='field-grid'>").Append(Field("mac", "Client MAC", request.QueryString["mac"])).Append(Field("ip", "Client IP", request.QueryString["ip"])).Append("</div><div class='form-actions'><button name='preview' value='1'>Evaluate device</button></div>");
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
        sb.Append("</div></form>");
    }
}
