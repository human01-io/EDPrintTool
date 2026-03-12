// Prevents console window on Windows in release builds
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod printer;
mod server;

use printer::PrinterStore;
use std::path::PathBuf;
use std::sync::Arc;
use tauri_plugin_autostart::ManagerExt;
use tauri::{
    menu::{MenuBuilder, MenuItemBuilder, PredefinedMenuItem, CheckMenuItemBuilder},
    tray::TrayIconEvent,
    Manager,
};
use tauri_plugin_autostart::MacosLauncher;

fn config_path() -> PathBuf {
    // Store printers.json next to the executable (portable) or in app data
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    exe_dir.join("printers.json")
}

fn public_dir() -> PathBuf {
    // In development, use ../public. In production, use the bundled resource.
    let dev_path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../public");
    if dev_path.exists() {
        return dev_path;
    }
    // Fallback: try relative to executable
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    exe_dir.join("_up_/public")
}

fn main() {
    let store = Arc::new(PrinterStore::new(config_path()));
    let server_store = store.clone();
    let static_dir = public_dir();

    tauri::Builder::default()
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec![]),
        ))
        .plugin(tauri_plugin_shell::init())
        .setup(move |app| {
            // Start the HTTP server on a background thread
            let sd = static_dir.clone();
            let ss = server_store.clone();
            tauri::async_runtime::spawn(async move {
                server::start_server(ss, sd).await;
            });

            // ── System Tray ──────────────────────────────
            let open_item = MenuItemBuilder::with_id("open", "Open EDPrintTool").build(app)?;
            let browser_item = MenuItemBuilder::with_id("browser", "Open in Browser").build(app)?;
            let separator1 = PredefinedMenuItem::separator(app)?;

            let autostart_mgr = app.autolaunch();
            let is_enabled = autostart_mgr.is_enabled().unwrap_or(false);
            let autostart_item = CheckMenuItemBuilder::with_id("autostart", "Start with Windows")
                .checked(is_enabled)
                .build(app)?;

            let separator2 = PredefinedMenuItem::separator(app)?;
            let server_label = MenuItemBuilder::with_id("server_info", "Server: localhost:8189")
                .enabled(false)
                .build(app)?;
            let separator3 = PredefinedMenuItem::separator(app)?;
            let quit_item = MenuItemBuilder::with_id("quit", "Quit EDPrintTool").build(app)?;

            let menu = MenuBuilder::new(app)
                .item(&open_item)
                .item(&browser_item)
                .item(&separator1)
                .item(&autostart_item)
                .item(&separator2)
                .item(&server_label)
                .item(&separator3)
                .item(&quit_item)
                .build()?;

            if let Some(tray) = app.tray_by_id("main-tray") {
                tray.set_menu(Some(menu))?;

                let app_handle = app.handle().clone();
                tray.on_menu_event(move |_app, event| {
                    match event.id().as_ref() {
                        "open" => {
                            if let Some(window) = app_handle.get_webview_window("main") {
                                let _ = window.show();
                                let _ = window.set_focus();
                            }
                        }
                        "browser" => {
                            if let Some(window) = app_handle.get_webview_window("main") {
                                let _ = window.eval("window.open('http://localhost:8189', '_blank')");
                            }
                            // Fallback: use shell command
                            #[cfg(target_os = "windows")]
                            { let _ = std::process::Command::new("cmd").args(["/c", "start", "http://localhost:8189"]).spawn(); }
                            #[cfg(target_os = "macos")]
                            { let _ = std::process::Command::new("open").arg("http://localhost:8189").spawn(); }
                            #[cfg(target_os = "linux")]
                            { let _ = std::process::Command::new("xdg-open").arg("http://localhost:8189").spawn(); }
                        }
                        "autostart" => {
                            let mgr = app_handle.autolaunch();
                            let enabled = mgr.is_enabled().unwrap_or(false);
                            if enabled {
                                let _ = mgr.disable();
                            } else {
                                let _ = mgr.enable();
                            }
                        }
                        "quit" => {
                            app_handle.exit(0);
                        }
                        _ => {}
                    }
                });

                let app_handle2 = app.handle().clone();
                tray.on_tray_icon_event(move |_tray, event| {
                    if let TrayIconEvent::DoubleClick { .. } = event {
                        if let Some(window) = app_handle2.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.set_focus();
                        }
                    }
                });
            }

            // ── Window close → hide to tray ──────────────
            let _app_handle3 = app.handle().clone();
            if let Some(window) = app.get_webview_window("main") {
                let win = window.clone();
                window.on_window_event(move |event| {
                    if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                        api.prevent_close();
                        let _ = win.hide();
                        // Keep server running in tray
                    }
                });

                // Load the localhost URL (served by our axum server)
                // Small delay to let the server start
                let win2 = window.clone();
                tauri::async_runtime::spawn(async move {
                    tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
                    let _ = win2.eval("window.location.href = 'http://localhost:8189'");
                });
            }

            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running EDPrintTool");
}
