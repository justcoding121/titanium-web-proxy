/*
 * Tagged binary codec shared with LuaCodec.cs. Every value starts with one tag byte:
 *   'n' nil   'f' false   't' true
 *   'i' int64 (8 bytes little endian)   'd' double (8 bytes little endian)
 *   's' u32 length + bytes
 *   'T' u32 pair count + that many (key, value) pairs
 * Encoding runs inside a protected call; the buffer belongs to the caller, so a raise never leaks it.
 */
#include "twp_internal.h"

#include <stdlib.h>
#include <string.h>

#define TWP_MAX_DEPTH 32

void twp_buf_put(lua_State *L, twp_buf *b, const void *p, size_t n)
{
    if (n > b->limit || b->len > b->limit - n) {
        luaL_error(L, "the result is larger than the %d byte command buffer", (int)b->limit);
    }
    if (b->len + n > b->cap) {
        size_t cap = b->cap == 0 ? 256 : b->cap;
        unsigned char *grown;
        while (cap < b->len + n) {
            cap *= 2;
        }
        grown = (unsigned char *)realloc(b->data, cap);
        if (grown == NULL) {
            luaL_error(L, "out of memory while building the command buffer");
        }
        b->data = grown;
        b->cap = cap;
    }
    memcpy(b->data + b->len, p, n);
    b->len += n;
}

void twp_buf_byte(lua_State *L, twp_buf *b, unsigned char c) { twp_buf_put(L, b, &c, 1); }

void twp_buf_u32(lua_State *L, twp_buf *b, uint32_t v)
{
    unsigned char x[4];
    x[0] = (unsigned char)v;
    x[1] = (unsigned char)(v >> 8);
    x[2] = (unsigned char)(v >> 16);
    x[3] = (unsigned char)(v >> 24);
    twp_buf_put(L, b, x, 4);
}

static void put_u64(lua_State *L, twp_buf *b, uint64_t v)
{
    unsigned char x[8];
    int i;
    for (i = 0; i < 8; i++) {
        x[i] = (unsigned char)(v >> (8 * i));
    }
    twp_buf_put(L, b, x, 8);
}

void twp_buf_str(lua_State *L, twp_buf *b, const char *s, size_t n)
{
    if (n > 0xFFFFFFFFu) {
        luaL_error(L, "string too long for the command buffer");
    }
    twp_buf_byte(L, b, 's');
    twp_buf_u32(L, b, (uint32_t)n);
    twp_buf_put(L, b, s, n);
}

void twp_encode(lua_State *L, int idx, twp_buf *b, int depth)
{
    idx = lua_absindex(L, idx);
    switch (lua_type(L, idx)) {
    case LUA_TNIL:
        twp_buf_byte(L, b, 'n');
        return;
    case LUA_TBOOLEAN:
        twp_buf_byte(L, b, lua_toboolean(L, idx) ? 't' : 'f');
        return;
    case LUA_TNUMBER:
        if (lua_isinteger(L, idx)) {
            twp_buf_byte(L, b, 'i');
            put_u64(L, b, (uint64_t)lua_tointeger(L, idx));
        } else {
            double d = lua_tonumber(L, idx);
            uint64_t bits;
            memcpy(&bits, &d, 8);
            twp_buf_byte(L, b, 'd');
            put_u64(L, b, bits);
        }
        return;
    case LUA_TSTRING: {
        size_t n;
        const char *s = lua_tolstring(L, idx, &n);
        twp_buf_str(L, b, s, n);
        return;
    }
    case LUA_TLIGHTUSERDATA:
        /* json.null and other sentinels cross as nil. */
        twp_buf_byte(L, b, 'n');
        return;
    case LUA_TTABLE: {
        size_t count_at;
        uint32_t count = 0;
        if (depth >= TWP_MAX_DEPTH) {
            luaL_error(L, "table nested deeper than %d levels (or a cycle) cannot be passed to the host", TWP_MAX_DEPTH);
        }
        luaL_checkstack(L, 4, "encoding a table");
        twp_buf_byte(L, b, 'T');
        count_at = b->len;
        twp_buf_u32(L, b, 0);
        lua_pushnil(L);
        while (lua_next(L, idx) != 0) {
            twp_encode(L, -2, b, depth + 1);
            twp_encode(L, -1, b, depth + 1);
            lua_pop(L, 1);
            count++;
        }
        b->data[count_at] = (unsigned char)count;
        b->data[count_at + 1] = (unsigned char)(count >> 8);
        b->data[count_at + 2] = (unsigned char)(count >> 16);
        b->data[count_at + 3] = (unsigned char)(count >> 24);
        return;
    }
    default:
        luaL_error(L, "a %s cannot be passed to the host", luaL_typename(L, idx));
    }
}

static uint32_t get_u32(const unsigned char *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static uint64_t get_u64(const unsigned char *p)
{
    uint64_t v = 0;
    int i;
    for (i = 7; i >= 0; i--) {
        v = (v << 8) | p[i];
    }
    return v;
}

size_t twp_decode(lua_State *L, const unsigned char *p, size_t len, int depth)
{
    if (len < 1) {
        luaL_error(L, "truncated host value");
    }
    if (depth >= TWP_MAX_DEPTH) {
        luaL_error(L, "host value nested too deeply");
    }
    luaL_checkstack(L, 4, "decoding a host value");
    switch (p[0]) {
    case 'n':
        lua_pushnil(L);
        return 1;
    case 'f':
        lua_pushboolean(L, 0);
        return 1;
    case 't':
        lua_pushboolean(L, 1);
        return 1;
    case 'i':
        if (len < 9) {
            luaL_error(L, "truncated host integer");
        }
        lua_pushinteger(L, (lua_Integer)get_u64(p + 1));
        return 9;
    case 'd': {
        uint64_t bits;
        double d;
        if (len < 9) {
            luaL_error(L, "truncated host number");
        }
        bits = get_u64(p + 1);
        memcpy(&d, &bits, 8);
        lua_pushnumber(L, d);
        return 9;
    }
    case 's': {
        uint32_t n;
        if (len < 5) {
            luaL_error(L, "truncated host string");
        }
        n = get_u32(p + 1);
        if ((size_t)n > len - 5) {
            luaL_error(L, "truncated host string");
        }
        lua_pushlstring(L, (const char *)p + 5, n);
        return 5 + (size_t)n;
    }
    case 'T': {
        uint32_t n, i;
        size_t at = 5;
        if (len < 5) {
            luaL_error(L, "truncated host table");
        }
        n = get_u32(p + 1);
        lua_createtable(L, 0, n > 64 ? 64 : (int)n);
        for (i = 0; i < n; i++) {
            at += twp_decode(L, p + at, len - at, depth + 1);
            at += twp_decode(L, p + at, len - at, depth + 1);
            if (lua_isnil(L, -2) || (lua_type(L, -2) == LUA_TNUMBER && lua_tonumber(L, -2) != lua_tonumber(L, -2))) {
                lua_pop(L, 2);
                continue;
            }
            lua_rawset(L, -3);
        }
        return at;
    }
    default:
        luaL_error(L, "unknown host value tag %d", (int)p[0]);
        return 0;
    }
}
