-- request_logger.lua: write one structured line per finished request.
--
-- `on_log` runs after the response was sent, so it cannot change the outcome, and a failure in it is logged and ignored.
-- It runs only when the plugin is in the listener-wide `scriptPipeline`.

function on_log(ctx)
  local line = {
    method = ctx.method,
    host = ctx.host,
    path = ctx.path,
    status = ctx.status,
    route = ctx.route_id,
    cluster = ctx.cluster_id,
    client = ctx.client_ip,
    elapsed_ms = ctx.elapsed_ms,
  }
  if ctx.error ~= nil then
    line.error = ctx.error
  end

  log.info(json.encode(line))

  metrics.histogram("request_ms", ctx.elapsed_ms or 0, { route = ctx.route_id or "none" })
end
