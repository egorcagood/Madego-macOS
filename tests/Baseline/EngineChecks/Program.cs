using EgorVpn.Branding;
using EgorVpn.Presentation;
using EgorVpn.Controls;
using System.Text.Json;
using EgorVpn.Models;
using EgorVpn.Services;

internal static class Program
{
    private static int _passed, _failed;
    private static void Check(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void True(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Expected ArgumentException"); }
    public static int Main()
    {
        var temp = Path.Combine(Path.GetTempPath(), "MadyarVPN-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Check("Sharing exports only the selected VLESS configuration", () => {
                var uri="vless://11111111-1111-4111-8111-111111111111@one.invalid:443?security=reality&type=tcp&pbk=PUBLIC&sid=ab&sni=one.invalid#Friend";
                True(VlessUriParser.TryParse(uri,out var node,out _),"Fixture URI");
                var exported=SingleServerShare.Export(node!);
                True(exported==uri,"Selected configuration changed");
                var imported=SingleServerShare.Import(exported);
                True(imported.Nodes.Count==1&&imported.Nodes[0].Host=="one.invalid"&&imported.Url=="","Whole subscription or remote URL was imported");
                True(SingleServerShare.IsLocal(imported),"Imported configuration can trigger subscription HTTP refresh");
            });
            Check("Sharing cannot import subscription URLs or multiple nodes", () => {
                var uri="vless://11111111-1111-4111-8111-111111111111@one.invalid:443";
                Throws(()=>SingleServerShare.Import("https://provider.invalid/subscription-secret"));
                Throws(()=>SingleServerShare.Import(uri+"\n"+uri.Replace("one.invalid","two.invalid")));
                Throws(()=>SingleServerShare.Import("vless://@one.invalid"));
                Throws(()=>SingleServerShare.Import(new string('x',32769)));
            });
            Check("Second server credentials in a name cannot be forwarded as one server", () => {
                var a="vless://11111111-1111-4111-8111-111111111111@one.invalid:443#First";
                var b="vless://22222222-2222-4222-8222-222222222222@two.invalid:443#Second";
                Throws(()=>SingleServerShare.Import(a+" "+b));
                Throws(()=>SingleServerShare.Import(a+";"+b));
                Throws(()=>SingleServerShare.Import(a+Uri.EscapeDataString(" "+b)));
                Throws(()=>SingleServerShare.Export(new VpnNode {RawUri=a+";"+b}));
            });
            Check("Single server export preserves JSON-derived connection parameters", () => {
                var node=new VpnNode {RawUri="{\"protocol\":\"vless\"}",UserId="11111111-1111-4111-8111-111111111111",Host="2001:db8::1",Port=8443,Name="Тест & 1",Transport="xhttp",Security="reality",Flow="xtls-rprx-vision",PublicKey="pbk",ShortId="aa",Sni="cdn.invalid",Path="/x?y=1",ExtraJson="{\"mode\":\"auto\"}",Mldsa65Verify="pqv"};
                var decoded=SingleServerShare.Import(SingleServerShare.Export(node)).Nodes.Single();
                True(SingleServerShare.ConnectionKey(decoded)==SingleServerShare.ConnectionKey(node),"JSON and URI forms fail deduplication");
                True(decoded.Host==node.Host&&decoded.Port==node.Port&&decoded.Path==node.Path&&decoded.Name==node.Name&&decoded.Flow==node.Flow&&decoded.PublicKey==node.PublicKey&&decoded.ExtraJson==node.ExtraJson&&decoded.Mldsa65Verify==node.Mldsa65Verify,"Connection parameters lost during export");
            });
            Check("Imported single server persists independently without provider URL", () => {
                var local=SingleServerShare.Import("vless://11111111-1111-4111-8111-111111111111@one.invalid:443#Local");
                var saved=new SubscriptionItem(local).ToSaved();
                var restored=SubscriptionItem.FromSaved(JsonSerializer.Deserialize<SavedSubscription>(JsonSerializer.Serialize(saved))!);
                True(restored.Nodes.Count==1&&restored.Url==""&&SingleServerShare.IsLocal(restored.Source),"Restart loses standalone server or refresh marker");
            });
            foreach (var phase in new[] { "Idle", "Connecting", "Stopping", "Degraded", "Error", "unknown" })
                Check("Unverified phase never shows connection success: " + phase, () => {
                    var visual = ConnectionVisualState.ForPhase(phase, "#57C7E8");
                    True(!visual.IsVerified && visual.Icon != "Check", "False success indicator");
                    True(visual.Status != "Подключено" && visual.FeedbackTitle != "Соединение проверено", "False success copy");
                });
            Check("Error does not assert that a previously running tunnel is disconnected", () => {
                var visual = ConnectionVisualState.ForPhase("Error", "#57C7E8");
                True(visual.Title == "Ошибка соединения" && visual.FeedbackTitle == "Состояние не подтверждено", "Error misrepresents failed tunnel cleanup");
            });
            Check("Verified connection uses success feedback", () => {
                var visual = ConnectionVisualState.ForPhase("Connected", "#57C7E8");
                True(visual.IsVerified && visual.Icon == "Check", "Verified success missing");
                True(visual.Status == "Подключено", "Connected phase lost");
            });
            Check("Degraded tunnel remains visibly unverified", () => {
                var visual = ConnectionVisualState.ForPhase("Degraded", "#57C7E8");
                True(visual.Status == "Нет проверки" && visual.Color == "#DEB16D", "Degraded phase looks healthy");
            });
            Check("Partner accent applies to pending connection, not success or error", () => {
                True(ConnectionVisualState.ForPhase("Connecting", "#AABBCC").Color == "#AABBCC", "Partner accent ignored");
                True(ConnectionVisualState.ForPhase("Error", "#AABBCC").Color == "#E58A8A", "Error inherits accent");
            });
            // Consumer-facing regressions: raw names or lost labels must fail these.
            Check("Display removes matching country flag without mutating subscription", () => {
                var source = new VpnNode { Name = "🇩🇪 Frankfurt 🚀", Country = "Germany", CountryFlag = "🇩🇪", RawUri = "vless://fixture" };
                var item = new NodeItem(source, "g", "Provider");
                True(item.Name == "Frankfurt 🚀", "Repeated flag remains in visible name");
                True(source.Name == "🇩🇪 Frankfurt 🚀" && source.RawUri == "vless://fixture", "Raw subscription changed");
            });
            Check("Mixed-region title retains the other country's meaning", () => {
                var item = new NodeItem(new VpnNode { Name = "🇩🇪 → 🇫🇷 Paris", CountryFlag = "🇩🇪" }, "g", "P");
                True(item.Name == "→ 🇫🇷 Paris", "Unrelated region removed or matching flag retained");
            });
            Check("Flag-only name retains useful country title", () => {
                var item = new NodeItem(new VpnNode { Name = "🇯🇵", Country = "Japan", CountryFlag = "🇯🇵" }, "g", "P");
                True(item.Name == "Japan", "Flag-only name becomes unreadable");
            });
            Check("Repeated AUTO nodes become distinguishable without inventing countries", () => {
                var group = new SubscriptionItem(new SubscriptionGroup { Name = "P", Nodes = new() {
                    new VpnNode { Name = "AUTO", Country = "Other" }, new VpnNode { Name = "AUTO", Country = "Other" } } });
                True(group.Nodes[0].Name == "Сервер 1" && group.Nodes[1].Name == "Сервер 2", "AUTO nodes indistinguishable");
                True(group.Nodes.All(n => n.Source.Name == "AUTO" && n.Country == "Other"), "Provider data rewritten");
            });
            Check("Duplicate provider titles stay distinguishable after refresh", () => {
                var source = new SubscriptionGroup { Name="P", Nodes=new() { new VpnNode { Name="Fast" }, new VpnNode { Name="Fast" } } };
                var group=new SubscriptionItem(source); group.Replace(source);
                True(group.Nodes[0].Name == "1 · Fast" && group.Nodes[1].Name == "2 · Fast", "Duplicate titles collide");
            });
            Check("Generated labels cannot collide with provider titles", () => {
                var group = new SubscriptionItem(new SubscriptionGroup { Name="P", Nodes=new() {
                    new VpnNode { Name="AUTO" }, new VpnNode { Name="AUTO" }, new VpnNode { Name="Сервер 1" },
                    new VpnNode { Name="Fast" }, new VpnNode { Name="Fast" }, new VpnNode { Name="1 · Fast" } } });
                True(group.Nodes.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6, "Generated and original labels collide");
                True(group.Nodes[2].Name == "Сервер 1" && group.Nodes[5].Name == "1 · Fast", "Unique provider names were rewritten");
            });
            Check("Display rename is allowed while operational identity remains fixed", () => {
                (Brand.Current with { ProductName = "Мадяр Клиент", ShortName = "Мадяр" }).Validate();
                Throws(() => (Brand.Current with { ExecutableName = "RenamedClient" }).Validate());
            });
            Check("Embedded default profile preserves existing identity", () => {
                var b = Brand.Current; b.Validate();
                True(b.ProductName == "Madego" && b.DataFolder == "MadyarVPN" && b.TunName == "MadyarVPN", "Default identity changed");
                True(b.Telegram == "https://t.me/egormadyar", "Channel changed");
            });
            Check("Unsafe brand folder and executable names are rejected", () => {
                foreach (var value in new[] { "../MadyarVPN", "CON", "NUL", "C:\\Windows", "a/b", "a b", "abc;def" })
                    True(!BrandProfile.IsSafeIdentifier(value), "Unsafe identifier accepted: " + value);
            });
            Check("Only explicit HTTPS Telegram channels can be opened", () => {
                True(BrandProfile.IsAllowedTelegram("https://t.me/egormadyar"), "Valid channel rejected");
                foreach (var value in new[] { "http://t.me/egormadyar", "file:///c:/x", "javascript:alert(1)", "https://t.me.evil.invalid/egormadyar", "https://user@t.me/egormadyar", "https://t.me/egormadyar?x=1" })
                    True(!BrandProfile.IsAllowedTelegram(value), "Unsafe external link accepted");
            });
            Check("Partner profile cannot reuse personal storage", () => {
                Throws(() => (Brand.Current with { ApplicationId = "PartnerClient", MigrateLegacyData = false }).Validate());
                Throws(() => (Brand.Current with { ApplicationId = "PartnerClient" }).Validate());
            });
            Check("Partner migration never copies personal credentials", () => {
                var root = Path.Combine(temp, "partner-migration");
                Directory.CreateDirectory(Path.Combine(root, "FuseVPN"));
                File.WriteAllText(Path.Combine(root, "FuseVPN", "state.json"), "private-state");
                var brand = Brand.Current with { ApplicationId = "PartnerClient", ExecutableName = "PartnerClient", DataFolder = "PartnerClient", TunName = "PartnerClient", InstallFolder = "Partner Client", MigrateLegacyData = false };
                AppPaths.EnsureMigrated(root, brand);
                True(!File.Exists(Path.Combine(root, "PartnerClient", "state.json")), "Personal subscriptions copied into partner profile");
                True(File.Exists(Path.Combine(root, "FuseVPN", "state.json")), "Original state removed");
            });
            Check("Original identity cannot silently move cached subscriptions", () => {
                Throws(() => (Brand.Current with { DataFolder = "PartnerClient" }).Validate());
                Throws(() => (Brand.Current with { InstallFolder = "Partner Client" }).Validate());
            });
            Check("Reserved Windows directory names are rejected", () => {
                Throws(() => (Brand.Current with { InstallFolder = "CON.txt" }).Validate());
                Throws(() => (Brand.Current with { ProductName = "NUL" }).Validate());
            });
            Check("Invalid theme colour is rejected before loading WPF", () => {
                Throws(() => (Brand.Current with { Accent = "cyan;execute" }).Validate());
            });
            Check("Brand migration preserves legacy data without overwrite", () => {
                var root = Path.Combine(temp, "brand-migration");
                var fuse = Path.Combine(root, "FuseVPN");
                var old = Path.Combine(root, "EgorMadyarVPN");
                Directory.CreateDirectory(fuse); Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(fuse, "state.json"), "legacy-state");
                File.WriteAllText(Path.Combine(old, "device-id.txt"), "legacy-device");
                File.WriteAllText(Path.Combine(old, "settings.json"), "legacy-settings");
                File.WriteAllText(Path.Combine(old, "statistics.json"), "legacy-statistics");
                AppPaths.EnsureMigrated(root);
                var target = Path.Combine(root, "MadyarVPN");
                True(File.ReadAllText(Path.Combine(target, "state.json")) == "legacy-state", "State not migrated");
                True(File.ReadAllText(Path.Combine(target, "device-id.txt")) == "legacy-device", "Device ID not migrated");
                File.WriteAllText(Path.Combine(target, "state.json"), "new-state");
                File.WriteAllText(Path.Combine(fuse, "state.json"), "changed-legacy-state");
                AppPaths.EnsureMigrated(root);
                True(File.ReadAllText(Path.Combine(target, "state.json")) == "new-state", "New Madyar data was overwritten");
            });
            Check("Wide Home retains a separate server column", () => {
                var x=DesktopLayout.ForHome(1120);
                True(!x.Stacked && x.OverviewWidth==720, "Wrong desktop geometry");
            });
            Check("Short wide Home keeps server actions reachable by stacking", () => {
                var shortView = DesktopLayout.ForHome(1120, 360);
                True(shortView.Stacked && shortView.OverviewWidth == 1120, "Short server panel would clip its fixed controls");
                True(!DesktopLayout.ForHome(1120, 760).Stacked, "Tall desktop no longer restores right column");
            });
            Check("Narrow Home stacks panels instead of pushing outside window", () => {
                var x=DesktopLayout.ForHome(700);
                True(x.Stacked && x.OverviewWidth==700, "Narrow view not stacked");
            });
            Check("Small overview stacks hero and detail cards", () => {
                var x=DesktopLayout.ForHome(500);
                True(x.HeroStacked && x.DetailsStacked, "Controls will overlap");
            });
            Check("Work area at 150 percent scaling remains respected", () => {
                var b=DesktopLayout.ForWorkArea(910,512);
                True(b.Width<=886 && b.Height<=488 && b.MinHeight<=b.Height && b.MinWidth<=b.Width,"Window exceeds work area");
            });
            Check("Tiny work areas do not inherit an oversized hard minimum", () => {
                var b=DesktopLayout.ForWorkArea(640,400);
                True(b.Width<=616 && b.Height<=376 && b.MinWidth<=616 && b.MinHeight<=376,"Hardcoded minimum clipped the window");
            });
            Check("Invalid layout extents produce finite safe defaults", () => {
                var b=DesktopLayout.ForWorkArea(double.NaN,double.PositiveInfinity);
                True(double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width>0 && b.Height>0,"Invalid window extent");
            });
            Check("Startup report preserves nested cause beyond long wrapper and hides credentials", () => {
                var key="fixture.app:"+new string('x',32);
                var error=new InvalidOperationException(new string('w',2400),new FormatException("ROOT_CAUSE "+key+" https://example.invalid/private?token=test"));
                var report=StartupDiagnostics.Format(error);
                True(report.Contains("ROOT_CAUSE") && report.Contains("FormatException"),"Nested cause was truncated");
                True(!report.Contains(key) && !report.Contains("token=test"),"Credential present in diagnostics");
            });
            Check("Default settings are valid", () => True(new AppSettings().Validate() is null, "Invalid defaults"));
            Check("Draft settings cannot mutate active settings", () => { var a = new AppSettings(); var b = a.Copy(); b.Mtu = 1280; True(a.Mtu == 1500, "Copy mutated original"); });
            Check("Unsafe MTU and timeout are rejected", () => { True(new AppSettings { Mtu = 1200 }.Validate() is not null, "MTU accepted"); True(new AppSettings { ConnectTimeoutSeconds = 0 }.Validate() is not null, "Timeout accepted"); });
            Check("Multiple subscription links are deduplicated", () => { var r = SubscriptionRequest.Parse("", "https://a.invalid/one\nhttps://b.invalid/two\r\nhttps://a.invalid/one"); True(r.Urls.Count == 2, "Wrong link count"); });
            Check("Editing accepts only one HTTP link", () => { Throws(() => SubscriptionRequest.Parse(null, "file:///C:/x")); Throws(() => SubscriptionRequest.Parse(null, "https://a.invalid\nhttps://b.invalid", true)); });
            Check("URL credentials cannot become subscription login", () => Throws(() => SubscriptionRequest.Parse(null, "https://alice:secret@example.invalid")));
            Check("Lithuanian flag resolves without font support", () => { var x = CountryResolver.Resolve("🇱🇹 Литва / узел 2"); True(x.Country == "Lithuania" && x.Flag == "🇱🇹", "Country not resolved"); });
            Check("Hungarian flag resolves", () => True(CountryResolver.Resolve("🇭🇺 Budapest").Country == "Hungary", "HU unresolved"));
            Check("Russian Netherlands label resolves", () => True(CountryResolver.Resolve("Нидерланды, Extra").Country == "Netherlands", "NL unresolved"));
            Check("Unknown server is not assigned fake geography", () => True(CountryResolver.Resolve("AUTO fastest").Country == "Other", "Made-up country"));
            Check("Flag converts to ISO code", () => True(DisplayFormat.CountryCode("🇳🇱") == "NL" && DisplayFormat.CountryCode("🌐") == "ZZ", "Wrong ISO"));
            Check("IPv6 endpoint is unambiguous", () => True(DisplayFormat.Endpoint("2001:db8::1", 443) == "[2001:db8::1]:443", "IPv6 formatting"));
            Check("Missing traffic remains unknown", () => True(DisplayFormat.Bytes(null) == "—", "Invented zero"));
            Check("Log export redacts links and identifiers", () => { var t = DisplayFormat.Redact("URL https://example.invalid/private?token=abc VLESS vless://deadbeef-aaaa-bbbb-cccc-123456789abc@host.invalid:443 ID deadbeef-aaaa-bbbb-cccc-123456789abc"); True(!t.Contains("token=") && !t.Contains("deadbeef"), "Credential leak"); });
            Check("ANSI escapes are removed", () => True(DisplayFormat.Redact("\u001b[31mERROR\u001b[0m") == "ERROR", "ANSI remained"));
            Check("Cache can read legacy 0.3 state", () => { var a = JsonSerializer.Deserialize<AppState>("{\"Subscriptions\":[{\"Id\":\"a\",\"Name\":\"Example\",\"Url\":\"https://example.invalid\"}],\"LastNodeRawUri\":null}")!; True(a.Subscriptions.Count == 1 && a.Subscriptions[0].CachedNodes.Count == 0, "Legacy migration failed"); });
            Check("Traffic first sample is a baseline, not historical traffic", () => { var a = new TrafficAccumulator(); var s = a.Add(900000, 500000, DateTimeOffset.UtcNow); True(s.ReceivedBytes == 0 && s.SentBytes == 0, "Counted old traffic"); });
            Check("Traffic deltas and rates are measured", () => { var a = new TrafficAccumulator(); var t = DateTimeOffset.UtcNow; a.Add(100, 200, t); var s = a.Add(3100, 4200, t.AddSeconds(2)); True(s.ReceivedBytes == 3000 && s.SentBytes == 4000 && s.DownloadRate == 1500, "Wrong counters"); });
            Check("Counter reset does not create negative traffic", () => { var a = new TrafficAccumulator(); var t = DateTimeOffset.UtcNow; a.Add(1000, 1000, t); var s = a.Add(10, 10, t.AddSeconds(1)); True(s.ReceivedBytes == 0 && s.DownloadRate == 0, "Negative counter"); });
            Check("TUN keeps native interface detection and strict routes", () => { using var d = JsonDocument.Parse(SingBoxConfigBuilder.Build("192.0.2.10")); var r = d.RootElement; True(r.GetProperty("route").GetProperty("auto_detect_interface").GetBoolean(), "No auto detection"); True(r.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean(), "No strict routes"); True(r.GetProperty("inbounds")[0].GetProperty("interface_name").GetString() == "MadyarVPN", "Old TUN name remained"); True(r.GetProperty("route").GetProperty("rules")[0].GetProperty("ip_cidr")[0].GetString() == "192.0.2.10/32", "No endpoint bypass"); });
            Check("IPv6 endpoint guard uses /128", () => True(SingBoxConfigBuilder.Build("2001:db8::10").Contains("2001:db8::10/128"), "Missing IPv6 guard"));
            Check("DNS and MTU settings affect generated config", () => { using var d = JsonDocument.Parse(SingBoxConfigBuilder.Build("192.0.2.10", settings: new AppSettings { DnsProvider = "Quad9", Mtu = 1400 })); var r = d.RootElement; True(r.GetProperty("inbounds")[0].GetProperty("mtu").GetInt32() == 1400, "MTU ignored"); True(r.GetProperty("dns").GetProperty("servers")[0].GetProperty("server").GetString() == "9.9.9.9", "DNS ignored"); True(r.GetProperty("dns").GetProperty("servers")[0].GetProperty("detour").GetString() == "xray", "DNS bypassed tunnel"); });
            Check("VLESS REALITY fields survive parsing", () => { var ok = VlessUriParser.TryParse("vless://00000000-0000-4000-8000-000000000001@example.invalid:443?type=tcp&security=reality&sni=example.org&pbk=test-public-key&sid=1234#Netherlands", out var n, out var error); True(ok && n!.Transport == "raw" && n.PublicKey == "test-public-key" && n.ShortId == "1234", error ?? "Parser changed"); using var d = JsonDocument.Parse(XrayConfigBuilder.Build(n!, "192.0.2.10")); True(d.RootElement.GetProperty("inbounds")[0].GetProperty("listen").GetString() == "127.0.0.1", "SOCKS exposed outside loopback"); });
            Check("Atomic store preserves prior version", () => { var path = Path.Combine(temp, "settings.json"); var s = new AtomicJsonStore<AppSettings>(path); s.SaveAsync(new AppSettings { Mtu = 1500 }).GetAwaiter().GetResult(); s.SaveAsync(new AppSettings { Mtu = 1400 }).GetAwaiter().GetResult(); True(s.LoadAsync().GetAwaiter().GetResult().Mtu == 1400, "New version missing"); True(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak"))!.Mtu == 1500, "Backup missing"); });
            Check("Corrupt JSON restores backup without destroying original", () => { var path = Path.Combine(temp, "broken.json"); File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "{\"Mtu\":1400}"); var s = new AtomicJsonStore<AppSettings>(path); True(s.LoadAsync().GetAwaiter().GetResult().Mtu == 1400, "Did not recover"); True(File.ReadAllText(path) == "broken" && Directory.GetFiles(temp, "broken.json.corrupt-*").Length > 0, "Original not preserved"); });
            Check("Save snapshots before asynchronous mutation", () => { var s = new AtomicJsonStore<AppSettings>(Path.Combine(temp, "snapshot.json")); var v = new AppSettings { Mtu = 1400 }; var pending = s.SaveAsync(v); v.Mtu = 1280; pending.GetAwaiter().GetResult(); True(s.LoadAsync().GetAwaiter().GetResult().Mtu == 1400, "Snapshot was mutable"); });
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        Console.WriteLine($"Checks: {_passed} passed, {_failed} failed. No VPN connection or real user data accessed.");
        return _failed == 0 ? 0 : 1;
    }
}
