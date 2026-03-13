using EDPrintTool.Models;

namespace EDPrintTool;

public class MainForm : Form
{
    private readonly PrinterStore _store;
    private readonly HttpServer _server;

    // Colors matching the web dashboard
    private static readonly Color BgColor = Color.FromArgb(15, 17, 23);
    private static readonly Color SurfaceColor = Color.FromArgb(26, 29, 39);
    private static readonly Color BorderColor = Color.FromArgb(42, 45, 58);
    private static readonly Color AccentColor = Color.FromArgb(79, 140, 255);
    private static readonly Color TextColor = Color.FromArgb(228, 228, 231);
    private static readonly Color MutedColor = Color.FromArgb(113, 113, 122);
    private static readonly Color GreenColor = Color.FromArgb(52, 211, 153);
    private static readonly Color RedColor = Color.FromArgb(248, 113, 113);

    // Controls
    private readonly TabControl _tabs;
    private readonly TextBox _nameInput;
    private readonly TextBox _winPrinterInput;
    private readonly TextBox _netNameInput;
    private readonly TextBox _netHostInput;
    private readonly NumericUpDown _netPortInput;
    private readonly ListView _printerList;
    private readonly ComboBox _printTarget;
    private readonly NumericUpDown _copiesInput;
    private readonly TextBox _zplInput;
    private readonly TextBox _qpHost;
    private readonly NumericUpDown _qpPort;
    private readonly TextBox _qpZpl;
    private readonly ListBox _logBox;

    // Settings panel controls
    private readonly GroupBox _settingsGroup;
    private readonly ComboBox _setPreset;
    private readonly NumericUpDown _setWidth;
    private readonly NumericUpDown _setHeight;
    private readonly ComboBox _setDpi;
    private readonly TrackBar _setDarkness;
    private readonly Label _darknessValue;
    private readonly NumericUpDown _setSpeed;
    private readonly ComboBox _setMediaType;
    private readonly ComboBox _setPrintMode;
    private readonly ComboBox _setOrientation;
    private string? _selectedPrinterId;

