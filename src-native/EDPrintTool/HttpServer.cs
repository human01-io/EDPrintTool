using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EDPrintTool.Models;

namespace EDPrintTool;

public class HttpServer
{
    private readonly HttpListener _listener;
    private readonly PrinterStore _store;
    private readonly string _publicDir;
    private CancellationTokenSource? _cts;

    public event Action<string, bool>? OnActivity; // message, isError

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly Dictionary<string, string> MimeTypes = new()
    {
        [".html"] = "text/html",
        [".js"] = "application/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
    };

    public HttpServer(PrinterStore store, string publicDir)
    {
        _store = store;
        _publicDir = publicDir;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://localhost:8189/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        Console.WriteLine("[EDPrintTool] Server running on http://localhost:8189");
        _ = AcceptLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleRequest(ctx);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // CORS headers
        res.Headers.Set("Access-Control-Allow-Origin", "*");
        res.Headers.Set("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS");
        res.Headers.Set("Access-Control-Allow-Headers", "Content-Type");

        try
        {
            // OPTIONS preflight
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            // WebSocket upgrade
            if (req.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await HandleWebSocket(wsCtx.WebSocket);
                return;
            }

            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            // ─── API Routes ───────────────────────────────
            if (path.StartsWith("/api/"))
            {
                await RouteApi(method, path, req, res);
                return;
            }

            // ─── Static files ─────────────────────────────
            ServeStaticFile(path, res);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJson(res, 500, new { error = ex.Message });
            }
            catch { }
        }
    }

    private async Task RouteApi(string method, string path, HttpListenerRequest req, HttpListenerResponse res)
    {
        // GET /api/status
        if (method == "GET" && path == "/api/status")
        {
            await WriteJson(res, 200, new
            {
                status = "running",
                version = "1.0.0",
                printers = _store.GetPrinters().Count,
            });
            return;
        }

        // GET /api/label-presets
        if (method == "GET" && path == "/api/label-presets")
        {
            await WriteJson(res, 200, LabelPreset.GetAll());
            return;
        }

        // GET /api/printers
        if (method == "GET" && path == "/api/printers")
        {
            await WriteJson(res, 200, _store.GetPrinters());
            return;
        }

        // GET /api/printers/discover
        if (method == "GET" && path == "/api/printers/discover")
        {
            var discovered = await PrinterDiscovery.DiscoverAsync();
            await WriteJson(res, 200, discovered);
            return;
        }

        // POST /api/printers
        if (method == "POST" && path == "/api/printers")
        {
            var body = await ReadBody(req);
            var printer = JsonSerializer.Deserialize<PrinterInfo>(body, JsonOpts);
            if (printer == null) { await WriteJson(res, 400, new { error = "Invalid body" }); return; }
            var added = _store.AddPrinter(printer);
            OnActivity?.Invoke($"Printer added: {added.Name} ({added.Id})", false);
            await WriteJson(res, 201, added);
            return;
        }

        // PATCH /api/printers/{id}/settings
        if (method == "PATCH" && path.StartsWith("/api/printers/") && path.EndsWith("/settings"))
        {
            var id = ExtractSegment(path, 2); // /api/printers/{id}/settings
            var body = await ReadBody(req);
            var partial = JsonNode.Parse(body)?.AsObject();
            if (partial == null) { await WriteJson(res, 400, new { error = "Invalid body" }); return; }
            var updated = _store.UpdateSettings(id, partial);
            if (updated == null) { await WriteJson(res, 404, new { error = $"Printer not found: {id}" }); return; }
            OnActivity?.Invoke($"Settings updated: {id}", false);
            await WriteJson(res, 200, updated);
            return;
        }

        // DELETE /api/printers/{id}
        if (method == "DELETE" && path.StartsWith("/api/printers/") && !path.Contains("/settings"))
        {
            var id = ExtractSegment(path, 2);
            var removed = _store.RemovePrinter(id);
            OnActivity?.Invoke($"Printer removed: {id}", false);
            await WriteJson(res, 200, new { removed });
            return;
        }

        // POST /api/print/{printerId}
        if (method == "POST" && path.StartsWith("/api/print/") && path != "/api/print-raw")
        {
            var printerId = ExtractSegment(path, 2);
            var body = await ReadBody(req);

            string zpl;
            int copies = 1;
            bool applySettings = true;

            try
            {
                var obj = JsonNode.Parse(body)?.AsObject();
                if (obj != null)
                {
                    zpl = obj["zpl"]?.GetValue<string>() ?? "";
                    if (obj.TryGetPropertyValue("copies", out var c)) copies = c!.GetValue<int>();
                    if (obj.TryGetPropertyValue("applySettings", out var a)) applySettings = a!.GetValue<bool>();
                }
                else
                {
                    zpl = body;
                }
            }
            catch
            {
                zpl = body;
            }

            if (string.IsNullOrWhiteSpace(zpl))
            {
                await WriteJson(res, 400, new { error = "Missing ZPL" });
                return;
            }

            var printer = _store.GetPrinter(printerId);
            if (printer == null)
            {
                await WriteJson(res, 404, new { error = $"Printer not found: {printerId}" });
                return;
            }

            try
            {
                var msg = await RawPrinter.PrintAsync(printer, zpl, copies, applySettings);
                OnActivity?.Invoke($"Print OK ({copies}x) → {printerId}: {msg}", false);
                await WriteJson(res, 200, new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                OnActivity?.Invoke($"Print FAILED → {printerId}: {ex.Message}", true);
                await WriteJson(res, 500, new { error = ex.Message });
            }
            return;
        }

        // POST /api/print-escpos/{printerId} — structured ESC/POS commands
        if (method == "POST" && path.StartsWith("/api/print-escpos/"))
        {
            var printerId = ExtractSegment(path, 2);
            var body = await ReadBody(req);

            try
            {
                var obj = JsonNode.Parse(body)?.AsObject();
                var commandsNode = obj?["commands"]?.AsArray();
                var copies = obj?["copies"]?.GetValue<int>() ?? 1;

                if (commandsNode == null)
                {
                    await WriteJson(res, 400, new { error = "Missing commands array" });
                    return;
                }

                var printer = _store.GetPrinter(printerId);
                if (printer == null)
                {
                    await WriteJson(res, 404, new { error = $"Printer not found: {printerId}" });
                    return;
                }

                var s = printer.Settings;
                var payload = RawPrinter.BuildFromCommands(s.PaperWidth, commandsNode, copies);
                var result = await RawPrinter.SendRawAsync(printer, payload);
                OnActivity?.Invoke($"ESC/POS print OK ({copies}x) → {printerId}: {result}", false);
                await WriteJson(res, 200, new { success = true, message = result });
            }
            catch (Exception ex)
            {
                OnActivity?.Invoke($"ESC/POS print FAILED → {printerId}: {ex.Message}", true);
                await WriteJson(res, 500, new { error = ex.Message });
            }
            return;
        }

        // POST /api/print-raw
        if (method == "POST" && path == "/api/print-raw")
        {
            var body = await ReadBody(req);
            var obj = JsonNode.Parse(body)?.AsObject();
            var host = obj?["host"]?.GetValue<string>();
            var port = obj?["port"]?.GetValue<int>() ?? 9100;
            var zpl = obj?["zpl"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(zpl))
            {
                await WriteJson(res, 400, new { error = "host and zpl are required" });
                return;
            }

            try
            {
                var msg = await RawPrinter.PrintNetworkAsync(host, port, zpl);
                OnActivity?.Invoke($"Raw print OK → {host}:{port}", false);
                await WriteJson(res, 200, new { success = true, message = msg });
            }
            catch (Exception ex)
            {
                OnActivity?.Invoke($"Raw print FAILED → {host}:{port}: {ex.Message}", true);
                await WriteJson(res, 500, new { error = ex.Message });
            }
            return;
        }

        await WriteJson(res, 404, new { error = "Not found" });
    }

    // ─── WebSocket handler ──────────────────────────────────────

    private async Task HandleWebSocket(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var response = await HandleWsMessage(text);
                var bytes = Encoding.UTF8.GetBytes(response);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch { }
        finally
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
    }

    private async Task<string> HandleWsMessage(string text)
    {
        try
        {
            var msg = JsonNode.Parse(text)?.AsObject();
            if (msg == null) return JsonErr(null, "Invalid JSON");

            var action = msg["action"]?.GetValue<string>();
            var requestId = msg["requestId"]?.GetValue<string>();

            switch (action)
            {
                case "status":
                    return JsonOk(requestId, new { status = "running", version = "1.0.0", printers = _store.GetPrinters().Count });

                case "listPrinters":
                    return JsonOk(requestId, _store.GetPrinters());

                case "getLabelPresets":
                    return JsonOk(requestId, LabelPreset.GetAll());

                case "discoverPrinters":
                    var discovered = await PrinterDiscovery.DiscoverAsync();
                    return JsonOk(requestId, discovered);

                case "addPrinter":
                    var printerData = msg["printer"];
                    if (printerData == null) return JsonErr(requestId, "Missing printer");
                    var printer = JsonSerializer.Deserialize<PrinterInfo>(printerData.ToJsonString(), JsonOpts);
                    if (printer == null) return JsonErr(requestId, "Invalid printer data");
                    var added = _store.AddPrinter(printer);
                    return JsonOk(requestId, added);

                case "updateSettings":
                {
                    var id = msg["printerId"]?.GetValue<string>();
                    var settings = msg["settings"]?.AsObject();
                    if (id == null || settings == null) return JsonErr(requestId, "Missing printerId or settings");
                    var updated = _store.UpdateSettings(id, settings);
                    return updated != null ? JsonOk(requestId, updated) : JsonErr(requestId, $"Printer not found: {id}");
                }

                case "removePrinter":
                {
                    var id = msg["printerId"]?.GetValue<string>();
                    if (id == null) return JsonErr(requestId, "Missing printerId");
                    var removed = _store.RemovePrinter(id);
                    return JsonOk(requestId, new { removed });
                }

                case "print":
                {
                    var pid = msg["printerId"]?.GetValue<string>();
                    var zpl = msg["zpl"]?.GetValue<string>();
                    var copies = msg["copies"]?.GetValue<int>() ?? 1;
                    var apply = msg["applySettings"]?.GetValue<bool>() ?? true;
                    if (pid == null || zpl == null) return JsonErr(requestId, "Missing printerId or zpl");
                    var p = _store.GetPrinter(pid);
                    if (p == null) return JsonErr(requestId, $"Printer not found: {pid}");
                    var result = await RawPrinter.PrintAsync(p, zpl, copies, apply);
                    OnActivity?.Invoke($"Print OK ({copies}x) → {pid}", false);
                    return JsonOk(requestId, new { success = true, message = result });
                }

                case "printEscPos":
                {
                    var pid2 = msg["printerId"]?.GetValue<string>();
                    var cmds = msg["commands"]?.AsArray();
                    var copies2 = msg["copies"]?.GetValue<int>() ?? 1;
                    if (pid2 == null || cmds == null) return JsonErr(requestId, "Missing printerId or commands");
                    var p2 = _store.GetPrinter(pid2);
                    if (p2 == null) return JsonErr(requestId, $"Printer not found: {pid2}");
                    var payload = RawPrinter.BuildFromCommands(p2.Settings.PaperWidth, cmds, copies2);
                    var result2 = await RawPrinter.SendRawAsync(p2, payload);
                    OnActivity?.Invoke($"ESC/POS print OK ({copies2}x) → {pid2}", false);
                    return JsonOk(requestId, new { success = true, message = result2 });
                }

                case "printRaw":
                {
                    var host = msg["host"]?.GetValue<string>();
                    var port = msg["port"]?.GetValue<int>() ?? 9100;
                    var zpl = msg["zpl"]?.GetValue<string>();
                    if (host == null || zpl == null) return JsonErr(requestId, "Missing host or zpl");
                    var result = await RawPrinter.PrintNetworkAsync(host, port, zpl);
                    return JsonOk(requestId, new { success = true, message = result });
                }

                default:
                    return JsonErr(requestId, $"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return JsonErr(null, ex.Message);
        }
    }

    // ─── Static file serving ────────────────────────────────────

    private void ServeStaticFile(string urlPath, HttpListenerResponse res)
    {
        if (urlPath == "/") urlPath = "/index.html";

        var filePath = Path.Combine(_publicDir, urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        filePath = Path.GetFullPath(filePath);

        // Security: ensure path is within public dir
        if (!filePath.StartsWith(Path.GetFullPath(_publicDir)))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }

        if (!File.Exists(filePath))
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        res.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");
        var bytes = File.ReadAllBytes(filePath);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static async Task<string> ReadBody(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static string ExtractSegment(string path, int index)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return index < segments.Length ? Uri.UnescapeDataString(segments[index]) : "";
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, object data)
    {
        res.StatusCode = status;
        res.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static string JsonOk(string? requestId, object data)
    {
        return JsonSerializer.Serialize(new { requestId, success = true, data }, JsonOpts);
    }

    private static string JsonErr(string? requestId, string error)
    {
        return JsonSerializer.Serialize(new { requestId, success = false, error }, JsonOpts);
    }
}
