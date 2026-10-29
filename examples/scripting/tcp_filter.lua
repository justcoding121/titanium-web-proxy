-- tcp_filter.lua: filter raw TCP connections on a passthrough listener.
--
--   on_connect  refuses clients in `settings.denyCidrs` (comma separated) and counts bytes from here on.
--   on_data     closes the connection when it moves more than `settings.maxBytes` bytes in both directions together.
--   on_close    logs what the connection moved.
--
-- Wire it with `listeners[].streamFilters: [tcp-filter]` on a listener with `passthrough: true`.

local function deny_list()
  local list = {}
  for entry in (settings.denyCidrs or ""):gmatch("[^,%s]+") do
    list[#list + 1] = entry
  end
  return list
end

function on_connect(ctx)
  local denied = deny_list()
  if #denied > 0 and cidr.match(ctx.client_ip, denied) then
    log.info("refused " .. ctx.client_ip)
    return ctx.deny()
  end

  ctx.vars.bytes = 0
end

function on_data(ctx)
  local limit = tonumber(settings.maxBytes) or 10 * 1024 * 1024
  ctx.vars.bytes = (ctx.vars.bytes or 0) + #ctx.data
  if ctx.vars.bytes > limit then
    log.warn(ctx.client_ip .. " passed " .. limit .. " bytes; closing")
    return ctx.deny()
  end
end

function on_close(ctx)
  log.info(string.format("%s closed after %d bytes", ctx.client_ip or "?", ctx.vars.bytes or 0))
end