    public MainForm(PrinterStore store, HttpServer server)
    {
        _store = store;
        _server = server;
        _server.OnActivity += (msg, isErr) => LogMessage(msg, isErr);

        Text = "EDPrintTool";
        Size = new Size(1000, 750);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgColor;
        ForeColor = TextColor;

        var mainPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) };
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Width = 940,
        };

        // ─── Header ──────────────────────────────────
        var header = new Label
        {
            Text = "EDPrintTool — Native",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = AccentColor,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
        };
        layout.Controls.Add(header);

        // ─── Add Printer ─────────────────────────────
        var addGroup = CreateGroupBox("ADD PRINTER", 280);

        _tabs = new TabControl { Left = 10, Top = 25, Width = 900, Height = 240 };
        _tabs.BackColor = SurfaceColor;

        // USB tab
        var usbTab = new TabPage("USB (Windows)") { BackColor = SurfaceColor, ForeColor = TextColor };
        _nameInput = AddLabeledTextBox(usbTab, "Friendly Name:", 10, 15, 400);
        _winPrinterInput = AddLabeledTextBox(usbTab, "Windows Printer Name:", 10, 60, 400);

        var addUsbBtn = CreateButton("Add Printer", 10, 110);
        addUsbBtn.Click += async (_, _) => await AddUsbPrinter();
        usbTab.Controls.Add(addUsbBtn);

        var discoverBtn = CreateButton("Discover Printers", 140, 110);
        discoverBtn.BackColor = BorderColor;
        discoverBtn.Click += async (_, _) => await DiscoverPrinters();
        usbTab.Controls.Add(discoverBtn);

        _tabs.TabPages.Add(usbTab);

        // Network tab
        var netTab = new TabPage("Network (TCP)") { BackColor = SurfaceColor, ForeColor = TextColor };
        _netNameInput = AddLabeledTextBox(netTab, "Printer Name:", 10, 15, 300);
        _netHostInput = AddLabeledTextBox(netTab, "IP Address:", 10, 60, 250);

        var portLabel = new Label { Text = "Port:", Left = 280, Top = 63, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };
        _netPortInput = new NumericUpDown { Left = 280, Top = 80, Width = 80, Minimum = 1, Maximum = 65535, Value = 9100, BackColor = BgColor, ForeColor = TextColor };
        netTab.Controls.AddRange(new Control[] { portLabel, _netPortInput });

        var addNetBtn = CreateButton("Add Printer", 10, 110);
        addNetBtn.Click += async (_, _) => await AddNetworkPrinter();
        netTab.Controls.Add(addNetBtn);

        _tabs.TabPages.Add(netTab);
        addGroup.Controls.Add(_tabs);
        layout.Controls.Add(addGroup);

        // ─── Printer List ────────────────────────────
        var listGroup = CreateGroupBox("CONFIGURED PRINTERS", 210);
        _printerList = new ListView
        {
            Left = 10, Top = 25, Width = 900, Height = 130,
            View = View.Details, FullRowSelect = true,
            BackColor = BgColor, ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9),
        };
        _printerList.Columns.Add("Name", 200);
        _printerList.Columns.Add("Type", 80);
        _printerList.Columns.Add("Connection", 250);
        _printerList.Columns.Add("Label Size", 120);
        _printerList.Columns.Add("Darkness", 70);
        _printerList.Columns.Add("Speed", 60);

        var settingsBtn = CreateButton("Settings", 10, 165);
        settingsBtn.Width = 100;
        settingsBtn.BackColor = BorderColor;
        settingsBtn.Click += (_, _) => ToggleSettings();

        var removeBtn = CreateButton("Remove Selected", 120, 165);
        removeBtn.Width = 130;
        removeBtn.BackColor = Color.FromArgb(60, 30, 30);
        removeBtn.ForeColor = RedColor;
        removeBtn.Click += (_, _) => RemoveSelected();

        listGroup.Controls.Add(_printerList);
        listGroup.Controls.Add(settingsBtn);
        listGroup.Controls.Add(removeBtn);
        layout.Controls.Add(listGroup);

        // ─── Settings Panel ─────────────────────────
        _settingsGroup = CreateGroupBox("PRINTER SETTINGS", 220);
        _settingsGroup.Visible = false;

        var normalFont = new Font("Segoe UI", 9, FontStyle.Regular);

        // Row 1: Preset, Width, Height, DPI
        var presetLbl = new Label { Text = "Label Preset:", Left = 10, Top = 25, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setPreset = new ComboBox { Left = 10, Top = 43, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat, Font = normalFont };
        foreach (var p in LabelPreset.GetAll())
            _setPreset.Items.Add(new PresetItem(p.Id, $"{p.Desc} ({p.Id})"));
        _setPreset.Items.Add(new PresetItem("custom", "Custom"));
        _setPreset.SelectedIndexChanged += (_, _) => OnPresetChanged();

        var widthLbl = new Label { Text = "Width (dots):", Left = 250, Top = 25, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setWidth = new NumericUpDown { Left = 250, Top = 43, Width = 100, Minimum = 50, Maximum = 5000, BackColor = BgColor, ForeColor = TextColor, Enabled = false };

        var heightLbl = new Label { Text = "Height (dots):", Left = 370, Top = 25, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setHeight = new NumericUpDown { Left = 370, Top = 43, Width = 100, Minimum = 50, Maximum = 5000, BackColor = BgColor, ForeColor = TextColor, Enabled = false };

        var dpiLbl = new Label { Text = "DPI:", Left = 490, Top = 25, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setDpi = new ComboBox { Left = 490, Top = 43, Width = 70, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat, Font = normalFont };
        _setDpi.Items.AddRange(new object[] { "203", "300" });

        // Row 2: Darkness, Speed, Media Type, Print Mode, Orientation
        var darkLbl = new Label { Text = "Darkness (0-30):", Left = 10, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setDarkness = new TrackBar { Left = 10, Top = 96, Width = 200, Minimum = 0, Maximum = 30, TickFrequency = 5, BackColor = SurfaceColor };
        _darknessValue = new Label { Text = "15", Left = 215, Top = 100, ForeColor = TextColor, AutoSize = true, Font = normalFont };
        _setDarkness.ValueChanged += (_, _) => _darknessValue.Text = _setDarkness.Value.ToString();

        var speedLbl = new Label { Text = "Speed (in/sec):", Left = 260, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setSpeed = new NumericUpDown { Left = 260, Top = 96, Width = 70, Minimum = 2, Maximum = 14, BackColor = BgColor, ForeColor = TextColor };

        var mediaLbl = new Label { Text = "Media Type:", Left = 350, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setMediaType = new ComboBox { Left = 350, Top = 96, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat, Font = normalFont };
        _setMediaType.Items.AddRange(new object[] { "Direct Thermal (D)", "Thermal Transfer (T)" });

        var modeLbl = new Label { Text = "Print Mode:", Left = 510, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setPrintMode = new ComboBox { Left = 510, Top = 96, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat, Font = normalFont };
        _setPrintMode.Items.AddRange(new object[] { "Tear-Off (T)", "Peel-Off (P)", "Cutter (C)" });

        var orientLbl = new Label { Text = "Orientation:", Left = 650, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = normalFont };
        _setOrientation = new ComboBox { Left = 650, Top = 96, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat, Font = normalFont };
        _setOrientation.Items.AddRange(new object[] { "Normal (N)", "Rotated 90 (R)", "Inverted 180 (I)", "Bottom-Up 270 (B)" });

        var saveSettingsBtn = CreateButton("Save Settings", 10, 140);
        saveSettingsBtn.Width = 120;
        saveSettingsBtn.Click += (_, _) => SaveSettings();

        var settingsPrinterLabel = new Label { Text = "", Left = 140, Top = 148, ForeColor = AccentColor, AutoSize = true, Font = normalFont, Name = "settingsPrinterLabel" };

        _settingsGroup.Controls.AddRange(new Control[]
        {
            presetLbl, _setPreset, widthLbl, _setWidth, heightLbl, _setHeight, dpiLbl, _setDpi,
            darkLbl, _setDarkness, _darknessValue, speedLbl, _setSpeed,
            mediaLbl, _setMediaType, modeLbl, _setPrintMode, orientLbl, _setOrientation,
            saveSettingsBtn, settingsPrinterLabel,
        });
        layout.Controls.Add(_settingsGroup);

        // ─── Print ZPL ───────────────────────────────
        var printGroup = CreateGroupBox("PRINT ZPL", 260);

        var targetLabel = new Label { Text = "Target Printer:", Left = 10, Top = 28, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };
        _printTarget = new ComboBox
        {
            Left = 10, Top = 46, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat,
        };

        var copiesLabel = new Label { Text = "Copies:", Left = 10, Top = 78, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };
        _copiesInput = new NumericUpDown
        {
            Left = 10, Top = 96, Width = 80, Minimum = 1, Maximum = 100, Value = 1,
            BackColor = BgColor, ForeColor = TextColor,
        };

        var zplLabel = new Label { Text = "ZPL Code:", Left = 280, Top = 28, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };
        _zplInput = new TextBox
        {
            Left = 280, Top = 46, Width = 630, Height = 130, Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 10),
            BackColor = BgColor, ForeColor = TextColor,
        };

        var printBtn = CreateButton("Print Label", 10, 220);
        printBtn.Click += async (_, _) => await PrintLabel();

        var sampleBtn = CreateButton("Load Sample ZPL", 140, 220);
        sampleBtn.BackColor = BorderColor;
        sampleBtn.Click += (_, _) => LoadSampleZpl();

        printGroup.Controls.AddRange(new Control[] { targetLabel, _printTarget, copiesLabel, _copiesInput, zplLabel, _zplInput, printBtn, sampleBtn });
        layout.Controls.Add(printGroup);

        // ─── Quick Print ─────────────────────────────
        var quickGroup = CreateGroupBox("QUICK PRINT (DIRECT IP)", 140);
        _qpHost = AddLabeledTextBox(quickGroup, "IP Address:", 10, 20, 200);
        var qpPortLabel = new Label { Text = "Port:", Left = 240, Top = 23, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };
        _qpPort = new NumericUpDown { Left = 240, Top = 40, Width = 80, Minimum = 1, Maximum = 65535, Value = 9100, BackColor = BgColor, ForeColor = TextColor };
        _qpZpl = new TextBox { Left = 340, Top = 40, Width = 570, Height = 50, Multiline = true, Font = new Font("Consolas", 9), BackColor = BgColor, ForeColor = TextColor };
        var qpLabel = new Label { Text = "ZPL:", Left = 340, Top = 23, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9) };

        var qpBtn = CreateButton("Send to Printer", 10, 100);
        qpBtn.Click += async (_, _) => await QuickPrint();

        quickGroup.Controls.AddRange(new Control[] { qpPortLabel, _qpPort, _qpZpl, qpLabel, qpBtn });
        layout.Controls.Add(quickGroup);

        // ─── Activity Log ────────────────────────────
        var logGroup = CreateGroupBox("ACTIVITY LOG", 180);
        _logBox = new ListBox
        {
            Left = 10, Top = 25, Width = 900, Height = 140,
            BackColor = BgColor, ForeColor = MutedColor,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9),
        };
        logGroup.Controls.Add(_logBox);
        layout.Controls.Add(logGroup);

        mainPanel.Controls.Add(layout);
        Controls.Add(mainPanel);

        // Initial load
        RefreshPrinterList();
        LogMessage("EDPrintTool started — server on http://localhost:8189", false);
    }

    // ─── Actions ────────────────────────────────────────────────

    private async Task AddUsbPrinter()
    {
        var name = _nameInput.Text.Trim();
        var winName = _winPrinterInput.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(winName))
        {
            LogMessage("Name and Windows printer name are required", true);
            return;
        }
        _store.AddPrinter(new PrinterInfo { Name = name, Type = "usb", WindowsPrinter = winName });
        _nameInput.Clear();
        _winPrinterInput.Clear();
        RefreshPrinterList();
        LogMessage($"Printer added: {name}", false);
    }

    private async Task AddNetworkPrinter()
    {
        var name = _netNameInput.Text.Trim();
        var host = _netHostInput.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(host))
        {
            LogMessage("Name and IP address are required", true);
            return;
        }
        _store.AddPrinter(new PrinterInfo { Name = name, Type = "network", Host = host, Port = (int)_netPortInput.Value });
        _netNameInput.Clear();
        _netHostInput.Clear();
        RefreshPrinterList();
        LogMessage($"Network printer added: {name} ({host})", false);
    }

    private async Task DiscoverPrinters()
    {
        LogMessage("Discovering printers...", false);
        var list = await PrinterDiscovery.DiscoverAsync();
        if (list.Count == 0)
        {
            LogMessage("No printers found", true);
            return;
        }
        LogMessage($"Found {list.Count} printer(s)", false);
        foreach (var p in list)
        {
            var name = p["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            // Auto-add discovered printers
            if (_store.GetPrinter(name.ToLowerInvariant().Replace(' ', '-')) == null)
            {
                _store.AddPrinter(new PrinterInfo { Name = name, Type = "usb", WindowsPrinter = name });
                LogMessage($"Auto-added: {name}", false);
            }
        }
        RefreshPrinterList();
    }

    private void RemoveSelected()
    {
        if (_printerList.SelectedItems.Count == 0) return;
        var id = _printerList.SelectedItems[0].Tag as string;
        if (id != null)
        {
            _store.RemovePrinter(id);
            RefreshPrinterList();
            LogMessage($"Printer removed: {id}", false);
        }
    }

    private async Task PrintLabel()
    {
        if (_printTarget.SelectedItem is not ComboItem item)
        {
            LogMessage("Select a printer first", true);
            return;
        }
        var zpl = _zplInput.Text.Trim();
        if (string.IsNullOrEmpty(zpl))
        {
            LogMessage("Enter ZPL code", true);
            return;
        }
        var printer = _store.GetPrinter(item.Id);
        if (printer == null)
        {
            LogMessage($"Printer not found: {item.Id}", true);
            return;
        }
        try
        {
            var copies = (int)_copiesInput.Value;
            var msg = await RawPrinter.PrintAsync(printer, zpl, copies);
            LogMessage($"Print OK ({copies}x) → {item.Id}: {msg}", false);
        }
        catch (Exception ex)
        {
            LogMessage($"Print FAILED: {ex.Message}", true);
        }
    }

    private async Task QuickPrint()
    {
        var host = _qpHost.Text.Trim();
        var zpl = _qpZpl.Text.Trim();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(zpl))
        {
            LogMessage("IP and ZPL are required", true);
            return;
        }
        try
        {
            var msg = await RawPrinter.PrintNetworkAsync(host, (int)_qpPort.Value, zpl);
            LogMessage($"Quick print sent to {host}: {msg}", false);
        }
        catch (Exception ex)
        {
            LogMessage($"Quick print FAILED: {ex.Message}", true);
        }
    }

    private void LoadSampleZpl()
    {
        _zplInput.Text = @"^XA

^FX --- Top section with company info ---
^CF0,40
^FO50,50^FDShipping Label^FS
^CF0,25
^FO50,100^FDEDPrintTool Demo^FS
^FO50,130^FD123 Main Street^FS
^FO50,160^FDNew York, NY 10001^FS

^FX --- Bar Code ---
^BY3,2,120
^FO50,220^BC^FD1234567890^FS

^FX --- Bottom section ---
^CF0,30
^FO50,380^FDShip To:^FS
^CF0,25
^FO50,420^FDJohn Doe^FS
^FO50,450^FD456 Oak Avenue^FS
^FO50,480^FDLos Angeles, CA 90001^FS

^FX --- QR Code ---
^FO400,380^BQN,2,5^FDMA,https://example.com^FS

^XZ";
        LogMessage("Sample ZPL loaded", false);
    }

    // ─── Settings Panel ─────────────────────────────────────────

    private void ToggleSettings()
    {
        if (_printerList.SelectedItems.Count == 0)
        {
            LogMessage("Select a printer first to edit settings", true);
            return;
        }

        var id = _printerList.SelectedItems[0].Tag as string;
        if (id == null) return;

        // If already showing this printer's settings, hide
        if (_settingsGroup.Visible && _selectedPrinterId == id)
        {
            _settingsGroup.Visible = false;
            _selectedPrinterId = null;
            return;
        }

        _selectedPrinterId = id;
        var printer = _store.GetPrinter(id);
        if (printer == null) return;

        LoadSettingsIntoPanel(printer);
        _settingsGroup.Visible = true;
    }

    private void LoadSettingsIntoPanel(PrinterInfo printer)
    {
        var s = printer.Settings;

        // Label preset
        for (int i = 0; i < _setPreset.Items.Count; i++)
        {
            if (_setPreset.Items[i] is PresetItem pi && pi.Id == s.LabelPreset)
            { _setPreset.SelectedIndex = i; break; }
        }

        _setWidth.Value = Math.Clamp(s.WidthDots, 50, 5000);
        _setHeight.Value = Math.Clamp(s.HeightDots, 50, 5000);
        _setDpi.SelectedItem = s.Dpi.ToString();
        _setDarkness.Value = Math.Clamp(s.Darkness, 0, 30);
        _darknessValue.Text = s.Darkness.ToString();
        _setSpeed.Value = Math.Clamp(s.Speed, 2, 14);

        // Media type: D or T
        _setMediaType.SelectedIndex = s.MediaType == "D" ? 0 : 1;

        // Print mode: T, P, C
        _setPrintMode.SelectedIndex = s.PrintMode switch { "P" => 1, "C" => 2, _ => 0 };

        // Orientation: N, R, I, B
        _setOrientation.SelectedIndex = s.Orientation switch { "R" => 1, "I" => 2, "B" => 3, _ => 0 };

        // Show which printer is being configured
        var label = _settingsGroup.Controls.Find("settingsPrinterLabel", false).FirstOrDefault();
        if (label != null) label.Text = $"Editing: {printer.Name}";
    }

    private void OnPresetChanged()
    {
        if (_setPreset.SelectedItem is PresetItem pi)
        {
            bool isCustom = pi.Id == "custom";
            _setWidth.Enabled = isCustom;
            _setHeight.Enabled = isCustom;

            if (!isCustom)
            {
                var preset = Models.LabelPreset.Get(pi.Id);
                if (preset != null)
                {
                    _setWidth.Value = preset.WidthDots;
                    _setHeight.Value = preset.HeightDots;
                }
            }
        }
    }

    private void SaveSettings()
    {
        if (_selectedPrinterId == null) return;

        var presetId = (_setPreset.SelectedItem as PresetItem)?.Id ?? "4x6";
        var mediaCode = _setMediaType.SelectedIndex == 0 ? "D" : "T";
        var modeCode = _setPrintMode.SelectedIndex switch { 1 => "P", 2 => "C", _ => "T" };
        var orientCode = _setOrientation.SelectedIndex switch { 1 => "R", 2 => "I", 3 => "B", _ => "N" };

        var partial = new System.Text.Json.Nodes.JsonObject
        {
            ["labelPreset"] = presetId,
            ["widthDots"] = (int)_setWidth.Value,
            ["heightDots"] = (int)_setHeight.Value,
            ["dpi"] = int.Parse(_setDpi.SelectedItem?.ToString() ?? "203"),
            ["darkness"] = _setDarkness.Value,
            ["speed"] = (int)_setSpeed.Value,
            ["mediaType"] = mediaCode,
            ["printMode"] = modeCode,
            ["orientation"] = orientCode,
        };

        var updated = _store.UpdateSettings(_selectedPrinterId, partial);
        if (updated != null)
        {
            RefreshPrinterList();
            LogMessage($"Settings saved for {_selectedPrinterId}", false);
        }
        else
        {
            LogMessage($"Printer not found: {_selectedPrinterId}", true);
        }
    }

    private record PresetItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // ─── UI Helpers ─────────────────────────────────────────────

    private void RefreshPrinterList()
    {
        _printerList.Items.Clear();
        _printTarget.Items.Clear();

        foreach (var p in _store.GetPrinters())
        {
            var s = p.Settings;
            var conn = p.Type == "network" ? $"{p.Host}:{p.Port}" : (p.WindowsPrinter ?? p.CupsQueue ?? "");
            var preset = LabelPreset.Get(s.LabelPreset);
            var sizeStr = preset?.Desc ?? $"{s.WidthDots}x{s.HeightDots}";

            var item = new ListViewItem(p.Name) { Tag = p.Id, ForeColor = TextColor };
            item.SubItems.Add(p.Type);
            item.SubItems.Add(conn);
            item.SubItems.Add(sizeStr);
            item.SubItems.Add(s.Darkness.ToString());
            item.SubItems.Add(s.Speed.ToString());
            _printerList.Items.Add(item);

            _printTarget.Items.Add(new ComboItem(p.Id, p.Name));
        }

        if (_printTarget.Items.Count > 0)
            _printTarget.SelectedIndex = 0;
    }

    private void LogMessage(string message, bool isError)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => LogMessage(message, isError));
            return;
        }
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logBox.Items.Insert(0, $"[{ts}] {message}");
        if (_logBox.Items.Count > 200)
            _logBox.Items.RemoveAt(_logBox.Items.Count - 1);
    }

    private GroupBox CreateGroupBox(string title, int height)
    {
        return new GroupBox
        {
            Text = title,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Width = 920,
            Height = height,
            Margin = new Padding(0, 0, 0, 12),
        };
    }

    private static TextBox AddLabeledTextBox(Control parent, string label, int x, int y, int width)
    {
        var lbl = new Label { Text = label, Left = x, Top = y, ForeColor = MutedColor, AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Regular) };
        var txt = new TextBox { Left = x, Top = y + 18, Width = width, BackColor = BgColor, ForeColor = TextColor, Font = new Font("Segoe UI", 10) };
        parent.Controls.Add(lbl);
        parent.Controls.Add(txt);
        return txt;
    }

    private static Button CreateButton(string text, int x, int y)
    {
        return new Button
        {
            Text = text,
            Left = x, Top = y,
            Width = 120, Height = 32,
            BackColor = AccentColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Hand,
        };
    }

    private record ComboItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
