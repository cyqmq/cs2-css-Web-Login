using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        AddCommand(Config.CommandName, "Request a QR login link", OnLoginCommand);
        AddCommand("css_testconn", "Test backend connection", OnTestConnection);
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

        player.PrintToCenterHtml("Connecting...<br>Please wait");

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
        string qrNormal = GenerateQrAscii(loginUrl, "#", " ");
        string qrInvert = GenerateQrAscii(loginUrl, " ", "#");

        // Log raw QR to server console for debugging
        Console.WriteLine($"[{ModuleName}] QR for {player.PlayerName}:");
        Console.WriteLine(qrNormal);

        // 1. Center screen instruction
        player.PrintToCenterHtml("<font color='#00FF00'>Open console (~) to scan QR code</font>");

        // 2. Print QR code to player's console (monospace font, scannable)
        player.PrintToConsole("=============== QR Login (try both) ===============");
        player.PrintToConsole("--- Version 1 (dark on light) ---");
        foreach (string line in qrNormal.Split('\n'))
            player.PrintToConsole(line.TrimEnd('\r'));
        player.PrintToConsole("");
        player.PrintToConsole("--- Version 2 (light on dark) ---");
        foreach (string line in qrInvert.Split('\n'))
            player.PrintToConsole(line.TrimEnd('\r'));
        player.PrintToConsole("---------------------------------------------------");
        player.PrintToConsole($"URL: {loginUrl}");
        player.PrintToConsole("Scan the QR code above with your phone.");

        // 3. Chat fallback
        player.PrintToChat($" {ChatColors.Green}Open console (~) to scan QR code");
        player.PrintToChat($" {ChatColors.Default}{loginUrl}");
    }

    private static string GenerateQrAscii(string loginUrl, string dark, string light)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(loginUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new AsciiQRCode(data);
        return qr.GetGraphic(2, dark, light);
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
    }
}

public class LoginResponse
{
    public string LoginUrl { get; set; } = "";
    public string? QrCodeBase64 { get; set; }
}
