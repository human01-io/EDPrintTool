const express = require('express');
const http = require('http');
const { WebSocketServer } = require('ws');
const cors = require('cors');
const path = require('path');
const printer = require('./printer');
const { EscPosEncoder } = require('./escpos');

const swaggerUiPath = require('swagger-ui-dist').absolutePath();

const PORT = process.env.PORT || 8189;
const app = express();

app.use(cors());
app.use(express.json({ limit: '5mb' }));
app.use(express.text({ limit: '5mb', type: 'text/plain' }));

// Swagger UI at /docs
app.get('/docs', (req, res) => {
  res.send(`<!DOCTYPE html>
<html><head>
  <title>EDPrintTool API</title>
  <link rel="stylesheet" href="/swagger-ui/swagger-ui.css">
</head><body>
  <div id="swagger-ui"></div>
  <script src="/swagger-ui/swagger-ui-bundle.js"></script>
  <script>SwaggerUIBundle({ url: '/openapi.json', dom_id: '#swagger-ui' });</script>
</body></html>`);
});
app.use('/swagger-ui', express.static(swaggerUiPath));

app.use(express.static(path.join(__dirname, '..', 'public')));

// ─── REST API ────────────────────────────────────────────────

// Health check
app.get('/api/status', (req, res) => {
  res.json({ status: 'running', version: '1.3.0', printers: printer.getPrinters().length });
});

// Label presets
app.get('/api/label-presets', (req, res) => {
  res.json(printer.getLabelPresets());
});

// Printer profiles (ESC/POS capability sets)
app.get('/api/printer-profiles', (req, res) => {
  res.json(printer.getProfileList());
});

// List configured printers
app.get('/api/printers', (req, res) => {
  res.json(printer.getPrinters());
});

// Debug — shows settings + hex dump of what would be sent
app.get('/api/printers/:id/debug', (req, res) => {
  const p = printer.getPrinter(req.params.id);
  if (!p) return res.status(404).json({ error: `Printer not found: ${req.params.id}` });
  const s = p.settings || {};
  const isEscPos = s.language === 'ESC/POS';
  let payload;
  if (isEscPos) {
    const enc = new EscPosEncoder({ paperWidth: s.paperWidth, codepage: s.codepage || null });
    enc.initialize();
    enc.raw('Test line\n');
    if (s.autoCut) enc.cut(s.cutType || 'partial', s.feedLines ?? 4);
    payload = enc.encode();
  } else {
    payload = Buffer.from(printer.buildSetupZPL(s) + '\nTest line\n', 'utf8');
  }
  res.json({
    printerId: p.id,
    printerName: p.name,
    type: p.type,
    host: p.host,
    port: p.port,
    windowsPrinter: p.windowsPrinter,
    settings: {
      language: s.language,
      paperWidth: s.paperWidth,
      autoCut: s.autoCut,
      cutType: s.cutType,
      feedLines: s.feedLines,
      codepage: s.codepage,
      printerProfile: s.printerProfile,
    },
    testPayloadHex: Array.from(payload).map(b => b.toString(16).padStart(2, '0')).join(' '),
    testPayloadLength: payload.length,
  });
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

// Print structured ESC/POS commands to a printer
// Body: { "commands": [...], "copies": 1 }
// Each command is [method, ...args], e.g. ["bold", true], ["line", "Hello"], ["cut"]
app.post('/api/print-escpos/:printerId', async (req, res) => {
  try {
    const { commands, copies = 1 } = req.body;
    if (!commands || !Array.isArray(commands)) {
      return res.status(400).json({ error: 'Missing commands array' });
    }
    const p = printer.getPrinter(req.params.printerId);
    if (!p) return res.status(404).json({ error: `Printer not found: ${req.params.printerId}` });

    const s = p.settings || {};
    const encoder = new EscPosEncoder({ paperWidth: s.paperWidth });
    let payload;
    try {
      for (let i = 0; i < copies; i++) {
        for (const cmd of commands) {
          const [method, ...args] = Array.isArray(cmd) ? cmd : [cmd];
          if (typeof encoder[method] !== 'function') {
            return res.status(400).json({ error: `Unknown ESC/POS command: ${method}` });
          }
          encoder[method](...args);
        }
      }
      payload = encoder.encode();
    } catch (encErr) {
      return res.status(400).json({ error: encErr.message });
    }

    let result;
    if (p.type === 'network') {
      result = await printer.printNetwork(p.host, p.port, payload);
    } else if (p.type === 'usb') {
      if (require('os').platform() === 'win32') {
        result = await printer.printWindows(p.windowsPrinter, payload);
      } else {
        result = await printer.printCUPS(p.cupsQueue, payload);
      }
    } else {
      throw new Error(`Unknown printer type: ${p.type}`);
    }
    res.json(result);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// Print a PDF document through the OS spooler (USB printers only)
app.post('/api/print-document/:printerId', async (req, res) => {
  try {
    const { file, copies = 1 } = req.body;
    if (!file) return res.status(400).json({ error: 'Missing file (base64-encoded PDF)' });
    const result = await printer.printDocument(req.params.printerId, file, { copies });
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
          result = { status: 'running', version: '1.3.0', printers: printer.getPrinters().length };
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

        case 'printDocument':
          if (!msg.printerId || !msg.file) throw new Error('Missing printerId or file');
          result = await printer.printDocument(msg.printerId, msg.file, {
            copies: msg.copies || 1,
          });
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
  ║          EDPrintTool v1.3.0              ║
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
