using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EDPrintTool;

public static partial class PrinterDiscovery
{
    public static async Task<List<JsonObject>> DiscoverAsync()
    {
        if (OperatingSystem.IsWindows())
            return await DiscoverWindowsAsync();
        return await DiscoverCupsAsync();
    }

    private static async Task<List<JsonObject>> DiscoverWindowsAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                Arguments = "-NoProfile -NonInteractive -Command \"Get-Printer | Select-Object Name,DriverName,PrinterStatus | ConvertTo-Json\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc == null) return [];

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var results = new List<JsonObject>();
            var node = JsonNode.Parse(output);

            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                    if (item is JsonObject obj)
                        results.Add(MakePrinterEntry(obj));
            }
            else if (node is JsonObject single)
            {
                results.Add(MakePrinterEntry(single));
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static JsonObject MakePrinterEntry(JsonObject src)
    {
        return new JsonObject
        {
            ["name"] = src["Name"]?.GetValue<string>() ?? "",
            ["driver"] = src["DriverName"]?.GetValue<string>() ?? "",
            ["status"] = src["PrinterStatus"]?.ToJsonString() ?? "",
        };
    }

    private static async Task<List<JsonObject>> DiscoverCupsAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("lpstat", "-p -d")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc == null) return [];

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var results = new List<JsonObject>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = PrinterLine().Match(line);
                if (match.Success)
                {
                    var status = line.Contains("idle") ? "idle"
                        : line.Contains("disabled") ? "disabled"
                        : "unknown";
                    results.Add(new JsonObject
                    {
                        ["name"] = match.Groups[1].Value,
                        ["status"] = status,
                    });
                }
            }
            return results;
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"^printer\s+(\S+)")]
    private static partial Regex PrinterLine();
}
