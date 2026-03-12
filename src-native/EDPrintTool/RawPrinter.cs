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

    // ─── Print to Windows USB printer via raw spooler ───────────

    public static Task<string> PrintWindowsAsync(string printerName, string zpl)
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
                        var data = Encoding.UTF8.GetBytes(zpl);
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

    // ─── Print to network printer via TCP port 9100 ─────────────

    public static async Task<string> PrintNetworkAsync(string host, int port, string zpl)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await client.ConnectAsync(host, port, cts.Token);
        var stream = client.GetStream();
        var data = Encoding.UTF8.GetBytes(zpl);
        await stream.WriteAsync(data, cts.Token);
        await stream.FlushAsync(cts.Token);
        client.Close();

        return $"Sent to {host}:{port}";
    }

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
        PrinterInfo printer, string zpl, int copies = 1, bool applySettings = true)
    {
        var payload = new StringBuilder();

        if (applySettings)
        {
            payload.AppendLine(printer.Settings.BuildSetupZPL());
        }

        for (int i = 0; i < copies; i++)
        {
            payload.Append(zpl);
            if (i < copies - 1) payload.AppendLine();
        }

        var data = payload.ToString();

        return printer.Type switch
        {
            "network" => await PrintNetworkAsync(
                printer.Host ?? throw new Exception($"No host for printer: {printer.Id}"),
                printer.Port, data),

            "usb" when OperatingSystem.IsWindows() => await PrintWindowsAsync(
                printer.WindowsPrinter ?? throw new Exception($"No Windows printer name for: {printer.Id}"),
                data),

            "usb" => await PrintCupsAsync(
                printer.CupsQueue ?? throw new Exception($"No CUPS queue for: {printer.Id}"),
                data),

            _ => throw new Exception($"Unknown printer type: {printer.Type}")
        };
    }
}
