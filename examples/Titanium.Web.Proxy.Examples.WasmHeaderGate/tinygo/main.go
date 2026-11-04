// A proxy-wasm HTTP filter for TinyGo: the Go version of src/lib.rs.
//
// A request without the required header is answered 403. A request with it goes on, with x-route-plugin: header-gate
// added. The plugin configuration (settings.configuration in twp.yaml) is the required header name; the default is
// x-api-client.
//
// Build:  tinygo build -o header_gate.wasm -scheduler=none -target=wasi main.go
package main

import (
	"strings"

	"github.com/tetratelabs/proxy-wasm-go-sdk/proxywasm"
	"github.com/tetratelabs/proxy-wasm-go-sdk/proxywasm/types"
)

const (
	defaultRequiredHeader = "x-api-client"
	stampHeader           = "x-route-plugin"
	stampValue            = "header-gate"
)

func main() {
	proxywasm.SetVMContext(&vmContext{})
}

type vmContext struct {
	types.DefaultVMContext
}

func (*vmContext) NewPluginContext(contextID uint32) types.PluginContext {
	return &pluginContext{requiredHeader: defaultRequiredHeader}
}

// pluginContext is one per plugin. It reads the configuration once and hands it to every request context.
type pluginContext struct {
	types.DefaultPluginContext
	requiredHeader string
}

func (p *pluginContext) OnPluginStart(configurationSize int) types.OnPluginStartStatus {
	data, err := proxywasm.GetPluginConfiguration()
	if err != nil && err != types.ErrorStatusNotFound {
		proxywasm.LogCriticalf("header-gate: cannot read the configuration: %v", err)
		return types.OnPluginStartStatusFailed
	}

	if name := strings.ToLower(strings.TrimSpace(string(data))); name != "" {
		p.requiredHeader = name
	}

	proxywasm.LogInfof("header-gate: requiring %s", p.requiredHeader)
	return types.OnPluginStartStatusOK
}

func (p *pluginContext) NewHttpContext(contextID uint32) types.HttpContext {
	return &headerGate{requiredHeader: p.requiredHeader}
}

// headerGate is one per request.
type headerGate struct {
	types.DefaultHttpContext
	requiredHeader string
}

func (h *headerGate) OnHttpRequestHeaders(numHeaders int, endOfStream bool) types.Action {
	if _, err := proxywasm.GetHttpRequestHeader(h.requiredHeader); err != nil {
		body := []byte("missing " + h.requiredHeader)
		if err := proxywasm.SendHttpResponse(403, [][2]string{{"content-type", "text/plain"}}, body, -1); err != nil {
			proxywasm.LogErrorf("header-gate: cannot send the response: %v", err)
		}

		// The local response is the answer; the request goes no further.
		return types.ActionPause
	}

	if err := proxywasm.AddHttpRequestHeader(stampHeader, stampValue); err != nil {
		proxywasm.LogErrorf("header-gate: cannot add %s: %v", stampHeader, err)
	}

	return types.ActionContinue
}
