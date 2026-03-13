# EDPrintTool

Free, open-source alternative to QZ Tray. Send raw ZPL and ESC/POS commands to thermal printers from any web application — no Java, no browser plugins.

EDPrintTool runs a lightweight local service (port 8189) that accepts print jobs via REST API or WebSocket from your web app and forwards raw bytes to Zebra label printers, receipt printers (Star, Epson, Bixolon, Citizen), and other thermal printers.

## Features

- **ZPL label printing** — Zebra printers with configurable label size, darkness, speed, orientation, and media type
- **ESC/POS receipt printing** — structured command API with text formatting, code pages, auto-cut
- **Network & USB** — print over TCP (port 9100) or through the OS spooler (Windows/macOS/Linux)
- **REST + WebSocket** — two ways to integrate, with a drop-in JavaScript client library
- **Web dashboard** — configure printers, adjust settings, test print from the browser
- **No drivers needed** — sends raw bytes directly, bypassing print drivers entirely
- **Printer profiles** — capability presets for Epson, Star, Bixolon, Citizen, and generic printers
- **12 label presets** — common Zebra label sizes from 4x8 down to 1x0.5 (jewelry)

## Quick Start

```bash
npm install
npm start
```

Open http://localhost:8189 to access the dashboard. Add a printer, configure its settings, and print a test page.

## Integration

### JavaScript Client (WebSocket)

Include the client library from the running service:

```html
<script src="http://localhost:8189/edprint.js"></script>
<script>
  const ep = new EDPrint();
  await ep.connect();

  // List configured printers
  const printers = await ep.listPrinters();

  // Print a ZPL label
  await ep.print('my-printer-id', '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ');

  // Print multiple copies
  await ep.print('my-printer-id', zpl, { copies: 3 });

  // Print without applying saved settings (raw passthrough)
  await ep.print('my-printer-id', zpl, { applySettings: false });

  // Quick print to IP without saving a printer
  await ep.printRaw('192.168.1.100', zpl);
</script>
```

### REST API

No client library required — just `fetch`:

```js
// Print ZPL
fetch('http://localhost:8189/api/print/my-printer-id', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    zpl: '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ',
    copies: 1
  })
});

// Print ESC/POS receipt
fetch('http://localhost:8189/api/print-escpos/my-printer-id', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    commands: [
      ["initialize"],
      ["align", "center"],
      ["bold", true],
      ["line", "MY STORE"],
      ["bold", false],
      ["rule"],
      ["align", "left"],
      ["line", "Item              Qty   Price"],
      ["line", "Widget A           2    $9.99"],
      ["cut", "partial", 4]
    ]
  })
});

// Quick print — send raw data to an IP:port without saving a printer
fetch('http://localhost:8189/api/print-raw', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    host: '192.168.1.100',
    port: 9100,
    zpl: '^XA^FO50,50^ADN,36,20^FDHello^FS^XZ'
  })
});
```

## API Reference

### REST Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/status` | Server status and printer count |
| `GET` | `/api/printers` | List configured printers |
| `GET` | `/api/printers/discover` | Discover system printers |
| `GET` | `/api/label-presets` | Available ZPL label sizes |
| `GET` | `/api/printer-profiles` | ESC/POS printer capability profiles |
| `GET` | `/api/printers/:id/debug` | Debug info + hex dump of test payload |
| `POST` | `/api/printers` | Add a printer |
| `PATCH` | `/api/printers/:id/settings` | Update printer settings |
| `DELETE` | `/api/printers/:id` | Remove a printer |
| `POST` | `/api/print/:id` | Print ZPL to a printer |
| `POST` | `/api/print-escpos/:id` | Print ESC/POS commands to a printer |
| `POST` | `/api/print-raw` | Quick print to IP:port (no saved printer) |

### WebSocket

Connect to `ws://localhost:8189` and send JSON messages:

```json
{ "action": "listPrinters", "requestId": "1" }
{ "action": "print", "requestId": "2", "printerId": "abc", "zpl": "^XA...^XZ" }
{ "action": "printRaw", "requestId": "3", "host": "192.168.1.100", "zpl": "^XA...^XZ" }
```

Responses include the `requestId` for correlation:

```json
{ "requestId": "1", "success": true, "data": [...] }
```

## Printer Settings

### ZPL (Label Printers)

| Setting | Default | Description |
|---------|---------|-------------|
| `labelPreset` | `4x6` | Label size preset |
| `widthDots` | `812` | Label width in dots |
| `heightDots` | `1218` | Label height in dots |
| `dpi` | `203` | Printer DPI (203 or 304) |
| `darkness` | `15` | Print darkness (0–30) |
| `speed` | `4` | Print speed in inches/sec (2–14) |
| `orientation` | `N` | N=normal, R=rotated, I=inverted, B=bottom-up |
| `mediaType` | `T` | T=thermal transfer, D=direct thermal |
| `printMode` | `T` | T=tear-off, P=peel-off, C=cutter |

### ESC/POS (Receipt Printers)

| Setting | Default | Description |
|---------|---------|-------------|
| `paperWidth` | `80mm` | Paper width (80mm or 58mm) |
| `autoCut` | `true` | Send cut command after print |
| `cutType` | `partial` | `partial` or `full` cut |
| `feedLines` | `4` | Lines to feed before cutting |
| `codepage` | _(default)_ | Code page: cp437, cp850, cp858, cp860, cp863, cp865, cp1252 |
| `printerProfile` | `generic` | Printer capability profile |

## ESC/POS Commands

The ESC/POS endpoint accepts an array of commands, each as `[method, ...args]`:

| Command | Args | Description |
|---------|------|-------------|
| `initialize` | — | Reset printer |
| `line` | `text` | Print text + line feed |
| `raw` | `text` | Print text without line feed |
| `bold` | `true/false` | Toggle bold |
| `underline` | `true/false` | Toggle underline |
| `align` | `left/center/right` | Set alignment |
| `font` | `A/B` | Select font |
| `size` | `width, height` | Text size multiplier (1–8) |
| `rule` | `[char]` | Print horizontal rule |
| `columns` | `[col1, col2, ...]` | Print columns across page width |
| `feed` | `[lines]` | Feed paper |
| `cut` | `[type, feedLines]` | Cut paper |
| `cashDrawer` | `[pin]` | Open cash drawer |
| `codepage` | `name` | Set code page |

## Windows Desktop App

A native Windows app (`src-native/`) wraps the same functionality in a WinForms desktop application with system tray integration. It uses .NET 8, `HttpListener` for the HTTP server, and P/Invoke to `winspool.drv` for direct USB printing.

Build with:

```bash
dotnet publish src-native/EDPrintTool/EDPrintTool.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## License

MIT
