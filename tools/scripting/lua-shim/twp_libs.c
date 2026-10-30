/*
 * base64, hex, url, uuid, random, time, cidr, log, metrics, timer, http, and jwt.
 *
 * http.call, time.sleep, and jwt.verify do not call the host. They yield a pending-operation table; the host does
 * the work and resumes the coroutine with the answer, which becomes the function's return values.
 */
#include "twp_internal.h"

#include <string.h>

/* ---- base64 / hex ---------------------------------------------------------------------------------------------- */

static const char B64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
static const char B64URL[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

static int b64_encode(lua_State *L, const char *alpha, int pad)
{
    size_t n, i, o = 0;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    luaL_Buffer b;
    char *out;
    if (n > TWP_MAX_STRING_BUILD / 2) {
        return luaL_error(L, "base64: input too large");
    }
    twp_charge(L, (int64_t)(n / 16));
    out = luaL_buffinitsize(L, &b, (n + 2) / 3 * 4);
    for (i = 0; i + 2 < n; i += 3) {
        uint32_t v = ((uint32_t)s[i] << 16) | ((uint32_t)s[i + 1] << 8) | s[i + 2];
        out[o++] = alpha[(v >> 18) & 63];
        out[o++] = alpha[(v >> 12) & 63];
        out[o++] = alpha[(v >> 6) & 63];
        out[o++] = alpha[v & 63];
    }
    if (i < n) {
        uint32_t v = (uint32_t)s[i] << 16;
        if (i + 1 < n) {
            v |= (uint32_t)s[i + 1] << 8;
        }
        out[o++] = alpha[(v >> 18) & 63];
        out[o++] = alpha[(v >> 12) & 63];
        if (i + 1 < n) {
            out[o++] = alpha[(v >> 6) & 63];
        } else if (pad) {
            out[o++] = '=';
        }
        if (pad) {
            out[o++] = '=';
        }
    }
    luaL_pushresultsize(&b, o);
    return 1;
}

static int b64_val(unsigned char c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+' || c == '-') return 62;
    if (c == '/' || c == '_') return 63;
    return -1;
}

/* Accepts the standard and URL alphabets, with or without padding. Returns nil, err on bad input. */
static int b64_decode(lua_State *L)
{
    size_t n, i, o = 0;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    luaL_Buffer b;
    char *out;
    uint32_t acc = 0;
    int bits = 0;
    twp_charge(L, (int64_t)(n / 16));
    while (n > 0 && s[n - 1] == '=') {
        n--;
    }
    out = luaL_buffinitsize(L, &b, n / 4 * 3 + 3);
    for (i = 0; i < n; i++) {
        int v = b64_val(s[i]);
        if (v < 0) {
            luaL_pushresultsize(&b, 0);
            lua_pop(L, 1);
            luaL_pushfail(L);
            lua_pushliteral(L, "invalid base64");
            return 2;
        }
        acc = (acc << 6) | (uint32_t)v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out[o++] = (char)((acc >> bits) & 0xFF);
        }
    }
    if (bits >= 6) {
        luaL_pushresultsize(&b, 0);
        lua_pop(L, 1);
        luaL_pushfail(L);
        lua_pushliteral(L, "invalid base64 length");
        return 2;
    }
    luaL_pushresultsize(&b, o);
    return 1;
}

static int l_b64_encode(lua_State *L) { return b64_encode(L, B64, 1); }
static int l_b64_encode_url(lua_State *L) { return b64_encode(L, B64URL, 0); }

static int l_hex_encode(lua_State *L)
{
    static const char hexd[] = "0123456789abcdef";
    size_t n, i;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    luaL_Buffer b;
    char *out;
    if (n > TWP_MAX_STRING_BUILD / 2) {
        return luaL_error(L, "hex: input too large");
    }
    twp_charge(L, (int64_t)(n / 16));
    out = luaL_buffinitsize(L, &b, n * 2);
    for (i = 0; i < n; i++) {
        out[i * 2] = hexd[s[i] >> 4];
        out[i * 2 + 1] = hexd[s[i] & 15];
    }
    luaL_pushresultsize(&b, n * 2);
    return 1;
}

