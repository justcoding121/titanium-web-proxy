/*
 * json.encode / json.decode / json.null. A table whose keys are exactly 1..n (n > 0) encodes as an array; any
 * other table encodes as an object with string keys. json.null round-trips JSON null inside arrays and objects.
 */
#include "twp_internal.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

#define JSON_MAX_DEPTH 64

/*
 * Output buffer backed by a userdata at a fixed stack slot. luaL_Buffer expects its box at the stack top, which the
 * recursive encoder cannot promise; this buffer is replaced in place when it grows, wherever the top is.
 */
typedef struct jbuf {
    lua_State *L;
    char *p;
    size_t n;
    size_t cap;
    int slot;
} jbuf;

static void jb_init(lua_State *L, jbuf *b)
{
    b->L = L;
    b->n = 0;
    b->cap = 256;
    b->p = (char *)lua_newuserdatauv(L, b->cap, 0);
    b->slot = lua_gettop(L);
}

static void jb_put(jbuf *b, const char *s, size_t n)
{
    if (b->n + n > b->cap) {
        size_t cap = b->cap;
        char *grown;
        if (b->n + n > TWP_MAX_STRING_BUILD) {
            luaL_error(b->L, "json: result is larger than %d bytes", TWP_MAX_STRING_BUILD);
        }
        while (cap < b->n + n) {
            cap *= 2;
        }
        grown = (char *)lua_newuserdatauv(b->L, cap, 0);
        memcpy(grown, b->p, b->n);
        lua_replace(b->L, b->slot);
        b->p = grown;
        b->cap = cap;
    }
    memcpy(b->p + b->n, s, n);
    b->n += n;
}

static void jb_char(jbuf *b, char c) { jb_put(b, &c, 1); }
static void jb_str(jbuf *b, const char *s) { jb_put(b, s, strlen(s)); }

/* Pushes the result and drops the backing userdata; the string takes the slot's place. */
static void jb_push(jbuf *b)
{
    lua_pushlstring(b->L, b->p, b->n);
    lua_remove(b->L, b->slot);
}

static void json_encode_value(lua_State *L, jbuf *b, int idx, int depth);

static void json_string(jbuf *b, const char *s, size_t n)
{
    static const char hexd[] = "0123456789abcdef";
    size_t i;
    jb_char(b, '"');
    for (i = 0; i < n; i++) {
        unsigned char c = (unsigned char)s[i];
        switch (c) {
        case '"': jb_str(b, "\\\""); break;
        case '\\': jb_str(b, "\\\\"); break;
        case '\b': jb_str(b, "\\b"); break;
        case '\f': jb_str(b, "\\f"); break;
        case '\n': jb_str(b, "\\n"); break;
        case '\r': jb_str(b, "\\r"); break;
        case '\t': jb_str(b, "\\t"); break;
        default:
            if (c < 0x20) {
                char esc[6] = {'\\', 'u', '0', '0', hexd[c >> 4], hexd[c & 15]};
                jb_put(b, esc, 6);
            } else {
                jb_char(b, (char)c);
            }
        }
    }
    jb_char(b, '"');
}

/* Returns n > 0 when the table at idx is a sequence 1..n, else 0. */
static lua_Integer array_length(lua_State *L, int idx)
{
    lua_Integer n = 0, max = 0;
    lua_pushnil(L);
    while (lua_next(L, idx) != 0) {
        lua_pop(L, 1);
        if (lua_type(L, -1) == LUA_TNUMBER && lua_isinteger(L, -1)) {
            lua_Integer k = lua_tointeger(L, -1);
            if (k >= 1) {
                n++;
                if (k > max) {
                    max = k;
                }
                continue;
            }
        }
        lua_pop(L, 1);
        return 0;
    }
    return n == max ? n : 0;
}

