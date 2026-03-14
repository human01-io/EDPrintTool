/**
 * EDPrintTool Cloud Relay Server
 *
 * A lightweight, self-hostable relay that routes print jobs from web apps
 * to remote EDPrintTool instances connected via WebSocket.
 *
 * Flow:
 *   [Web App] → HTTPS → [This Relay] ← WSS ← [EDPrintTool @ Store]
 *
 * Environment variables:
 *   RELAY_PORT       - HTTP/WS listen port (default: 8190)
 *   RELAY_ADMIN_KEY  - Admin API key for managing locations (required)
 *   RELAY_DATA_DIR   - Directory for locations.json (default: ./data)
 *   RELAY_JOB_TIMEOUT - Ms to wait for print result (default: 30000)
 */

const http = require('http');
const { WebSocketServer } = require('ws');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const PORT = parseInt(process.env.RELAY_PORT) || 8190;
const ADMIN_KEY = process.env.RELAY_ADMIN_KEY || '';
const DATA_DIR = process.env.RELAY_DATA_DIR || path.join(__dirname, 'data');
const JOB_TIMEOUT = parseInt(process.env.RELAY_JOB_TIMEOUT) || 30000;

if (!ADMIN_KEY) {
  console.error('[Relay] RELAY_ADMIN_KEY is required. Set it as an environment variable.');
  process.exit(1);
}

// ─── Location store ──────────────────────────────────────────

if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true });
const LOCATIONS_PATH = path.join(DATA_DIR, 'locations.json');

// Map<locationId, { apiKey, name, createdAt }>
const locations = new Map();

function saveLocations() {
  const data = JSON.stringify(Array.from(locations.entries()).map(([id, v]) => ({ id, ...v })), null, 2);
  fs.writeFileSync(LOCATIONS_PATH, data, 'utf8');
}

function loadLocations() {
  try {
    if (fs.existsSync(LOCATIONS_PATH)) {
      const data = JSON.parse(fs.readFileSync(LOCATIONS_PATH, 'utf8'));
      for (const loc of data) {
        locations.set(loc.id, { apiKey: loc.apiKey, name: loc.name, createdAt: loc.createdAt });
      }
      console.log(`[Relay] Loaded ${locations.size} location(s)`);
    }
  } catch (err) {
    console.error('[Relay] Failed to load locations:', err.message);
  }
}

loadLocations();

// ─── Connected EDPrintTool instances ─────────────────────────

// Map<locationId, WebSocket>
const connections = new Map();

// Map<jobId, { resolve, reject, timer }>
const pendingJobs = new Map();

// ─── HTTP Server ─────────────────────────────────────────────

const server = http.createServer(async (req, res) => {
  // CORS
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, DELETE, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-API-Key, X-Admin-Key');

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  try {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const segments = url.pathname.split('/').filter(Boolean); // ['api', ...]

    // GET /api/status
    if (req.method === 'GET' && url.pathname === '/api/status') {
      return json(res, 200, {
        status: 'running',
        type: 'relay',
        locations: locations.size,
        connected: connections.size,
      });
    }

    // ─── Admin routes (require X-Admin-Key) ────────────

    // POST /api/locations — register a new location
    if (req.method === 'POST' && url.pathname === '/api/locations') {
      if (!checkAdmin(req, res)) return;
      const body = await readBody(req);
      const { name, locationId } = body;
      const id = locationId || (name || 'location').toLowerCase().replace(/[^a-z0-9]+/g, '-');
      if (locations.has(id)) return json(res, 409, { error: `Location already exists: ${id}` });
      const apiKey = crypto.randomBytes(16).toString('hex');
      locations.set(id, { apiKey, name: name || id, createdAt: new Date().toISOString() });
      saveLocations();
      return json(res, 201, { locationId: id, apiKey, name: name || id });
    }

    // GET /api/locations — list all locations
    if (req.method === 'GET' && url.pathname === '/api/locations') {
      if (!checkAdmin(req, res)) return;
      const list = Array.from(locations.entries()).map(([id, v]) => ({
        locationId: id,
        name: v.name,
        online: connections.has(id),
        createdAt: v.createdAt,
      }));
      return json(res, 200, list);
    }

    // DELETE /api/locations/:id
    if (req.method === 'DELETE' && segments[0] === 'api' && segments[1] === 'locations' && segments[2]) {
      if (!checkAdmin(req, res)) return;
      const id = decodeURIComponent(segments[2]);
      const removed = locations.delete(id);
      if (removed) saveLocations();
      // Disconnect if connected
      const ws = connections.get(id);
      if (ws) { ws.close(); connections.delete(id); }
      return json(res, 200, { removed });
    }

    // ─── Location routes (require X-API-Key) ───────────

    // All routes under /api/locations/:id/...
    if (segments[0] === 'api' && segments[1] === 'locations' && segments[2]) {
      const locationId = decodeURIComponent(segments[2]);
      const loc = locations.get(locationId);
      if (!loc) return json(res, 404, { error: `Location not found: ${locationId}` });

      const apiKey = req.headers['x-api-key'];
      if (apiKey !== loc.apiKey) return json(res, 401, { error: 'Invalid API key' });

      const ws = connections.get(locationId);
      if (!ws || ws.readyState !== 1) {
        return json(res, 503, { error: `EDPrintTool at "${locationId}" is not connected` });
      }

      const subPath = segments.slice(3); // e.g. ['printers'] or ['print', 'printer-1']

      // GET /api/locations/:id/printers
      if (req.method === 'GET' && subPath[0] === 'printers' && subPath.length === 1) {
        return await forwardJob(ws, res, { action: 'listPrinters' });
      }

      // GET /api/locations/:id/status
      if (req.method === 'GET' && subPath[0] === 'status') {
        return await forwardJob(ws, res, { action: 'status' });
      }

      // POST /api/locations/:id/print/:printerId
      if (req.method === 'POST' && subPath[0] === 'print' && subPath[1]) {
        const body = await readBody(req);
        return await forwardJob(ws, res, {
          action: 'print',
          printerId: decodeURIComponent(subPath[1]),
          zpl: body.zpl,
          copies: body.copies || 1,
          applySettings: body.applySettings !== false,
        });
      }

      // POST /api/locations/:id/print-escpos/:printerId
      if (req.method === 'POST' && subPath[0] === 'print-escpos' && subPath[1]) {
        const body = await readBody(req);
        return await forwardJob(ws, res, {
          action: 'printEscPos',
          printerId: decodeURIComponent(subPath[1]),
          commands: body.commands,
          copies: body.copies || 1,
        });
      }

      // POST /api/locations/:id/print-document/:printerId
      if (req.method === 'POST' && subPath[0] === 'print-document' && subPath[1]) {
        const body = await readBody(req);
        return await forwardJob(ws, res, {
          action: 'printDocument',
          printerId: decodeURIComponent(subPath[1]),
          file: body.file,
          copies: body.copies || 1,
        });
      }

      // POST /api/locations/:id/print-raw
      if (req.method === 'POST' && subPath[0] === 'print-raw') {
        const body = await readBody(req);
        return await forwardJob(ws, res, {
          action: 'printRaw',
          host: body.host,
          port: body.port || 9100,
          zpl: body.zpl,
        });
      }

      return json(res, 404, { error: 'Not found' });
    }

    json(res, 404, { error: 'Not found' });
  } catch (err) {
    json(res, 500, { error: err.message });
  }
});

