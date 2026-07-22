using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using QRCoder;

namespace cs2_basic_plugin;

public class PluginConfig : BasePluginConfig
{
    public override int Version { get; set; } = 1;
    public string BackendUrl { get; set; } = "http://localhost:3000/api/auth/login";
    public int HttpTimeoutSeconds { get; set; } = 10;
    public string CommandName { get; set; } = "css_login";
    public int QrDisplaySeconds { get; set; } = 30;
}

public class Cs2BasicPlugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "CS2 QR Login";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "cyqmq";
    public override string ModuleDescription => "QR code login plugin";

    public PluginConfig Config { get; set; } = new();
    private readonly HttpClient _httpClient = new();
    private readonly Dictionary<int, ActiveQr> _activeQrs = new();

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        AddCommand(Config.CommandName, "Request a QR login link", OnLoginCommand);
        AddCommand("css_testconn", "Test backend connection", OnTestConnection);
        AddTimer(0.3f, OnRefreshTick, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        Console.WriteLine($"[{ModuleName}] Loaded. Backend: {Config.BackendUrl}");
    }

    [CommandHelper(minArgs: 0, usage: "!login")]
    public void OnLoginCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            info.ReplyToCommand("Only real players can use this command.");
            return;
        }

        player.PrintToCenterHtml("Connecting...");

        var steamId = player.SteamID.ToString();
        var playerName = player.PlayerName;
        var ipAddress = player.IpAddress ?? "0.0.0.0";

        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.HttpTimeoutSeconds));
                var payload = new { steam_id = steamId, player_name = playerName, ip_address = ipAddress };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(Config.BackendUrl, content, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    await DispatchToMain(() =>
                        player.PrintToChat($" {ChatColors.Red}Auth server error ({(int)response.StatusCode})"));
                    return;
                }

                var result = JsonSerializer.Deserialize<LoginResponse>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result?.LoginUrl == null)
                {
                    await DispatchToMain(() =>
                        player.PrintToChat($" {ChatColors.Red}Invalid server response"));
                    return;
                }

                await DispatchToMain(() => ShowQrToPlayer(player, result.LoginUrl));
            }
            catch (TaskCanceledException)
            {
                await DispatchToMain(() =>
                    player.PrintToChat($" {ChatColors.Red}Auth server timed out"));
            }
            catch (Exception)
            {
                await DispatchToMain(() =>
                    player.PrintToChat($" {ChatColors.Red}Failed to connect to auth server"));
            }
        });
    }

    [CommandHelper(minArgs: 0, usage: "!testconn")]
    public void OnTestConnection(CCSPlayerController? player, CommandInfo info)
    {
        string caller = player != null ? $"{player.PlayerName} ({player.SteamID})" : "Server console";
        Console.WriteLine($"[{ModuleName}] Connection test initiated by {caller}...");

        info.ReplyToCommand($" Testing backend: {Config.BackendUrl}");

        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.HttpTimeoutSeconds));
                var payload = new { steam_id = "0", player_name = "test", ip_address = "127.0.0.1" };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(Config.BackendUrl, content, cts.Token);
                sw.Stop();

                var body = await response.Content.ReadAsStringAsync(cts.Token);
                string msg = $"[{ModuleName}] Connection test result: {response.StatusCode} ({sw.ElapsedMilliseconds}ms)\n  Body: {body}";

                Console.WriteLine(msg);
                await DispatchToMain(() => info.ReplyToCommand(msg.Replace($"[{ModuleName}] ", "")));
            }
            catch (TaskCanceledException)
            {
                string msg = $"[{ModuleName}] Connection test FAILED: timeout ({Config.HttpTimeoutSeconds}s)";
                Console.WriteLine(msg);
                await DispatchToMain(() => info.ReplyToCommand(" Connection test FAILED: timeout"));
            }
            catch (Exception ex)
            {
                string msg = $"[{ModuleName}] Connection test FAILED: {ex.Message}";
                Console.WriteLine(msg);
                await DispatchToMain(() => info.ReplyToCommand($" Connection test FAILED: {ex.Message}"));
            }
        });
    }

    private void ShowQrToPlayer(CCSPlayerController player, string loginUrl)
    {
        string qrHtml = BuildQrHtml(loginUrl);

        lock (_activeQrs)
        {
            _activeQrs[player.Slot] = new ActiveQr
            {
                Html = qrHtml,
                ExpireAt = DateTime.UtcNow.AddSeconds(Config.QrDisplaySeconds)
            };
        }

        player.PrintToCenterHtml(qrHtml);
        player.PrintToChat($" {ChatColors.Green}Scan QR code to login:");
        player.PrintToChat($" {ChatColors.Default}{loginUrl}");
    }

    private void OnRefreshTick()
    {
        lock (_activeQrs)
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<int>();

            foreach (var kvp in _activeQrs)
            {
                int slot = kvp.Key;
                var qr = kvp.Value;

                if (now >= qr.ExpireAt)
                {
                    toRemove.Add(slot);
                    continue;
                }

                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid)
                {
                    toRemove.Add(slot);
                    continue;
                }

                player.PrintToCenterHtml(qr.Html);
            }

            foreach (int slot in toRemove)
                _activeQrs.Remove(slot);
        }
    }

    private static string BuildQrHtml(string loginUrl)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(loginUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new AsciiQRCode(data);
        string ascii = qr.GetGraphic(1);
        return ascii.Replace("\r\n", "<br>").Replace("\n", "<br>")
            + "<br><font color='#00FF00'>Scan QR code to login</font>";
    }

    private Task DispatchToMain(Action action)
    {
        var tcs = new TaskCompletionSource();
        Server.NextFrame(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public override void Unload(bool hotReload)
    {
        _httpClient.Dispose();
        _activeQrs.Clear();
    }
}

public class ActiveQr
{
    public string Html { get; set; } = "";
    public DateTime ExpireAt { get; set; }
}

public class LoginResponse
{
    public string LoginUrl { get; set; } = "";
    public string? QrCodeBase64 { get; set; }
}
