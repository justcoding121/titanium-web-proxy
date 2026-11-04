-- header_gate.lua: the Lua version of Titanium.Web.Proxy.Examples.RoutePlugin/HeaderGatePlugin.cs.
--
-- A request that lacks one header is refused with 403. A request that has it goes on, with one more request header
-- stamped on it. Both header names come from the plugin's `settings`, with the same defaults as the C# plugin.

local function setting(name, default)
  local value = settings[name]
  if value == nil or value == "" then
    return default
  end
  return value
end

function on_request_headers(ctx)
  local required = setting("requiredHeader", "X-Api-Client")
  if ctx.get_header(required) == nil then
    return ctx.deny(403, "missing " .. required)
  end

  ctx.set_header(setting("stampHeader", "X-Route-Plugin"), "header-gate")
end
