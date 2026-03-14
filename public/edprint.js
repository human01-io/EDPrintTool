/**
 * EDPrintTool Client Library v1.0
 *
 * Drop-in JavaScript client for web apps to print ZPL to Zebra printers
 * via the EDPrintTool local service.
 *
 * === QUICK START (WebSocket) ===
 *
 *   <script src="http://localhost:8189/edprint.js"></script>
 *   <script>
 *     const ep = new EDPrint();
 *     await ep.connect();
 *
 *     // List printers configured in the dashboard
 *     const printers = await ep.listPrinters();
 *     console.log(printers);
 *
 *     // Print a label
 *     await ep.print('my-printer-id', '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ');
 *
 *     // Print 3 copies
 *     await ep.print('my-printer-id', zpl, { copies: 3 });
 *
 *     // Print without applying saved printer settings (raw passthrough)
 *     await ep.print('my-printer-id', zpl, { applySettings: false });
 *   </script>
 *
 * === QUICK START (REST API — no library needed) ===
 *
 *   // Print via fetch
 *   fetch('http://localhost:8189/api/print/my-printer-id', {
 *     method: 'POST',
 *     headers: { 'Content-Type': 'application/json' },
 *     body: JSON.stringify({
 *       zpl: '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ',
 *       copies: 1
 *     })
 *   });
 */
class EDPrint {
  constructor(url = 'ws://localhost:8189') {
    this.url = url;
    this.ws = null;
    this._requestId = 0;
    this._pending = new Map();
    this._listeners = { open: [], close: [], error: [], message: [] };
  }

  /** Connect to the EDPrintTool server */
  connect() {
    return new Promise((resolve, reject) => {
      this.ws = new WebSocket(this.url);
      this.ws.onopen = () => { this._emit('open'); resolve(); };
      this.ws.onclose = (e) => {
        this._emit('close', e);
        for (const [, { reject: rej }] of this._pending) rej(new Error('WebSocket closed'));
        this._pending.clear();
      };
      this.ws.onerror = (e) => { this._emit('error', e); reject(new Error('WebSocket connection failed')); };
      this.ws.onmessage = (e) => {
        const msg = JSON.parse(e.data);
        this._emit('message', msg);
        if (msg.requestId && this._pending.has(msg.requestId)) {
          const { resolve: res, reject: rej } = this._pending.get(msg.requestId);
          this._pending.delete(msg.requestId);
          msg.success ? res(msg.data) : rej(new Error(msg.error));
        }
      };
    });
  }

  /** Disconnect */
  disconnect() { if (this.ws) this.ws.close(); }

  /** @private Send a command and wait for the response */
  _send(payload) {
    return new Promise((resolve, reject) => {
      if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return reject(new Error('Not connected'));
      const requestId = String(++this._requestId);
      this._pending.set(requestId, { resolve, reject });
      this.ws.send(JSON.stringify({ ...payload, requestId }));
    });
  }

  /** Get server status */
  status() { return this._send({ action: 'status' }); }

  /** List configured printers (with their settings) */
  listPrinters() { return this._send({ action: 'listPrinters' }); }

  /** Get available label size presets */
  getLabelPresets() { return this._send({ action: 'getLabelPresets' }); }

  /** Discover system printers (Windows/macOS/Linux) */
  discoverPrinters() { return this._send({ action: 'discoverPrinters' }); }

  /** Add a network printer */
  addNetworkPrinter(name, host, port = 9100, settings = {}) {
    return this._send({ action: 'addPrinter', printer: { name, type: 'network', host, port, settings } });
  }

  /** Add a Windows USB printer */
  addWindowsPrinter(name, windowsPrinter, settings = {}) {
    return this._send({ action: 'addPrinter', printer: { name, type: 'usb', windowsPrinter, settings } });
  }

  /** Add a CUPS (macOS/Linux) USB printer */
  addCUPSPrinter(name, cupsQueue, settings = {}) {
    return this._send({ action: 'addPrinter', printer: { name, type: 'usb', cupsQueue, settings } });
  }

  /**
   * Update printer settings (label size, darkness, speed, etc.)
   * @param {string} printerId
   * @param {object} settings - any of: labelPreset, widthDots, heightDots, dpi,
   *                            darkness (0-30), speed (2-14), mediaType (D/T),
   *                            printMode (T/P/C), orientation (N/R/I/B)
   */
  updateSettings(printerId, settings) {
    return this._send({ action: 'updateSettings', printerId, settings });
  }

  /** Remove a printer */
  removePrinter(printerId) {
    return this._send({ action: 'removePrinter', printerId });
  }

  /**
   * Print ZPL to a configured printer
   * @param {string} printerId
   * @param {string} zpl - raw ZPL code
   * @param {object} [options]
   * @param {number} [options.copies=1] - number of copies
   * @param {boolean} [options.applySettings=true] - prepend printer setup ZPL
   */
  print(printerId, zpl, options = {}) {
    return this._send({
      action: 'print',
      printerId,
      zpl,
      copies: options.copies || 1,
      applySettings: options.applySettings !== false,
    });
  }

  /** Quick print — send ZPL directly to an IP:port (no saved printer needed) */
  printRaw(host, zpl, port = 9100) {
    return this._send({ action: 'printRaw', host, port, zpl });
  }

  /**
   * Print a PDF document through the OS spooler (USB printers only).
   * @param {string} printerId
   * @param {string} fileBase64 - base64-encoded PDF file
   * @param {object} [options]
   * @param {number} [options.copies=1]
   */
  printDocument(printerId, fileBase64, options = {}) {
    return this._send({
      action: 'printDocument',
      printerId,
      file: fileBase64,
      copies: options.copies || 1,
    });
  }

  /** Register event listener (open, close, error, message) */
  on(event, fn) { if (this._listeners[event]) this._listeners[event].push(fn); return this; }

  /** Remove event listener */
  off(event, fn) {
    if (this._listeners[event]) this._listeners[event] = this._listeners[event].filter(f => f !== fn);
    return this;
  }

  /** @private */
  _emit(event, ...args) {
    if (this._listeners[event]) for (const fn of this._listeners[event]) fn(...args);
  }
}

// Export for Node.js / module bundlers, or attach to window
if (typeof module !== 'undefined' && module.exports) {
  module.exports = EDPrint;
} else if (typeof window !== 'undefined') {
  window.EDPrint = EDPrint;
}
