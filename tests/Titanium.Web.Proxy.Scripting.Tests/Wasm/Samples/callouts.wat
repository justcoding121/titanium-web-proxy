;; A proxy-wasm 0.2.1 filter that pauses a request on a callout and resumes it from the callout's callback.
;;  - x-http-call: <target>   -> http_call GET /hello; the response's :status and body become x-call-status / x-call-body.
;;  - x-grpc-call: <target>   -> grpc_call echo.Echo/Unary "ping"; the reply becomes x-grpc-msg.
;;  - x-grpc-bad: <target>    -> grpc_call echo.Echo/Nope; the close status becomes x-grpc-status.
;;  - x-grpc-stream: <target> -> grpc_stream echo.Echo/Stream, sends "a" then "b" with end of stream; each reply is
;;    added as x-grpc-msg, metadata as x-grpc-initial / x-grpc-trailers, and the close status as x-grpc-status.
;;  A callout the host refuses adds x-call-error: 1 and the request continues at once.
(module
  (import "env" "proxy_get_header_map_value" (func $get_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_add_header_map_value" (func $add_header (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_get_buffer_bytes" (func $get_buffer (param i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_continue_stream" (func $continue_stream (param i32) (result i32)))
  (import "env" "proxy_http_call" (func $http_call (param i32 i32 i32 i32 i32 i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_grpc_call" (func $grpc_call (param i32 i32 i32 i32 i32 i32 i32 i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_grpc_stream" (func $grpc_stream (param i32 i32 i32 i32 i32 i32 i32 i32 i32) (result i32)))
  (import "env" "proxy_grpc_send" (func $grpc_send (param i32 i32 i32 i32) (result i32)))
  (memory (export "memory") 2)
  (global $heap (mut i32) (i32.const 8192))
  (global $stream (mut i32) (i32.const -1))
  (data (i32.const 16) "x-http-call")
  (data (i32.const 32) "x-grpc-call")
  (data (i32.const 48) "x-grpc-stream")
  (data (i32.const 64) "x-call-status")
  (data (i32.const 80) "x-call-body")
  (data (i32.const 96) ":status")
  (data (i32.const 112) "x-call-error")
  (data (i32.const 128) "1")
  (data (i32.const 144) "echo.Echo")
  (data (i32.const 160) "Unary")
  (data (i32.const 176) "Stream")
  (data (i32.const 184) "Nope")
  (data (i32.const 192) "ping")
  (data (i32.const 200) "a")
  (data (i32.const 204) "b")
  (data (i32.const 208) "x-grpc-msg")
  (data (i32.const 224) "x-grpc-status")
  (data (i32.const 240) "x-grpc-initial")
  (data (i32.const 256) "x-grpc-trailers")
  (data (i32.const 272) "x-grpc-bad")
  ;; Request headers for http_call: :method GET, :path /hello, :authority origin.test.
  (data (i32.const 512)
    "\03\00\00\00"
    "\07\00\00\00\03\00\00\00" "\05\00\00\00\06\00\00\00" "\0a\00\00\00\0b\00\00\00"
    ":method\00GET\00:path\00/hello\00:authority\00origin.test\00")

  (func (export "proxy_abi_version_0_2_1"))

  (func (export "proxy_on_memory_allocate") (param $size i32) (result i32)
    (local $p i32)
    (local.set $p (global.get $heap))
    (global.set $heap (i32.and (i32.add (i32.add (global.get $heap) (local.get $size)) (i32.const 7)) (i32.const -8)))
    (local.get $p))

  (func (export "proxy_on_context_create") (param i32 i32))
  (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
  (func (export "proxy_on_configure") (param i32 i32) (result i32) (i32.const 1))
  (func (export "proxy_on_done") (param i32) (result i32) (i32.const 1))
  (func (export "proxy_on_delete") (param i32))

  (func $add (param $kp i32) (param $kl i32) (param $vp i32) (param $vl i32)
    (drop (call $add_header (i32.const 0) (local.get $kp) (local.get $kl) (local.get $vp) (local.get $vl))))

  ;; 1 when the request has header $kp/$kl; its value is left at 1036 (pointer) and 1040 (size).
  (func $has (param $kp i32) (param $kl i32) (result i32)
    (i32.eqz (call $get_header (i32.const 0) (local.get $kp) (local.get $kl) (i32.const 1036) (i32.const 1040))))

  (func $refused (result i32)
    (call $add (i32.const 112) (i32.const 12) (i32.const 128) (i32.const 1))
    (i32.const 0))

  (func (export "proxy_on_request_headers") (param $ctx i32) (param $n i32) (param $eos i32) (result i32)
    (if (call $has (i32.const 16) (i32.const 11))
      (then
        (if (call $http_call (i32.load (i32.const 1036)) (i32.load (i32.const 1040)) (i32.const 512) (i32.const 76)
              (i32.const 0) (i32.const 0) (i32.const 0) (i32.const 0) (i32.const 5000) (i32.const 1032))
          (then (return (call $refused))))
        (return (i32.const 1))))
    (if (call $has (i32.const 32) (i32.const 11))
      (then
        (if (call $grpc_call (i32.load (i32.const 1036)) (i32.load (i32.const 1040)) (i32.const 144) (i32.const 9)
              (i32.const 160) (i32.const 5) (i32.const 0) (i32.const 0) (i32.const 192) (i32.const 4) (i32.const 5000) (i32.const 1032))
          (then (return (call $refused))))
        (return (i32.const 1))))
    (if (call $has (i32.const 272) (i32.const 10))
      (then
        (if (call $grpc_call (i32.load (i32.const 1036)) (i32.load (i32.const 1040)) (i32.const 144) (i32.const 9)
              (i32.const 184) (i32.const 4) (i32.const 0) (i32.const 0) (i32.const 192) (i32.const 4) (i32.const 5000) (i32.const 1032))
          (then (return (call $refused))))
        (return (i32.const 1))))
    (if (call $has (i32.const 48) (i32.const 13))
      (then
        (if (call $grpc_stream (i32.load (i32.const 1036)) (i32.load (i32.const 1040)) (i32.const 144) (i32.const 9)
              (i32.const 176) (i32.const 6) (i32.const 0) (i32.const 0) (i32.const 1032))
          (then (return (call $refused))))
        (global.set $stream (i32.load (i32.const 1032)))
        (drop (call $grpc_send (global.get $stream) (i32.const 200) (i32.const 1) (i32.const 0)))
        (drop (call $grpc_send (global.get $stream) (i32.const 204) (i32.const 1) (i32.const 1)))
        (return (i32.const 1))))
    (i32.const 0))

  (func (export "proxy_on_http_call_response") (param $ctx i32) (param $token i32) (param $headers i32) (param $size i32) (param $trailers i32)
    (if (i32.eqz (call $get_header (i32.const 6) (i32.const 96) (i32.const 7) (i32.const 1024) (i32.const 1028)))
      (then (call $add (i32.const 64) (i32.const 13) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)))))
    (if (i32.gt_u (local.get $size) (i32.const 0))
      (then
        (if (i32.eqz (call $get_buffer (i32.const 4) (i32.const 0) (local.get $size) (i32.const 1024) (i32.const 1028)))
          (then (call $add (i32.const 80) (i32.const 11) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)))))))
    (drop (call $continue_stream (i32.const 0))))

  (func (export "proxy_on_grpc_receive_initial_metadata") (param i32 i32 i32)
    (call $add (i32.const 240) (i32.const 14) (i32.const 128) (i32.const 1)))

  (func (export "proxy_on_grpc_receive") (param $ctx i32) (param $token i32) (param $size i32)
    (if (i32.eqz (call $get_buffer (i32.const 5) (i32.const 0) (local.get $size) (i32.const 1024) (i32.const 1028)))
      (then (call $add (i32.const 208) (i32.const 10) (i32.load (i32.const 1024)) (i32.load (i32.const 1028)))))
    (if (i32.ne (local.get $token) (global.get $stream))
      (then (drop (call $continue_stream (i32.const 0))))))

  (func (export "proxy_on_grpc_receive_trailing_metadata") (param i32 i32 i32)
    (call $add (i32.const 256) (i32.const 15) (i32.const 128) (i32.const 1)))

  (func (export "proxy_on_grpc_close") (param $ctx i32) (param $token i32) (param $status i32)
    (call $add (i32.const 224) (i32.const 13) (i32.const 2000) (call $digits (local.get $status)))
    (drop (call $continue_stream (i32.const 0))))

  ;; Writes $v (0-99) as decimal at 2000 and returns its length.
  (func $digits (param $v i32) (result i32)
    (if (result i32) (i32.lt_u (local.get $v) (i32.const 10))
      (then
        (i32.store8 (i32.const 2000) (i32.add (i32.const 48) (local.get $v)))
        (i32.const 1))
      (else
        (i32.store8 (i32.const 2000) (i32.add (i32.const 48) (i32.div_u (local.get $v) (i32.const 10))))
        (i32.store8 (i32.const 2001) (i32.add (i32.const 48) (i32.rem_u (local.get $v) (i32.const 10))))
        (i32.const 2))))
)
