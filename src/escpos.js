/**
 * ESC/POS Encoder — chainable command builder for thermal receipt printers.
 *
 * Architecture follows ReceiptPrinterEncoder / node-thermal-printer patterns:
 *   1. Encoder builds a byte buffer (no I/O)
 *   2. Transport layer sends it (printer.js handles that)
 *
 * Text is encoded using 'latin1' (ISO-8859-1) by default, which matches
 * cp1252 for all printable characters. This ensures accented characters
 * like é, ñ, í are sent as single bytes that match the printer's codepage.
 * Sending UTF-8 would produce multi-byte sequences that thermal printers
 * cannot interpret.
 *
 * Usage:
 *   const data = new EscPosEncoder({ columns: 48 })
 *     .initialize()
 *     .align('center').bold(true).line('MY STORE').bold(false)
 *     .rule()
 *     .align('left').line('Item              Qty   Price')
 *     .rule()
 *     .columns(['Widget A', '2', '$9.99'])
 *     .cut()
 *     .encode();
 */

// Common code pages: id → ESC t argument
const CODEPAGES = {
  cp437:  0,  // US (default on most printers)
  cp850:  2,  // Multilingual Latin I
  cp858: 19,  // Latin I + Euro sign
  cp860:  3,  // Portuguese
  cp863:  4,  // Canadian French
  cp865:  5,  // Nordic
  cp1252: 16, // Windows Latin 1
};

// Paper width → default character columns (Font A, 12x24)
const COLUMNS = {
  '80mm': 48,
  '72mm': 42,
  '58mm': 32,
};

class EscPosEncoder {
  /**
   * @param {object} [options]
   * @param {string} [options.paperWidth='80mm'] - '80mm', '72mm', or '58mm'
   * @param {number} [options.columns] - override character columns
   * @param {string} [options.codepage] - default code page name
   */
  constructor(options = {}) {
    this._parts = [];
    this._columns = options.columns || COLUMNS[options.paperWidth] || 48;
    this._codepage = options.codepage || null;
    // Use latin1 encoding — maps 1:1 to the lower 256 Unicode codepoints
    // which covers all cp1252/ISO-8859-1 printable characters (á, é, ñ, etc.)
    this._textEncoding = 'latin1';
  }

  // ─── Printer control ──────────────────────────────────────

  /** ESC @ — Reset printer to defaults */
  initialize() {
    // Only ESC @ — the safest init. Extra commands (ESC M, GS !, ESC a)
    // can cause errors on printers with limited ESC/POS support like
    // Star TSP100 in emulation mode.
    this._parts.push(Buffer.from([0x1B, 0x40]));
    if (this._codepage && CODEPAGES[this._codepage] !== undefined) {
      this.codepage(this._codepage);
    }
    return this;
  }

  /** ESC t n — Select character code page */
  codepage(name) {
    const n = typeof name === 'number' ? name : (CODEPAGES[name] ?? 0);
    this._parts.push(Buffer.from([0x1B, 0x74, n & 0xFF]));
    return this;
  }

  // ─── Text formatting ──────────────────────────────────────

  /** ESC a n — Set justification (0=left, 1=center, 2=right) */
  align(mode) {
    const n = mode === 'center' ? 1 : mode === 'right' ? 2 : 0;
    this._parts.push(Buffer.from([0x1B, 0x61, n]));
    return this;
  }

  /** ESC E n — Bold on/off */
  bold(on = true) {
    this._parts.push(Buffer.from([0x1B, 0x45, on ? 1 : 0]));
    return this;
  }

  /** ESC - n — Underline (0=off, 1=thin, 2=thick) */
  underline(on = true) {
    this._parts.push(Buffer.from([0x1B, 0x2D, on === true ? 1 : (on || 0)]));
    return this;
  }

  /** ESC M n — Select font (0=Font A 12x24, 1=Font B 9x17) */
  font(n = 0) {
    this._parts.push(Buffer.from([0x1B, 0x4D, n & 1]));
    return this;
  }

  /**
   * GS ! n — Character size multiplier.
   * @param {number} width  1-8 (multiplier)
   * @param {number} height 1-8 (multiplier)
   */
  textSize(width = 1, height = 1) {
    const w = Math.max(0, Math.min(7, width - 1));
    const h = Math.max(0, Math.min(7, height - 1));
    this._parts.push(Buffer.from([0x1D, 0x21, (w << 4) | h]));
    return this;
  }

