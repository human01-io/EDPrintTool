using System.Text;

namespace EDPrintTool;

/// <summary>
/// Chainable ESC/POS command builder for thermal receipt printers.
/// Architecture follows ReceiptPrinterEncoder / node-thermal-printer patterns:
///   1. Encoder builds a byte buffer (no I/O)
///   2. Transport layer sends it (RawPrinter handles that)
/// </summary>
public class EscPosEncoder
{
    private readonly MemoryStream _stream = new();
    private readonly int _columns;

    // Paper width → default character columns (Font A, 12x24)
    public static readonly Dictionary<string, int> ColumnsByWidth = new()
    {
        ["80mm"] = 48,
        ["58mm"] = 32,
    };

    // Common code pages: name → ESC t argument
    public static readonly Dictionary<string, byte> Codepages = new()
    {
        ["cp437"] = 0,   // US (default)
        ["cp850"] = 2,   // Multilingual Latin I
        ["cp858"] = 19,  // Latin I + Euro
        ["cp860"] = 3,   // Portuguese
        ["cp863"] = 4,   // Canadian French
        ["cp865"] = 5,   // Nordic
        ["cp1252"] = 16, // Windows Latin 1
    };

    public int ColumnWidth => _columns;

    public EscPosEncoder(string paperWidth = "80mm", int? columns = null, string? codepage = null)
    {
        _columns = columns ?? (ColumnsByWidth.TryGetValue(paperWidth, out var c) ? c : 48);
    }

    // ─── Printer control ──────────────────────────────────────

    /// <summary>ESC @ — Reset printer to defaults.</summary>
    public EscPosEncoder Initialize()
    {
        Write(0x1B, 0x40);
        return this;
    }

    /// <summary>ESC t n — Select character code page.</summary>
    public EscPosEncoder Codepage(string name)
    {
        var n = Codepages.TryGetValue(name, out var v) ? v : (byte)0;
        Write(0x1B, 0x74, n);
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
        _stream.Write(Encoding.UTF8.GetBytes(str));
        return this;
    }

    /// <summary>Write text followed by newline.</summary>
    public EscPosEncoder Line(string str)
    {
        _stream.Write(Encoding.UTF8.GetBytes(str + "\n"));
        return this;
    }

    /// <summary>Write a newline.</summary>
    public EscPosEncoder Newline()
    {
        _stream.WriteByte(0x0A);
        return this;
    }

    /// <summary>Write pre-formatted content (string). For raw receipt text from API.</summary>
    public EscPosEncoder Raw(string content)
    {
        _stream.Write(Encoding.UTF8.GetBytes(content));
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
        _stream.Write(Encoding.UTF8.GetBytes(new string(ch, _columns) + "\n"));
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

        _stream.Write(Encoding.UTF8.GetBytes(sb + "\n"));
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
        _stream.Write(Encoding.UTF8.GetBytes(left + middle + right + "\n"));
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
    /// Cut paper — feeds first, then sends GS V Function A.
    /// Uses separate ESC d (feed) + GS V (cut) for maximum compatibility.
    /// Function A works on virtually all ESC/POS printers.
    /// </summary>
    public EscPosEncoder Cut(string type = "partial", int feedLines = 4)
    {
        // Feed lines first (ESC d n)
        if (feedLines > 0)
            Write(0x1B, 0x64, (byte)Math.Clamp(feedLines, 0, 255));
        // GS V Function A — simple, widely supported
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
}
