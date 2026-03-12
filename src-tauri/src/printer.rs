use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;
use std::sync::Mutex;
use tokio::io::AsyncWriteExt;
use tokio::net::TcpStream;
use tokio::time::{timeout, Duration};

// ─── Label Presets ───────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LabelPreset {
    pub id: String,
    pub desc: String,
    pub width_dots: u32,
    pub height_dots: u32,
}

pub fn label_presets() -> Vec<LabelPreset> {
    vec![
        LabelPreset { id: "4x8".into(), desc: "4\" x 8\"".into(), width_dots: 812, height_dots: 1624 },
        LabelPreset { id: "4x6".into(), desc: "4\" x 6\" (shipping)".into(), width_dots: 812, height_dots: 1218 },
        LabelPreset { id: "4x4".into(), desc: "4\" x 4\"".into(), width_dots: 812, height_dots: 812 },
        LabelPreset { id: "4x3".into(), desc: "4\" x 3\"".into(), width_dots: 812, height_dots: 609 },
        LabelPreset { id: "4x2".into(), desc: "4\" x 2\"".into(), width_dots: 812, height_dots: 406 },
        LabelPreset { id: "4x1".into(), desc: "4\" x 1\"".into(), width_dots: 812, height_dots: 203 },
        LabelPreset { id: "3x2".into(), desc: "3\" x 2\"".into(), width_dots: 609, height_dots: 406 },
        LabelPreset { id: "3x1".into(), desc: "3\" x 1\"".into(), width_dots: 609, height_dots: 203 },
        LabelPreset { id: "2.25x1.25".into(), desc: "2.25\" x 1.25\"".into(), width_dots: 457, height_dots: 254 },
        LabelPreset { id: "2x1".into(), desc: "2\" x 1\"".into(), width_dots: 406, height_dots: 203 },
        LabelPreset { id: "1.5x1".into(), desc: "1.5\" x 1\"".into(), width_dots: 305, height_dots: 203 },
        LabelPreset { id: "1x0.5".into(), desc: "1\" x 0.5\" (jewelry)".into(), width_dots: 203, height_dots: 102 },
    ]
}

// ─── Printer Settings ────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrinterSettings {
    #[serde(default = "default_preset")]
    pub label_preset: String,
    #[serde(default = "default_width")]
    pub width_dots: u32,
    #[serde(default = "default_height")]
    pub height_dots: u32,
    #[serde(default = "default_dpi")]
    pub dpi: u32,
    #[serde(default = "default_darkness")]
    pub darkness: u32,
    #[serde(default = "default_speed")]
    pub speed: u32,
    #[serde(default = "default_orientation")]
    pub orientation: String,
    #[serde(default = "default_media_type")]
    pub media_type: String,
    #[serde(default = "default_print_mode")]
    pub print_mode: String,
}

fn default_preset() -> String { "4x6".into() }
fn default_width() -> u32 { 812 }
fn default_height() -> u32 { 1218 }
fn default_dpi() -> u32 { 203 }
fn default_darkness() -> u32 { 15 }
fn default_speed() -> u32 { 4 }
fn default_orientation() -> String { "N".into() }
fn default_media_type() -> String { "T".into() }
fn default_print_mode() -> String { "T".into() }

impl Default for PrinterSettings {
    fn default() -> Self {
        Self {
            label_preset: default_preset(),
            width_dots: default_width(),
            height_dots: default_height(),
            dpi: default_dpi(),
            darkness: default_darkness(),
            speed: default_speed(),
            orientation: default_orientation(),
            media_type: default_media_type(),
            print_mode: default_print_mode(),
        }
    }
}

impl PrinterSettings {
    pub fn build_setup_zpl(&self) -> String {
        format!(
            "^XA\n^PW{}\n^LL{}\n^PR{},{},{}\n~SD{:02}\n^FW{}\n^MT{}\n^MM{}\n^XZ",
            self.width_dots,
            self.height_dots,
            self.speed, self.speed, self.speed,
            self.darkness,
            self.orientation,
            self.media_type,
            self.print_mode,
        )
    }

    pub fn apply_preset(&mut self) {
        if let Some(preset) = label_presets().iter().find(|p| p.id == self.label_preset) {
            self.width_dots = preset.width_dots;
            self.height_dots = preset.height_dots;
        }
    }
}

// ─── Printer Config ──────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrinterConfig {
    pub id: String,
    pub name: String,
    #[serde(rename = "type")]
    pub printer_type: String, // "network" | "usb"
    #[serde(default)]
    pub host: Option<String>,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default)]
    pub cups_queue: Option<String>,
    #[serde(default)]
    pub windows_printer: Option<String>,
    #[serde(default)]
    pub settings: PrinterSettings,
    #[serde(default)]
    pub added_at: String,
}

fn default_port() -> u16 { 9100 }

// ─── Printer Store (thread-safe) ─────────────────────────────

pub struct PrinterStore {
    printers: Mutex<Vec<PrinterConfig>>,
    config_path: PathBuf,
}

