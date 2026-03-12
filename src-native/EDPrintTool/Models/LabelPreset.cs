using System.Text.Json.Serialization;

namespace EDPrintTool.Models;

public class LabelPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("widthDots")]
    public int WidthDots { get; set; }

    [JsonPropertyName("heightDots")]
    public int HeightDots { get; set; }

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    private static readonly LabelPreset[] _presets =
    [
        new() { Id = "4x8",       WidthDots = 812,  HeightDots = 1624, Desc = "4\" x 8\"" },
        new() { Id = "4x6",       WidthDots = 812,  HeightDots = 1218, Desc = "4\" x 6\" (shipping)" },
        new() { Id = "4x4",       WidthDots = 812,  HeightDots = 812,  Desc = "4\" x 4\"" },
        new() { Id = "4x3",       WidthDots = 812,  HeightDots = 609,  Desc = "4\" x 3\"" },
        new() { Id = "4x2",       WidthDots = 812,  HeightDots = 406,  Desc = "4\" x 2\"" },
        new() { Id = "4x1",       WidthDots = 812,  HeightDots = 203,  Desc = "4\" x 1\"" },
        new() { Id = "3x2",       WidthDots = 609,  HeightDots = 406,  Desc = "3\" x 2\"" },
        new() { Id = "3x1",       WidthDots = 609,  HeightDots = 203,  Desc = "3\" x 1\"" },
        new() { Id = "2.25x1.25", WidthDots = 457,  HeightDots = 254,  Desc = "2.25\" x 1.25\"" },
        new() { Id = "2x1",       WidthDots = 406,  HeightDots = 203,  Desc = "2\" x 1\"" },
        new() { Id = "1.5x1",    WidthDots = 305,  HeightDots = 203,  Desc = "1.5\" x 1\"" },
        new() { Id = "1x0.5",    WidthDots = 203,  HeightDots = 102,  Desc = "1\" x 0.5\" (jewelry)" },
    ];

    public static LabelPreset[] GetAll() => _presets;

    public static LabelPreset? Get(string id) =>
        Array.Find(_presets, p => p.Id == id);
}
