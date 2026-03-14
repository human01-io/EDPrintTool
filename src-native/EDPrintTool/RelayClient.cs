using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EDPrintTool;

/// <summary>
/// Connects EDPrintTool to a cloud relay server via outbound WebSocket.
/// Reads config from %APPDATA%\EDPrintTool\relay.json or environment variables.
/// Runs alongside the local HTTP server — both work simultaneously.
/// </summary>
public class RelayClient
{
    private readonly PrinterStore _store;
    private readonly RelayConfig _config;
    private CancellationTokenSource? _cts;
    private bool _stopped;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public event Action<string, bool>? OnActivity; // message, isError

    private class RelayConfig
    {
        public bool Enabled { get; set; }
        public string RelayUrl { get; set; } = "";
        public string LocationId { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    private RelayClient(PrinterStore store, RelayConfig config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Try to load relay config and start the client. Returns null if not configured.
    /// </summary>
    public static RelayClient? TryStart(PrinterStore store)
    {
        var config = LoadConfig();
        if (config == null) return null;

        var client = new RelayClient(store, config);
        client.Start();
        return client;
    }

    private static RelayConfig? LoadConfig()
    {
        // Priority: env vars > relay.json
        var url = Environment.GetEnvironmentVariable("RELAY_URL");
        var locationId = Environment.GetEnvironmentVariable("RELAY_LOCATION_ID");
        var apiKey = Environment.GetEnvironmentVariable("RELAY_API_KEY");

        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(locationId) && !string.IsNullOrEmpty(apiKey))
        {
            return new RelayConfig { Enabled = true, RelayUrl = url, LocationId = locationId, ApiKey = apiKey };
        }

        // Try relay.json
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EDPrintTool");
        var configPath = Path.Combine(configDir, "relay.json");

        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<RelayConfig>(json, JsonOpts);
                if (config != null && config.Enabled
                    && !string.IsNullOrEmpty(config.RelayUrl)
                    && !string.IsNullOrEmpty(config.LocationId)
                    && !string.IsNullOrEmpty(config.ApiKey))
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] Failed to load relay.json: {ex.Message}");
        }

        return null;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = ConnectLoop(_cts.Token);
    }

    public void Stop()
    {
        _stopped = true;
        _cts?.Cancel();
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        int delay = 1000;

        while (!ct.IsCancellationRequested && !_stopped)
        {
            try
            {
                OnActivity?.Invoke($"[Relay] Connecting to {_config.RelayUrl}...", false);
                await RunConnection(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                OnActivity?.Invoke($"[Relay] Connection error: {ex.Message}", true);
            }

            if (_stopped || ct.IsCancellationRequested) break;

            OnActivity?.Invoke($"[Relay] Reconnecting in {delay / 1000}s...", false);
            try { await Task.Delay(delay, ct); } catch { break; }
            delay = Math.Min(delay * 2, 30000); // exponential backoff, max 30s
        }
    }

    private async Task RunConnection(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        var uri = new Uri(_config.RelayUrl);
        await ws.ConnectAsync(uri, ct);

        OnActivity?.Invoke("[Relay] Connected to relay server", false);

        // Send auth
        var authMsg = JsonSerializer.Serialize(new
        {
            type = "auth",
            locationId = _config.LocationId,
            apiKey = _config.ApiKey,
        });
        await SendText(ws, authMsg, ct);

        var buffer = new byte[256 * 1024]; // 256KB buffer for large payloads (PDF base64)

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            JsonObject? msg;
            try { msg = JsonNode.Parse(text)?.AsObject(); }
            catch { continue; }
            if (msg == null) continue;

            var type = msg["type"]?.GetValue<string>();

            // Auth response
            if (type == "auth")
            {
                var success = msg["success"]?.GetValue<bool>() ?? false;
                if (success)
                {
                    OnActivity?.Invoke("[Relay] Authenticated successfully", false);
                }
                else
                {
                    var error = msg["error"]?.GetValue<string>() ?? "Unknown error";
                    OnActivity?.Invoke($"[Relay] Auth failed: {error}", true);
                    _stopped = true;
                    return;
                }
                continue;
            }

            // Job from relay
            if (type == "job")
            {
                var jobId = msg["jobId"]?.GetValue<string>() ?? "";
                _ = HandleJob(ws, msg, jobId, ct);
                continue;
            }
        }
    }

