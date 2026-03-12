const { app, BrowserWindow, Tray, Menu, nativeImage, dialog, shell } = require('electron');
const path = require('path');

// ─── Prevent multiple instances ──────────────────────────────
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
}

// ─── Globals ─────────────────────────────────────────────────
let mainWindow = null;
let tray = null;
let isQuitting = false;
let server = null;

const PORT = 8189;

// ─── Create tray icon programmatically (no .ico needed) ─────
function createTrayIcon() {
  // 16x16 icon: blue printer shape on transparent background
  const size = 16;
  const img = nativeImage.createEmpty();

  // Build a simple 16x16 RGBA buffer — blue printer silhouette
  const buf = Buffer.alloc(size * size * 4, 0);

  function setPixel(x, y, r, g, b, a = 255) {
    if (x < 0 || x >= size || y < 0 || y >= size) return;
    const i = (y * size + x) * 4;
    buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = a;
  }

  function fillRect(x1, y1, x2, y2, r, g, b) {
    for (let y = y1; y <= y2; y++)
      for (let x = x1; x <= x2; x++)
        setPixel(x, y, r, g, b);
  }

  // Paper (top)
  fillRect(4, 1, 11, 4, 230, 230, 230);
  // Printer body
  fillRect(2, 5, 13, 10, 79, 140, 255);
  // Output slot
  fillRect(4, 8, 11, 9, 50, 100, 220);
  // Output paper (bottom)
  fillRect(4, 11, 11, 14, 240, 240, 240);
  // Feed line
  fillRect(5, 12, 10, 12, 200, 200, 200);
  fillRect(6, 13, 9, 13, 200, 200, 200);

  return nativeImage.createFromBuffer(buf, { width: size, height: size });
}

// ─── Start the Express + WebSocket server ────────────────────
function startServer() {
  process.env.PORT = String(PORT);
  // Tell printer.js where to store config (writable path outside asar)
  process.env.EDPRINT_DATA_DIR = app.getPath('userData');
  const { start } = require('./server');
  return start();
}

// ─── Create the main window ─────────────────────────────────
function createWindow() {
  if (mainWindow) {
    mainWindow.show();
    mainWindow.focus();
    return;
  }

  mainWindow = new BrowserWindow({
    width: 1000,
    height: 750,
    minWidth: 800,
    minHeight: 600,
    title: 'EDPrintTool',
    icon: createTrayIcon(),
    backgroundColor: '#0f1117',
    show: false,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
    },
  });

  mainWindow.loadURL(`http://localhost:${PORT}`);

  // Remove the menu bar
  mainWindow.setMenuBarVisibility(false);

  mainWindow.once('ready-to-show', () => {
    mainWindow.show();
  });

  // Minimize to tray instead of closing
  mainWindow.on('close', (e) => {
    if (!isQuitting) {
      e.preventDefault();
      mainWindow.hide();
    }
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

// ─── Create system tray ──────────────────────────────────────
function createTray() {
  const icon = createTrayIcon();
  tray = new Tray(icon);
  tray.setToolTip('EDPrintTool — ZPL Label Printer');

  const contextMenu = Menu.buildFromTemplate([
    {
      label: 'Open EDPrintTool',
      click: () => createWindow(),
    },
    {
      label: 'Open in Browser',
      click: () => shell.openExternal(`http://localhost:${PORT}`),
    },
    { type: 'separator' },
    {
      label: 'Start with Windows',
      type: 'checkbox',
      checked: app.getLoginItemSettings().openAtLogin,
      click: (menuItem) => {
        app.setLoginItemSettings({
          openAtLogin: menuItem.checked,
          path: app.getPath('exe'),
        });
      },
    },
    { type: 'separator' },
    {
      label: `Server: localhost:${PORT}`,
      enabled: false,
    },
    { type: 'separator' },
    {
      label: 'Quit EDPrintTool',
      click: () => {
        isQuitting = true;
        app.quit();
      },
    },
  ]);

  tray.setContextMenu(contextMenu);

  // Double-click tray to open window
  tray.on('double-click', () => {
    createWindow();
  });
}

// ─── App lifecycle ───────────────────────────────────────────
app.whenReady().then(async () => {
  try {
    await startServer();
  } catch (err) {
    dialog.showErrorBox(
      'EDPrintTool - Server Error',
      `Failed to start the print server:\n\n${err.message}\n\nPort ${PORT} may already be in use.`
    );
    app.quit();
    return;
  }

  createTray();
  createWindow();
});

// On macOS: re-create window when dock icon clicked
app.on('activate', () => {
  createWindow();
});

// Handle second instance: show existing window
app.on('second-instance', () => {
  createWindow();
});

app.on('before-quit', () => {
  isQuitting = true;
});

// Keep running in tray when all windows closed
app.on('window-all-closed', () => {
  // Don't quit — stay in system tray
});
