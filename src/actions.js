/**
 * Shared action dispatcher for EDPrintTool.
 *
 * Used by both the local WebSocket handler (server.js) and
 * the relay client (relay-client.js) to avoid duplicating
 * the switch/case dispatch logic.
 */

const printer = require('./printer');
const { EscPosEncoder } = require('./escpos');
const { version: VERSION } = require('../package.json');

/**
 * Dispatch a print action message and return the result.
 * @param {object} msg - Action message (same format as WebSocket messages)
 * @returns {Promise<any>} Result object
 */
async function dispatch(msg) {
  const { action } = msg;

  switch (action) {
    case 'status':
      return { status: 'running', version: VERSION, printers: printer.getPrinters().length };

    case 'listPrinters':
      return printer.getPrinters();

    case 'getLabelPresets':
      return printer.getLabelPresets();

    case 'getProfileList':
      return printer.getProfileList();

    case 'discoverPrinters':
      return await printer.discoverPrinters();

    case 'addPrinter':
      return printer.addPrinter(msg.printer);

    case 'updateSettings':
      if (!msg.printerId) throw new Error('Missing printerId');
      return printer.updatePrinterSettings(msg.printerId, msg.settings || {});

    case 'removePrinter':
      return { removed: printer.removePrinter(msg.printerId) };

    case 'print':
      if (!msg.printerId || !msg.zpl) throw new Error('Missing printerId or zpl');
      return await printer.print(msg.printerId, msg.zpl, {
        copies: msg.copies || 1,
        applySettings: msg.applySettings !== false,
      });

    case 'printEscPos': {
      if (!msg.printerId || !msg.commands) throw new Error('Missing printerId or commands');
      const p = printer.getPrinter(msg.printerId);
      if (!p) throw new Error(`Printer not found: ${msg.printerId}`);
      const s = p.settings || {};
      const encoder = new EscPosEncoder({ paperWidth: s.paperWidth });
      const copies = msg.copies || 1;
      for (let i = 0; i < copies; i++) {
        for (const cmd of msg.commands) {
          const [method, ...args] = Array.isArray(cmd) ? cmd : [cmd];
          if (typeof encoder[method] !== 'function') {
            throw new Error(`Unknown ESC/POS command: ${method}`);
          }
          encoder[method](...args);
        }
      }
      const payload = encoder.encode();
      const os = require('os');
      if (p.type === 'network') {
        return await printer.printNetwork(p.host, p.port, payload);
      } else if (p.type === 'usb') {
        if (os.platform() === 'win32') {
          return await printer.printWindows(p.windowsPrinter, payload);
        } else {
          return await printer.printCUPS(p.cupsQueue, payload);
        }
      }
      throw new Error(`Unknown printer type: ${p.type}`);
    }

    case 'printRaw':
      if (!msg.host || !msg.zpl) throw new Error('Missing host or zpl');
      return await printer.printNetwork(msg.host, msg.port || 9100, msg.zpl);

    case 'printDocument':
      if (!msg.printerId || !msg.file) throw new Error('Missing printerId or file');
      return await printer.printDocument(msg.printerId, msg.file, {
        copies: msg.copies || 1,
      });

    default:
      throw new Error(`Unknown action: ${action}`);
  }
}

module.exports = { dispatch };
