const net = require('net');
const { execFile } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// ─── Config persistence ─────────────────────────────────────
// When running inside Electron's asar, __dirname is read-only.
// Use APPDATA (Windows), HOME (mac/linux), or fallback to cwd.
function getConfigDir() {
  // Electron sets this env var before loading us
  if (process.env.EDPRINT_DATA_DIR) {
    const dir = process.env.EDPRINT_DATA_DIR;
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
    return dir;
  }
  // Standalone mode: use OS-appropriate writable directory
  const dir = process.env.APPDATA
    ? path.join(process.env.APPDATA, 'EDPrintTool')
    : (os.platform() === 'darwin'
      ? path.join(os.homedir(), 'Library', 'Application Support', 'EDPrintTool')
      : path.join(os.homedir(), '.edprinttool'));
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
  return dir;
}
const CONFIG_PATH = path.join(getConfigDir(), 'printers.json');
const printers = new Map();

function saveConfig() {
  const data = JSON.stringify(Array.from(printers.values()), null, 2);
  fs.writeFileSync(CONFIG_PATH, data, 'utf8');
}

function loadConfig() {
  try {
    if (fs.existsSync(CONFIG_PATH)) {
      const data = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
      for (const p of data) printers.set(p.id, p);
      console.log(`[Printers] Loaded ${printers.size} printer(s) from config`);
    }
  } catch (err) {
    console.error('[Printers] Failed to load config:', err.message);
  }
}

// Load on startup
loadConfig();

// ─── Windows raw printing via PowerShell (no native addons) ─

// ─── Common label presets (width x height in dots @ 203dpi) ──
// 203 dpi: 1 inch = 203 dots.  304 dpi: 1 inch = 304 dots.
const LABEL_PRESETS = {
  '4x8':    { widthDots: 812,  heightDots: 1624, desc: '4" x 8"' },
  '4x6':    { widthDots: 812,  heightDots: 1218, desc: '4" x 6" (shipping)' },
  '4x4':    { widthDots: 812,  heightDots: 812,  desc: '4" x 4"' },
  '4x3':    { widthDots: 812,  heightDots: 609,  desc: '4" x 3"' },
  '4x2':    { widthDots: 812,  heightDots: 406,  desc: '4" x 2"' },
  '4x1':    { widthDots: 812,  heightDots: 203,  desc: '4" x 1"' },
  '3x2':    { widthDots: 609,  heightDots: 406,  desc: '3" x 2"' },
  '3x1':    { widthDots: 609,  heightDots: 203,  desc: '3" x 1"' },
  '2.25x1.25': { widthDots: 457, heightDots: 254, desc: '2.25" x 1.25"' },
  '2x1':    { widthDots: 406,  heightDots: 203,  desc: '2" x 1"' },
  '1.5x1':  { widthDots: 305,  heightDots: 203,  desc: '1.5" x 1"' },
  '1x0.5':  { widthDots: 203,  heightDots: 102,  desc: '1" x 0.5" (jewelry)' },
};

function getLabelPresets() {
  return Object.entries(LABEL_PRESETS).map(([key, val]) => ({
    id: key,
    ...val,
  }));
}

// ─── Printer CRUD ────────────────────────────────────────────

const DEFAULT_SETTINGS = {
  language: 'ZPL',            // 'ZPL' or 'ESC/POS'
  labelPreset: '4x6',        // preset name or 'custom'
  widthDots: 812,             // label width in dots
  heightDots: 1218,           // label height in dots
  dpi: 203,                   // 203 or 300
  darkness: 15,               // 0-30 (ZPL ~SD command)
  speed: 4,                   // 2-14 inches/sec
  orientation: 'N',           // N=normal, R=rotated, I=inverted, B=bottom-up
  mediaType: 'T',             // T=thermal transfer, D=direct thermal
  printMode: 'T',             // T=tear-off, P=peel-off, C=cutter
  encoding: 'UTF-8',
  // ESC/POS settings
  paperWidth: '80mm',         // '80mm' or '58mm'
  autoCut: true,              // send cut command after print
  cutType: 'partial',         // 'full' or 'partial'
  feedLines: 4,               // lines to feed before cut
};

