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

    [JsonPropertyName("encoding")]
    public string Encoding { get; set; } = "UTF-8";

    // ESC/POS specific
    [JsonPropertyName("paperWidth")]
    public string PaperWidth { get; set; } = "80mm"; // "80mm" | "58mm"

    [JsonPropertyName("autoCut")]
    public bool AutoCut { get; set; } = true;

    [JsonPropertyName("cutType")]
    public string CutType { get; set; } = "partial"; // "full" | "partial"

    [JsonPropertyName("feedLines")]
    public int FeedLines { get; set; } = 4; // lines to feed before cut

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

    /// <summary>
    /// Build ESC/POS initialization sequence.
    /// </summary>
    public byte[] BuildEscPosInit()
    {
        // ESC @ — Initialize printer (reset to defaults)
        // Note: we do NOT send GS W (set print area width) because most receipt
        // printers auto-detect paper width and GS W can confuse some models.
        return new byte[] { 0x1B, 0x40 };
    }

    /// <summary>
    /// Build ESC/POS cut sequence appended after content.
    /// Uses GS V function B (0x41/0x42) with integrated feed for wide compatibility.
    /// </summary>
    public byte[] BuildEscPosCut()
    {
        var bytes = new List<byte>();
        byte feed = (byte)Math.Clamp(FeedLines, 0, 255);
        if (AutoCut)
        {
            if (CutType == "full")
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x41, feed }); // GS V m=65 n — Function B: full cut, feed n
            else
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x42, feed }); // GS V m=66 n — Function B: partial cut, feed n
        }
        else if (FeedLines > 0)
        {
            // No cut, just feed
            bytes.AddRange(new byte[] { 0x1B, 0x64, feed });
        }
        return bytes.ToArray();
    }

    public PrinterSettings Clone() => (PrinterSettings)MemberwiseClone();
}
