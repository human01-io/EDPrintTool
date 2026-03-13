/**
 * ESC/POS Encoder — chainable command builder for thermal receipt printers.
 *
 * Architecture follows ReceiptPrinterEncoder / node-thermal-printer patterns:
 *   1. Encoder builds a byte buffer (no I/O)
 *   2. Transport layer sends it (printer.js handles that)
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
  '58mm': 32,
};

class EscPosEncoder {
  /**
   * @param {object} [options]
   * @param {string} [options.paperWidth='80mm'] - '80mm' or '58mm'
   * @param {number} [options.columns] - override character columns
   * @param {string} [options.codepage] - default code page name
   */
  constructor(options = {}) {
    this._parts = [];
    this._columns = options.columns || COLUMNS[options.paperWidth] || 48;
    this._codepage = options.codepage || null;
  }

  // ─── Printer control ──────────────────────────────────────

  /** ESC @ — Reset printer to defaults */
  initialize() {
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
    this._parts.push(Buffer.from(str, 'utf8'));
    return this;
  }

  /** Write text followed by newline */
  line(str) {
    this._parts.push(Buffer.from(str + '\n', 'utf8'));
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
      this._parts.push(Buffer.from(content, 'utf8'));
    }
    return this;
  }

  /** Print a horizontal rule filling the full paper width */
  rule(char = '-') {
    this._parts.push(Buffer.from(char[0].repeat(this._columns) + '\n', 'utf8'));
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

    this._parts.push(Buffer.from(row + '\n', 'utf8'));
    return this;
  }

  /**
   * Print a key-value pair with dots or spaces between.
   * Example: pair('Subtotal', '$37.47') → "Subtotal................$37.47"
   */
  pair(left, right, fill = '.') {
    const gap = this._columns - left.length - right.length;
    const middle = gap > 0 ? fill[0].repeat(gap) : ' ';
    this._parts.push(Buffer.from(left + middle + right + '\n', 'utf8'));
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
   * Cut paper — feeds first, then sends GS V Function A.
   * Uses separate ESC d (feed) + GS V (cut) for maximum compatibility.
   * Function A (no feed param) works on virtually all ESC/POS printers
   * including Star TSP in emulation mode and budget Chinese printers.
   * @param {'full'|'partial'} [type='partial']
   * @param {number} [feedLines=4] - lines to feed before cutting
   */
  cut(type = 'partial', feedLines = 4) {
    // Feed lines first (ESC d n)
    if (feedLines > 0) {
      this._parts.push(Buffer.from([0x1B, 0x64, Math.min(feedLines, 255)]));
    }
    // GS V Function A — simple, widely supported
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
