;; Imports a function the host does not provide; loading must fail and name it.
(module
  (import "env" "proxy_teleport" (func $teleport (param i32) (result i32)))
  (memory (export "memory") 1)
  (func (export "proxy_abi_version_0_2_1"))
  (func (export "proxy_on_request_headers") (param i32 i32 i32) (result i32)
    (call $teleport (i32.const 0)))
)
