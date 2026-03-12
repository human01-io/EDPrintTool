using System.Text.Json;
using System.Text.Json.Nodes;
using EDPrintTool.Models;

namespace EDPrintTool;

public class PrinterStore
{
    private readonly Dictionary<string, PrinterInfo> _printers = new();
    private readonly string _configPath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public PrinterStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EDPrintTool");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "printers.json");
        LoadConfig();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var list = JsonSerializer.Deserialize<List<PrinterInfo>>(json, JsonOpts);
                if (list != null)
                {
                    foreach (var p in list)
                        _printers[p.Id] = p;
                    Console.WriteLine($"[Printers] Loaded {_printers.Count} printer(s) from config");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Printers] Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        var json = JsonSerializer.Serialize(_printers.Values.ToList(), JsonOpts);
        File.WriteAllText(_configPath, json);
    }

    public List<PrinterInfo> GetPrinters()
    {
        lock (_lock)
            return _printers.Values.ToList();
    }

    public PrinterInfo? GetPrinter(string id)
    {
        lock (_lock)
            return _printers.TryGetValue(id, out var p) ? p : null;
    }

    public PrinterInfo AddPrinter(PrinterInfo printer)
    {
        lock (_lock)
        {
            printer.EnsureId();
            if (string.IsNullOrEmpty(printer.AddedAt))
                printer.AddedAt = DateTime.UtcNow.ToString("o");
            printer.Settings.ApplyPreset();
            _printers[printer.Id] = printer;
            SaveConfig();
            return printer;
        }
    }

    public PrinterInfo? UpdateSettings(string id, JsonObject partial)
    {
        lock (_lock)
        {
            if (!_printers.TryGetValue(id, out var printer))
                return null;

            var s = printer.Settings;

            if (partial.TryGetPropertyValue("labelPreset", out var lp)) s.LabelPreset = lp!.GetValue<string>();
            if (partial.TryGetPropertyValue("widthDots", out var w)) s.WidthDots = w!.GetValue<int>();
            if (partial.TryGetPropertyValue("heightDots", out var h)) s.HeightDots = h!.GetValue<int>();
            if (partial.TryGetPropertyValue("dpi", out var d)) s.Dpi = d!.GetValue<int>();
            if (partial.TryGetPropertyValue("darkness", out var dk)) s.Darkness = dk!.GetValue<int>();
            if (partial.TryGetPropertyValue("speed", out var sp)) s.Speed = sp!.GetValue<int>();
            if (partial.TryGetPropertyValue("orientation", out var o)) s.Orientation = o!.GetValue<string>();
            if (partial.TryGetPropertyValue("mediaType", out var mt)) s.MediaType = mt!.GetValue<string>();
            if (partial.TryGetPropertyValue("printMode", out var pm)) s.PrintMode = pm!.GetValue<string>();

            s.ApplyPreset();
            SaveConfig();
            return printer;
        }
    }

    public bool RemovePrinter(string id)
    {
        lock (_lock)
        {
            var removed = _printers.Remove(id);
            if (removed) SaveConfig();
            return removed;
        }
    }
}
