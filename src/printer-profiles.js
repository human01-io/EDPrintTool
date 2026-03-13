/**
 * Printer capability profiles — lightweight version of escpos-printer-db.
 *
 * Each profile declares what the printer supports so the encoder can
 * skip unsupported commands instead of sending bytes the printer ignores.
 *
 * Usage:
 *   const profile = getProfile('epson');
 *   if (profile.features.cut) encoder.cut();
 *   if (profile.features.cashDrawer) encoder.openCashDrawer();
 */

const PROFILES = {
  'generic': {
    name: 'Generic ESC/POS',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: false,       // not all printers support reverse video
      codepage: true,
    },
    defaultCodepage: 'cp437',
    cutCommand: 'gsv',     // GS V Function B
  },

  'epson': {
    name: 'Epson TM Series',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: true,
      codepage: true,
    },
    defaultCodepage: 'cp437',
    cutCommand: 'gsv',     // GS V 0x41/0x42 — native, full support
  },

  'star': {
    name: 'Star TSP Series (ESC/POS mode)',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: false,       // limited in ESC/POS emulation
      codepage: true,
    },
    defaultCodepage: 'cp437',
    cutCommand: 'gsv',     // GS V in emulation mode — works on TSP100/TSP143
  },

  'bixolon': {
    name: 'Bixolon SRP Series',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: true,
      codepage: true,
    },
    defaultCodepage: 'cp437',
    cutCommand: 'gsv',
  },

  'citizen': {
    name: 'Citizen CT Series',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: true,
      codepage: true,
    },
    defaultCodepage: 'cp437',
    cutCommand: 'gsv',
  },

  'custom': {
    name: 'Custom (user-defined)',
    columns: { '80mm': 48, '58mm': 32 },
    features: {
      cut: true,
      partialCut: true,
      cashDrawer: true,
      bold: true,
      underline: true,
      align: true,
      textSize: true,
      invert: true,
      codepage: true,
    },
    defaultCodepage: '',
    cutCommand: 'gsv',
  },
};

function getProfile(name) {
  return PROFILES[name] || PROFILES['generic'];
}

function getProfileList() {
  return Object.entries(PROFILES).map(([id, p]) => ({
    id,
    name: p.name,
  }));
}

module.exports = { PROFILES, getProfile, getProfileList };
