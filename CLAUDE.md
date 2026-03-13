# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

EDPrintTool is a QZ Tray alternative for sending raw ZPL and ESC/POS commands to thermal printers (Zebra, Star TSP100, Epson, etc.) from web applications. It has two implementations:

- **Node.js server** (`src/`) — cross-platform standalone mode
- **Windows desktop app** (`src-native/EDPrintTool/`) — C# .NET 8 WinForms with embedded HTTP server

Both expose the same REST API + WebSocket interface on port 8189 and serve the same web dashboard from `public/`.

## Commands

```bash
npm install          # install dependencies
npm start            # run Node.js server on http://localhost:8189
npm run dev          # run with watch mode (--watch flag)
```

There are no tests, linter, or formatter configured.

The C# app builds via GitHub Actions (`.github/workflows/build-native.yml`) as a self-contained single-file Win-x64 exe.

## Architecture

### Dual Implementation Pattern

Every server feature exists in both Node.js and C#. When modifying behavior, both must stay in sync:

| Concern | Node.js | C# |
|---------|---------|-----|
| HTTP/WS server | `src/server.js` | `HttpServer.cs` |
| Printer config & ZPL dispatch | `src/printer.js` | `PrinterStore.cs` |
| ESC/POS encoder | `src/escpos.js` | `EscPosEncoder.cs` |
| Printer profiles | `src/printer-profiles.js` | (inline in `EscPosEncoder.cs`) |
| Raw printing (Win32) | PowerShell inline C# | `RawPrinter.cs` (P/Invoke winspool.drv) |
| Printer discovery | PowerShell/`lpstat` | `PrinterDiscovery.cs` |

### Print Flow

1. Client sends ZPL or ESC/POS commands via REST (`POST /api/print/:id` or `/api/print-escpos/:id`) or WebSocket
2. Server looks up printer config from `printers.json` (stored in OS-appropriate app data dir)
3. For ZPL: prepends setup commands (`^PW`, `^LL`, `^PR`, `~SD`, etc.) then sends raw bytes
4. For ESC/POS: processes structured command array through encoder, wraps with init/cut sequences
5. Sends to printer via TCP socket (network, port 9100) or OS print spooler (USB)

### ESC/POS Encoder

Chainable API that builds a byte buffer. Supports text formatting, code pages, paper cut (sends both Epson and standard cut commands for cross-printer compatibility), and cash drawer control.

### Printer Profiles

Lightweight capability declarations (`generic`, `epson`, `star`, `bixolon`, `citizen`, `custom`) that gate which ESC/POS commands are safe to send to a given printer model.

### Frontend

`public/index.html` — vanilla JS dark-themed dashboard. `public/edprint.js` — client library supporting both REST and WebSocket connections.

### Config Storage

- Windows: `%APPDATA%\EDPrintTool\printers.json`
- macOS: `~/Library/Application Support/EDPrintTool/printers.json`
- Linux: `~/.edprinttool/printers.json`

## Key Design Decisions

- **Dual cut commands**: Both Epson-specific (`ESC i`/`ESC m`) and standard (`GS V`) cut commands are sent for maximum printer compatibility
- **Minimal ESC/POS init**: Init sequence was stripped to bare minimum (`ESC @`) for Star TSP100 compatibility — avoid adding extra init bytes
- **Raw printing**: Bypasses OS print drivers entirely (no GDI) to send raw bytes to printers
- **Single-instance**: Windows app uses named Mutex to prevent multiple instances
