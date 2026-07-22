using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Enum;
using CS2MenuManager.API.Menu;
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

        player.PrintToCenterHtml("Connecting to auth server...");

        var steamId = player.SteamID.ToString();
        var playerName = player.PlayerName;
        var ipAddress = player.IpAddress ?? "0.0.0.0";

        Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.HttpTimeoutSeconds));

                var payload = new { steam_id = steamId, player_name = playerName, ip_address = ipAddress };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

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

                var loginUrl = result.LoginUrl;
                await DispatchToMain(() => ShowQrToPlayer(player, loginUrl));
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

    private void ShowQrToPlayer(CCSPlayerController player, string loginUrl)
    {
        string qrHtml = BuildQrHtml(loginUrl);

        // Show QR on screen, refresh every second to keep it visible
        player.PrintToCenterHtml(qrHtml);

        int elapsed = 0;
        AddTimer(1.0f, () =>
        {
            elapsed++;
            if (elapsed >= Config.QrDisplaySeconds || !player.IsValid)
                return;
            player.PrintToCenterHtml(qrHtml);
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        // Console menu (CS2MenuManager)
        var consoleMenu = new ConsoleMenu("QR Login", this);
        consoleMenu.AddItem($"URL: {loginUrl}", DisableOption.DisableHideNumber);
        consoleMenu.AddItem("Re-display QR", (p, o) =>
        {
            p.PrintToCenterHtml(qrHtml);
            elapsed = 0;
        });
        consoleMenu.AddItem("Close", (p, o) => { });
        consoleMenu.Display(player, Config.QrDisplaySeconds);

        // Chat fallback
        player.PrintToChat($" {ChatColors.Green}Scan QR code to login:");
        player.PrintToChat($" {ChatColors.Default}{loginUrl}");
    }

    private static string BuildQrHtml(string loginUrl)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(loginUrl, QRCodeGenerator.ECCLevel.Q);
        using var qr = new AsciiQRCode(data);
        string qrAscii = qr.GetGraphic(1);
        string[] lines = qrAscii.Split('\n');

        var sb = new StringBuilder();
        sb.AppendLine("<pre>");
        foreach (string line in lines)
            sb.AppendLine(line);
        sb.AppendLine("</pre>");
        sb.AppendLine("<font color='#00FF00'>Scan QR code to login</font>");
        return sb.ToString();
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