static int hexv(unsigned char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static int l_hex_decode(lua_State *L)
{
    size_t n, i;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    luaL_Buffer b;
    char *out;
    if (n % 2 != 0) {
        luaL_pushfail(L);
        lua_pushliteral(L, "odd-length hex");
        return 2;
    }
    twp_charge(L, (int64_t)(n / 16));
    out = luaL_buffinitsize(L, &b, n / 2);
    for (i = 0; i < n; i += 2) {
        int hi = hexv(s[i]), lo = hexv(s[i + 1]);
        if (hi < 0 || lo < 0) {
            luaL_pushresultsize(&b, 0);
            lua_pop(L, 1);
            luaL_pushfail(L);
            lua_pushliteral(L, "invalid hex");
            return 2;
        }
        out[i / 2] = (char)((hi << 4) | lo);
    }
    luaL_pushresultsize(&b, n / 2);
    return 1;
}

/* ---- url ------------------------------------------------------------------------------------------------------- */

static int unreserved(unsigned char c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '.' ||
        c == '_' || c == '~';
}

static void url_encode_into(luaL_Buffer *b, const unsigned char *s, size_t n)
{
    static const char hexd[] = "0123456789ABCDEF";
    size_t i;
    for (i = 0; i < n; i++) {
        if (unreserved(s[i])) {
            luaL_addchar(b, (char)s[i]);
        } else {
            luaL_addchar(b, '%');
            luaL_addchar(b, hexd[s[i] >> 4]);
            luaL_addchar(b, hexd[s[i] & 15]);
        }
    }
}

static int l_url_encode(lua_State *L)
{
    size_t n;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    luaL_Buffer b;
    twp_charge(L, (int64_t)(n / 16));
    luaL_buffinit(L, &b);
    url_encode_into(&b, s, n);
    luaL_pushresult(&b);
    return 1;
}

/* Pushes the percent-decoded s. plus_space also turns '+' into a space (form encoding). */
static void url_decode_push(lua_State *L, const unsigned char *s, size_t n, int plus_space)
{
    luaL_Buffer b;
    size_t i, o = 0;
    char *out = luaL_buffinitsize(L, &b, n);
    for (i = 0; i < n; i++) {
        if (s[i] == '%' && i + 2 < n && hexv(s[i + 1]) >= 0 && hexv(s[i + 2]) >= 0) {
            out[o++] = (char)((hexv(s[i + 1]) << 4) | hexv(s[i + 2]));
            i += 2;
        } else if (s[i] == '+' && plus_space) {
            out[o++] = ' ';
        } else {
            out[o++] = (char)s[i];
        }
    }
    luaL_pushresultsize(&b, o);
}

static int l_url_decode(lua_State *L)
{
    size_t n;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    twp_charge(L, (int64_t)(n / 16));
    url_decode_push(L, s, n, lua_toboolean(L, 2));
    return 1;
}

/* url.parse_query("a=1&b=x+y") -> { a = "1", b = "x y" }. A repeated key keeps its first value. */
static int l_url_parse_query(lua_State *L)
{
    size_t n, i = 0;
    const unsigned char *s = (const unsigned char *)luaL_checklstring(L, 1, &n);
    int count = 0;
    twp_charge(L, (int64_t)(n / 16));
    if (n > 0 && s[0] == '?') {
        i = 1;
    }
    lua_newtable(L);
    while (i < n) {
        size_t start = i, eq = (size_t)-1;
        while (i < n && s[i] != '&') {
            if (s[i] == '=' && eq == (size_t)-1) {
                eq = i;
            }
            i++;
        }
        if (i > start) {
            size_t kend = eq == (size_t)-1 ? i : eq;
            if (++count > 1024) {
                return luaL_error(L, "url.parse_query: more than 1024 parameters");
            }
            url_decode_push(L, s + start, kend - start, 1);
            lua_pushvalue(L, -1);
            if (lua_rawget(L, -3) == LUA_TNIL) {
                lua_pop(L, 1);
                if (eq == (size_t)-1) {
                    lua_pushliteral(L, "");
                } else {
                    url_decode_push(L, s + eq + 1, i - eq - 1, 1);
                }
                lua_rawset(L, -3);
            } else {
                lua_pop(L, 2);
            }
        }
        i++;
    }
    return 1;
}

/* url.build_query({ a = "1", b = "x y" }) -> "a=1&b=x%20y" in key order. */
static int l_url_build_query(lua_State *L)
{
    luaL_Buffer b;
    int first = 1, i, count = 0;
    const char *keys[1024];
    size_t klen[1024];
    const char *vals[1024];
    size_t vlen[1024];
    luaL_checktype(L, 1, LUA_TTABLE);
    lua_settop(L, 1);
    lua_pushnil(L);
    while (lua_next(L, 1) != 0) {
        lua_pop(L, 1);
        if (lua_type(L, -1) != LUA_TSTRING) {
            return luaL_error(L, "url.build_query: keys must be strings");
        }
        if (count >= 1024) {
            return luaL_error(L, "url.build_query: more than 1024 parameters");
        }
        keys[count] = lua_tolstring(L, -1, &klen[count]);
        count++;
    }
    /* Insertion sort by key so the output is stable; the strings stay alive in the table. */
    for (i = 1; i < count; i++) {
        const char *k = keys[i];
        size_t kl = klen[i];
        int j = i - 1;
        while (j >= 0) {
            size_t m = klen[j] < kl ? klen[j] : kl;
            int c = memcmp(keys[j], k, m);
            if (c < 0 || (c == 0 && klen[j] <= kl)) {
                break;
            }
            keys[j + 1] = keys[j];
            klen[j + 1] = klen[j];
            j--;
        }
        keys[j + 1] = k;
        klen[j + 1] = kl;
    }
    /* Values as strings in a table at index 2, so the pointers stay valid while the buffer is in use. */
    lua_createtable(L, count, 0);
    for (i = 0; i < count; i++) {
        lua_pushlstring(L, keys[i], klen[i]);
        lua_rawget(L, 1);
        if (!lua_isstring(L, -1)) {
            return luaL_error(L, "url.build_query: value of '%s' is not a string or number", keys[i]);
        }
        lua_tolstring(L, -1, NULL);
        lua_rawseti(L, 2, i + 1);
    }
    for (i = 0; i < count; i++) {
        lua_rawgeti(L, 2, i + 1);
        vals[i] = lua_tolstring(L, -1, &vlen[i]);
        lua_pop(L, 1);
    }
    luaL_buffinit(L, &b);
    for (i = 0; i < count; i++) {
        size_t vn = vlen[i];
        const char *v = vals[i];
        if (!first) {
            luaL_addchar(&b, '&');
        }
        first = 0;
        url_encode_into(&b, (const unsigned char *)keys[i], klen[i]);
        luaL_addchar(&b, '=');
        url_encode_into(&b, (const unsigned char *)v, vn);
    }
    luaL_pushresult(&b);
    return 1;
}

/* ---- uuid / random / time -------------------------------------------------------------------------------------- */

static void fill_random(lua_State *L, void *buf, size_t n)
{
    if (twp_random_bytes(buf, n) != 0) {
        luaL_error(L, "the system random source is not available");
    }
}

static int l_uuid_v4(lua_State *L)
{
    static const char hexd[] = "0123456789abcdef";
    unsigned char r[16];
    char out[36];
    int i, o = 0;
    fill_random(L, r, 16);
    r[6] = (unsigned char)((r[6] & 0x0F) | 0x40);
    r[8] = (unsigned char)((r[8] & 0x3F) | 0x80);
    for (i = 0; i < 16; i++) {
        if (i == 4 || i == 6 || i == 8 || i == 10) {
            out[o++] = '-';
        }
        out[o++] = hexd[r[i] >> 4];
        out[o++] = hexd[r[i] & 15];
    }
    lua_pushlstring(L, out, 36);
    return 1;
}

static int l_random_bytes(lua_State *L)
{
    lua_Integer n = luaL_checkinteger(L, 1);
    luaL_Buffer b;
    char *out;
    if (n < 0 || n > 65536) {
        return luaL_argerror(L, 1, "count must be 0 to 65536");
    }
    out = luaL_buffinitsize(L, &b, (size_t)n);
    if (n > 0) {
        fill_random(L, out, (size_t)n);
    }
    luaL_pushresultsize(&b, (size_t)n);
    return 1;
}

/* random.int(m, n): uniform in [m, n] from the system CSPRNG, by rejection. */
static int l_random_int(lua_State *L)
{
    lua_Integer lo = luaL_checkinteger(L, 1);
    lua_Integer hi = luaL_checkinteger(L, 2);
    uint64_t span, limit, v;
    luaL_argcheck(L, lo <= hi, 2, "interval is empty");
    span = (uint64_t)hi - (uint64_t)lo;
    if (span == UINT64_MAX) {
        fill_random(L, &v, sizeof(v));
        lua_pushinteger(L, (lua_Integer)v);
        return 1;
    }
    span += 1;
    limit = UINT64_MAX - (UINT64_MAX % span);
    do {
        fill_random(L, &v, sizeof(v));
    } while (v >= limit);
    lua_pushinteger(L, (lua_Integer)((uint64_t)lo + v % span));
    return 1;
}

static int l_time_now(lua_State *L)
{
    lua_pushnumber(L, (lua_Number)twp_unix_ms() / 1000.0);
    return 1;
}

static int l_time_now_ms(lua_State *L)
{
    lua_pushinteger(L, twp_unix_ms());
    return 1;
}

static int l_time_monotonic_ms(lua_State *L)
{
    lua_pushinteger(L, twp_mono_us() / 1000);
    return 1;
}

static int l_time_sleep(lua_State *L)
{
    lua_Integer ms = luaL_checkinteger(L, 1);
    luaL_argcheck(L, ms >= 0 && ms <= 60000, 1, "sleep must be 0 to 60000 ms");
    twp_check_can_wait(L, "time.sleep");
    lua_createtable(L, 0, 2);
    lua_pushliteral(L, "sleep");
    lua_setfield(L, -2, "op");
    lua_pushinteger(L, ms);
    lua_setfield(L, -2, "ms");
    return lua_yield(L, 1);
}

/* ---- cidr ------------------------------------------------------------------------------------------------------ */

/* Parses dotted IPv4 into 4 bytes. Returns 1 on success. */
static int parse_v4(const char *s, size_t n, unsigned char out[4])
{
    size_t i = 0;
    int part;
    for (part = 0; part < 4; part++) {
        unsigned v = 0;
        size_t digits = 0;
        while (i < n && s[i] >= '0' && s[i] <= '9') {
            v = v * 10 + (unsigned)(s[i] - '0');
            if (++digits > 3 || v > 255) {
                return 0;
            }
            i++;
        }
        if (digits == 0) {
            return 0;
        }
        out[part] = (unsigned char)v;
        if (part < 3) {
            if (i >= n || s[i] != '.') {
                return 0;
            }
            i++;
        }
    }
    return i == n;
}

/* Parses IPv6 (with :: and an optional trailing dotted IPv4) into 16 bytes. Returns 1 on success. */
static int parse_v6(const char *s, size_t n, unsigned char out[16])
{
    unsigned char buf[16];
    int idx = 0, gap = -1;
    size_t i = 0;
    memset(buf, 0, 16);
    if (n >= 2 && s[0] == ':' && s[1] == ':') {
        gap = 0;
        i = 2;
        if (i == n) {
            memset(out, 0, 16);
            return 1;
        }
    } else if (n >= 1 && s[0] == ':') {
        return 0;
    }
    while (i < n) {
        size_t start = i, digits = 0;
        unsigned v = 0;
        int h;
        while (i < n && (h = hexv((unsigned char)s[i])) >= 0) {
            v = (v << 4) | (unsigned)h;
            if (++digits > 4) {
                return 0;
            }
            i++;
        }
        if (i < n && s[i] == '.') {
            if (idx > 12 || !parse_v4(s + start, n - start, buf + idx)) {
                return 0;
            }
            idx += 4;
            i = n;
            break;
        }
        if (digits == 0 || idx > 14) {
            return 0;
        }
        buf[idx++] = (unsigned char)(v >> 8);
        buf[idx++] = (unsigned char)v;
        if (i == n) {
            break;
        }
        if (s[i] != ':') {
            return 0;
        }
        i++;
        if (i < n && s[i] == ':') {
            if (gap >= 0) {
                return 0;
            }
            gap = idx;
            i++;
            if (i == n) {
                break;
            }
        } else if (i == n) {
            return 0;
        }
    }
    if (gap >= 0) {
        int tail = idx - gap;
        if (idx == 16) {
            return 0;
        }
        memset(out, 0, 16);
        memcpy(out, buf, (size_t)gap);
        memcpy(out + 16 - tail, buf + gap, (size_t)tail);
        return 1;
    }
    if (idx != 16) {
        return 0;
    }
    memcpy(out, buf, 16);
    return 1;
}

/* Parses an address into 16 bytes; IPv4 and IPv4-mapped IPv6 become ::ffff:a.b.c.d. Sets *v4 for those. */
static int parse_ip(const char *s, size_t n, unsigned char out[16], int *v4)
{
    static const unsigned char mapped[12] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF};
    unsigned char a4[4];
    if (n > 0 && s[0] == '[' && s[n - 1] == ']') {
        s++;
        n -= 2;
    }
    if (parse_v4(s, n, a4)) {
        memcpy(out, mapped, 12);
        memcpy(out + 12, a4, 4);
        *v4 = 1;
        return 1;
    }
    if (n > 64 || !parse_v6(s, n, out)) {
        return 0;
    }
    *v4 = memcmp(out, mapped, 12) == 0;
    return 1;
}

