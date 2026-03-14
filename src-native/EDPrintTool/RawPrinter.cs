using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using EDPrintTool.Models;

namespace EDPrintTool;

public static class RawPrinter
{
    // ─── Win32 P/Invoke (winspool.drv) ──────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOW pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    // ─── Send raw bytes to Windows USB printer via spooler ──────

    public static Task<string> PrintWindowsRawAsync(string printerName, byte[] data)
    {
        return Task.Run(() =>
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
                throw new Exception($"OpenPrinter failed for '{printerName}', error {Marshal.GetLastWin32Error()}");

            try
            {
                var di = new DOCINFOW
                {
                    pDocName = "EDPrintTool RAW",
                    pOutputFile = null,
                    pDatatype = "RAW"
                };

                if (StartDocPrinter(hPrinter, 1, ref di) == 0)
                {
                    var err = Marshal.GetLastWin32Error();
                    throw new Exception($"StartDocPrinter failed, error {err}");
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new Exception($"StartPagePrinter failed, error {err}");
                    }

                    try
                    {
                        var pBytes = Marshal.AllocCoTaskMem(data.Length);
                        try
                        {
                            Marshal.Copy(data, 0, pBytes, data.Length);
                            if (!WritePrinter(hPrinter, pBytes, data.Length, out var written))
                            {
                                var err = Marshal.GetLastWin32Error();
                                throw new Exception($"WritePrinter failed, error {err}");
                            }
                            return $"Sent {written} bytes to {printerName}";
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pBytes);
                        }
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        });
    }

    public static Task<string> PrintWindowsAsync(string printerName, string text)
        => PrintWindowsRawAsync(printerName, Encoding.UTF8.GetBytes(text));

    // ─── Print to network printer via TCP port 9100 ─────────────

    public static async Task<string> PrintNetworkRawAsync(string host, int port, byte[] data)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(host, port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(data, cts.Token);
        await stream.FlushAsync(cts.Token);
        client.Close();

        return $"Sent {data.Length} bytes to {host}:{port}";
    }

    public static Task<string> PrintNetworkAsync(string host, int port, string text)
        => PrintNetworkRawAsync(host, port, Encoding.UTF8.GetBytes(text));

    // ─── Print to CUPS printer (macOS/Linux) ────────────────────

    public static async Task<string> PrintCupsRawAsync(string queue, byte[] data)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("lp", $"-d {queue} -o raw")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Failed to start lp");

