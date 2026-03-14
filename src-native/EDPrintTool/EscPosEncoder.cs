using System.Text;

namespace EDPrintTool;

/// <summary>
/// Chainable ESC/POS command builder for thermal receipt printers.
/// Architecture follows ReceiptPrinterEncoder / node-thermal-printer patterns:
///   1. Encoder builds a byte buffer (no I/O)
///   2. Transport layer sends it (RawPrinter handles that)
///
/// Text is encoded using the active codepage (default cp1252 for Latin/Spanish).
/// ESC/POS printers use single-byte encodings — sending UTF-8 directly causes
/// multi-byte characters (é, ñ, í) to print as garbage.
/// </summary>
public class EscPosEncoder
{
    private readonly MemoryStream _stream = new();
    private readonly int _columns;
    private Encoding _encoding;

    // Paper width → default character columns (Font A, 12x24)
    public static readonly Dictionary<string, int> ColumnsByWidth = new()
    {
        ["80mm"] = 48,
        ["72mm"] = 42,
        ["58mm"] = 32,
    };

    // Common code pages: name → (ESC t argument, .NET codepage number)
    public static readonly Dictionary<string, (byte escArg, int dotnetCp)> Codepages = new()
    {
        ["cp437"]  = (0,  437),   // US (default)
        ["cp850"]  = (2,  850),   // Multilingual Latin I
        ["cp858"]  = (19, 858),   // Latin I + Euro
        ["cp860"]  = (3,  860),   // Portuguese
        ["cp863"]  = (4,  863),   // Canadian French
        ["cp865"]  = (5,  865),   // Nordic
        ["cp1252"] = (16, 1252),  // Windows Latin 1 (best for Spanish)
    };

    public int ColumnWidth => _columns;

    public EscPosEncoder(string paperWidth = "80mm", int? columns = null, string? codepage = null)
    {
        _columns = columns ?? (ColumnsByWidth.TryGetValue(paperWidth, out var c) ? c : 48);

        // Register codepage providers for .NET (required for non-UTF encodings)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Default to cp1252 (Windows Latin 1) — covers all Western European/Spanish chars
        var cpName = codepage ?? "cp1252";
        if (Codepages.TryGetValue(cpName, out var info))
            _encoding = Encoding.GetEncoding(info.dotnetCp);
        else
            _encoding = Encoding.GetEncoding(1252);
    }

    // ─── Printer control ──────────────────────────────────────

    /// <summary>ESC @ — Reset printer to defaults.</summary>
    public EscPosEncoder Initialize()
    {
        Write(0x1B, 0x40);
        return this;
    }

    /// <summary>ESC t n — Select character code page on the printer and update encoder.</summary>
    public EscPosEncoder Codepage(string name)
    {
        if (Codepages.TryGetValue(name, out var info))
        {
            Write(0x1B, 0x74, info.escArg);
            _encoding = Encoding.GetEncoding(info.dotnetCp);
        }
        else
        {
            Write(0x1B, 0x74, 0);
            _encoding = Encoding.GetEncoding(437);
        }
        return this;
    }

    /// <summary>ESC t n — Select character code page by number.</summary>
    public EscPosEncoder Codepage(byte n)
    {
        Write(0x1B, 0x74, n);
        return this;
    }

    // ─── Text formatting ──────────────────────────────────────

    /// <summary>ESC a n — Set justification (0=left, 1=center, 2=right).</summary>
    public EscPosEncoder Align(string mode)
    {
        byte n = mode switch { "center" => 1, "right" => 2, _ => 0 };
        Write(0x1B, 0x61, n);
        return this;
    }

    /// <summary>ESC E n — Bold on/off.</summary>
    public EscPosEncoder Bold(bool on = true)
    {
        Write(0x1B, 0x45, (byte)(on ? 1 : 0));
        return this;
    }

    /// <summary>ESC - n — Underline (0=off, 1=thin, 2=thick).</summary>
    public EscPosEncoder Underline(byte mode = 1)
    {
        Write(0x1B, 0x2D, mode);
        return this;
    }

    /// <summary>ESC M n — Select font (0=Font A 12x24, 1=Font B 9x17).</summary>
    public EscPosEncoder Font(byte n = 0)
    {
        Write(0x1B, 0x4D, (byte)(n & 1));
        return this;
    }