static int cidr_contains(const char *net, size_t nn, const unsigned char ip[16], int ip_v4, int *ok)
{
    const char *slash = (const char *)memchr(net, '/', nn);
    unsigned char addr[16];
    int is_v4, prefix, i, full, rem;
    size_t alen = slash ? (size_t)(slash - net) : nn;
    *ok = 0;
    if (!parse_ip(net, alen, addr, &is_v4)) {
        return 0;
    }
    if (slash) {
        size_t j;
        prefix = 0;
        if ((size_t)(slash - net) + 1 >= nn) {
            return 0;
        }
        for (j = alen + 1; j < nn; j++) {
            if (net[j] < '0' || net[j] > '9' || prefix > 128) {
                return 0;
            }
            prefix = prefix * 10 + (net[j] - '0');
        }
        if (prefix > (is_v4 ? 32 : 128)) {
            return 0;
        }
    } else {
        prefix = is_v4 ? 32 : 128;
    }
    *ok = 1;
    if (is_v4) {
        if (!ip_v4) {
            return 0;
        }
        prefix += 96;
    }
    full = prefix / 8;
    rem = prefix % 8;
    for (i = 0; i < full; i++) {
        if (addr[i] != ip[i]) {
            return 0;
        }
    }
    if (rem) {
        unsigned char mask = (unsigned char)(0xFF << (8 - rem));
        if ((addr[full] & mask) != (ip[full] & mask)) {
            return 0;
        }
    }
    return 1;
}