// ─── ESC/POS helpers ──────────────────────────────────────────
function buildEscPosInit() {
  // ESC @ — Initialize printer
  return Buffer.from([0x1B, 0x40]);
}

function buildEscPosCut(settings) {
  const bytes = [];
  // ESC d n — Print and feed n lines
  if (settings.feedLines > 0) {
    bytes.push(0x1B, 0x64, Math.min(settings.feedLines, 255));
  }
  // GS V — Cut paper
  if (settings.autoCut) {
    if (settings.cutType === 'full') {
      bytes.push(0x1D, 0x56, 0x00); // GS V 0 = full cut
    } else {
      bytes.push(0x1D, 0x56, 0x01); // GS V 1 = partial cut
    }
  }
  return Buffer.from(bytes);
}

function addPrinter({ id, name, type, host, port, cupsQueue, windowsPrinter, settings }) {
  const printer = {
    id: id || name.toLowerCase().replace(/[^a-z0-9]+/g, '-'),
    name,
    type, // 'network' | 'usb'
    host: host || null,
    port: port || 9100,
    cupsQueue: cupsQueue || null,
    windowsPrinter: windowsPrinter || null,
    settings: { ...DEFAULT_SETTINGS, ...(settings || {}) },
    addedAt: new Date().toISOString(),
  };

  // If preset selected, apply its dimensions
  if (printer.settings.labelPreset && LABEL_PRESETS[printer.settings.labelPreset]) {
    const preset = LABEL_PRESETS[printer.settings.labelPreset];
    printer.settings.widthDots = preset.widthDots;
    printer.settings.heightDots = preset.heightDots;
  }

  printers.set(printer.id, printer);
  saveConfig();
  return printer;
}

function updatePrinterSettings(id, settings) {
  const p = printers.get(id);
  if (!p) throw new Error(`Printer not found: ${id}`);

  p.settings = { ...p.settings, ...settings };

  // If preset changed, apply its dimensions
  if (settings.labelPreset && LABEL_PRESETS[settings.labelPreset]) {
    const preset = LABEL_PRESETS[settings.labelPreset];
    p.settings.widthDots = preset.widthDots;
    p.settings.heightDots = preset.heightDots;
  }

  saveConfig();
  return p;
}

function removePrinter(id) {
  const result = printers.delete(id);
  if (result) saveConfig();
  return result;
}

function getPrinters() {
  return Array.from(printers.values());
}

function getPrinter(id) {
  return printers.get(id) || null;
}

// ─── ZPL Setup Commands ──────────────────────────────────────

/**
 * Build a ZPL config prefix from printer settings.
 * This is prepended to every print job so the printer uses the right
 * label size, speed, darkness, etc.
 */
function buildSetupZPL(settings) {
  const s = { ...DEFAULT_SETTINGS, ...settings };
  const lines = ['^XA'];

  // Label width (^PW)
  lines.push(`^PW${s.widthDots}`);

  // Label length (^LL)
  lines.push(`^LL${s.heightDots}`);

  // Print speed (^PR<print>,<slew>,<backfeed>)
  lines.push(`^PR${s.speed},${s.speed},${s.speed}`);

  // Darkness (^MD = relative offset, ~SD = absolute 0-30)
  lines.push(`~SD${String(s.darkness).padStart(2, '0')}`);

  // Orientation (^FW)
  lines.push(`^FW${s.orientation}`);

  // Media type: ^MT for thermal transfer or direct thermal
  lines.push(`^MT${s.mediaType}`);

  // Print mode: ^MM for tear-off, peel, cutter
  lines.push(`^MM${s.printMode}`);

  lines.push('^XZ');
  return lines.join('\n');
}

// ─── Print methods ───────────────────────────────────────────

/**
 * Send raw ZPL to a network printer via TCP port 9100
 */