        // Write raw bytes directly to avoid text encoding corruption
        await proc.StandardInput.BaseStream.WriteAsync(data);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new Exception($"lp failed: {err}");
        }

        return $"Sent {data.Length} bytes to CUPS queue: {queue}";
    }

    public static Task<string> PrintCupsAsync(string queue, string text)
        => PrintCupsRawAsync(queue, Encoding.UTF8.GetBytes(text));

    // ─── Main dispatcher ────────────────────────────────────────

    public static async Task<string> PrintAsync(
        PrinterInfo printer, string content, int copies = 1, bool applySettings = true)
    {
        var isEscPos = printer.Settings.Language == "ESC/POS";

        byte[] rawData;

        if (isEscPos)
        {
            rawData = BuildEscPosPayload(printer.Settings, content, copies, applySettings);
        }
        else
        {
            // ZPL: text-based payload
            var payload = new StringBuilder();
            if (applySettings)
                payload.AppendLine(printer.Settings.BuildSetupZPL());
            for (int i = 0; i < copies; i++)
            {
                payload.Append(content);
                if (i < copies - 1) payload.AppendLine();
            }
            rawData = Encoding.UTF8.GetBytes(payload.ToString());
        }

        return printer.Type switch
        {
            "network" => await PrintNetworkRawAsync(
                printer.Host ?? throw new Exception($"No host for printer: {printer.Id}"),
                printer.Port, rawData),

            "usb" when OperatingSystem.IsWindows() => await PrintWindowsRawAsync(
                printer.WindowsPrinter ?? throw new Exception($"No Windows printer name for: {printer.Id}"),
                rawData),

            "usb" => await PrintCupsRawAsync(
                printer.CupsQueue ?? throw new Exception($"No CUPS queue for: {printer.Id}"),
                rawData),

            _ => throw new Exception($"Unknown printer type: {printer.Type}")
        };
    }

    /// <summary>
    /// Build a complete ESC/POS byte payload using the encoder.
    /// </summary>
    private static byte[] BuildEscPosPayload(PrinterSettings settings, string content, int copies, bool applySettings)
    {
        if (!applySettings)
        {
            // Raw pass-through: just encode content as UTF-8, no init/cut
            var raw = Encoding.UTF8.GetBytes(content);
            if (copies == 1) return raw;
            using var ms = new MemoryStream(raw.Length * copies);
            for (int i = 0; i < copies; i++) ms.Write(raw);
            return ms.ToArray();
        }

        var encoder = new EscPosEncoder(settings.PaperWidth);
        for (int i = 0; i < copies; i++)
        {
            encoder.Initialize();
            if (!string.IsNullOrEmpty(settings.Codepage))
                encoder.Codepage(settings.Codepage);
            encoder.Raw(content);
            if (settings.AutoCut)
                encoder.Cut(settings.CutType ?? "partial", settings.FeedLines);
            else if (settings.FeedLines > 0)
                encoder.Feed(settings.FeedLines);
        }
        return encoder.Encode();
    }

    /// <summary>
    /// Build ESC/POS payload from structured command array.
    /// Each command is a JSON array: ["method", arg1, arg2, ...]
    /// </summary>
    public static byte[] BuildFromCommands(string paperWidth, JsonArray commands, int copies = 1, string? codepage = null)
    {
        // Default to cp1252 for Latin/Spanish character support
        var encoder = new EscPosEncoder(paperWidth, codepage: codepage ?? "cp1252");
        // Tell the printer to use the same codepage
        encoder.Codepage(codepage ?? "cp1252");

        for (int i = 0; i < copies; i++)
        {
            foreach (var cmdNode in commands)
            {
                if (cmdNode is JsonArray arr && arr.Count > 0)
                {
                    var method = arr[0]?.GetValue<string>() ?? "";
                    ApplyCommand(encoder, method, arr);
                }
                else if (cmdNode is JsonValue val)
                {
                    ApplyCommand(encoder, val.GetValue<string>(), new JsonArray());
                }
            }
        }
        return encoder.Encode();
    }

    private static void ApplyCommand(EscPosEncoder enc, string method, JsonArray args)
    {
        // args[0] is the method name, actual args start at [1]
        switch (method)
        {
            case "initialize": enc.Initialize(); break;
            case "codepage":
                if (args.Count > 1) enc.Codepage(args[1]!.GetValue<string>());
                break;
            case "align":
                enc.Align(args.Count > 1 ? args[1]!.GetValue<string>() : "left");
                break;
            case "bold":
                enc.Bold(args.Count > 1 ? args[1]!.GetValue<bool>() : true);
                break;
            case "underline":
                enc.Underline(args.Count > 1 ? (byte)args[1]!.GetValue<int>() : (byte)1);
                break;
            case "font":
                enc.Font(args.Count > 1 ? (byte)args[1]!.GetValue<int>() : (byte)0);
                break;
            case "textSize":
                enc.TextSize(
                    args.Count > 1 ? args[1]!.GetValue<int>() : 1,
                    args.Count > 2 ? args[2]!.GetValue<int>() : 1);
                break;
            case "invert":
                enc.Invert(args.Count > 1 ? args[1]!.GetValue<bool>() : true);
                break;
            case "text":
                if (args.Count > 1) enc.Text(args[1]!.GetValue<string>());
                break;
            case "line":
                if (args.Count > 1) enc.Line(args[1]!.GetValue<string>());
                break;
            case "newline": enc.Newline(); break;
            case "empty": enc.Newline(); break;
            case "raw":
                if (args.Count > 1) enc.Raw(args[1]!.GetValue<string>());
                break;
            case "rule":
                enc.Rule(args.Count > 1 ? args[1]!.GetValue<string>()[0] : '-');
                break;
            case "columns":
                if (args.Count > 1 && args[1] is JsonArray colArr)
                    enc.Columns(colArr.Select(c => c?.GetValue<string>() ?? "").ToArray());
                break;
            case "pair":
                if (args.Count >= 3)
                    enc.Pair(
                        args[1]!.GetValue<string>(),
                        args[2]!.GetValue<string>(),
                        args.Count > 3 ? args[3]!.GetValue<string>()[0] : '.');
                break;
            case "feed":
                enc.Feed(args.Count > 1 ? args[1]!.GetValue<int>() : 1);
                break;
            case "cut":
                enc.Cut(
                    args.Count > 1 ? args[1]!.GetValue<string>() : "partial",
                    args.Count > 2 ? args[2]!.GetValue<int>() : 4);
                break;
            case "openCashDrawer":
                enc.OpenCashDrawer(args.Count > 1 ? args[1]!.GetValue<int>() : 0);
                break;
            case "image":
                if (args.Count > 1)
                {
                    var imgData = args[1]!.GetValue<string>();
                    var imgOpts = args.Count > 2 && args[2] is JsonObject imgObj ? imgObj : null;
                    var imgWidth = imgOpts?["width"]?.GetValue<int>() ?? 0;
                    if (imgWidth <= 0)
                        throw new ArgumentException("image: width is required and must be > 0");
                    enc.Image(imgData, imgWidth,
                        imgOpts?["height"]?.GetValue<int>(),
                        imgOpts?["mode"]?.GetValue<int>() ?? 0);
                }
                break;
            case "barcode":
                if (args.Count > 1)
                {
                    var bData = args[1]!.GetValue<string>();
                    var bOpts = args.Count > 2 && args[2] is JsonObject bObj ? bObj : null;
                    enc.Barcode(bData,
                        bOpts?["type"]?.GetValue<string>() ?? "CODE128",
                        bOpts?["height"]?.GetValue<int>() ?? 80,
                        bOpts?["width"]?.GetValue<int>() ?? 2,
                        bOpts?["hri"]?.GetValue<string>() ?? "below");
                }
                break;
            case "qrcode":
                if (args.Count > 1)
                {
                    var qData = args[1]!.GetValue<string>();
                    var qOpts = args.Count > 2 && args[2] is JsonObject qObj ? qObj : null;
                    enc.Qrcode(qData,
                        qOpts?["size"]?.GetValue<int>() ?? 6,
                        qOpts?["errorCorrection"]?.GetValue<string>() ?? "M");
                }
                break;
            case "pdf417":
                if (args.Count > 1)
                {
                    var pData = args[1]!.GetValue<string>();
                    var pOpts = args.Count > 2 && args[2] is JsonObject pObj ? pObj : null;
                    enc.Pdf417(pData,
                        pOpts?["columns"]?.GetValue<int>() ?? 0,
                        pOpts?["rows"]?.GetValue<int>() ?? 0,
                        pOpts?["width"]?.GetValue<int>() ?? 3,
                        pOpts?["height"]?.GetValue<int>() ?? 3,
                        pOpts?["errorCorrection"]?.GetValue<int>() ?? 1);
                }
                break;
        }
    }

    /// <summary>
    /// Send raw bytes to a printer using the appropriate transport.
    /// </summary>
    public static async Task<string> SendRawAsync(PrinterInfo printer, byte[] data)
    {
        return printer.Type switch
        {
            "network" => await PrintNetworkRawAsync(
                printer.Host ?? throw new Exception($"No host for printer: {printer.Id}"),
                printer.Port, data),

            "usb" when OperatingSystem.IsWindows() => await PrintWindowsRawAsync(
                printer.WindowsPrinter ?? throw new Exception($"No Windows printer name for: {printer.Id}"),
                data),

            "usb" => await PrintCupsRawAsync(
                printer.CupsQueue ?? throw new Exception($"No CUPS queue for: {printer.Id}"),
                data),

            _ => throw new Exception($"Unknown printer type: {printer.Type}")
        };
    }

    // ─── Document (PDF) printing ─────────────────────────────────
    // Prints through the OS spooler (not raw mode), so the driver handles rendering.

    /// <summary>
    /// Print a PDF document to a configured printer via the OS spooler.
    /// Only works with USB/spooler printers — not raw network (port 9100).
    /// </summary>
    public static async Task<string> PrintDocumentAsync(PrinterInfo printer, string fileBase64, int copies = 1)
    {
        if (printer.Type == "network")
            throw new Exception("Document printing is not supported for network (port 9100) printers. Use a USB/spooler printer instead.");

        // Save base64 PDF to temp file
        var tmpFile = Path.Combine(Path.GetTempPath(), $"edprint_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.pdf");
        await File.WriteAllBytesAsync(tmpFile, Convert.FromBase64String(fileBase64));

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var printerName = printer.WindowsPrinter
                    ?? throw new Exception($"No Windows printer name configured for: {printer.Id}");
                return await PrintDocumentWindowsAsync(printerName, tmpFile, copies);
            }
            else
            {
                var queue = printer.CupsQueue
                    ?? throw new Exception($"No CUPS queue configured for printer: {printer.Id}");
                return await PrintDocumentCupsAsync(queue, tmpFile, copies);
            }
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    /// <summary>Print PDF via CUPS lp command (without -o raw, so CUPS renders it).</summary>
    private static Task<string> PrintDocumentCupsAsync(string queue, string filePath, int copies)
    {
        var tcs = new TaskCompletionSource<string>();
        var psi = new System.Diagnostics.ProcessStartInfo("lp", $"-d {queue} -n {copies} \"{filePath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            var stderr = proc.StandardError.ReadToEnd().Trim();
            if (proc.ExitCode != 0)
                tcs.SetException(new Exception($"CUPS document print failed ({queue}): {stderr}"));
            else
                tcs.SetResult($"OK: {stdout}");
            proc.Dispose();
        };
        return tcs.Task;
    }

    /// <summary>Print PDF via Windows Start-Process -Verb PrintTo.</summary>
    private static Task<string> PrintDocumentWindowsAsync(string printerName, string filePath, int copies)
    {
        var tcs = new TaskCompletionSource<string>();
        var ps1 = $@"
param()
for ($i = 0; $i -lt {copies}; $i++) {{
  Start-Process -FilePath '{filePath.Replace("'", "''")}' -Verb PrintTo -ArgumentList '{printerName.Replace("'", "''")}' -Wait -WindowStyle Hidden
}}
Write-Output 'OK: Printed {copies} copy/copies to {printerName}'
";
        var tmpPs1 = Path.Combine(Path.GetTempPath(), $"edprint_doc_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.ps1");
        File.WriteAllText(tmpPs1, ps1);

        var psi = new System.Diagnostics.ProcessStartInfo("powershell",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmpPs1}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            var stderr = proc.StandardError.ReadToEnd().Trim();
            try { File.Delete(tmpPs1); } catch { }
            if (proc.ExitCode != 0)
                tcs.SetException(new Exception($"Windows document print failed ({printerName}): {stderr}"));
            else
                tcs.SetResult(stdout);
            proc.Dispose();
        };
        return tcs.Task;
    }
}
