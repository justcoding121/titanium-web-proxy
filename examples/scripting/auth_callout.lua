-- auth_callout.lua: ask an authorization service whether a request may pass.
--
-- The script forwards the client's Authorization header to `settings.authUrl`. Any 2xx answer lets the request through,
-- 401 and 403 answers are passed on to the client, and anything else (a timeout, a refused callout, a 5xx) denies with 503,
-- so a broken auth service never opens a route.
--
-- Needs `calloutHosts` to name the auth service, or the callout is refused.

local function number_setting(name, default)
  return tonumber(settings[name]) or default
end

function on_request_headers(ctx)
  -- Never trust identity headers a client sends; only the auth service may set them.
  ctx.remove_header("x-auth-user")

  local credentials = ctx.get_header("authorization")
  if credentials == nil then
    return ctx.deny(401, "credentials required", { ["www-authenticate"] = "Bearer" })
  end

  local answer, err = http.call(settings.authUrl, {
    method = "GET",
    timeout_ms = number_setting("timeoutMs", 300),
    headers = {
      authorization = credentials,
      ["x-original-method"] = ctx.method,
      ["x-original-path"] = ctx.path,
      ["x-original-host"] = ctx.host,
    },
  })

  if answer == nil then
    log.warn("auth callout failed: " .. tostring(err))
    metrics.counter("auth_callout_errors", 1, { route = ctx.route_id or "none" })
    return ctx.deny(503, "authorization service unavailable")
  end

  if answer.status == 401 or answer.status == 403 then
    return ctx.deny(answer.status, "denied")
  end

  if answer.status < 200 or answer.status >= 300 then
    log.warn("auth service answered " .. answer.status)
    return ctx.deny(503, "authorization service unavailable")
  end

  local user = answer.headers["x-auth-user"]
  if user ~= nil then
    ctx.set_header("x-auth-user", user)
  end
end
