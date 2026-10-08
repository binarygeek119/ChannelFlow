//! HTTP surface: the JSON API for channels plus the static web UI shell.

use axum::{
    extract::{Path, State},
    http::StatusCode,
    response::{Html, IntoResponse, Response},
    routing::get,
    Json, Router,
};
use serde_json::json;
use uuid::Uuid;

use crate::model::{Channel, NewChannel, UpdateChannel};
use crate::store::{Store, StoreError};

pub fn router(store: Store) -> Router {
    Router::new()
        .route("/", get(index))
        .route("/app.css", get(css))
        .route("/app.js", get(js))
        .route("/api/health", get(health))
        .route("/api/channels", get(list_channels).post(create_channel))
        .route(
            "/api/channels/{id}",
            get(get_channel).put(update_channel).delete(delete_channel),
        )
        .with_state(store)
}

/// Storage errors translated into HTTP status codes.
struct ApiError(StoreError);

impl From<StoreError> for ApiError {
    fn from(error: StoreError) -> Self {
        ApiError(error)
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let (status, message) = match &self.0 {
            StoreError::NotFound(_) => (StatusCode::NOT_FOUND, self.0.to_string()),
            StoreError::DuplicateNumber(_) => (StatusCode::CONFLICT, self.0.to_string()),
            StoreError::Invalid(_) => (StatusCode::BAD_REQUEST, self.0.to_string()),
            StoreError::Io(_) | StoreError::Json(_) => {
                tracing::error!(error = %self.0, "storage failure");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "internal storage error".to_string(),
                )
            }
        };
        (status, Json(json!({ "error": message }))).into_response()
    }
}

async fn health() -> Json<serde_json::Value> {
    Json(json!({
        "status": "ok",
        "name": "ChannelFlow",
        "version": env!("CARGO_PKG_VERSION"),
    }))
}

async fn list_channels(State(store): State<Store>) -> Result<Json<Vec<Channel>>, ApiError> {
    Ok(Json(store.list()?))
}

async fn get_channel(
    State(store): State<Store>,
    Path(id): Path<Uuid>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(store.get(id)?))
}

async fn create_channel(
    State(store): State<Store>,
    Json(input): Json<NewChannel>,
) -> Result<(StatusCode, Json<Channel>), ApiError> {
    let channel = store.create(input)?;
    Ok((StatusCode::CREATED, Json(channel)))
}

async fn update_channel(
    State(store): State<Store>,
    Path(id): Path<Uuid>,
    Json(input): Json<UpdateChannel>,
) -> Result<Json<Channel>, ApiError> {
    Ok(Json(store.update(id, input)?))
}

async fn delete_channel(
    State(store): State<Store>,
    Path(id): Path<Uuid>,
) -> Result<StatusCode, ApiError> {
    store.delete(id)?;
    Ok(StatusCode::NO_CONTENT)
}

// The UI is compiled into the binary so the shipped image needs no asset
// directory and cannot start with a half-copied web root.
async fn index() -> Html<&'static str> {
    Html(include_str!("../static/index.html"))
}

async fn css() -> impl IntoResponse {
    (
        [(axum::http::header::CONTENT_TYPE, "text/css; charset=utf-8")],
        include_str!("../static/app.css"),
    )
}

async fn js() -> impl IntoResponse {
    (
        [(
            axum::http::header::CONTENT_TYPE,
            "text/javascript; charset=utf-8",
        )],
        include_str!("../static/app.js"),
    )
}