/* cidr.match(ip, "10.0.0.0/8" | { "10.0.0.0/8", "::1" }) -> boolean. A malformed network raises. */
static int l_cidr_match(lua_State *L)
{
    size_t n, nn;
    const char *s = luaL_checklstring(L, 1, &n);
    unsigned char ip[16];
    int v4, ok;
    if (!parse_ip(s, n, ip, &v4)) {
        lua_pushboolean(L, 0);
        return 1;
    }
    if (lua_type(L, 2) == LUA_TTABLE) {
        lua_Integer i, len = (lua_Integer)lua_rawlen(L, 2);
        if (len > 4096) {
            return luaL_argerror(L, 2, "at most 4096 networks");
        }
        twp_charge(L, len);
        for (i = 1; i <= len; i++) {
            const char *net;
            lua_rawgeti(L, 2, i);
            net = lua_tolstring(L, -1, &nn);
            if (net == NULL) {
                return luaL_argerror(L, 2, "networks must be strings");
            }
            if (cidr_contains(net, nn, ip, v4, &ok)) {
                lua_pushboolean(L, 1);
                return 1;
            }
            if (!ok) {
                return luaL_error(L, "cidr.match: '%s' is not a network", net);
            }
            lua_pop(L, 1);
        }
        lua_pushboolean(L, 0);
        return 1;
    } else {
        const char *net = luaL_checklstring(L, 2, &nn);
        int hit = cidr_contains(net, nn, ip, v4, &ok);
        if (!ok) {
            return luaL_error(L, "cidr.match: '%s' is not a network", net);
        }
        lua_pushboolean(L, hit);
        return 1;
    }
}