impl PrinterStore {
    pub fn new(config_path: PathBuf) -> Self {
        let store = Self {
            printers: Mutex::new(Vec::new()),
            config_path,
        };
        store.load();
        store
    }

    fn load(&self) {
        if self.config_path.exists() {
            if let Ok(data) = fs::read_to_string(&self.config_path) {
                if let Ok(list) = serde_json::from_str::<Vec<PrinterConfig>>(&data) {
                    let mut printers = self.printers.lock().unwrap();
                    *printers = list;
                    println!("[Printers] Loaded {} printer(s)", printers.len());
                }
            }
        }
    }

    fn save(&self) {
        let printers = self.printers.lock().unwrap();
        if let Ok(data) = serde_json::to_string_pretty(&*printers) {
            let _ = fs::write(&self.config_path, data);
        }
    }

    pub fn list(&self) -> Vec<PrinterConfig> {
        self.printers.lock().unwrap().clone()
    }

    pub fn get(&self, id: &str) -> Option<PrinterConfig> {
        self.printers.lock().unwrap().iter().find(|p| p.id == id).cloned()
    }

    pub fn add(&self, mut cfg: PrinterConfig) -> PrinterConfig {
        if cfg.id.is_empty() {
            cfg.id = cfg.name.to_lowercase()
                .chars().map(|c| if c.is_alphanumeric() { c } else { '-' }).collect();
        }
        if cfg.added_at.is_empty() {
            cfg.added_at = chrono_now();
        }
        cfg.settings.apply_preset();
        let mut printers = self.printers.lock().unwrap();
        // Remove existing with same id
        printers.retain(|p| p.id != cfg.id);
        printers.push(cfg.clone());
        drop(printers);
        self.save();
        cfg
    }

    pub fn update_settings(&self, id: &str, new_settings: serde_json::Value) -> Result<PrinterConfig, String> {
        let mut printers = self.printers.lock().unwrap();
        let printer = printers.iter_mut().find(|p| p.id == id)
            .ok_or_else(|| format!("Printer not found: {}", id))?;

        // Merge settings
        let mut current = serde_json::to_value(&printer.settings).unwrap();
        if let (Some(cur_obj), Some(new_obj)) = (current.as_object_mut(), new_settings.as_object()) {
            for (k, v) in new_obj {
                cur_obj.insert(k.clone(), v.clone());
            }
        }
        printer.settings = serde_json::from_value(current).map_err(|e| e.to_string())?;
        printer.settings.apply_preset();
        let result = printer.clone();
        drop(printers);
        self.save();
        Ok(result)
    }

    pub fn remove(&self, id: &str) -> bool {
        let mut printers = self.printers.lock().unwrap();
        let len_before = printers.len();
        printers.retain(|p| p.id != id);
        let removed = printers.len() < len_before;
        drop(printers);
        if removed { self.save(); }
        removed
    }
}

fn chrono_now() -> String {
    // Simple ISO timestamp without chrono crate
    let d = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    format!("{}", d.as_secs())
}

// ─── Print Functions ─────────────────────────────────────────

/// Send raw ZPL over TCP to a network printer (port 9100)
pub async fn print_network(host: &str, port: u16, zpl: &str) -> Result<String, String> {
    let addr = format!("{}:{}", host, port);
    let stream = timeout(Duration::from_secs(10), TcpStream::connect(&addr))
        .await
        .map_err(|_| format!("Connection to {} timed out", addr))?
        .map_err(|e| format!("Connection to {} failed: {}", addr, e))?;

    let (_, mut writer) = tokio::io::split(stream);
    writer.write_all(zpl.as_bytes()).await
        .map_err(|e| format!("Write to {} failed: {}", addr, e))?;
    writer.shutdown().await
        .map_err(|e| format!("Shutdown failed: {}", e))?;

    Ok(format!("Sent to {}:{}", host, port))
}

/// Send raw ZPL via CUPS lp command (macOS/Linux)
#[cfg(not(target_os = "windows"))]
pub async fn print_usb(queue: &str, zpl: &str) -> Result<String, String> {
    use tokio::process::Command;
    let mut child = Command::new("lp")
        .args(["-d", queue, "-o", "raw"])
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .spawn()
        .map_err(|e| format!("Failed to run lp: {}", e))?;

    if let Some(mut stdin) = child.stdin.take() {
        stdin.write_all(zpl.as_bytes()).await
            .map_err(|e| format!("Write to lp failed: {}", e))?;
    }

    let output = child.wait_with_output().await
        .map_err(|e| format!("lp failed: {}", e))?;

    if output.status.success() {
        Ok(format!("Sent to CUPS queue: {}", queue))
    } else {
        Err(format!("lp failed: {}", String::from_utf8_lossy(&output.stderr)))
    }
}

