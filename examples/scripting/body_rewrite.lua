-- body_rewrite.lua: rewrite text in the origin's response body.
--
-- Replaces every occurrence of `settings.from` with `settings.to` in text and JSON responses. Set `bodyMode: buffered` on
-- the plugin so the script sees the whole body in one call; in the default streaming mode a match that straddles two pieces
-- would be missed.

local function plain_pattern(text)
  return (text:gsub("%p", "%%%0"))
end

local function plain_replacement(text)
  return (text:gsub("%%", "%%%%"))
end

function on_response_body(ctx)
  local from, to = settings.from, settings.to
  if from == nil or from == "" or to == nil then
    return
  end

  local content_type = ctx.headers["content-type"] or ""
  if not (content_type:find("^text/") or content_type:find("json", 1, true)) then
    return
  end

  local rewritten, count = ctx.body:gsub(plain_pattern(from), plain_replacement(to))
  if count > 0 then
    ctx.set_body(rewritten)
  end
end