    /// <summary>GS ! n — Character size multiplier (1-8 each).</summary>
    public EscPosEncoder TextSize(int width = 1, int height = 1)
    {
        var w = Math.Clamp(width - 1, 0, 7);
        var h = Math.Clamp(height - 1, 0, 7);
        Write(0x1D, 0x21, (byte)((w << 4) | h));
        return this;
    }

    /// <summary>GS B n — Reverse (white on black) on/off.</summary>
    public EscPosEncoder Invert(bool on = true)
    {
        Write(0x1D, 0x42, (byte)(on ? 1 : 0));
        return this;
    }

    // ─── Content ──────────────────────────────────────────────

    /// <summary>Write text without newline.</summary>
    public EscPosEncoder Text(string str)
    {
        WriteText(str);
        return this;
    }

    /// <summary>Write text followed by newline.</summary>
    public EscPosEncoder Line(string str)
    {
        WriteText(str + "\n");
        return this;
    }

    /// <summary>Write a newline.</summary>
    public EscPosEncoder Newline()
    {
        _stream.WriteByte(0x0A);
        return this;
    }

    /// <summary>Write pre-formatted content (string).</summary>
    public EscPosEncoder Raw(string content)
    {
        WriteText(content);
        return this;
    }

    /// <summary>Write pre-formatted content (bytes).</summary>
    public EscPosEncoder Raw(byte[] data)
    {
        _stream.Write(data);
        return this;
    }

    /// <summary>Print a horizontal rule filling the full paper width.</summary>
    public EscPosEncoder Rule(char ch = '-')
    {
        WriteText(new string(ch, _columns) + "\n");
        return this;
    }

    /// <summary>
    /// Print a row of columns, auto-spaced to fill the paper width.
    /// Last column is right-aligned, others are left-aligned.
    /// </summary>
    public EscPosEncoder Columns(params string[] cols)
    {
        if (cols.Length == 0) return Newline();
        if (cols.Length == 1) return Line(cols[0]);

        int n = cols.Length;
        int rightWidth = Math.Max(10, _columns / n);
        int leftWidth = _columns - rightWidth * (n - 1);

        var sb = new StringBuilder(_columns);
        for (int i = 0; i < n; i++)
        {
            string val = cols[i] ?? "";
            int w = i == 0 ? leftWidth : rightWidth;

            if (i == n - 1)
                sb.Append(val.Length >= w ? val[..w] : val.PadLeft(w));
            else
                sb.Append(val.Length >= w ? val[..w] : val.PadRight(w));
        }

        WriteText(sb + "\n");
        return this;
    }

    /// <summary>
    /// Print a key-value pair with fill character between.
    /// Example: Pair("Subtotal", "$37.47") → "Subtotal............$37.47"
    /// </summary>
    public EscPosEncoder Pair(string left, string right, char fill = '.')
    {
        int gap = _columns - left.Length - right.Length;
        string middle = gap > 0 ? new string(fill, gap) : " ";
        WriteText(left + middle + right + "\n");
        return this;
    }

    // ─── Paper handling ───────────────────────────────────────

    /// <summary>ESC d n — Feed n lines.</summary>
    public EscPosEncoder Feed(int lines = 1)
    {
        Write(0x1B, 0x64, (byte)Math.Clamp(lines, 0, 255));
        return this;
    }

    /// <summary>
    /// Cut paper — sends multiple cut command formats for maximum compatibility.
    /// Sends BOTH legacy Epson cut (ESC i/m) AND standard GS V.
    /// The printer responds to whichever it understands and ignores the rest.
    /// Covers Epson TM, Star TSP in ESC/POS emulation, and generic printers.
    /// </summary>
    public EscPosEncoder Cut(string type = "partial", int feedLines = 4)
    {
        // Feed lines first (ESC d n)
        if (feedLines > 0)
            Write(0x1B, 0x64, (byte)Math.Clamp(feedLines, 0, 255));
        // GS V Function A — the most standard cut command
        byte m = type == "full" ? (byte)0x00 : (byte)0x01;
        Write(0x1D, 0x56, m);
        return this;
    }

    // ─── Peripheral ───────────────────────────────────────────

    /// <summary>ESC p m t1 t2 — Open cash drawer (pin 0=drawer1, 1=drawer2).</summary>
    public EscPosEncoder OpenCashDrawer(int pin = 0)
    {
        Write(0x1B, 0x70, (byte)(pin & 1), 25, 250);
        return this;
    }

    // ─── Barcodes & 2D codes ─────────────────────────────────