  /** ESC G n — Double-strike on/off */
  doubleStrike(on = true) {
    this._parts.push(Buffer.from([0x1B, 0x47, on ? 1 : 0]));
    return this;
  }

  /** GS B n — Reverse (white on black) on/off */
  invert(on = true) {
    this._parts.push(Buffer.from([0x1D, 0x42, on ? 1 : 0]));
    return this;
  }

  // ─── Content ──────────────────────────────────────────────

  /** Write text without newline */
  text(str) {
    this._parts.push(Buffer.from(str, this._textEncoding));
    return this;
  }

  /** Write text followed by newline */
  line(str) {
    this._parts.push(Buffer.from(str + '\n', this._textEncoding));
    return this;
  }

  /** Write a newline */
  newline() {
    this._parts.push(Buffer.from('\n'));
    return this;
  }

  /**
   * Write pre-formatted content (string or Buffer).
   * Use this for raw receipt text from the API.
   */
  raw(content) {
    if (Buffer.isBuffer(content)) {
      this._parts.push(content);
    } else {
      this._parts.push(Buffer.from(content, this._textEncoding));
    }
    return this;
  }

  /** Print a horizontal rule filling the full paper width */
  rule(char = '-') {
    this._parts.push(Buffer.from(char[0].repeat(this._columns) + '\n', this._textEncoding));
    return this;
  }

  /**
   * Print a row of columns, auto-spaced to fill the paper width.
   * Last column is right-aligned, others are left-aligned.
   *
   * Example: columns(['Widget A', '2', '$9.99'])
   *          → "Widget A          2     $9.99"
   *
   * @param {string[]} cols - array of column values
   * @param {number[]} [widths] - optional explicit widths (must sum to columns)
   */
  columns(cols, widths) {
    const n = cols.length;
    if (n === 0) return this.newline();
    if (n === 1) return this.line(cols[0]);

    if (!widths) {
      // Auto-distribute: first column gets most space, rest get equal share
      const rightWidth = Math.max(10, Math.floor(this._columns / n));
      const leftWidth = this._columns - rightWidth * (n - 1);
      widths = [leftWidth, ...Array(n - 1).fill(rightWidth)];
    }

    let row = '';
    for (let i = 0; i < n; i++) {
      const val = String(cols[i]);
      const w = widths[i];
      if (i === n - 1) {
        // Last column: right-align
        row += val.length >= w ? val.slice(0, w) : val.padStart(w);
      } else {
        // Other columns: left-align
        row += val.length >= w ? val.slice(0, w) : val.padEnd(w);
      }
    }

    this._parts.push(Buffer.from(row + '\n', this._textEncoding));
    return this;
  }

  /**
   * Print a key-value pair with dots or spaces between.
   * Example: pair('Subtotal', '$37.47') → "Subtotal................$37.47"
   */
  pair(left, right, fill = '.') {
    const gap = this._columns - left.length - right.length;
    const middle = gap > 0 ? fill[0].repeat(gap) : ' ';
    this._parts.push(Buffer.from(left + middle + right + '\n', this._textEncoding));
    return this;
  }

  /** Insert an empty line (line feed) */
  empty() {
    this._parts.push(Buffer.from('\n'));
    return this;
  }

  // ─── Paper handling ───────────────────────────────────────

  /** ESC d n — Feed n lines */
  feed(lines = 1) {
    this._parts.push(Buffer.from([0x1B, 0x64, Math.min(lines, 255)]));
    return this;
  }

  /**
   * Cut paper — sends multiple cut command formats for maximum compatibility.
   *
   * Strategy: feed first, then send BOTH legacy Epson cut (ESC i / ESC m)
   * AND standard GS V. The printer responds to whichever it understands
   * and ignores the rest. This covers:
   *   - Epson TM series (GS V)
   *   - Star TSP in ESC/POS emulation (ESC i / ESC m)
   *   - Budget/generic Chinese printers (usually one of the above)
   *
   * @param {'full'|'partial'} [type='partial']
   * @param {number} [feedLines=4] - lines to feed before cutting
   */
  cut(type = 'partial', feedLines = 4) {
    // Feed lines first (ESC d n)
    if (feedLines > 0) {
      this._parts.push(Buffer.from([0x1B, 0x64, Math.min(feedLines, 255)]));
    }
    // GS V Function A — the most standard cut command
    const m = type === 'full' ? 0x00 : 0x01;
    this._parts.push(Buffer.from([0x1D, 0x56, m]));
    return this;
  }

