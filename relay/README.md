# EDPrintTool Cloud Relay

A lightweight, self-hostable relay server that routes print jobs from web applications to remote EDPrintTool instances over WebSocket.

## How It Works

```
[Web App] → HTTPS → [Cloud Relay] ← WSS ← [EDPrintTool @ Store A]
                                   ← WSS ← [EDPrintTool @ Store B]
```

1. EDPrintTool instances connect **outbound** to the relay via WebSocket (no port forwarding needed)
2. Web apps send print jobs to the relay's REST API
3. The relay forwards jobs to the correct EDPrintTool instance and returns the result

## Quick Start

```bash
cd relay
npm install

# Set the admin key (required)
export RELAY_ADMIN_KEY=your-secret-admin-key

npm start
```

The relay runs on port 8190 by default.

## Register a Location

Each EDPrintTool instance is a "location" with its own API key:

```bash
# Create a location
curl -X POST http://localhost:8190/api/locations \
  -H "Content-Type: application/json" \
  -H "X-Admin-Key: your-secret-admin-key" \
  -d '{"name": "Store A", "locationId": "store-a"}'

# Response: { "locationId": "store-a", "apiKey": "generated-key-here", "name": "Store A" }
```

## Configure EDPrintTool to Connect

On the machine with printers, create `relay.json` in the EDPrintTool config directory:

- **Windows:** `%APPDATA%\EDPrintTool\relay.json`
- **macOS:** `~/Library/Application Support/EDPrintTool/relay.json`
- **Linux:** `~/.edprinttool/relay.json`

```json
{
  "enabled": true,
  "relayUrl": "wss://relay.example.com/ws/connect",
  "locationId": "store-a",
  "apiKey": "generated-key-here"
}
```

Or use environment variables:

```bash
RELAY_URL=wss://relay.example.com/ws/connect
RELAY_LOCATION_ID=store-a
RELAY_API_KEY=generated-key-here
```

Then start EDPrintTool normally — it will connect to the relay automatically while still serving localhost:8189.

## Print via the Relay

From your web app, send print jobs to the relay instead of localhost:

```js
// Using the client library (relay mode)
const ep = new EDPrint({
  mode: 'relay',
  relayUrl: 'https://relay.example.com',
  locationId: 'store-a',
  apiKey: 'generated-key-here',
});
await ep.print('my-printer', '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ');

// Or plain fetch
fetch('https://relay.example.com/api/locations/store-a/print/my-printer', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-API-Key': 'generated-key-here' },
  body: JSON.stringify({ zpl: '^XA...^XZ', copies: 1 })
});
```

## Relay API

### Admin Endpoints (require `X-Admin-Key` header)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/locations` | Register a location (`{ name, locationId }`) |
| `GET` | `/api/locations` | List all locations with online status |
| `DELETE` | `/api/locations/:id` | Remove a location |

### Print Endpoints (require `X-API-Key` header)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/locations/:id/status` | Get EDPrintTool status |
| `GET` | `/api/locations/:id/printers` | List printers at location |
| `POST` | `/api/locations/:id/print/:printerId` | Print ZPL |
| `POST` | `/api/locations/:id/print-escpos/:printerId` | Print ESC/POS commands |
| `POST` | `/api/locations/:id/print-document/:printerId` | Print PDF document |
| `POST` | `/api/locations/:id/print-raw` | Quick print to IP:port |

### WebSocket Endpoint

| Path | Description |
|------|-------------|
| `/ws/connect` | EDPrintTool instances connect here |

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `RELAY_PORT` | `8190` | HTTP/WS listen port |
| `RELAY_ADMIN_KEY` | *(required)* | Admin API key |
| `RELAY_DATA_DIR` | `./data` | Directory for `locations.json` |
| `RELAY_JOB_TIMEOUT` | `30000` | Ms to wait for print result |

## Deployment

The relay is a standard Node.js server. Deploy anywhere:

- **VPS**: `npm start` behind nginx/Caddy for TLS
- **Railway/Render/Fly.io**: Set env vars, deploy from `relay/` directory
- **Docker**: `FROM node:20-alpine`, copy `relay/`, `npm install`, `npm start`

The relay should be behind TLS (HTTPS/WSS) in production. Use a reverse proxy like Caddy or nginx for automatic HTTPS.

## License

MIT
