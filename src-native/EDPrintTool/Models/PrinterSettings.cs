using System.Text.Json.Serialization;

namespace EDPrintTool.Models;

public class PrinterSettings
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "ZPL"; // "ZPL" | "ESC/POS"

    [JsonPropertyName("labelPreset")]
    public string LabelPreset { get; set; } = "4x6";

    [JsonPropertyName("widthDots")]
    public int WidthDots { get; set; } = 812;

    [JsonPropertyName("heightDots")]
    public int HeightDots { get; set; } = 1218;

    [JsonPropertyName("dpi")]
    public int Dpi { get; set; } = 203;

    [JsonPropertyName("darkness")]
    public int Darkness { get; set; } = 15;

    [JsonPropertyName("speed")]
    public int Speed { get; set; } = 4;

    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = "N";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "T";

    [JsonPropertyName("printMode")]
    public string PrintMode { get; set; } = "T";

    [JsonPropertyName("codepage")]
    public string Codepage { get; set; } = ""; // "" = printer default, or "cp437", "cp850", "cp858", "cp1252"

    // ESC/POS specific
    [JsonPropertyName("paperWidth")]
    public string PaperWidth { get; set; } = "80mm"; // "80mm" | "58mm"

    [JsonPropertyName("autoCut")]
    public bool AutoCut { get; set; } = true;

    [JsonPropertyName("cutType")]
    public string CutType { get; set; } = "partial"; // "full" | "partial"

    [JsonPropertyName("feedLines")]
    public int FeedLines { get; set; } = 4; // lines to feed before cut

    [JsonPropertyName("printerProfile")]
    public string PrinterProfile { get; set; } = "generic"; // printer capability profile

    public void ApplyPreset()
    {
        if (Language == "ESC/POS") return; // no label presets for receipt printers
        var preset = Models.LabelPreset.Get(LabelPreset);
        if (preset != null)
        {
            WidthDots = preset.WidthDots;
            HeightDots = preset.HeightDots;
        }
    }

    public string BuildSetupZPL()
    {
        return string.Join("\n",
            "^XA",
            $"^PW{WidthDots}",
            $"^LL{HeightDots}",
            $"^PR{Speed},{Speed},{Speed}",
            $"~SD{Darkness:D2}",
            $"^FW{Orientation}",
            $"^MT{MediaType}",
            $"^MM{PrintMode}",
            "^XZ"
        );
    }

    public PrinterSettings Clone() => (PrinterSettings)MemberwiseClone();
}
