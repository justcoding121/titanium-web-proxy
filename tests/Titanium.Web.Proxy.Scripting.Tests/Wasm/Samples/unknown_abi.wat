;; Carries only an ABI marker this host does not serve; loading must fail and name it.
(module
  (memory (export "memory") 1)
  (func (export "proxy_abi_version_9_9_9"))
)
