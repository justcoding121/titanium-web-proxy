;; A WASI command module whose _start exits 3: proc_exit traps, so the plugin fails to load.
(module
  (import "wasi_snapshot_preview1" "proc_exit" (func $exit (param i32)))
  (memory (export "memory") 1)
  (func (export "proxy_abi_version_0_2_1"))
  (func (export "_start") (call $exit (i32.const 3)))
  (func (export "proxy_on_vm_start") (param i32 i32) (result i32) (i32.const 1))
)
