using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace EDPrintTool;

public static class UpdateChecker
{
    public const string CurrentVersion = "1.0.0";
    private const string ReleasesUrl = "https://api.github.com/repos/human01-io/EDPrintTool/releases/latest";

    public static async Task CheckForUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "EDPrintTool");
            client.Timeout = TimeSpan.FromSeconds(10);

            var json = await client.GetStringAsync(ReleasesUrl);
            var release = JsonNode.Parse(json);
            if (release == null) return;

            var tagName = release["tag_name"]?.GetValue<string>()?.TrimStart('v') ?? "";
            if (string.IsNullOrEmpty(tagName)) return;

            if (!IsNewer(tagName, CurrentVersion)) return;

            var downloadUrl = "";
            var assets = release["assets"]?.AsArray();
            if (assets != null)
            {
                foreach (var asset in assets)
                {
                    var name = asset?["name"]?.GetValue<string>() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                        break;
                    }
                }
            }

            // Fallback to release page
            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = release["html_url"]?.GetValue<string>() ?? "";

            var result = MessageBox.Show(
                $"A new version of EDPrintTool is available!\n\n" +
                $"Current: v{CurrentVersion}\n" +
                $"Latest: v{tagName}\n\n" +
                $"Would you like to download the update?",
                "EDPrintTool — Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result == DialogResult.Yes && !string.IsNullOrEmpty(downloadUrl))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = downloadUrl,
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // Silently ignore update check failures
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        var latestParts = latest.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var currentParts = current.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();

        for (int i = 0; i < Math.Max(latestParts.Length, currentParts.Length); i++)
        {
            var l = i < latestParts.Length ? latestParts[i] : 0;
            var c = i < currentParts.Length ? currentParts[i] : 0;
            if (l > c) return true;
            if (l < c) return false;
        }
        return false;
    }
}
