/**
 * EDPrintTool Client Library v2.0
 *
 * Drop-in JavaScript client for web apps to print ZPL/ESC-POS to thermal printers
 * via EDPrintTool — supports both local (WebSocket) and cloud relay (REST) modes.
 *
 * === LOCAL MODE (WebSocket to localhost) ===
 *
 *   const ep = new EDPrint();
 *   await ep.connect();
 *   const printers = await ep.listPrinters();
 *   await ep.print('my-printer', '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ');
 *
 * === RELAY MODE (REST to cloud relay) ===
 *
 *   const ep = new EDPrint({
 *     mode: 'relay',
 *     relayUrl: 'https://relay.example.com',
 *     locationId: 'store-42',
 *     apiKey: 'your-api-key',
 *   });
 *   // No connect() needed — uses fetch()
 *   const printers = await ep.listPrinters();
 *   await ep.print('my-printer', '^XA...^XZ');
 */
class EDPrint {
  constructor(urlOrOptions = 'ws://localhost:8189') {
    if (typeof urlOrOptions === 'string') {
      // Local WebSocket mode (backward compatible)
      this._mode = 'local';
      this.url = urlOrOptions;
    } else {
      const opts = urlOrOptions;
      this._mode = opts.mode || 'local';
      if (this._mode === 'relay') {
        this._relayUrl = (opts.relayUrl || '').replace(/\/$/, '');
        this._locationId = opts.locationId;
        this._apiKey = opts.apiKey;
        if (!this._relayUrl || !this._locationId || !this._apiKey) {
          throw new Error('Relay mode requires relayUrl, locationId, and apiKey');
        }
      } else {
        this.url = opts.url || 'ws://localhost:8189';
      }
    }
    this.ws = null;
    this._requestId = 0;
    this._pending = new Map();
    this._listeners = { open: [], close: [], error: [], message: [] };
  }

  // ─── Relay mode helpers ──────────────────────────────────

  /** @private REST call to relay server */
  async _relay(method, path, body) {
    const url = `${this._relayUrl}/api/locations/${encodeURIComponent(this._locationId)}${path}`;
    const opts = {
      method,
      headers: { 'Content-Type': 'application/json', 'X-API-Key': this._apiKey },
    };
    if (body) opts.body = JSON.stringify(body);
    const res = await fetch(url, opts);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
    return data;
  }

  // ─── Connection (local mode only) ────────────────────────

  /** Connect to the EDPrintTool server (local mode only, no-op in relay mode) */
  connect() {
    if (this._mode === 'relay') return Promise.resolve();
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

  /** @private Send a command and wait for the response (local mode) */
  _send(payload) {
    return new Promise((resolve, reject) => {
      if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return reject(new Error('Not connected'));
      const requestId = String(++this._requestId);
      this._pending.set(requestId, { resolve, reject });
      this.ws.send(JSON.stringify({ ...payload, requestId }));
    });
  }

  // ─── API methods (work in both modes) ────────────────────

  /** Get server status */
  status() {
    return this._mode === 'relay' ? this._relay('GET', '/status') : this._send({ action: 'status' });
  }

  /** List configured printers (with their settings) */
  listPrinters() {
    return this._mode === 'relay' ? this._relay('GET', '/printers') : this._send({ action: 'listPrinters' });
  }

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
   * @param {object} settings
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
   * @param {number} [options.copies=1]
   * @param {boolean} [options.applySettings=true]
   */
  print(printerId, zpl, options = {}) {
    if (this._mode === 'relay') {
      return this._relay('POST', `/print/${encodeURIComponent(printerId)}`, {
        zpl, copies: options.copies || 1, applySettings: options.applySettings !== false,
      });
    }
    return this._send({
      action: 'print', printerId, zpl,
      copies: options.copies || 1, applySettings: options.applySettings !== false,
    });
  }

  /** Quick print — send ZPL directly to an IP:port (no saved printer needed) */
  printRaw(host, zpl, port = 9100) {
    if (this._mode === 'relay') {
      return this._relay('POST', '/print-raw', { host, port, zpl });
    }
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
    if (this._mode === 'relay') {
      return this._relay('POST', `/print-document/${encodeURIComponent(printerId)}`, {
        file: fileBase64, copies: options.copies || 1,
      });
    }
    return this._send({
      action: 'printDocument', printerId, file: fileBase64, copies: options.copies || 1,
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
