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
      ["textSize", 1, 2],
      ["line", "MI TIENDA"],
      ["textSize", 1, 1],
      ["bold", false],
      ["line", "RFC: XAXX010101000"],
      ["rule", "="],
      ["align", "left"],
      ["columns", ["Widget A", "2", "$9.99"]],
      ["columns", ["Widget B", "1", "$4.50"]],
      ["rule"],
      ["pair", "Subtotal", "$14.49"],
      ["pair", "IVA 16%", "$2.32"],
      ["bold", true],
      ["pair", "TOTAL", "$16.81"],
      ["bold", false],
      ["cut", "partial", 4]
    ]
  })
});

// Print a PDF document (USB/spooler printers only)
const pdfBase64 = btoa(/* ... raw PDF bytes ... */); // or use FileReader
fetch('http://localhost:8189/api/print-document/my-printer-id', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ file: pdfBase64, copies: 1 })
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
| `POST` | `/api/print-document/:id` | Print PDF through OS spooler (USB only) |
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
| `paperWidth` | `80mm` | Paper width (80mm, 72mm, or 58mm) |
| `autoCut` | `true` | Send cut command after print |
| `cutType` | `partial` | `partial` or `full` cut |
| `feedLines` | `4` | Lines to feed before cutting |
| `codepage` | `cp1252` | Code page: cp437, cp850, cp858, cp860, cp863, cp865, cp1252 |
| `printerProfile` | `generic` | Printer capability profile |

## Character Encoding

ESC/POS printers use single-byte codepages, not UTF-8. EDPrintTool defaults to **cp1252** (Windows Latin 1) which supports all Western European and Spanish characters: á é í ó ú ñ ü ¡ ¿.

You can change the codepage per-printer in settings, or per-job with the `codepage` command. The encoder automatically converts text to the correct single-byte encoding.

| Codepage | ESC/POS ID | Coverage |
|----------|------------|----------|
| `cp1252` | 16 | **Default.** Western European, Spanish, Portuguese, French, German |
| `cp437` | 0 | US ASCII + box-drawing characters |
| `cp850` | 2 | Multilingual Latin I |
| `cp858` | 19 | Latin I + Euro sign (€) |
| `cp860` | 3 | Portuguese |
| `cp863` | 4 | Canadian French |
| `cp865` | 5 | Nordic |

## ESC/POS Commands

The ESC/POS endpoint accepts an array of commands, each as `[method, ...args]`:

| Command | Args | Description |
|---------|------|-------------|
| `initialize` | — | Reset printer |
| `codepage` | `name` | Set code page (e.g. `"cp1252"`) |
| `line` | `text` | Print text + line feed |
| `text` | `text` | Print text without line feed |
| `raw` | `text` | Print pre-formatted text |
| `newline` | — | Print empty line |
| `empty` | — | Print empty line (alias) |
| `bold` | `true/false` | Toggle bold |
| `underline` | `0/1/2` | Underline off / thin / thick |
| `align` | `left/center/right` | Set alignment |
| `font` | `0/1` | Select font (0=Font A 12x24, 1=Font B 9x17) |
| `textSize` | `width, height` | Text size multiplier (1–8) |
| `invert` | `true/false` | Reverse (white on black) |
| `rule` | `[char]` | Print horizontal rule (default `"-"`) |
| `columns` | `[col1, col2, ...]` | Print columns across page width |
| `pair` | `left, right, [fill]` | Key-value with dot fill (e.g. `Subtotal......$9.99`) |
| `feed` | `[lines]` | Feed paper |
| `cut` | `[type, feedLines]` | Cut paper (`"partial"` or `"full"`) |
| `openCashDrawer` | `[pin]` | Open cash drawer (0=pin 2, 1=pin 5) |
| `barcode` | `data, {options}` | Print 1D barcode (see below) |
| `qrcode` | `data, {options}` | Print QR code (see below) |
| `pdf417` | `data, {options}` | Print PDF417 barcode (see below) |

### Barcode Options

```json
["barcode", "12345678", { "type": "CODE128", "height": 80, "width": 2, "hri": "below" }]
```

| Option | Default | Description |
|--------|---------|-------------|
| `type` | `CODE128` | `UPC-A`, `UPC-E`, `EAN13`, `EAN8`, `CODE39`, `ITF`, `CODABAR`, `CODE93`, `CODE128` |
| `height` | `80` | Barcode height in dots (1–255) |
| `width` | `2` | Bar width multiplier (2–6) |
| `hri` | `below` | Human-readable text: `none`, `above`, `below`, `both` |

### QR Code Options

```json
["qrcode", "https://example.com", { "size": 6, "errorCorrection": "M" }]
```

| Option | Default | Description |
|--------|---------|-------------|
| `size` | `6` | Module size in dots (1–16) |
| `errorCorrection` | `M` | Error correction level: `L` (7%), `M` (15%), `Q` (25%), `H` (30%) |

### PDF417 Options

```json
["pdf417", "Invoice data here", { "columns": 0, "width": 3, "errorCorrection": 1 }]
```

| Option | Default | Description |
|--------|---------|-------------|
| `columns` | `0` | Number of columns (0=auto, 1–30) |
| `rows` | `0` | Number of rows (0=auto, 3–90) |
| `width` | `3` | Module width (2–8) |
| `height` | `3` | Row height (2–8) |
| `errorCorrection` | `1` | Error correction level (0–8) |

## Windows Desktop App

A native Windows app (`src-native/`) wraps the same functionality in a WinForms desktop application with system tray integration. It uses .NET 8, `HttpListener` for the HTTP server, and P/Invoke to `winspool.drv` for direct USB printing.

Build with:

```bash
dotnet publish src-native/EDPrintTool/EDPrintTool.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## License

MIT
