;; header_gate.wat: a hand-written proxy-wasm 0.2.1 filter, the same as src/lib.rs with its default configuration.
;;
;; A request without an x-api-client header is answered 403 "missing x-api-client". A request with it goes on, with
;; x-route-plugin: header-gate added. The Titanium WASM engine compiles .wat files itself, so this runs with no toolchain:
;;
;;   plugins:
;;     - name: header-gate
;;       kind: wasm
;;       file: ./header_gate.wat
;;
;; Build src/lib.rs (Rust) or tinygo/main.go (TinyGo) when you want to read the header name from the configuration.
(module
  (import "env" "proxy_get_header_map_value" (func $get_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_add_header_map_value" (func $add_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_send_local_response" (func $send_local (param i32 i32 i32 i32 i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 2)
  (global $heap (mut i32) (i32.const 8192))
  (data (i32.const 16) "x-api-client")
  (data (i32.const 32) "x-route-plugin")
  (data (i32.const 48) "header-gate")
  (data (i32.const 64) "missing x-api-client")

  (func (export "proxy_abi_version_0_2_1"))

  ;; The host calls this to copy data into the guest (header values it returns, configuration bytes).
  (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
    (local $p i32)
    (local.set $p (global.get $heap))
    (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
    (local.get $p))

  (func (export "proxy_on_context_create") (param i32 i32))
  (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
  (func (export "proxy_on_configure") (param i32 i32) (result i32) (i32.const 1))

  ;; Map type 0 is the request headers. A return of 0 means the header was found.
  (func (export "proxy_on_request_headers") (param $ctx i32) (param $headers i32) (param $end_of_stream i32) (result i32)
    (if (i32.eqz (call $get_header (i32.const 0) (i32.const 16) (i32.const 12) (i32.const 1024) (i32.const 1028)))
      (then
        (drop (call $add_header (i32.const 0) (i32.const 32) (i32.const 14) (i32.const 48) (i32.const 11)))
        (return (i32.const 0))))   ;; Action::Continue
    (drop (call $send_local (i32.const 403) (i32.const 0) (i32.const 0) (i32.const 64) (i32.const 20)
                            (i32.const 0) (i32.const 0) (i32.const -1)))
    (i32.const 1))                  ;; Action::Pause: the local response is the answer

  (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
  (func (export "proxy_on_delete") (param i32))
)
