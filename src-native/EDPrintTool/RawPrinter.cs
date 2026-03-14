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
}
