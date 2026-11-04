;; A hand-written proxy-wasm 0.2.1 HTTP filter used by the engine and conformance tests.
;;  - request headers: x-block -> local 403 "blocked"; otherwise add x-wasm-seen: 1, remove x-remove-me,
;;    copy the request.path property into x-path, and probe that a bad guest pointer returns InvalidMemoryAccess.
;;  - request body: hold pieces until the end of the body, then replace the whole body with "REPLACED".
;;  - response headers: add x-wasm-response: ok.
;;  - on_configure: reject the configuration "reject".
;;  - on_log: log "ok" at info.
(module
  (import "env" "proxy_log" (func $log (param i32 i32 i32) (result i32)))
  (import "env" "proxy_get_header_map_value" (func $get_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_add_header_map_value" (func $add_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_remove_header_map_value" (func $remove_header (param i32 i32 i32) (result i32)))
  (import "env" "proxy_send_local_response" (func $send_local (param i32 i32 i32 i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_get_buffer_bytes" (func $get_buffer (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_set_buffer_bytes" (func $set_buffer (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_get_property" (func $get_property (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 2)
  (global $heap (mut i32) (i32.const 8192))
  (data (i32.const 16) "x-block")
  (data (i32.const 32) "x-wasm-seen")
  (data (i32.const 48) "1")
  (data (i32.const 64) "x-remove-me")
  (data (i32.const 80) "blocked")
  (data (i32.const 96) "x-wasm-response")
  (data (i32.const 112) "ok")
  (data (i32.const 128) "REPLACED")
  (data (i32.const 144) "reject")
  (data (i32.const 160) "request\00path")
  (data (i32.const 176) "x-path")

  (func (export "proxy_abi_version_0_2_1"))

  (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
    (local $p i32)
    (local.set $p (global.get $heap))
    (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
    (local.get $p))

  (func (export "proxy_on_context_create") (param i32 i32))
  (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))

  (func (export "proxy_on_configure") (param $root i32) (param $size i32) (result i32)
    (if (i32.ne (local.get $size) (i32.const 6)) (then (return (i32.const 1))))
    (drop (call $get_buffer (i32.const 7) (i32.const 0) (local.get $size) (i32.const 1024) (i32.const 1028)))
    (if (result i32) (i32.eq (i32.load (i32.load (i32.const 1024))) (i32.load (i32.const 144)))
      (then (i32.const 0))
      (else (i32.const 1))))

  (func (export "proxy_on_request_headers") (param $ctx i32) (param $n i32) (param $eos i32) (result i32)
    ;; A key pointer far outside memory must come back as InvalidMemoryAccess (6), not crash the host.
    (if (i32.ne (call $get_header (i32.const 0) (i32.const 0x7ffffff0) (i32.const 16) (i32.const 1024) (i32.const 1028)) (i32.const 6))
      (then unreachable))
    (if (i32.eqz (call $get_header (i32.const 0) (i32.const 16) (i32.const 7) (i32.const 1024) (i32.const 1028)))
      (then
        (drop (call $send_local (i32.const 403) (i32.const 0) (i32.const 0) (i32.const 80) (i32.const 7) (i32.const 0) (i32.const 0) (i32.const -1)))
        (return (i32.const 1))))
    (drop (call $add_header (i32.const 0) (i32.const 32) (i32.const 11) (i32.const 48) (i32.const 1)))
    (drop (call $remove_header (i32.const 0) (i32.const 64) (i32.const 11)))
    (if (i32.eqz (call $get_property (i32.const 160) (i32.const 12) (i32.const 1024) (i32.const 1028)))
      (then (drop (call $add_header (i32.const 0) (i32.const 176) (i32.const 6) (i32.load (i32.const 1024)) (i32.load (i32.const 1028))))))
    (i32.const 0))

  (func (export "proxy_on_request_body") (param $ctx i32) (param $size i32) (param $eos i32) (result i32)
    (if (i32.eqz (local.get $eos)) (then (return (i32.const 1))))
    (drop (call $set_buffer (i32.const 0) (i32.const 0) (local.get $size) (i32.const 128) (i32.const 8)))
    (i32.const 0))

  (func (export "proxy_on_response_headers") (param i32 i32 i32) (result i32)
    (drop (call $add_header (i32.const 2) (i32.const 96) (i32.const 15) (i32.const 112) (i32.const 2)))
    (i32.const 0))

  (func (export "proxy_on_log") (param i32)
    (drop (call $log (i32.const 2) (i32.const 112) (i32.const 2))))

  (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
  (func (export "proxy_on_delete") (param i32))
)