/* cidr.valid(ip) -> boolean */
static int l_cidr_valid_ip(lua_State *L)
{
    size_t n;
    const char *s = luaL_checklstring(L, 1, &n);
    unsigned char ip[16];
    int v4;
    lua_pushboolean(L, parse_ip(s, n, ip, &v4));
    return 1;
}

/* ---- log / metrics / timer ------------------------------------------------------------------------------------- */

#define TWP_LOG_MAX 4096

static int log_at(lua_State *L, const char *level)
{
    int i, n = lua_gettop(L);
    luaL_Buffer b;
    luaL_buffinit(L, &b);
    for (i = 1; i <= n; i++) {
        if (i > 1) {
            luaL_addchar(&b, ' ');
        }
        luaL_tolstring(L, i, NULL);
        luaL_addvalue(&b);
    }
    luaL_pushresult(&b);
    if (lua_rawlen(L, -1) > TWP_LOG_MAX) {
        const char *s = lua_tostring(L, -1);
        lua_pushlstring(L, s, TWP_LOG_MAX);
        lua_remove(L, -2);
    }
    lua_pushstring(L, "log");
    lua_insert(L, -2);
    lua_pushstring(L, level);
    lua_insert(L, -2);
    twp_emit(L, 3);
    return 0;
}

static int l_log_debug(lua_State *L) { return log_at(L, "debug"); }
static int l_log_info(lua_State *L) { return log_at(L, "info"); }
static int l_log_warn(lua_State *L) { return log_at(L, "warn"); }
static int l_log_error(lua_State *L) { return log_at(L, "error"); }