    private async Task HandleJob(ClientWebSocket ws, JsonObject msg, string jobId, CancellationToken ct)
    {
        try
        {
            var action = msg["action"]?.GetValue<string>();
            object? result = null;

            switch (action)
            {
                case "status":
                    result = new { status = "running", version = UpdateChecker.CurrentVersion, printers = _store.GetPrinters().Count };
                    break;

                case "listPrinters":
                    result = _store.GetPrinters();
                    break;

                case "getLabelPresets":
                    result = LabelPreset.GetAll();
                    break;

                case "discoverPrinters":
                    result = await PrinterDiscovery.DiscoverAsync();
                    break;

                case "print":
                {
                    var pid = msg["printerId"]?.GetValue<string>();
                    var zpl = msg["zpl"]?.GetValue<string>();
                    var copies = msg["copies"]?.GetValue<int>() ?? 1;
                    var apply = msg["applySettings"]?.GetValue<bool>() ?? true;
                    if (pid == null || zpl == null) throw new Exception("Missing printerId or zpl");
                    var p = _store.GetPrinter(pid) ?? throw new Exception($"Printer not found: {pid}");
                    var printResult = await RawPrinter.PrintAsync(p, zpl, copies, apply);
                    result = new { success = true, message = printResult };
                    break;
                }

                case "printEscPos":
                {
                    var pid = msg["printerId"]?.GetValue<string>();
                    var cmds = msg["commands"]?.AsArray();
                    var copies = msg["copies"]?.GetValue<int>() ?? 1;
                    if (pid == null || cmds == null) throw new Exception("Missing printerId or commands");
                    var p = _store.GetPrinter(pid) ?? throw new Exception($"Printer not found: {pid}");
                    var payload = RawPrinter.BuildFromCommands(p.Settings.PaperWidth, cmds, copies);
                    var printResult = await RawPrinter.SendRawAsync(p, payload);
                    result = new { success = true, message = printResult };
                    break;
                }

                case "printRaw":
                {
                    var host = msg["host"]?.GetValue<string>();
                    var port = msg["port"]?.GetValue<int>() ?? 9100;
                    var zpl = msg["zpl"]?.GetValue<string>();
                    if (host == null || zpl == null) throw new Exception("Missing host or zpl");
                    var printResult = await RawPrinter.PrintNetworkAsync(host, port, zpl);
                    result = new { success = true, message = printResult };
                    break;
                }

                case "printDocument":
                {
                    var pid = msg["printerId"]?.GetValue<string>();
                    var file = msg["file"]?.GetValue<string>();
                    var copies = msg["copies"]?.GetValue<int>() ?? 1;
                    if (pid == null || file == null) throw new Exception("Missing printerId or file");
                    var p = _store.GetPrinter(pid) ?? throw new Exception($"Printer not found: {pid}");
                    var printResult = await RawPrinter.PrintDocumentAsync(p, file, copies);
                    result = new { success = true, message = printResult };
                    break;
                }

                default:
                    throw new Exception($"Unknown action: {action}");
            }

            var response = JsonSerializer.Serialize(new { type = "jobResult", jobId, success = true, data = result }, JsonOpts);
            await SendText(ws, response, ct);
            OnActivity?.Invoke($"[Relay] Job OK: {msg["action"]}", false);
        }
        catch (Exception ex)
        {
            var response = JsonSerializer.Serialize(new { type = "jobResult", jobId, success = false, error = ex.Message }, JsonOpts);
            try { await SendText(ws, response, ct); } catch { }
            OnActivity?.Invoke($"[Relay] Job FAILED: {ex.Message}", true);
        }
    }

    private static async Task SendText(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