function printNetwork(host, port, zpl) {
  return new Promise((resolve, reject) => {
    const client = new net.Socket();
    let settled = false;

    const timer = setTimeout(() => {
      if (!settled) {
        settled = true;
        client.destroy();
        reject(new Error(`Connection to ${host}:${port} timed out after 10s`));
      }
    }, 10000);

    client.connect(port, host, () => {
      client.write(zpl, () => {
        clearTimeout(timer);
        client.end();
      });
    });

    client.on('close', () => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve({ success: true, method: 'network', host, port });
      }
    });

    client.on('error', (err) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        reject(new Error(`Network print failed (${host}:${port}): ${err.message}`));
      }
    });
  });
}

/**
 * Send raw ZPL to a USB printer via CUPS (macOS/Linux)
 */
function printCUPS(queue, zpl) {
  return new Promise((resolve, reject) => {
    const child = execFile('lp', ['-d', queue, '-o', 'raw'], (err, stdout) => {
      if (err) {
        reject(new Error(`CUPS print failed (${queue}): ${err.message}`));
        return;
      }
      resolve({ success: true, method: 'cups', queue, output: stdout.trim() });
    });
    child.stdin.write(zpl);
    child.stdin.end();
  });
}

/**
 * Send raw ZPL to a USB printer via Windows Print Spooler.
 * Uses PowerShell + inline C# to call Win32 winspool.drv directly:
 * OpenPrinter → StartDocPrinter(RAW) → WritePrinter → EndDocPrinter → ClosePrinter
 * This sends raw bytes that the Zebra firmware interprets as ZPL, bypassing GDI.
 */
function printWindows(printerName, zpl) {
  return new Promise((resolve, reject) => {
    const ts = Date.now();
    const tmpZpl = path.join(os.tmpdir(), 'edprint_' + ts + '.zpl');
    const tmpPs1 = path.join(os.tmpdir(), 'edprint_' + ts + '.ps1');
    fs.writeFileSync(tmpZpl, zpl, 'utf8');

    // PowerShell script that compiles C# inline to P/Invoke winspool.drv
    const ps1 = `param([string]$PrinterName, [string]$FilePath)

$code = @"
using System;
using System.Runtime.InteropServices;

public class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFOW
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartDocPrinter(IntPtr hPrinter, int Level, ref DOCINFOW pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    public static string SendRaw(string printerName, byte[] data)
    {
        IntPtr hPrinter;
        if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
            return "ERR:OpenPrinter failed, error " + Marshal.GetLastWin32Error();

        DOCINFOW di = new DOCINFOW();
        di.pDocName = "EDPrintTool RAW";
        di.pOutputFile = null;
        di.pDatatype = "RAW";

        if (StartDocPrinter(hPrinter, 1, ref di) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            ClosePrinter(hPrinter);
            return "ERR:StartDocPrinter failed, error " + err;
        }

        if (!StartPagePrinter(hPrinter))
        {
            int err = Marshal.GetLastWin32Error();
            EndDocPrinter(hPrinter);
            ClosePrinter(hPrinter);
            return "ERR:StartPagePrinter failed, error " + err;
        }

        IntPtr pBytes = Marshal.AllocCoTaskMem(data.Length);
        Marshal.Copy(data, 0, pBytes, data.Length);

        int written;
        bool ok = WritePrinter(hPrinter, pBytes, data.Length, out written);
        int writeErr = Marshal.GetLastWin32Error();

        Marshal.FreeCoTaskMem(pBytes);
        EndPagePrinter(hPrinter);
        EndDocPrinter(hPrinter);
        ClosePrinter(hPrinter);

        if (!ok)
            return "ERR:WritePrinter failed, error " + writeErr;

        return "OK:" + written + " bytes sent";
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp

$bytes = [System.IO.File]::ReadAllBytes($FilePath)
$result = [RawPrinter]::SendRaw($PrinterName, $bytes)
Write-Output $result

if ($result.StartsWith("ERR:")) { exit 1 }
`;

    fs.writeFileSync(tmpPs1, ps1, 'utf8');

    execFile('powershell', [
      '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
      '-File', tmpPs1, '-PrinterName', printerName, '-FilePath', tmpZpl,
    ], { timeout: 30000 }, (err, stdout, stderr) => {
      try { fs.unlinkSync(tmpZpl); } catch {}
      try { fs.unlinkSync(tmpPs1); } catch {}

      const output = (stdout || '').trim();
      const errors = (stderr || '').trim();

      if (err) {
        reject(new Error('Windows raw print failed (' + printerName + '): ' + (errors || output || err.message)));
        return;
      }

      if (output.startsWith('ERR:')) {
        reject(new Error('Windows raw print failed (' + printerName + '): ' + output));
        return;
      }

      resolve({ success: true, method: 'windows-raw', printer: printerName, detail: output });
    });
  });
}