static void check_metric_name(lua_State *L, int arg)
{
    size_t n, i;
    const char *s = luaL_checklstring(L, arg, &n);
    if (n == 0 || n > 64) {
        luaL_argerror(L, arg, "metric name must be 1 to 64 characters");
    }
    for (i = 0; i < n; i++) {
        char c = s[i];
        int ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || (i > 0 && c >= '0' && c <= '9');
        if (!ok) {
            luaL_argerror(L, arg, "metric name must match [A-Za-z_][A-Za-z0-9_]*");
        }
    }
}

static void check_labels(lua_State *L, int arg)
{
    int count = 0;
    if (lua_isnoneornil(L, arg)) {
        return;
    }
    luaL_checktype(L, arg, LUA_TTABLE);
    lua_pushnil(L);
    while (lua_next(L, arg) != 0) {
        if (lua_type(L, -2) != LUA_TSTRING || lua_type(L, -1) != LUA_TSTRING) {
            luaL_argerror(L, arg, "labels must map strings to strings");
        }
        if (lua_rawlen(L, -2) > 64 || lua_rawlen(L, -1) > 256) {
            luaL_argerror(L, arg, "label names are at most 64 and values at most 256 bytes");
        }
        lua_pop(L, 1);
        if (++count > 8) {
            luaL_argerror(L, arg, "at most 8 labels");
        }
    }
}

static int metric(lua_State *L, const char *kind, int value_required)
{
    lua_Number v;
    check_metric_name(L, 1);
    v = value_required ? luaL_checknumber(L, 2) : luaL_optnumber(L, 2, 1);
    check_labels(L, 3);
    lua_settop(L, 3);
    lua_pushliteral(L, "metric");
    lua_pushstring(L, kind);
    lua_pushvalue(L, 1);
    lua_pushnumber(L, v);
    lua_pushvalue(L, 3);
    twp_emit(L, 5);
    return 0;
}