static void json_encode_value(lua_State *L, jbuf *b, int idx, int depth)
{
    char num[64];
    idx = lua_absindex(L, idx);
    switch (lua_type(L, idx)) {
    case LUA_TNIL:
        jb_str(b, "null");
        return;
    case LUA_TLIGHTUSERDATA:
        if (lua_touserdata(L, idx) == NULL) {
            jb_str(b, "null");
            return;
        }
        break;
    case LUA_TBOOLEAN:
        jb_str(b, lua_toboolean(L, idx) ? "true" : "false");
        return;
    case LUA_TNUMBER:
        if (lua_isinteger(L, idx)) {
            snprintf(num, sizeof(num), LUA_INTEGER_FMT, (LUAI_UACINT)lua_tointeger(L, idx));
        } else {
            double d = lua_tonumber(L, idx);
            if (d != d || d == HUGE_VAL || d == -HUGE_VAL) {
                luaL_error(L, "json.encode: NaN and infinity have no JSON form");
            }
            snprintf(num, sizeof(num), "%.17g", d);
        }
        jb_str(b, num);
        return;
    case LUA_TSTRING: {
        size_t n;
        const char *s = lua_tolstring(L, idx, &n);
        json_string(b, s, n);
        return;
    }
    case LUA_TTABLE: {
        lua_Integer n, i;
        int first = 1;
        if (depth >= JSON_MAX_DEPTH) {
            luaL_error(L, "json.encode: nested deeper than %d levels (or a cycle)", JSON_MAX_DEPTH);
        }
        luaL_checkstack(L, 6, "json.encode");
        twp_charge(L, 8);
        n = array_length(L, idx);
        if (n > 0) {
            jb_char(b, '[');
            for (i = 1; i <= n; i++) {
                if (i > 1) {
                    jb_char(b, ',');
                }
                lua_rawgeti(L, idx, i);
                json_encode_value(L, b, -1, depth + 1);
                lua_pop(L, 1);
            }
            jb_char(b, ']');
            return;
        }
        jb_char(b, '{');
        lua_pushnil(L);
        while (lua_next(L, idx) != 0) {
            size_t kn;
            const char *k;
            if (lua_type(L, -2) != LUA_TSTRING) {
                luaL_error(L, "json.encode: object keys must be strings (got a %s)", luaL_typename(L, -2));
            }
            k = lua_tolstring(L, -2, &kn);
            if (!first) {
                jb_char(b, ',');
            }
            first = 0;
            json_string(b, k, kn);
            jb_char(b, ':');
            json_encode_value(L, b, -1, depth + 1);
            lua_pop(L, 1);
        }
        jb_char(b, '}');
        return;
    }
    default:
        break;
    }
    luaL_error(L, "json.encode: a %s has no JSON form", luaL_typename(L, idx));
}

static int l_encode(lua_State *L)
{
    jbuf b;
    luaL_checkany(L, 1);
    lua_settop(L, 1);
    jb_init(L, &b);
    json_encode_value(L, &b, 1, 0);
    jb_push(&b);
    return 1;
}

typedef struct jparser {
    lua_State *L;
    const char *p;
    const char *end;
    const char *start;
} jparser;

static void jfail(jparser *j, const char *what)
{
    luaL_error(j->L, "json.decode: %s at byte %d", what, (int)(j->p - j->start) + 1);
}

static void jskip(jparser *j)
{
    while (j->p < j->end && (*j->p == ' ' || *j->p == '\t' || *j->p == '\n' || *j->p == '\r')) {
        j->p++;
    }
}