// ─── Discover printers ───────────────────────────────────────

function discoverPrinters() {
  if (os.platform() === 'win32') {
    return discoverWindowsPrinters();
  }
  return discoverCUPSPrinters();
}

function discoverWindowsPrinters() {
  return new Promise((resolve) => {
    execFile(
      'powershell',
      ['-Command', 'Get-Printer | Select-Object Name,DriverName,PrinterStatus | ConvertTo-Json'],
      (err, stdout) => {
        if (err) { resolve([]); return; }
        try {
          let data = JSON.parse(stdout);
          if (!Array.isArray(data)) data = [data];
          resolve(data.map((p) => ({
            name: p.Name,
            driver: p.DriverName || '',
            status: p.PrinterStatus || '',
          })));
        } catch {
          resolve([]);
        }
      }
    );
  });
}

function discoverCUPSPrinters() {
  return new Promise((resolve) => {
    execFile('lpstat', ['-p', '-d'], (err, stdout) => {
      if (err) { resolve([]); return; }
      const lines = stdout.split('\n').filter(Boolean);
      const result = [];
      for (const line of lines) {
        const match = line.match(/^printer\s+(\S+)/);
        if (match) {
          result.push({
            name: match[1],
            status: line.includes('idle') ? 'idle' : line.includes('disabled') ? 'disabled' : 'unknown',
          });
        }
      }
      resolve(result);
    });
  });
}

// ─── Main print dispatcher ──────────────────────────────────

async function print(printerId, content, options = {}) {
  const p = printers.get(printerId);
  if (!p) throw new Error(`Printer not found: ${printerId}`);

  const applySettings = options.applySettings !== false;
  const copies = options.copies || 1;
  const isEscPos = p.settings && p.settings.language === 'ESC/POS';

  let payload;

  if (isEscPos) {
    // ESC/POS: build binary payload with init + content + feed + cut
    const parts = [];
    for (let i = 0; i < copies; i++) {
      if (applySettings) parts.push(buildEscPosInit());
      parts.push(Buffer.from(content, 'utf8'));
      if (applySettings) parts.push(buildEscPosCut(p.settings));
    }
    payload = Buffer.concat(parts).toString('binary');
  } else {
    // ZPL: text-based payload
    payload = '';
    if (applySettings && p.settings) {
      payload += buildSetupZPL(p.settings) + '\n';
    }
    for (let i = 0; i < copies; i++) {
      payload += content;
      if (i < copies - 1) payload += '\n';
    }
  }

  if (p.type === 'network') {
    if (!p.host) throw new Error(`No host configured for printer: ${printerId}`);
    return printNetwork(p.host, p.port, payload);
  }

  if (p.type === 'usb') {
    if (os.platform() === 'win32') {
      if (!p.windowsPrinter) throw new Error(`No Windows printer name configured for: ${printerId}`);
      return printWindows(p.windowsPrinter, payload);
    }
    if (!p.cupsQueue) throw new Error(`No CUPS queue configured for printer: ${printerId}`);
    return printCUPS(p.cupsQueue, payload);
  }

  throw new Error(`Unknown printer type: ${p.type}`);
}

module.exports = {
  addPrinter,
  updatePrinterSettings,
  removePrinter,
  getPrinters,
  getPrinter,
  print,
  printNetwork,
  printCUPS,
  printWindows,
  discoverPrinters,
  getLabelPresets,
  buildSetupZPL,
  DEFAULT_SETTINGS,
};