static int l_metric_counter(lua_State *L) { return metric(L, "counter", 0); }
static int l_metric_gauge(lua_State *L) { return metric(L, "gauge", 1); }
static int l_metric_histogram(lua_State *L) { return metric(L, "histogram", 1); }

static int timer(lua_State *L, const char *kind, lua_Integer min_ms)
{
    lua_Integer ms = luaL_checkinteger(L, 1);
    size_t n;
    luaL_argcheck(L, ms >= min_ms && ms <= 86400000, 1, "delay out of range");
    luaL_checklstring(L, 2, &n);
    luaL_argcheck(L, n > 0 && n <= 128, 2, "timer function name must be 1 to 128 characters");
    lua_settop(L, 3);
    lua_pushliteral(L, "timer");
    lua_pushstring(L, kind);
    lua_pushinteger(L, ms);
    lua_pushvalue(L, 2);
    lua_pushvalue(L, 3);
    twp_emit(L, 5);
    return 0;
}

static int l_timer_at(lua_State *L) { return timer(L, "at", 0); }
static int l_timer_every(lua_State *L) { return timer(L, "every", 50); }

/* ---- http / jwt ------------------------------------------------------------------------------------------------ */

/* http.call(url [, opts]) or http.call(opts). Returns { status, headers, body } or nil, err. */
static int l_http_call(lua_State *L)
{
    int opts;
    size_t n;
    if (lua_type(L, 1) == LUA_TSTRING) {
        if (lua_isnoneornil(L, 2)) {
            lua_settop(L, 1);
            lua_newtable(L);
        }
        luaL_checktype(L, 2, LUA_TTABLE);
        lua_pushvalue(L, 1);
        lua_setfield(L, 2, "url");
        opts = 2;
    } else {
        luaL_checktype(L, 1, LUA_TTABLE);
        opts = 1;
    }
    lua_createtable(L, 0, 7);
    lua_pushliteral(L, "http");
    lua_setfield(L, -2, "op");

    if (lua_getfield(L, opts, "url") != LUA_TSTRING) {
        return luaL_error(L, "http.call: url must be a string");
    }
    lua_tolstring(L, -1, &n);
    if (n > 8192) {
        return luaL_error(L, "http.call: url is longer than 8192 bytes");
    }
    lua_setfield(L, -2, "url");

    if (lua_getfield(L, opts, "method") == LUA_TNIL) {
        lua_pop(L, 1);
        lua_pushliteral(L, "GET");
    } else if (lua_type(L, -1) != LUA_TSTRING || lua_rawlen(L, -1) > 16) {
        return luaL_error(L, "http.call: method must be a short string");
    }
    lua_setfield(L, -2, "method");

    switch (lua_getfield(L, opts, "body")) {
    case LUA_TNIL:
    case LUA_TSTRING:
        break;
    default:
        return luaL_error(L, "http.call: body must be a string");
    }
    lua_setfield(L, -2, "body");

    switch (lua_getfield(L, opts, "timeout_ms")) {
    case LUA_TNIL:
        break;
    case LUA_TNUMBER: {
        lua_Integer t = lua_tointeger(L, -1);
        if (t < 1 || t > 60000) {
            return luaL_error(L, "http.call: timeout_ms must be 1 to 60000");
        }
        break;
    }
    default:
        return luaL_error(L, "http.call: timeout_ms must be a number");
    }
    lua_setfield(L, -2, "timeout_ms");

    if (lua_getfield(L, opts, "headers") == LUA_TTABLE) {
        int count = 0;
        lua_newtable(L);
        lua_pushnil(L);
        while (lua_next(L, -3) != 0) {
            if (lua_type(L, -2) != LUA_TSTRING || lua_type(L, -1) != LUA_TSTRING) {
                return luaL_error(L, "http.call: headers must map strings to strings");
            }
            if (++count > 64) {
                return luaL_error(L, "http.call: at most 64 headers");
            }
            lua_pushvalue(L, -2);
            lua_insert(L, -2);
            lua_rawset(L, -4);
        }
        lua_remove(L, -2);
    } else if (!lua_isnil(L, -1)) {
        return luaL_error(L, "http.call: headers must be a table");
    }
    lua_setfield(L, -2, "headers");

    twp_check_can_wait(L, "http.call");
    return lua_yield(L, 1);
}

