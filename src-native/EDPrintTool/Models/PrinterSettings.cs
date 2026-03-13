using System.Text.Json.Serialization;

namespace EDPrintTool.Models;

public class PrinterSettings
{
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

    public void ApplyPreset()
    {
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