/// Send raw ZPL via Windows Print Spooler (WritePrinter API)
#[cfg(target_os = "windows")]
pub async fn print_usb(printer_name: &str, zpl: &str) -> Result<String, String> {
    use windows::core::HSTRING;
    use windows::Win32::Graphics::Printing::*;
    use windows::Win32::Foundation::HANDLE;

    let name = printer_name.to_string();
    let data = zpl.as_bytes().to_vec();

    // Run blocking Win32 calls on a thread
    tokio::task::spawn_blocking(move || {
        unsafe {
            let hname = HSTRING::from(&name);
            let mut handle = HANDLE::default();

            OpenPrinterW(&hname, &mut handle, None)
                .map_err(|e| format!("OpenPrinter failed: {}", e))?;

            let doc_name = HSTRING::from("EDPrintTool Label");
            let doc_info = DOC_INFO_1W {
                pDocName: windows::core::PWSTR(doc_name.as_ptr() as *mut u16),
                pOutputFile: windows::core::PWSTR::null(),
                pDatatype: windows::core::PWSTR(HSTRING::from("RAW").as_ptr() as *mut u16),
            };

            let job_id = StartDocPrinterW(handle, 1, &doc_info as *const _ as *const _);
            if job_id == 0 {
                ClosePrinter(handle);
                return Err("StartDocPrinter failed".to_string());
            }

            StartPagePrinter(handle);

            let mut written: u32 = 0;
            let ok = WritePrinter(
                handle,
                data.as_ptr() as *const _,
                data.len() as u32,
                &mut written,
            );

            EndPagePrinter(handle);
            EndDocPrinter(handle);
            ClosePrinter(handle);

            if ok.is_ok() {
                Ok(format!("Sent {} bytes to {}", written, name))
            } else {
                Err(format!("WritePrinter failed for {}", name))
            }
        }
    })
    .await
    .map_err(|e| format!("Spawn blocking failed: {}", e))?
}

/// Discover printers on the system
#[cfg(not(target_os = "windows"))]
pub async fn discover_printers() -> Vec<serde_json::Value> {
    use tokio::process::Command;
    let output = Command::new("lpstat").args(["-p", "-d"]).output().await;
    match output {
        Ok(out) => {
            let text = String::from_utf8_lossy(&out.stdout);
            text.lines()
                .filter_map(|line| {
                    if line.starts_with("printer ") {
                        let name = line.split_whitespace().nth(1)?;
                        let status = if line.contains("idle") { "idle" }
                            else if line.contains("disabled") { "disabled" }
                            else { "unknown" };
                        Some(serde_json::json!({ "name": name, "status": status }))
                    } else {
                        None
                    }
                })
                .collect()
        }
        Err(_) => Vec::new(),
    }
}

#[cfg(target_os = "windows")]
pub async fn discover_printers() -> Vec<serde_json::Value> {
    use tokio::process::Command;
    let output = Command::new("powershell")
        .args(["-Command", "Get-Printer | Select-Object Name,DriverName,PrinterStatus | ConvertTo-Json"])
        .output()
        .await;

    match output {
        Ok(out) => {
            let text = String::from_utf8_lossy(&out.stdout);
            match serde_json::from_str::<serde_json::Value>(&text) {
                Ok(serde_json::Value::Array(arr)) => {
                    arr.iter().map(|p| serde_json::json!({
                        "name": p.get("Name").and_then(|v| v.as_str()).unwrap_or(""),
                        "driver": p.get("DriverName").and_then(|v| v.as_str()).unwrap_or(""),
                        "status": p.get("PrinterStatus").unwrap_or(&serde_json::Value::Null),
                    })).collect()
                }
                Ok(obj) if obj.is_object() => {
                    vec![serde_json::json!({
                        "name": obj.get("Name").and_then(|v| v.as_str()).unwrap_or(""),
                        "driver": obj.get("DriverName").and_then(|v| v.as_str()).unwrap_or(""),
                        "status": obj.get("PrinterStatus").unwrap_or(&serde_json::Value::Null),
                    })]
                }
                _ => Vec::new(),
            }
        }
        Err(_) => Vec::new(),
    }
}

/// Main print dispatcher
pub async fn print(store: &PrinterStore, printer_id: &str, zpl: &str, copies: u32, apply_settings: bool) -> Result<String, String> {
    let printer = store.get(printer_id)
        .ok_or_else(|| format!("Printer not found: {}", printer_id))?;

    let mut payload = String::new();

    if apply_settings {
        payload.push_str(&printer.settings.build_setup_zpl());
        payload.push('\n');
    }

    for i in 0..copies {
        payload.push_str(zpl);
        if i < copies - 1 { payload.push('\n'); }
    }

    match printer.printer_type.as_str() {
        "network" => {
            let host = printer.host.as_deref()
                .ok_or_else(|| format!("No host for printer: {}", printer_id))?;
            print_network(host, printer.port, &payload).await
        }
        "usb" => {
            #[cfg(target_os = "windows")]
            {
                let name = printer.windows_printer.as_deref()
                    .ok_or_else(|| format!("No Windows printer name for: {}", printer_id))?;
                print_usb(name, &payload).await
            }
            #[cfg(not(target_os = "windows"))]
            {
                let queue = printer.cups_queue.as_deref()
                    .ok_or_else(|| format!("No CUPS queue for: {}", printer_id))?;
                print_usb(queue, &payload).await
            }
        }
        _ => Err(format!("Unknown printer type: {}", printer.printer_type)),
    }
}
