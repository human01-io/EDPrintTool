using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EDPrintTool.Models;

public partial class PrinterInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "usb"; // "network" | "usb"

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 9100;

    [JsonPropertyName("cupsQueue")]
    public string? CupsQueue { get; set; }

    [JsonPropertyName("windowsPrinter")]
    public string? WindowsPrinter { get; set; }

    [JsonPropertyName("settings")]
    public PrinterSettings Settings { get; set; } = new();

    [JsonPropertyName("addedAt")]
    public string AddedAt { get; set; } = "";

    public void EnsureId()
    {
        if (string.IsNullOrEmpty(Id))
            Id = NonAlphaNum().Replace(Name.ToLowerInvariant(), "-");
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNum();
}
