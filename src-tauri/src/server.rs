use axum::{
    extract::{Path, State},
    http::StatusCode,
    response::Json,
    routing::{delete, get, patch, post},
    Router,
};
use std::sync::Arc;
use tower_http::cors::CorsLayer;
use tower_http::services::ServeDir;

use crate::printer::{self, PrinterConfig, PrinterStore};

type AppState = Arc<PrinterStore>;

// ─── REST Routes ─────────────────────────────────────────────

async fn status(State(store): State<AppState>) -> Json<serde_json::Value> {
    Json(serde_json::json!({
        "status": "running",
        "version": "1.0.0",
        "printers": store.list().len(),
    }))
}

async fn get_presets() -> Json<Vec<printer::LabelPreset>> {
    Json(printer::label_presets())
}

async fn list_printers(State(store): State<AppState>) -> Json<Vec<PrinterConfig>> {
    Json(store.list())
}

async fn discover(
) -> Json<Vec<serde_json::Value>> {
    Json(printer::discover_printers().await)
}

async fn add_printer(
    State(store): State<AppState>,
    Json(cfg): Json<PrinterConfig>,
) -> (StatusCode, Json<PrinterConfig>) {
    let p = store.add(cfg);
    (StatusCode::CREATED, Json(p))
}

async fn update_settings(
    State(store): State<AppState>,
    Path(id): Path<String>,
    Json(settings): Json<serde_json::Value>,
) -> Result<Json<PrinterConfig>, (StatusCode, Json<serde_json::Value>)> {
    store.update_settings(&id, settings)
        .map(Json)
        .map_err(|e| (StatusCode::BAD_REQUEST, Json(serde_json::json!({ "error": e }))))
}

async fn remove_printer(
    State(store): State<AppState>,
    Path(id): Path<String>,
) -> Json<serde_json::Value> {
    let removed = store.remove(&id);
    Json(serde_json::json!({ "removed": removed }))
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct PrintBody {
    zpl: Option<String>,
    copies: Option<u32>,
    apply_settings: Option<bool>,
}

async fn print_to_printer(
    State(store): State<AppState>,
    Path(printer_id): Path<String>,
    body: axum::body::Body,
) -> Result<Json<serde_json::Value>, (StatusCode, Json<serde_json::Value>)> {
    // Try to parse as JSON first, fallback to plain text
    let bytes = axum::body::to_bytes(body, 5 * 1024 * 1024)
        .await
        .map_err(|_| (StatusCode::BAD_REQUEST, Json(serde_json::json!({"error": "Body too large"}))))?;
    let text = String::from_utf8_lossy(&bytes).to_string();

    let (zpl, copies, apply_settings) = if let Ok(parsed) = serde_json::from_str::<PrintBody>(&text) {
        (
            parsed.zpl.unwrap_or_default(),
            parsed.copies.unwrap_or(1),
            parsed.apply_settings.unwrap_or(true),
        )
    } else {
        (text, 1, true)
    };

    if zpl.is_empty() {
        return Err((StatusCode::BAD_REQUEST, Json(serde_json::json!({"error": "Missing ZPL"}))));
    }

    match printer::print(&store, &printer_id, &zpl, copies, apply_settings).await {
        Ok(msg) => Ok(Json(serde_json::json!({ "success": true, "message": msg }))),
        Err(e) => Err((StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({ "error": e })))),
    }
}

#[derive(serde::Deserialize)]
struct RawPrintBody {
    host: String,
    port: Option<u16>,
    zpl: String,
}

async fn print_raw(
    Json(body): Json<RawPrintBody>,
) -> Result<Json<serde_json::Value>, (StatusCode, Json<serde_json::Value>)> {
    match printer::print_network(&body.host, body.port.unwrap_or(9100), &body.zpl).await {
        Ok(msg) => Ok(Json(serde_json::json!({ "success": true, "message": msg }))),
        Err(e) => Err((StatusCode::INTERNAL_SERVER_ERROR, Json(serde_json::json!({ "error": e })))),
    }
}

// ─── Start Server ────────────────────────────────────────────

pub async fn start_server(store: Arc<PrinterStore>, static_dir: std::path::PathBuf) {
    let app = Router::new()
        .route("/api/status", get(status))
        .route("/api/label-presets", get(get_presets))
        .route("/api/printers", get(list_printers).post(add_printer))
        .route("/api/printers/discover", get(discover))
        .route("/api/printers/{id}/settings", patch(update_settings))
        .route("/api/printers/{id}", delete(remove_printer))
        .route("/api/print/{printerId}", post(print_to_printer))
        .route("/api/print-raw", post(print_raw))
        .fallback_service(ServeDir::new(static_dir))
        .layer(CorsLayer::permissive())
        .with_state(store);

    let listener = tokio::net::TcpListener::bind("127.0.0.1:8189")
        .await
        .expect("Failed to bind port 8189");

    println!("[EDPrintTool] Server running on http://localhost:8189");

    axum::serve(listener, app).await.expect("Server failed");
}
