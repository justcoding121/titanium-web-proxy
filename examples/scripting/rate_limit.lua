-- rate_limit.lua: allow each client IP a number of requests per window.
--
-- The counter lives in a shared dictionary, so every Lua state of every plugin in the process sees the same count, and it
-- survives a reload. The window starts at a client's first request and the entry expires with it (a fixed window).
--
-- Needs `settings.sharedDicts` to size the dictionary, for example "rate:100000".

local function number_setting(name, default)
  return tonumber(settings[name]) or default
end

function on_request_headers(ctx)
  local limit = number_setting("limit", 100)
  local window = number_setting("windowSeconds", 60)

  local count, err = shared.dict("rate"):incr(ctx.client_ip, 1, 0, window)
  if count == nil then
    -- The dictionary refused the write. Let the request through rather than fail every client.
    log.warn("rate counter failed: " .. tostring(err))
    return
  end

  if count > limit then
    metrics.counter("rate_limited", 1, { route = ctx.route_id or "none" })
    return ctx.deny(429, "rate limit exceeded", { ["retry-after"] = tostring(window) })
  end
end