  // ─── Peripheral ───────────────────────────────────────────

  /**
   * ESC p m t1 t2 — Open cash drawer.
   * @param {number} [pin=0] - 0 = pin 2 (drawer 1), 1 = pin 5 (drawer 2)
   */
  openCashDrawer(pin = 0) {
    this._parts.push(Buffer.from([0x1B, 0x70, pin & 1, 25, 250]));
    return this;
  }

  // ─── Images ─────────────────────────────────────────────

  // Max print width in dots per paper size (203 dpi print heads)
  static IMAGE_WIDTHS = { '80mm': 576, '72mm': 512, '58mm': 384 };

  /**
   * GS v 0 — Print a 1-bit raster image.
   *
   * The caller must provide pre-processed 1-bit monochrome pixel data:
   *   - Each bit = 1 pixel (1=black, 0=white), MSB first
   *   - Rows are packed left-to-right, top-to-bottom
   *   - Each row is (width / 8) bytes, padded to byte boundary
   *   - Total data length must equal (width / 8) * height bytes
   *
   * @param {string} data - base64-encoded raw 1-bit pixel data (NOT a PNG/BMP file)
   * @param {object} [options]
   * @param {number} options.width - image width in pixels (must be multiple of 8)
   * @param {number} [options.height] - image height in pixels (auto-calculated if omitted)
   * @param {number} [options.mode=0] - 0=normal, 1=double width, 2=double height, 3=both
   */
  image(data, options = {}) {
    if (!data) throw new Error('image: missing data (base64-encoded 1-bit pixel data)');

    const imageBytes = Buffer.from(data, 'base64');
    if (imageBytes.length === 0) throw new Error('image: data is empty after base64 decode');

    const width = options.width;
    if (!width || width <= 0) throw new Error('image: width is required and must be > 0');
    if (width % 8 !== 0) throw new Error(`image: width must be a multiple of 8, got ${width}`);

    const bytesPerLine = width / 8;
    const height = options.height || Math.floor(imageBytes.length / bytesPerLine);

    if (height <= 0) throw new Error('image: calculated height is 0 — check width and data length');

    const expectedLen = bytesPerLine * height;
    if (imageBytes.length < expectedLen) {
      throw new Error(`image: data too short — expected ${expectedLen} bytes (${width}x${height} at 1bpp), got ${imageBytes.length}`);
    }

    if (width > 4096) throw new Error(`image: width ${width} exceeds maximum of 4096 pixels`);
    if (height > 4096) throw new Error(`image: height ${height} exceeds maximum of 4096 pixels`);

    const mode = Math.max(0, Math.min(3, options.mode || 0));

    // GS v 0 m xL xH yL yH data
    const xL = bytesPerLine & 0xFF;
    const xH = (bytesPerLine >> 8) & 0xFF;
    const yL = height & 0xFF;
    const yH = (height >> 8) & 0xFF;

    this._parts.push(Buffer.from([0x1D, 0x76, 0x30, mode, xL, xH, yL, yH]));
    // Only send exactly the expected bytes (ignore any trailing data)
    this._parts.push(imageBytes.subarray(0, expectedLen));
    return this;
  }

  // ─── Barcodes & 2D codes ─────────────────────────────────

