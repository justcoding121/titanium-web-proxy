-- jwt_check.lua: require a valid bearer token.
--
-- `jwt.verify` checks the signature against the key set in `settings.jwks` (RS/PS/ES/HS 256/384/512; `none` is always
-- refused) and the `exp`, `nbf`, `iss` and `aud` claims. The subject and scopes go upstream in headers the client cannot set.

function on_request_headers(ctx)
  -- Identity headers come from the token only.
  ctx.remove_header("x-jwt-sub")
  ctx.remove_header("x-jwt-scope")

  local authorization = ctx.get_header("authorization")
  local token = authorization and authorization:match("^[Bb]earer%s+(%S+)$")
  if token == nil then
    return ctx.deny(401, "bearer token required", { ["www-authenticate"] = "Bearer" })
  end

  local claims, err = jwt.verify(token, { iss = settings.issuer, aud = settings.audience })
  if claims == nil then
    log.info("token refused: " .. tostring(err))
    return ctx.deny(401, "invalid token", { ["www-authenticate"] = 'Bearer error="invalid_token"' })
  end

  if claims.sub ~= nil then
    ctx.set_header("x-jwt-sub", tostring(claims.sub))
  end
  if type(claims.scope) == "string" then
    ctx.set_header("x-jwt-scope", claims.scope)
  end
end