// ─── WebSocket Server ────────────────────────────────────────

const wss = new WebSocketServer({ server, path: '/ws/connect' });

wss.on('connection', (ws) => {
  let authenticatedLocation = null;

  // Auto-close if no auth within 10 seconds
  const authTimer = setTimeout(() => {
    if (!authenticatedLocation) {
      ws.send(JSON.stringify({ type: 'auth', success: false, error: 'Auth timeout' }));
      ws.close();
    }
  }, 10000);

  ws.on('message', (data) => {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }

    // Auth message
    if (msg.type === 'auth') {
      clearTimeout(authTimer);
      const loc = locations.get(msg.locationId);
      if (!loc || loc.apiKey !== msg.apiKey) {
        ws.send(JSON.stringify({ type: 'auth', success: false, error: 'Invalid locationId or apiKey' }));
        ws.close();
        return;
      }

      // Close existing connection for this location (if any)
      const existing = connections.get(msg.locationId);
      if (existing && existing.readyState === 1) {
        existing.close();
      }

      authenticatedLocation = msg.locationId;
      connections.set(msg.locationId, ws);
      ws.send(JSON.stringify({ type: 'auth', success: true }));
      console.log(`[Relay] Location "${msg.locationId}" connected`);
      return;
    }

    // Job result from EDPrintTool
    if (msg.type === 'jobResult' && msg.jobId) {
      const pending = pendingJobs.get(msg.jobId);
      if (pending) {
        clearTimeout(pending.timer);
        pendingJobs.delete(msg.jobId);
        pending.resolve(msg);
      }
      return;
    }
  });

  ws.on('close', () => {
    clearTimeout(authTimer);
    if (authenticatedLocation) {
      // Only remove if this is still the active connection
      if (connections.get(authenticatedLocation) === ws) {
        connections.delete(authenticatedLocation);
      }
      console.log(`[Relay] Location "${authenticatedLocation}" disconnected`);
    }
  });

  ws.on('error', () => {});
});

// Heartbeat: ping connected clients every 30s
setInterval(() => {
  for (const [locationId, ws] of connections) {
    if (ws.readyState === 1) {
      ws.ping();
    } else {
      connections.delete(locationId);
    }
  }
}, 30000);

// ─── Helpers ─────────────────────────────────────────────────

function json(res, status, data) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(data));
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString()));
      } catch {
        resolve({});
      }
    });
    req.on('error', reject);
  });
}

function checkAdmin(req, res) {
  const key = req.headers['x-admin-key'];
  if (key !== ADMIN_KEY) {
    json(res, 401, { error: 'Invalid or missing X-Admin-Key' });
    return false;
  }
  return true;
}

/**
 * Forward a job to a connected EDPrintTool and wait for the result.
 */
function forwardJob(ws, res, jobData) {
  return new Promise((resolve) => {
    const jobId = crypto.randomUUID();

    const timer = setTimeout(() => {
      pendingJobs.delete(jobId);
      json(res, 504, { error: 'Print job timed out — EDPrintTool did not respond' });
      resolve();
    }, JOB_TIMEOUT);

    pendingJobs.set(jobId, {
      resolve: (result) => {
        if (result.success) {
          json(res, 200, result.data);
        } else {
          json(res, 500, { error: result.error });
        }
        resolve();
      },
      timer,
    });

    ws.send(JSON.stringify({ type: 'job', jobId, ...jobData }));
  });
}

// ─── Start ───────────────────────────────────────────────────

server.listen(PORT, () => {
  console.log(`
  ╔══════════════════════════════════════════╗
  ║       EDPrintTool Relay Server           ║
  ╠══════════════════════════════════════════╣
  ║  REST API:   http://localhost:${String(PORT).padEnd(11)}║
  ║  WS Connect: ws://localhost:${String(PORT).padEnd(13)}║
  ║               /ws/connect               ║
  ╚══════════════════════════════════════════╝
  `);
});
