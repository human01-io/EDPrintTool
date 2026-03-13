using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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

    public static async Task<string> PrintCupsAsync(string queue, string zpl)
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

        await proc.StandardInput.WriteAsync(zpl);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new Exception($"lp failed: {err}");
        }

        return $"Sent to CUPS queue: {queue}";
    }

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

            "usb" => await PrintCupsAsync(
                printer.CupsQueue ?? throw new Exception($"No CUPS queue for: {printer.Id}"),
                Encoding.UTF8.GetString(rawData)),

            _ => throw new Exception($"Unknown printer type: {printer.Type}")
        };
    }

    /// <summary>
    /// Build a complete ESC/POS byte payload: init + content × copies + feed + cut
    /// </summary>
    private static byte[] BuildEscPosPayload(PrinterSettings settings, string content, int copies, bool applySettings)
    {
        using var ms = new MemoryStream();

        for (int i = 0; i < copies; i++)
        {
            // Init printer
            if (applySettings)
                ms.Write(settings.BuildEscPosInit());

            // Content as UTF-8 bytes
            ms.Write(Encoding.UTF8.GetBytes(content));

            // Feed + cut
            if (applySettings)
                ms.Write(settings.BuildEscPosCut());
        }

        return ms.ToArray();
    }
}
