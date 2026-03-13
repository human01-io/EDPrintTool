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
        var bytes = new List<byte>();
        // ESC @ — Initialize printer
        bytes.AddRange(new byte[] { 0x1B, 0x40 });
        return bytes.ToArray();
    }

    /// <summary>
    /// Build ESC/POS cut + feed sequence appended after content.
    /// </summary>
    public byte[] BuildEscPosCut()
    {
        var bytes = new List<byte>();
        // ESC d n — Print and feed n lines
        if (FeedLines > 0)
        {
            bytes.AddRange(new byte[] { 0x1B, 0x64, (byte)Math.Clamp(FeedLines, 0, 255) });
        }
        // GS V — Cut paper
        if (AutoCut)
        {
            if (CutType == "full")
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x00 }); // GS V 0 = full cut
            else
                bytes.AddRange(new byte[] { 0x1D, 0x56, 0x01 }); // GS V 1 = partial cut
        }
        return bytes.ToArray();
    }

    public PrinterSettings Clone() => (PrinterSettings)MemberwiseClone();
}