/* jwt.verify(token [, { iss = ..., aud = ... }]) -> claims table, or nil, err. Keys come from settings.jwks. */
static int l_jwt_verify(lua_State *L)
{
    size_t n;
    luaL_checklstring(L, 1, &n);
    if (n > 16384) {
        return luaL_argerror(L, 1, "token is longer than 16384 bytes");
    }
    lua_createtable(L, 0, 4);
    lua_pushliteral(L, "jwt");
    lua_setfield(L, -2, "op");
    lua_pushvalue(L, 1);
    lua_setfield(L, -2, "token");
    if (lua_type(L, 2) == LUA_TTABLE) {
        if (lua_getfield(L, 2, "iss") == LUA_TSTRING) {
            lua_setfield(L, -2, "iss");
        } else {
            lua_pop(L, 1);
        }
        if (lua_getfield(L, 2, "aud") == LUA_TSTRING) {
            lua_setfield(L, -2, "aud");
        } else {
            lua_pop(L, 1);
        }
    }
    twp_check_can_wait(L, "jwt.verify");
    return lua_yield(L, 1);
}

static void newlib(lua_State *L, const char *name, const luaL_Reg *fns)
{
    lua_newtable(L);
    luaL_setfuncs(L, fns, 0);
    lua_setglobal(L, name);
}

void twp_open_libs(lua_State *L)
{
    static const luaL_Reg base64[] = {
        {"encode", l_b64_encode}, {"encode_url", l_b64_encode_url}, {"decode", b64_decode}, {"decode_url", b64_decode},
        {NULL, NULL}};
    static const luaL_Reg hex[] = {{"encode", l_hex_encode}, {"decode", l_hex_decode}, {NULL, NULL}};
    static const luaL_Reg url[] = {{"encode", l_url_encode}, {"decode", l_url_decode},
        {"parse_query", l_url_parse_query}, {"build_query", l_url_build_query}, {NULL, NULL}};
    static const luaL_Reg uuid[] = {{"v4", l_uuid_v4}, {NULL, NULL}};
    static const luaL_Reg random[] = {{"bytes", l_random_bytes}, {"int", l_random_int}, {NULL, NULL}};
    static const luaL_Reg time[] = {{"now", l_time_now}, {"now_ms", l_time_now_ms},
        {"monotonic_ms", l_time_monotonic_ms}, {"sleep", l_time_sleep}, {NULL, NULL}};
    static const luaL_Reg cidr[] = {{"match", l_cidr_match}, {"valid", l_cidr_valid_ip}, {NULL, NULL}};
    static const luaL_Reg log[] = {{"debug", l_log_debug}, {"info", l_log_info}, {"warn", l_log_warn},
        {"error", l_log_error}, {NULL, NULL}};
    static const luaL_Reg metrics[] = {{"counter", l_metric_counter}, {"gauge", l_metric_gauge},
        {"histogram", l_metric_histogram}, {NULL, NULL}};
    static const luaL_Reg timer[] = {{"at", l_timer_at}, {"every", l_timer_every}, {NULL, NULL}};
    static const luaL_Reg http[] = {{"call", l_http_call}, {NULL, NULL}};
    static const luaL_Reg jwt[] = {{"verify", l_jwt_verify}, {NULL, NULL}};

    newlib(L, "base64", base64);
    newlib(L, "hex", hex);
    newlib(L, "url", url);
    newlib(L, "uuid", uuid);
    newlib(L, "random", random);
    newlib(L, "time", time);
    newlib(L, "cidr", cidr);
    newlib(L, "log", log);
    newlib(L, "metrics", metrics);
    newlib(L, "timer", timer);
    newlib(L, "http", http);
    newlib(L, "jwt", jwt);

    lua_pushcfunction(L, l_log_info);
    lua_setglobal(L, "print");
}
