/**
 * Relay Client — connects EDPrintTool to a cloud relay server.
 *
 * When configured, EDPrintTool opens a persistent outbound WebSocket
 * to the relay. The relay forwards print jobs from remote web apps,
 * and this client executes them using the same dispatch logic as
 * the local WebSocket handler.
 *
 * Configuration (env vars or <configDir>/relay.json):
 *   RELAY_URL          - WebSocket URL of the relay (e.g. wss://relay.example.com/ws/connect)
 *   RELAY_LOCATION_ID  - Location identifier for this EDPrintTool instance
 *   RELAY_API_KEY      - API key for authentication
 */

const WebSocket = require('ws');
const fs = require('fs');
const path = require('path');
const os = require('os');
const { dispatch } = require('./actions');

function getConfigDir() {
  if (process.env.EDPRINT_DATA_DIR) return process.env.EDPRINT_DATA_DIR;
  return process.env.APPDATA
    ? path.join(process.env.APPDATA, 'EDPrintTool')
    : (os.platform() === 'darwin'
      ? path.join(os.homedir(), 'Library', 'Application Support', 'EDPrintTool')
      : path.join(os.homedir(), '.edprinttool'));
}

function loadConfig() {
  // Priority: env vars > relay.json
  if (process.env.RELAY_URL && process.env.RELAY_LOCATION_ID && process.env.RELAY_API_KEY) {
    return {
      enabled: true,
      relayUrl: process.env.RELAY_URL,
      locationId: process.env.RELAY_LOCATION_ID,
      apiKey: process.env.RELAY_API_KEY,
    };
  }

  const configPath = path.join(getConfigDir(), 'relay.json');
  try {
    if (fs.existsSync(configPath)) {
      const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
      if (config.enabled && config.relayUrl && config.locationId && config.apiKey) {
        return config;
      }
    }
  } catch (err) {
    console.error('[Relay] Failed to load relay.json:', err.message);
  }

  return null;
}

function startRelayClient() {
  const config = loadConfig();
  if (!config) return null;

  console.log(`[Relay] Connecting to ${config.relayUrl} as location "${config.locationId}"...`);

  let ws = null;
  let reconnectDelay = 1000;
  let stopped = false;

  function connect() {
    if (stopped) return;

    ws = new WebSocket(config.relayUrl);

    ws.on('open', () => {
      console.log('[Relay] Connected to relay server');
      reconnectDelay = 1000; // reset backoff

      // Authenticate
      ws.send(JSON.stringify({
        type: 'auth',
        locationId: config.locationId,
        apiKey: config.apiKey,
      }));
    });

    ws.on('message', async (data) => {
      let msg;
      try {
        msg = JSON.parse(data);
      } catch {
        return;
      }

      // Auth response
      if (msg.type === 'auth') {
        if (msg.success) {
          console.log('[Relay] Authenticated successfully');
        } else {
          console.error('[Relay] Authentication failed:', msg.error);
          stopped = true;
          ws.close();
        }
        return;
      }

      // Print job from relay
      if (msg.type === 'job') {
        const { jobId } = msg;
        try {
          const result = await dispatch(msg);
          ws.send(JSON.stringify({ type: 'jobResult', jobId, success: true, data: result }));
        } catch (err) {
          ws.send(JSON.stringify({ type: 'jobResult', jobId, success: false, error: err.message }));
        }
        return;
      }

      // Ping/pong handled automatically by ws library
    });

    ws.on('close', () => {
      if (stopped) return;
      console.log(`[Relay] Disconnected. Reconnecting in ${reconnectDelay / 1000}s...`);
      setTimeout(connect, reconnectDelay);
      reconnectDelay = Math.min(reconnectDelay * 2, 30000); // exponential backoff, max 30s
    });

    ws.on('error', (err) => {
      console.error('[Relay] Connection error:', err.message);
      // 'close' event will fire after this, triggering reconnect
    });
  }

  connect();

  return {
    stop() {
      stopped = true;
      if (ws) ws.close();
    },
  };
}

module.exports = { startRelayClient };
