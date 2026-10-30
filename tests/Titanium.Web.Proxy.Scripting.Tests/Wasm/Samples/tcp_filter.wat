;; A proxy-wasm 0.2.1 network (stream) filter.
;;  - configuration "deny": close every connection at accept. "route-b": send connections to cluster "b".
;;  - downstream data: a piece starting with '!' closes the connection; a piece starting with '+' is held (Pause) until
;;    the data seen so far ends with '.', then forwarded without the '+'; anything else passes unchanged.
;;  - upstream data: every piece is prefixed with "wasm|".
;;  - downstream close logs "closed"; on_log logs "stream-log".
(module
  (import "env" "proxy_log" (func $log (param i32 i32 i32) (result i32)))
  (import "env" "proxy_get_buffer_bytes" (func $get_buffer (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_set_buffer_bytes" (func $set_buffer (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_set_property" (func $set_property (param i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_close_stream" (func $close_stream (param i32) (result i32)))
  (memory (export "memory") 2)
  (global $heap (mut i32) (i32.const 8192))
  (global $mode (mut i32) (i32.const 0))
  (data (i32.const 16) "deny")
  (data (i32.const 24) "route-b")
  (data (i32.const 32) "twp.upstream_cluster")
  (data (i32.const 56) "b")
  (data (i32.const 64) "wasm|")
  (data (i32.const 72) "closed")
  (data (i32.const 80) "stream-log")

  (func (export "proxy_abi_version_0_2_1"))

  (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
    (local $p i32)
    (local.set $p (global.get $heap))
    (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
    (local.get $p))

  (func (export "proxy_on_context_create") (param i32 i32))
  (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
  (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
  (func (export "proxy_on_delete") (param i32))

  (func (export "proxy_on_configure") (param $root i32) (param $size i32) (result i32)
    (local $p i32)
    (if (i32.eqz (local.get $size)) (then (return (i32.const 1))))
    (drop (call $get_buffer (i32.const 7) (i32.const 0) (local.get $size) (i32.const 1024) (i32.const 1028)))
    (local.set $p (i32.load (i32.const 1024)))
    (if (i32.and (i32.eq (local.get $size) (i32.const 4)) (i32.eq (i32.load (local.get $p)) (i32.load (i32.const 16))))
      (then (global.set $mode (i32.const 1))))
    (if (i32.and (i32.eq (local.get $size) (i32.const 7))
          (i32.and (i32.eq (i32.load (local.get $p)) (i32.load (i32.const 24)))
                   (i32.eq (i32.load offset=3 (local.get $p)) (i32.load (i32.const 27)))))
      (then (global.set $mode (i32.const 2))))
    (i32.const 1))

  (func (export "proxy_on_new_connection") (param $ctx i32) (result i32)
    (if (i32.eq (global.get $mode) (i32.const 1))
      (then (drop (call $close_stream (i32.const 2)))))
    (if (i32.eq (global.get $mode) (i32.const 2))
      (then (drop (call $set_property (i32.const 32) (i32.const 20) (i32.const 56) (i32.const 1)))))
    (i32.const 0))

  (func (export "proxy_on_downstream_data") (param $ctx i32) (param $size i32) (param $eos i32) (result i32)
    (local $p i32)
    (if (i32.eqz (local.get $size)) (then (return (i32.const 0))))
    (drop (call $get_buffer (i32.const 2) (i32.const 0) (local.get $size) (i32.const 1024) (i32.const 1028)))
    (local.set $p (i32.load (i32.const 1024)))
    (if (i32.eq (i32.load8_u (local.get $p)) (i32.const 33))
      (then
        (drop (call $close_stream (i32.const 2)))
        (return (i32.const 0))))
    (if (i32.eq (i32.load8_u (local.get $p)) (i32.const 43))
      (then
        (if (i32.ne (i32.load8_u (i32.add (local.get $p) (i32.sub (local.get $size) (i32.const 1)))) (i32.const 46))
          (then (return (i32.const 1))))
        (drop (call $set_buffer (i32.const 2) (i32.const 0) (i32.const 1) (i32.const 0) (i32.const 0)))))
    (i32.const 0))

  (func (export "proxy_on_upstream_data") (param $ctx i32) (param $size i32) (param $eos i32) (result i32)
    (drop (call $set_buffer (i32.const 3) (i32.const 0) (i32.const 0) (i32.const 64) (i32.const 5)))
    (i32.const 0))

  (func (export "proxy_on_downstream_connection_close") (param i32 i32)
    (drop (call $log (i32.const 2) (i32.const 72) (i32.const 6))))

  (func (export "proxy_on_log") (param i32)
    (drop (call $log (i32.const 2) (i32.const 80) (i32.const 10))))
)