    /// <summary>GS k — Print a 1D barcode.</summary>
    public EscPosEncoder Barcode(string data, string type = "CODE128", int height = 80, int width = 2, string hri = "below")
    {
        var typeMap = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["UPC-A"] = 65, ["UPC-E"] = 66, ["EAN13"] = 67, ["EAN8"] = 68,
            ["CODE39"] = 69, ["ITF"] = 70, ["CODABAR"] = 71, ["CODE93"] = 72,
            ["CODE128"] = 73,
        };
        if (!typeMap.TryGetValue(type, out var m)) return this;

        var hriMap = new Dictionary<string, byte> { ["none"] = 0, ["above"] = 1, ["below"] = 2, ["both"] = 3 };
        byte hriVal = hriMap.GetValueOrDefault(hri, 2);
        height = Math.Clamp(height, 1, 255);
        width = Math.Clamp(width, 2, 6);

        var dataBytes = Encoding.ASCII.GetBytes(data);
        // GS h n — barcode height
        Write(0x1D, 0x68, (byte)height);
        // GS w n — barcode width
        Write(0x1D, 0x77, (byte)width);
        // GS H n — HRI position
        Write(0x1D, 0x48, hriVal);
        // GS k m n data — print barcode (Function B)
        Write(0x1D, 0x6B, m, (byte)dataBytes.Length);
        _stream.Write(dataBytes);
        return this;
    }

    /// <summary>GS ( k — Print a QR code.</summary>
    public EscPosEncoder Qrcode(string data, int size = 6, string errorCorrection = "M")
    {
        size = Math.Clamp(size, 1, 16);
        var ecMap = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
            { ["L"] = 48, ["M"] = 49, ["Q"] = 50, ["H"] = 51 };
        byte ec = ecMap.GetValueOrDefault(errorCorrection, 49);
        var dataBytes = Encoding.UTF8.GetBytes(data);

        // Select model 2
        Write(0x1D, 0x28, 0x6B, 4, 0, 0x31, 0x41, 50, 0);
        // Set module size
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x43, (byte)size);
        // Set error correction
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x45, ec);
        // Store data
        int storeLen = dataBytes.Length + 3;
        Write(0x1D, 0x28, 0x6B, (byte)(storeLen & 0xFF), (byte)((storeLen >> 8) & 0xFF), 0x31, 0x50, 0x30);
        _stream.Write(dataBytes);
        // Print
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30);
        return this;
    }

    /// <summary>GS ( k — Print a PDF417 barcode.</summary>
    public EscPosEncoder Pdf417(string data, int columns = 0, int rows = 0, int width = 3, int height = 3, int errorCorrection = 1)
    {
        columns = Math.Clamp(columns, 0, 30);
        rows = Math.Clamp(rows, 0, 90);
        width = Math.Clamp(width, 2, 8);
        height = Math.Clamp(height, 2, 8);
        errorCorrection = Math.Clamp(errorCorrection, 0, 8);
        var dataBytes = Encoding.UTF8.GetBytes(data);

        // Set columns
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x41, (byte)columns);
        // Set rows
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x42, (byte)rows);
        // Set module width
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x43, (byte)width);
        // Set row height
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x44, (byte)height);
        // Set error correction
        Write(0x1D, 0x28, 0x6B, 4, 0, 0x30, 0x45, (byte)(48 + errorCorrection), 0);
        // Store data
        int storeLen = dataBytes.Length + 3;
        Write(0x1D, 0x28, 0x6B, (byte)(storeLen & 0xFF), (byte)((storeLen >> 8) & 0xFF), 0x30, 0x50, 0x30);
        _stream.Write(dataBytes);
        // Print
        Write(0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x51, 0x30);
        return this;
    }

    // ─── Low-level ────────────────────────────────────────────

    /// <summary>Append raw bytes.</summary>
    public EscPosEncoder Bytes(params byte[] data)
    {
        _stream.Write(data);
        return this;
    }

    /// <summary>Build and return the final byte array.</summary>
    public byte[] Encode()
    {
        return _stream.ToArray();
    }

    // ─── Private ────────────────────────────────────────────

    private void Write(params byte[] data) => _stream.Write(data);

    /// <summary>
    /// Write text using the active codepage encoding.
    /// This ensures characters like é, ñ, í are encoded as single bytes
    /// that match what the printer expects for its selected codepage.
    /// </summary>
    private void WriteText(string text)
    {
        _stream.Write(_encoding.GetBytes(text));
    }
}
