//! The API this plugin mounts. The base nests this router under
//! `/api/plugins/com.channelflow.ai`, so the shell's calls to `GET /` and
//! `POST /providers` resolve there.

use std::sync::Arc;

use axum::{
    extract::{Path, State},
    http::StatusCode,
    response::{IntoResponse, Response},
    routing::{get, post, put},
    Json, Router,
};
use serde_json::json;

use crate::openai;
use crate::{AiConfig, AiError, AiState, AiView, ProviderView};

pub fn router(state: Arc<AiState>) -> Router {
    Router::new()
        .route("/", get(get_providers))
        .route("/providers", post(create_provider))
        .route("/providers/{id}", put(update_provider).delete(delete_provider))
        .route("/providers/{id}/test", post(test_provider))
        .route("/test", post(test_draft))
        .route("/test-all", post(test_all))
        .with_state(state)
}

type StateRef = Arc<AiState>;

/// Plugin-handled request failures, translated into HTTP status codes.
pub struct HttpError {
    status: StatusCode,
    message: String,
}

impl HttpError {
    fn bad(message: impl Into<String>) -> Self {
        Self {
            status: StatusCode::BAD_REQUEST,
            message: message.into(),
        }
    }

    fn missing(id: &str) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            message: format!("no AI provider with id {id}"),
        }
    }
}

impl From<AiError> for HttpError {
    fn from(error: AiError) -> Self {
        HttpError::bad(error.to_string())
    }
}

impl IntoResponse for HttpError {
    fn into_response(self) -> Response {
        (self.status, Json(json!({ "error": self.message }))).into_response()
    }
}

fn storage_error(error: channelflow_plugin_api::plugin::PluginError) -> HttpError {
    HttpError {
        status: StatusCode::INTERNAL_SERVER_ERROR,
        message: format!("could not save the provider list: {error}"),
    }
}

async fn get_providers(State(state): State<StateRef>) -> Result<Json<AiView>, HttpError> {
    Ok(Json(state.lock().await.view()))
}

async fn create_provider(
    State(state): State<StateRef>,
    Json(body): Json<serde_json::Value>,
) -> Result<(StatusCode, Json<ProviderView>), HttpError> {
    let mut config = state.lock().await;
    let provider = config.create(&body)?;
    state.store(&config).await.map_err(storage_error)?;
    Ok((StatusCode::CREATED, Json(provider.view())))
}

async fn update_provider(
    State(state): State<StateRef>,
    Path(id): Path<String>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<ProviderView>, HttpError> {
    let mut config = state.lock().await;
    if config.find(&id).is_none() {
        return Err(HttpError::missing(&id));
    }
    let provider = config.update(&id, &body)?;
    state.store(&config).await.map_err(storage_error)?;
    Ok(Json(provider.view()))
}

async fn delete_provider(
    State(state): State<StateRef>,
    Path(id): Path<String>,
) -> Result<StatusCode, HttpError> {
    let mut config = state.lock().await;
    if config.find(&id).is_none() {
        return Err(HttpError::missing(&id));
    }
    config.delete(&id)?;
    state.store(&config).await.map_err(storage_error)?;
    Ok(StatusCode::NO_CONTENT)
}

/// Try an as-yet-unsaved provider (the "New provider" tab). Nothing is stored.
async fn test_draft(
    State(state): State<StateRef>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<openai::Report>, HttpError> {
    let provider = AiConfig::draft(&body)?;
    Ok(Json(openai::run(&state.http, &provider).await))
}

async fn test_provider(
    State(state): State<StateRef>,
    Path(id): Path<String>,
    Json(body): Json<serde_json::Value>,
) -> Result<Json<openai::Report>, HttpError> {
    let config = state.lock().await;
    if config.find(&id).is_none() {
        return Err(HttpError::missing(&id));
    }
    let provider = config.resolve(&id, &body)?;
    Ok(Json(openai::run(&state.http, &provider).await))
}

/// Walk the saved providers in priority order; the report shows which one the
/// app would actually use.
async fn test_all(
    State(state): State<StateRef>,
) -> Result<Json<openai::FailoverReport>, HttpError> {
    let config = state.lock().await;
    let providers = config.ordered();
    Ok(Json(openai::failover(&state.http, &providers).await))
}