  /**
   * GS k — Print a 1D barcode.
   * @param {string} data - barcode data
   * @param {object} [options]
   * @param {string} [options.type='CODE128'] - barcode type
   * @param {number} [options.height=80] - barcode height in dots (1-255)
   * @param {number} [options.width=2] - bar width (2-6)
   * @param {string} [options.hri='below'] - HRI position: 'none','above','below','both'
   */
  barcode(data, options = {}) {
    const type = (options.type || 'CODE128').toUpperCase();
    const height = Math.max(1, Math.min(255, options.height || 80));
    const width = Math.max(2, Math.min(6, options.width || 2));
    const hriMap = { none: 0, above: 1, below: 2, both: 3 };
    const hri = hriMap[options.hri || 'below'] ?? 2;

    const typeMap = {
      'UPC-A': 65, 'UPC-E': 66, 'EAN13': 67, 'EAN8': 68,
      'CODE39': 69, 'ITF': 70, 'CODABAR': 71, 'CODE93': 72,
      'CODE128': 73,
    };
    const m = typeMap[type];
    if (m === undefined) return this;

    // GS h n — set barcode height
    this._parts.push(Buffer.from([0x1D, 0x68, height]));
    // GS w n — set barcode width
    this._parts.push(Buffer.from([0x1D, 0x77, width]));
    // GS H n — set HRI position
    this._parts.push(Buffer.from([0x1D, 0x48, hri]));
    // GS k m n data — print barcode (Function B format)
    const dataBytes = Buffer.from(data, 'ascii');
    this._parts.push(Buffer.from([0x1D, 0x6B, m, dataBytes.length]));
    this._parts.push(dataBytes);
    return this;
  }

  /**
   * GS ( k — Print a QR code.
   * @param {string} data - QR code content
   * @param {object} [options]
   * @param {number} [options.size=6] - module size (1-16)
   * @param {string} [options.errorCorrection='M'] - error correction: 'L','M','Q','H'
   */
  qrcode(data, options = {}) {
    const size = Math.max(1, Math.min(16, options.size || 6));
    const ecMap = { L: 48, M: 49, Q: 50, H: 51 };
    const ec = ecMap[(options.errorCorrection || 'M').toUpperCase()] ?? 49;
    const dataBytes = Buffer.from(data, 'utf8');

    // QR Code: function 165 — select model 2
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 4, 0, 0x31, 0x41, 50, 0]));
    // QR Code: function 167 — set module size
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x43, size]));
    // QR Code: function 169 — set error correction
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x45, ec]));
    // QR Code: function 180 — store data
    const storeLen = dataBytes.length + 3;
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, storeLen & 0xFF, (storeLen >> 8) & 0xFF, 0x31, 0x50, 0x30]));
    this._parts.push(dataBytes);
    // QR Code: function 181 — print
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30]));
    return this;
  }

  /**
   * GS ( k — Print a PDF417 barcode.
   * @param {string} data - PDF417 content
   * @param {object} [options]
   * @param {number} [options.columns=0] - number of columns (0=auto, 1-30)
   * @param {number} [options.rows=0] - number of rows (0=auto, 3-90)
   * @param {number} [options.width=3] - module width (2-8)
   * @param {number} [options.height=3] - row height (2-8)
   * @param {number} [options.errorCorrection=1] - error correction level (0-8)
   */
  pdf417(data, options = {}) {
    const columns = Math.max(0, Math.min(30, options.columns || 0));
    const rows = Math.max(0, Math.min(90, options.rows || 0));
    const width = Math.max(2, Math.min(8, options.width || 3));
    const height = Math.max(2, Math.min(8, options.height || 3));
    const ec = Math.max(0, Math.min(8, options.errorCorrection ?? 1));
    const dataBytes = Buffer.from(data, 'utf8');

    // PDF417: function 65 — set number of columns
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x41, columns]));
    // PDF417: function 66 — set number of rows
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x42, rows]));
    // PDF417: function 67 — set module width
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x43, width]));
    // PDF417: function 68 — set row height
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x44, height]));
    // PDF417: function 69 — set error correction level
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 4, 0, 0x30, 0x45, 48 + ec, 0]));
    // PDF417: function 80 — store data
    const storeLen = dataBytes.length + 3;
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, storeLen & 0xFF, (storeLen >> 8) & 0xFF, 0x30, 0x50, 0x30]));
    this._parts.push(dataBytes);
    // PDF417: function 81 — print
    this._parts.push(Buffer.from([0x1D, 0x28, 0x6B, 3, 0, 0x30, 0x51, 0x30]));
    return this;
  }

  // ─── Low-level ────────────────────────────────────────────

  /** Append raw bytes (array or Buffer) */
  bytes(data) {
    this._parts.push(Buffer.isBuffer(data) ? data : Buffer.from(data));
    return this;
  }

  /** Build and return the final Buffer */
  encode() {
    return Buffer.concat(this._parts);
  }

  /** The configured column width */
  get columnWidth() {
    return this._columns;
  }
}

module.exports = { EscPosEncoder, CODEPAGES, COLUMNS };
