;; A proxy-wasm 0.2.1 filter that exercises host state from a request.
;;  - on_configure: 20 ms tick, registers shared queue "q", defines counter "requests" and gauge "gauge".
;;  - on_tick: logs "tick" once. on_queue_ready: dequeues and logs the item.
;;  - request headers:
;;      x-shared-found: 1 when shared key "k" already existed (set by another VM or plugin in the vm_id);
;;      sets k = v1, checks a stale CAS fails (x-cas-mismatch: 1), writes v2 with the right CAS, echoes x-shared: v2;
;;      enqueues "hello" on q; x-resolve: 1 when resolve_shared_queue("", "q") finds the same token;
;;      x-metric: 1 when the counter reads back at least 1; x-ff: ok when compress then uncompress round-trips;
;;      x-clock, x-random, x-stdout: 1 when the WASI calls work (stdout carries "stdout-line").
(module
  (import "env" "proxy_log" (func $log (param i32 i32 i32) (result i32)))
  (import "env" "proxy_add_header_map_value" (func $add_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_set_tick_period_milliseconds" (func $tick_period (param i32) (result i32)))
  (import "env" "proxy_get_shared_data" (func $get_shared (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_set_shared_data" (func $set_shared (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_register_shared_queue" (func $register_queue (param i32 i32 i32) (result i32)))
  (import "env" "proxy_resolve_shared_queue" (func $resolve_queue (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_enqueue_shared_queue" (func $enqueue (param i32 i32 i32) (result i32)))
  (import "env" "proxy_dequeue_shared_queue" (func $dequeue (param i32 i32 i32) (result i32)))
  (import "env" "proxy_define_metric" (func $define_metric (param i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_increment_metric" (func $increment_metric (param i32 i64) (result i32)))
  (import "env" "proxy_record_metric" (func $record_metric (param i32 i64) (result i32)))
  (import "env" "proxy_get_metric" (func $get_metric (param i32 i32) (result i32)))
  (import "env" "proxy_call_foreign_function" (func $foreign (param i32 i32 i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "clock_time_get" (func $clock_time_get (param i32 i64 i32) (result i32)))
  (import "wasi_snapshot_preview1" "random_get" (func $random_get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 2)
  (global $heap (mut i32) (i32.const 8192))
  (global $queue (mut i32) (i32.const 0))
  (global $counter (mut i32) (i32.const 0))
  (global $gauge (mut i32) (i32.const 0))
  (global $ticked (mut i32) (i32.const 0))
  (data (i32.const 16) "k")
  (data (i32.const 20) "v1")
  (data (i32.const 24) "v2")
  (data (i32.const 32) "x-cas-mismatch")
  (data (i32.const 48) "x-shared")
  (data (i32.const 64) "x-shared-found")
  (data (i32.const 80) "q")
  (data (i32.const 84) "hello")
  (data (i32.const 96) "requests")
  (data (i32.const 112) "x-ff")
  (data (i32.const 120) "ok")
  (data (i32.const 128) "compress")
  (data (i32.const 144) "uncompress")
  (data (i32.const 160) "aaaaaaaaaaaaaaaaaaaa")
  (data (i32.const 192) "x-metric")
  (data (i32.const 208) "x-clock")
  (data (i32.const 216) "1")
  (data (i32.const 224) "x-resolve")
  (data (i32.const 240) "tick")
  (data (i32.const 248) "stdout-line\n")
  (data (i32.const 264) "\f8\00\00\00\0c\00\00\00")
  (data (i32.const 288) "x-random")
  (data (i32.const 304) "x-stdout")
  (data (i32.const 320) "gauge")

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

  (func (export "proxy_on_configure") (param i32 i32) (result i32)
    (drop (call $tick_period (i32.const 20)))
    (drop (call $register_queue (i32.const 80) (i32.const 1) (i32.const 1036)))
    (global.set $queue (i32.load (i32.const 1036)))
    (drop (call $define_metric (i32.const 0) (i32.const 96) (i32.const 8) (i32.const 1040)))
    (global.set $counter (i32.load (i32.const 1040)))
    (drop (call $define_metric (i32.const 1) (i32.const 320) (i32.const 5) (i32.const 1040)))
    (global.set $gauge (i32.load (i32.const 1040)))
    (i32.const 1))

  (func (export "proxy_on_tick") (param i32)
    (if (i32.eqz (global.get $ticked))
      (then
        (global.set $ticked (i32.const 1))
        (drop (call $log (i32.const 2) (i32.const 240) (i32.const 4))))))

  (func (export "proxy_on_queue_ready") (param $root i32) (param $token i32)
    (if (i32.eqz (call $dequeue (local.get $token) (i32.const 1024) (i32.const 1028)))
      (then (drop (call $log (i32.const 2) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)))))))

  (func $add (param $kp i32) (param $kl i32) (param $vp i32) (param $vl i32)
    (drop (call $add_header (i32.const 0) (local.get $kp) (local.get $kl) (local.get $vp) (local.get $vl))))

  (func (export "proxy_on_request_headers") (param $ctx i32) (param $n i32) (param $eos i32) (result i32)
    (local $cas i32)
    ;; Metrics.
    (drop (call $increment_metric (global.get $counter) (i64.const 1)))
    (drop (call $record_metric (global.get $gauge) (i64.const 7)))
    (if (i32.eqz (call $get_metric (global.get $counter) (i32.const 1056)))
      (then
        (if (i64.ge_u (i64.load (i32.const 1056)) (i64.const 1))
          (then (call $add (i32.const 192) (i32.const 8) (i32.const 216) (i32.const 1))))))
    ;; Shared data with CAS.
    (if (i32.eqz (call $get_shared (i32.const 16) (i32.const 1) (i32.const 1024) (i32.const 1028) (i32.const 1032)))
      (then (call $add (i32.const 64) (i32.const 14) (i32.const 216) (i32.const 1)))
      (else (drop (call $set_shared (i32.const 16) (i32.const 1) (i32.const 20) (i32.const 2) (i32.const 0)))))
    (drop (call $get_shared (i32.const 16) (i32.const 1) (i32.const 1024) (i32.const 1028) (i32.const 1032)))
    (local.set $cas (i32.load (i32.const 1032)))
    (if (i32.eq (call $set_shared (i32.const 16) (i32.const 1) (i32.const 24) (i32.const 2) (i32.add (local.get $cas) (i32.const 1))) (i32.const 8))
      (then (call $add (i32.const 32) (i32.const 14) (i32.const 216) (i32.const 1))))
    (drop (call $set_shared (i32.const 16) (i32.const 1) (i32.const 24) (i32.const 2) (local.get $cas)))
    (drop (call $get_shared (i32.const 16) (i32.const 1) (i32.const 1024) (i32.const 1028) (i32.const 1032)))
    (call $add (i32.const 48) (i32.const 8) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)))
    ;; Shared queue.
    (drop (call $enqueue (global.get $queue) (i32.const 84) (i32.const 5)))
    (if (i32.eqz (call $resolve_queue (i32.const 0) (i32.const 0) (i32.const 80) (i32.const 1) (i32.const 1036)))
      (then
        (if (i32.eq (i32.load (i32.const 1036)) (global.get $queue))
          (then (call $add (i32.const 224) (i32.const 9) (i32.const 216) (i32.const 1))))))
    ;; Foreign functions: compress, then uncompress, back to the 20 bytes of 'a'.
    (if (i32.eqz (call $foreign (i32.const 128) (i32.const 8) (i32.const 160) (i32.const 20) (i32.const 1024) (i32.const 1028)))
      (then
        (if (i32.eqz (call $foreign (i32.const 144) (i32.const 10) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)) (i32.const 1024) (i32.const 1028)))
          (then
            (if (i32.and (i32.eq (i32.load (i32.const 1028)) (i32.const 20)) (i32.eq (i32.load8_u (i32.load (i32.const 1024))) (i32.const 97)))
              (then (call $add (i32.const 112) (i32.const 4) (i32.const 120) (i32.const 2))))))))
    ;; WASI.
    (if (i32.eqz (call $clock_time_get (i32.const 0) (i64.const 1) (i32.const 1048)))
      (then
        (if (i64.gt_u (i64.load (i32.const 1048)) (i64.const 0))
          (then (call $add (i32.const 208) (i32.const 7) (i32.const 216) (i32.const 1))))))
    (if (i32.eqz (call $random_get (i32.const 1064) (i32.const 16)))
      (then (call $add (i32.const 288) (i32.const 8) (i32.const 216) (i32.const 1))))
    (if (i32.eqz (call $fd_write (i32.const 1) (i32.const 264) (i32.const 1) (i32.const 1036)))
      (then
        (if (i32.eq (i32.load (i32.const 1036)) (i32.const 12))
          (then (call $add (i32.const 304) (i32.const 8) (i32.const 216) (i32.const 1))))))
    (i32.const 0))
)