static int hexval(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static unsigned read_u4(jparser *j)
{
    unsigned v = 0;
    int i;
    if (j->end - j->p < 4) {
        jfail(j, "short \\u escape");
    }
    for (i = 0; i < 4; i++) {
        int h = hexval(j->p[i]);
        if (h < 0) {
            jfail(j, "bad \\u escape");
        }
        v = (v << 4) | (unsigned)h;
    }
    j->p += 4;
    return v;
}

static void add_utf8(jbuf *b, unsigned cp)
{
    if (cp < 0x80) {
        jb_char(b, (char)cp);
    } else if (cp < 0x800) {
        jb_char(b, (char)(0xC0 | (cp >> 6)));
        jb_char(b, (char)(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        jb_char(b, (char)(0xE0 | (cp >> 12)));
        jb_char(b, (char)(0x80 | ((cp >> 6) & 0x3F)));
        jb_char(b, (char)(0x80 | (cp & 0x3F)));
    } else {
        jb_char(b, (char)(0xF0 | (cp >> 18)));
        jb_char(b, (char)(0x80 | ((cp >> 12) & 0x3F)));
        jb_char(b, (char)(0x80 | ((cp >> 6) & 0x3F)));
        jb_char(b, (char)(0x80 | (cp & 0x3F)));
    }
}

static void jstring(jparser *j)
{
    jbuf b;
    j->p++;
    jb_init(j->L, &b);
    for (;;) {
        char c;
        if (j->p >= j->end) {
            jfail(j, "unterminated string");
        }
        c = *j->p++;
        if (c == '"') {
            break;
        }
        if ((unsigned char)c < 0x20) {
            jfail(j, "control character in string");
        }
        if (c != '\\') {
            jb_char(&b, c);
            continue;
        }
        if (j->p >= j->end) {
            jfail(j, "unterminated escape");
        }
        c = *j->p++;
        switch (c) {
        case '"': jb_char(&b, '"'); break;
        case '\\': jb_char(&b, '\\'); break;
        case '/': jb_char(&b, '/'); break;
        case 'b': jb_char(&b, '\b'); break;
        case 'f': jb_char(&b, '\f'); break;
        case 'n': jb_char(&b, '\n'); break;
        case 'r': jb_char(&b, '\r'); break;
        case 't': jb_char(&b, '\t'); break;
        case 'u': {
            unsigned cp = read_u4(j);
            if (cp >= 0xD800 && cp <= 0xDBFF) {
                unsigned lo;
                if (j->end - j->p < 6 || j->p[0] != '\\' || j->p[1] != 'u') {
                    jfail(j, "lone high surrogate");
                }
                j->p += 2;
                lo = read_u4(j);
                if (lo < 0xDC00 || lo > 0xDFFF) {
                    jfail(j, "bad low surrogate");
                }
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
            } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
                jfail(j, "lone low surrogate");
            }
            add_utf8(&b, cp);
            break;
        }
        default:
            jfail(j, "bad escape");
        }
    }
    jb_push(&b);
}

static void jvalue(jparser *j, int depth);

static void jnumber(jparser *j)
{
    const char *s = j->p;
    char tmp[64];
    size_t n;
    int is_float = 0;
    if (j->p < j->end && *j->p == '-') j->p++;
    if (j->p >= j->end || *j->p < '0' || *j->p > '9') jfail(j, "bad number");
    if (*j->p == '0') {
        j->p++;
    } else {
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') j->p++;
    }
    if (j->p < j->end && *j->p == '.') {
        is_float = 1;
        j->p++;
        if (j->p >= j->end || *j->p < '0' || *j->p > '9') jfail(j, "bad number");
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') j->p++;
    }
    if (j->p < j->end && (*j->p == 'e' || *j->p == 'E')) {
        is_float = 1;
        j->p++;
        if (j->p < j->end && (*j->p == '+' || *j->p == '-')) j->p++;
        if (j->p >= j->end || *j->p < '0' || *j->p > '9') jfail(j, "bad number");
        while (j->p < j->end && *j->p >= '0' && *j->p <= '9') j->p++;
    }
    n = (size_t)(j->p - s);
    if (n >= sizeof(tmp)) jfail(j, "number too long");
    memcpy(tmp, s, n);
    tmp[n] = '\0';
    if (!is_float && lua_stringtonumber(j->L, tmp) != 0) {
        return;
    }
    if (lua_stringtonumber(j->L, tmp) == 0) jfail(j, "bad number");
    if (lua_isinteger(j->L, -1)) {
        lua_Integer v = lua_tointeger(j->L, -1);
        lua_pop(j->L, 1);
        lua_pushnumber(j->L, (lua_Number)v);
    }
}

static void jliteral(jparser *j, const char *word, size_t n)
{
    if ((size_t)(j->end - j->p) < n || memcmp(j->p, word, n) != 0) {
        jfail(j, "unexpected character");
    }
    j->p += n;
}

static void jvalue(jparser *j, int depth)
{
    lua_State *L = j->L;
    if (depth >= JSON_MAX_DEPTH) {
        jfail(j, "nested too deeply");
    }
    luaL_checkstack(L, 6, "json.decode");
    jskip(j);
    if (j->p >= j->end) {
        jfail(j, "unexpected end");
    }
    switch (*j->p) {
    case '{': {
        j->p++;
        lua_newtable(L);
        jskip(j);
        if (j->p < j->end && *j->p == '}') {
            j->p++;
            return;
        }
        for (;;) {
            twp_charge(L, 4);
            jskip(j);
            if (j->p >= j->end || *j->p != '"') jfail(j, "expected a key");
            jstring(j);
            jskip(j);
            if (j->p >= j->end || *j->p != ':') jfail(j, "expected ':'");
            j->p++;
            jvalue(j, depth + 1);
            lua_rawset(L, -3);
            jskip(j);
            if (j->p < j->end && *j->p == ',') {
                j->p++;
                continue;
            }
            if (j->p < j->end && *j->p == '}') {
                j->p++;
                return;
            }
            jfail(j, "expected ',' or '}'");
        }
    }
    case '[': {
        lua_Integer i = 0;
        j->p++;
        lua_newtable(L);
        jskip(j);
        if (j->p < j->end && *j->p == ']') {
            j->p++;
            return;
        }
        for (;;) {
            twp_charge(L, 4);
            jvalue(j, depth + 1);
            lua_rawseti(L, -2, ++i);
            jskip(j);
            if (j->p < j->end && *j->p == ',') {
                j->p++;
                continue;
            }
            if (j->p < j->end && *j->p == ']') {
                j->p++;
                return;
            }
            jfail(j, "expected ',' or ']'");
        }
    }
    case '"':
        jstring(j);
        return;
    case 't':
        jliteral(j, "true", 4);
        lua_pushboolean(L, 1);
        return;
    case 'f':
        jliteral(j, "false", 5);
        lua_pushboolean(L, 0);
        return;
    case 'n':
        jliteral(j, "null", 4);
        lua_pushlightuserdata(L, NULL);
        return;
    default:
        jnumber(j);
        return;
    }
}

static int l_decode(lua_State *L)
{
    size_t n;
    jparser j;
    const char *s = luaL_checklstring(L, 1, &n);
    j.L = L;
    j.p = s;
    j.start = s;
    j.end = s + n;
    twp_charge(L, (int64_t)(n / 16));
    jvalue(&j, 0);
    jskip(&j);
    if (j.p != j.end) {
        jfail(&j, "trailing characters");
    }
    return 1;
}

void twp_open_json(lua_State *L)
{
    static const luaL_Reg fns[] = {
        {"encode", l_encode},
        {"decode", l_decode},
        {NULL, NULL},
    };
    luaL_newlib(L, fns);
    lua_pushlightuserdata(L, NULL);
    lua_setfield(L, -2, "null");
    lua_setglobal(L, "json");
}
