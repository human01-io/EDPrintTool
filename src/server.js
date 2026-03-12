const express = require('express');
const http = require('http');
const { WebSocketServer } = require('ws');
const cors = require('cors');
const path = require('path');
const printer = require('./printer');

const PORT = process.env.PORT || 8189;
const app = express();

app.use(cors());
app.use(express.json({ limit: '5mb' }));
app.use(express.text({ limit: '5mb', type: 'text/plain' }));
app.use(express.static(path.join(__dirname, '..', 'public')));

// ─── REST API ────────────────────────────────────────────────

// Health check
app.get('/api/status', (req, res) => {
  res.json({ status: 'running', version: '1.0.0', printers: printer.getPrinters().length });
});

// Label presets
app.get('/api/label-presets', (req, res) => {
  res.json(printer.getLabelPresets());
});

// List configured printers
app.get('/api/printers', (req, res) => {
  res.json(printer.getPrinters());
});

// Discover system printers (Windows + macOS/Linux)
app.get('/api/printers/discover', async (req, res) => {
  try {
    const found = await printer.discoverPrinters();
    res.json(found);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Add a printer
app.post('/api/printers', (req, res) => {
  try {
    const p = printer.addPrinter(req.body);
    res.status(201).json(p);
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

// Update printer settings
app.patch('/api/printers/:id/settings', (req, res) => {
  try {
    const p = printer.updatePrinterSettings(req.params.id, req.body);
    res.json(p);
  } catch (err) {
    res.status(400).json({ error: err.message });
  }
});

// Remove a printer
app.delete('/api/printers/:id', (req, res) => {
  const removed = printer.removePrinter(req.params.id);
  res.json({ removed });
});

// Print ZPL to a specific printer
app.post('/api/print/:printerId', async (req, res) => {
  try {
    const zpl = typeof req.body === 'string' ? req.body : req.body.zpl;
    const copies = (typeof req.body === 'object' && req.body.copies) || 1;
    const applySettings = typeof req.body === 'object' ? req.body.applySettings : true;
    if (!zpl) return res.status(400).json({ error: 'Missing ZPL data. Send as text body or { "zpl": "...", "copies": 1 }' });
    const result = await printer.print(req.params.printerId, zpl, { copies, applySettings });
    res.json(result);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Quick print — send ZPL directly to an IP:port without saving the printer
app.post('/api/print-raw', async (req, res) => {
  try {
    const { host, port, zpl } = req.body;
    if (!host || !zpl) return res.status(400).json({ error: 'Missing host or zpl' });
    const result = await printer.printNetwork(host, port || 9100, zpl);
    res.json(result);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ─── HTTP + WebSocket Server ─────────────────────────────────

const server = http.createServer(app);
const wss = new WebSocketServer({ server });

wss.on('connection', (ws) => {
  console.log('[WS] Client connected');

  ws.on('message', async (data) => {
    let msg;
    try {
      msg = JSON.parse(data);
    } catch {
      ws.send(JSON.stringify({ error: 'Invalid JSON' }));
      return;
    }

    const { action } = msg;
    const requestId = msg.requestId || null;

    try {
      let result;

      switch (action) {
        case 'status':
          result = { status: 'running', version: '1.0.0', printers: printer.getPrinters().length };
          break;

        case 'listPrinters':
          result = printer.getPrinters();
          break;

        case 'getLabelPresets':
          result = printer.getLabelPresets();
          break;

        case 'discoverPrinters':
          result = await printer.discoverPrinters();
          break;

        case 'addPrinter':
          result = printer.addPrinter(msg.printer);
          break;

        case 'updateSettings':
          if (!msg.printerId) throw new Error('Missing printerId');
          result = printer.updatePrinterSettings(msg.printerId, msg.settings || {});
          break;

        case 'removePrinter':
          result = { removed: printer.removePrinter(msg.printerId) };
          break;

        case 'print':
          if (!msg.printerId || !msg.zpl) throw new Error('Missing printerId or zpl');
          result = await printer.print(msg.printerId, msg.zpl, {
            copies: msg.copies || 1,
            applySettings: msg.applySettings !== false,
          });
          break;

        case 'printRaw':
          if (!msg.host || !msg.zpl) throw new Error('Missing host or zpl');
          result = await printer.printNetwork(msg.host, msg.port || 9100, msg.zpl);
          break;

        default:
          throw new Error(`Unknown action: ${action}`);
      }

      ws.send(JSON.stringify({ requestId, success: true, data: result }));
    } catch (err) {
      ws.send(JSON.stringify({ requestId, success: false, error: err.message }));
    }
  });

  ws.on('close', () => console.log('[WS] Client disconnected'));
});

// ─── Start ───────────────────────────────────────────────────

function start() {
  return new Promise((resolve, reject) => {
    server.listen(PORT, () => {
      console.log(`[EDPrintTool] Server running on port ${PORT}`);
      resolve(server);
    });
    server.on('error', reject);
  });
}

if (require.main === module) {
  start().then(() => {
    console.log(`
  ╔══════════════════════════════════════════╗
  ║          EDPrintTool v1.0.0              ║
  ╠══════════════════════════════════════════╣
  ║  Dashboard:  http://localhost:${PORT}       ║
  ║  REST API:   http://localhost:${PORT}/api   ║
  ║  WebSocket:  ws://localhost:${PORT}         ║
  ╚══════════════════════════════════════════╝
    `);
  }).catch((err) => {
    console.error('Failed to start server:', err.message);
    process.exit(1);
  });
}

module.exports = { start, app, server };
