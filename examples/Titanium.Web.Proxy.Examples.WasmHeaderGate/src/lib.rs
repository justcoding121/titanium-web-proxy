//! A proxy-wasm HTTP filter: the Rust version of `Titanium.Web.Proxy.Examples.RoutePlugin/HeaderGatePlugin.cs`.
//!
//! A request without the required header is answered `403`. A request with it goes on, with `x-route-plugin: header-gate`
//! added. The plugin configuration (`settings.configuration` in `twp.yaml`) is the required header name; the default is
//! `x-api-client`.

use log::{info, warn};
use proxy_wasm::traits::*;
use proxy_wasm::types::*;

const DEFAULT_REQUIRED_HEADER: &str = "x-api-client";
const STAMP_HEADER: &str = "x-route-plugin";
const STAMP_VALUE: &str = "header-gate";

proxy_wasm::main! {{
    proxy_wasm::set_log_level(LogLevel::Info);
    proxy_wasm::set_root_context(|_| -> Box<dyn RootContext> {
        Box::new(HeaderGateRoot {
            required_header: DEFAULT_REQUIRED_HEADER.to_string(),
        })
    });
}}

/// One per plugin. It reads the configuration once and hands it to every request context.
struct HeaderGateRoot {
    required_header: String,
}

impl Context for HeaderGateRoot {}

impl RootContext for HeaderGateRoot {
    fn on_configure(&mut self, _plugin_configuration_size: usize) -> bool {
        if let Some(bytes) = self.get_plugin_configuration() {
            match String::from_utf8(bytes) {
                Ok(name) if !name.trim().is_empty() => {
                    self.required_header = name.trim().to_ascii_lowercase();
                }
                Ok(_) => {}
                Err(_) => {
                    // Refusing the configuration fails the load, so the proxy keeps the plugin it had.
                    warn!("header-gate: the configuration is not UTF-8");
                    return false;
                }
            }
        }

        info!("header-gate: requiring {}", self.required_header);
        true
    }

    fn get_type(&self) -> Option<ContextType> {
        Some(ContextType::HttpContext)
    }

    fn create_http_context(&self, _context_id: u32) -> Option<Box<dyn HttpContext>> {
        Some(Box::new(HeaderGate {
            required_header: self.required_header.clone(),
        }))
    }
}

/// One per request.
struct HeaderGate {
    required_header: String,
}

impl Context for HeaderGate {}

impl HttpContext for HeaderGate {
    fn on_http_request_headers(&mut self, _num_headers: usize, _end_of_stream: bool) -> Action {
        if self.get_http_request_header(&self.required_header).is_none() {
            let body = format!("missing {}", self.required_header);
            self.send_http_response(
                403,
                vec![("content-type", "text/plain")],
                Some(body.as_bytes()),
            );
            // The local response is the answer; the request goes no further.
            return Action::Pause;
        }

        self.add_http_request_header(STAMP_HEADER, STAMP_VALUE);
        Action::Continue
    }
}
