/*
 * shared.dict(name): a process-wide key/value store shared by every Lua state, with per-key TTL and LRU eviction.
 * Values are strings, numbers, and booleans. Dictionaries live for the life of the process, so they survive a
 * configuration reload.
 *
 * No Lua API call that can raise is made while a lock is held: arguments are checked first, and strings are
 * copied out under the lock into memory the state already owns.
 */
#include "twp_internal.h"

#include <stdlib.h>
#include <string.h>

#define DICT_MAX 64
#define DICT_NAME_MAX 64
#define DICT_KEY_MAX 256
#define DICT_DEFAULT_ITEMS 10000
#define DICT_DEFAULT_BYTES (16 * 1024 * 1024)
#define DICT_SMALL 512

enum { V_STR, V_NUM, V_INT, V_BOOL };

typedef struct dentry {
    char *key;
    size_t klen;
    uint32_t hash;
    int vtype;
    double num;
    int64_t i;
    char *str;
    size_t slen;
    int64_t expires_ms;
    struct dentry *hnext;
    struct dentry *prev;
    struct dentry *next;
} dentry;

typedef struct ddict {
    char name[DICT_NAME_MAX + 1];
    twp_mutex mu;
    dentry **buckets;
    size_t nbuckets;
    size_t count;
    size_t max_items;
    size_t bytes;
    size_t max_bytes;
    dentry *head; /* most recently used */
    dentry *tail;
    struct ddict *next;
} ddict;

static ddict *g_dicts;
static int g_ndicts;

static uint32_t fnv1a(const char *s, size_t n)
{
    uint32_t h = 2166136261u;
    size_t i;
    for (i = 0; i < n; i++) {
        h ^= (unsigned char)s[i];
        h *= 16777619u;
    }
    return h;
}

static int64_t now_ms(void) { return twp_mono_us() / 1000; }

static size_t entry_bytes(const dentry *e) { return sizeof(dentry) + e->klen + e->slen; }

static void lru_unlink(ddict *d, dentry *e)
{
    if (e->prev) e->prev->next = e->next; else d->head = e->next;
    if (e->next) e->next->prev = e->prev; else d->tail = e->prev;
    e->prev = e->next = NULL;
}

static void lru_push_front(ddict *d, dentry *e)
{
    e->prev = NULL;
    e->next = d->head;
    if (d->head) d->head->prev = e;
    d->head = e;
    if (!d->tail) d->tail = e;
}

static dentry **find_slot(ddict *d, const char *k, size_t kn, uint32_t h)
{
    dentry **pp = &d->buckets[h & (d->nbuckets - 1)];
    while (*pp != NULL) {
        dentry *e = *pp;
        if (e->hash == h && e->klen == kn && memcmp(e->key, k, kn) == 0) {
            return pp;
        }
        pp = &e->hnext;
    }
    return pp;
}

static void remove_entry(ddict *d, dentry **slot)
{
    dentry *e = *slot;
    *slot = e->hnext;
    lru_unlink(d, e);
    d->count--;
    d->bytes -= entry_bytes(e);
    free(e->str);
    free(e->key);
    free(e);
}

static void remove_by_ptr(ddict *d, dentry *e)
{
    dentry **slot = find_slot(d, e->key, e->klen, e->hash);
    if (*slot == e) {
        remove_entry(d, slot);
    }
}

/* Live lookup under the lock: drops an expired entry and returns NULL for it. */
static dentry *lookup(ddict *d, const char *k, size_t kn, uint32_t h, int touch)
{
    dentry **slot = find_slot(d, k, kn, h);
    dentry *e = *slot;
    if (e == NULL) {
        return NULL;
    }
    if (e->expires_ms != 0 && e->expires_ms <= now_ms()) {
        remove_entry(d, slot);
        return NULL;
    }
    if (touch) {
        lru_unlink(d, e);
        lru_push_front(d, e);
    }
    return e;
}

/* Makes room for an entry of need bytes: expired entries go first, then the least recently used. */
static void make_room(ddict *d, size_t need, int adding)
{
    int64_t now = now_ms();
    dentry *e = d->tail;
    while (e != NULL && ((adding && d->count >= d->max_items) || d->bytes + need > d->max_bytes)) {
        dentry *prev = e->prev;
        if (e->expires_ms != 0 && e->expires_ms <= now) {
            remove_by_ptr(d, e);
        }
        e = prev;
    }
    while (d->tail != NULL && ((adding && d->count >= d->max_items) || d->bytes + need > d->max_bytes)) {
        remove_by_ptr(d, d->tail);
    }
}

static ddict *dict_get(const char *name, size_t n, int create, size_t max_items, size_t max_bytes)
{
    ddict *d;
    twp_global_lock();
    for (d = g_dicts; d != NULL; d = d->next) {
        if (strlen(d->name) == n && memcmp(d->name, name, n) == 0) {
            if (max_items > 0) {
                twp_mutex_lock(&d->mu);
                d->max_items = max_items;
                d->max_bytes = max_bytes > 0 ? max_bytes : d->max_bytes;
                make_room(d, 0, 1);
                twp_mutex_unlock(&d->mu);
            }
            twp_global_unlock();
            return d;
        }
    }
    if (!create || g_ndicts >= DICT_MAX) {
        twp_global_unlock();
        return NULL;
    }
    d = (ddict *)calloc(1, sizeof(ddict));
    if (d == NULL) {
        twp_global_unlock();
        return NULL;
    }
    memcpy(d->name, name, n);
    d->name[n] = '\0';
    twp_mutex_init(&d->mu);
    d->max_items = max_items > 0 ? max_items : DICT_DEFAULT_ITEMS;
    d->max_bytes = max_bytes > 0 ? max_bytes : DICT_DEFAULT_BYTES;
    d->nbuckets = 64;
    while (d->nbuckets < d->max_items && d->nbuckets < 65536) {
        d->nbuckets *= 2;
    }
    d->buckets = (dentry **)calloc(d->nbuckets, sizeof(dentry *));
    if (d->buckets == NULL) {
        free(d);
        twp_global_unlock();
        return NULL;
    }
    d->next = g_dicts;
    g_dicts = d;
    g_ndicts++;
    twp_global_unlock();
    return d;
}

static int valid_name(const char *s, size_t n)
{
    size_t i;
    if (n == 0 || n > DICT_NAME_MAX) {
        return 0;
    }
    for (i = 0; i < n; i++) {
        char c = s[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' ||
                c == '.')) {
            return 0;
        }
    }
    return 1;
}

/* Exported so the host can size a dictionary before scripts use it. */
TWP_API int twp_dict_configure(const char *name, int64_t max_items, int64_t max_bytes)
{
    size_t n = name == NULL ? 0 : strlen(name);
    if (!valid_name(name, n) || max_items < 1) {
        return TWP_ERR_ARGUMENT;
    }
    return dict_get(name, n, 1, (size_t)max_items, max_bytes > 0 ? (size_t)max_bytes : 0) != NULL ? TWP_OK
                                                                                                    : TWP_ERR_MEMORY;
}

TWP_API int64_t twp_dict_count(const char *name)
{
    size_t n = name == NULL ? 0 : strlen(name);
    ddict *d;
    int64_t c;
    if (!valid_name(name, n) || (d = dict_get(name, n, 0, 0, 0)) == NULL) {
        return -1;
    }
    twp_mutex_lock(&d->mu);
    c = (int64_t)d->count;
    twp_mutex_unlock(&d->mu);
    return c;
}

TWP_API int twp_dict_flush(const char *name)
{
    size_t n = name == NULL ? 0 : strlen(name);
    ddict *d;
    if (!valid_name(name, n) || (d = dict_get(name, n, 0, 0, 0)) == NULL) {
        return TWP_ERR_ARGUMENT;
    }
    twp_mutex_lock(&d->mu);
    while (d->tail != NULL) {
        remove_by_ptr(d, d->tail);
    }
    twp_mutex_unlock(&d->mu);
    return TWP_OK;
}

/* ---- Lua binding ----------------------------------------------------------------------------------------------- */

static const char DICT_MT[] = "titanium.shared.dict";

static ddict *check_dict(lua_State *L)
{
    ddict **ud = (ddict **)luaL_checkudata(L, 1, DICT_MT);
    return *ud;
}

static const char *check_key(lua_State *L, size_t *kn)
{
    const char *k = luaL_checklstring(L, 2, kn);
    if (*kn == 0 || *kn > DICT_KEY_MAX) {
        luaL_argerror(L, 2, "key must be 1 to 256 bytes");
    }
    return k;
}

static int64_t check_ttl(lua_State *L, int arg)
{
    lua_Number ttl = luaL_optnumber(L, arg, 0);
    if (ttl < 0 || ttl > 31536000) {
        luaL_argerror(L, arg, "ttl must be 0 (no expiry) to 31536000 seconds");
    }
    return ttl > 0 ? now_ms() + (int64_t)(ttl * 1000) : 0;
}

typedef struct dval {
    int vtype;
    double num;
    int64_t i;
    const char *str;
    size_t slen;
} dval;

static void check_value(lua_State *L, int arg, dval *v)
{
    switch (lua_type(L, arg)) {
    case LUA_TSTRING:
        v->vtype = V_STR;
        v->str = lua_tolstring(L, arg, &v->slen);
        break;
    case LUA_TNUMBER:
        if (lua_isinteger(L, arg)) {
            v->vtype = V_INT;
            v->i = (int64_t)lua_tointeger(L, arg);
        } else {
            v->vtype = V_NUM;
            v->num = lua_tonumber(L, arg);
        }
        v->slen = 0;
        break;
    case LUA_TBOOLEAN:
        v->vtype = V_BOOL;
        v->i = lua_toboolean(L, arg);
        v->slen = 0;
        break;
    default:
        luaL_argerror(L, arg, "value must be a string, number, or boolean");
    }
}

/* Stores v under the lock. Returns 0, or -1 when memory or the size limit refuses it. */
static int store(ddict *d, const char *k, size_t kn, uint32_t h, const dval *v, int64_t expires)
{
    dentry **slot = find_slot(d, k, kn, h);
    dentry *e = *slot;
    char *str = NULL;
    size_t need = sizeof(dentry) + kn + v->slen;
    if (need > d->max_bytes) {
        return -1;
    }
    if (v->vtype == V_STR) {
        str = (char *)malloc(v->slen > 0 ? v->slen : 1);
        if (str == NULL) {
            return -1;
        }
        memcpy(str, v->str, v->slen);
    }
    if (e != NULL) {
        d->bytes -= entry_bytes(e);
        free(e->str);
        lru_unlink(d, e);
        make_room(d, need, 0);
    } else {
        make_room(d, need, 1);
        e = (dentry *)calloc(1, sizeof(dentry));
        if (e == NULL) {
            free(str);
            return -1;
        }
        e->key = (char *)malloc(kn);
        if (e->key == NULL) {
            free(str);
            free(e);
            return -1;
        }
        memcpy(e->key, k, kn);
        e->klen = kn;
        e->hash = h;
        slot = find_slot(d, k, kn, h);
        *slot = e;
        d->count++;
    }
    e->vtype = v->vtype;
    e->num = v->num;
    e->i = v->i;
    e->str = str;
    e->slen = v->vtype == V_STR ? v->slen : 0;
    e->expires_ms = expires;
    d->bytes += entry_bytes(e);
    lru_push_front(d, e);
    return 0;
}

static void push_scalar(lua_State *L, const dval *v)
{
    switch (v->vtype) {
    case V_NUM: lua_pushnumber(L, v->num); break;
    case V_INT: lua_pushinteger(L, (lua_Integer)v->i); break;
    case V_BOOL: lua_pushboolean(L, (int)v->i); break;
    default: lua_pushnil(L); break;
    }
}

/* d:get(key) -> value or nil */
static int l_get(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    char small[DICT_SMALL];
    int attempt;
    for (attempt = 0; attempt < 4; attempt++) {
        dval v;
        size_t big = 0;
        char *dst = NULL;
        dentry *e;
        memset(&v, 0, sizeof(v));
        twp_mutex_lock(&d->mu);
        e = lookup(d, k, kn, h, 1);
        if (e == NULL) {
            twp_mutex_unlock(&d->mu);
            lua_pushnil(L);
            return 1;
        }
        v.vtype = e->vtype;
        v.num = e->num;
        v.i = e->i;
        if (e->vtype == V_STR) {
            if (e->slen <= DICT_SMALL) {
                memcpy(small, e->str, e->slen);
                v.slen = e->slen;
                dst = small;
            } else {
                big = e->slen;
            }
        }
        twp_mutex_unlock(&d->mu);
        if (v.vtype != V_STR) {
            push_scalar(L, &v);
            return 1;
        }
        if (dst != NULL) {
            lua_pushlstring(L, small, v.slen);
            return 1;
        }
        /* Large value: allocate in the state first, then copy if it is still the same size. */
        dst = (char *)lua_newuserdatauv(L, big, 0);
        twp_mutex_lock(&d->mu);
        e = lookup(d, k, kn, h, 0);
        if (e != NULL && e->vtype == V_STR && e->slen == big) {
            memcpy(dst, e->str, big);
            twp_mutex_unlock(&d->mu);
            lua_pushlstring(L, dst, big);
            lua_remove(L, -2);
            return 1;
        }
        twp_mutex_unlock(&d->mu);
        lua_pop(L, 1);
    }
    lua_pushnil(L);
    return 1;
}

/* d:set(key, value [, ttl_seconds]) -> true, or nil, err. A nil value deletes. */
static int l_set(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    dval v;
    int64_t expires;
    int rc;
    memset(&v, 0, sizeof(v));
    if (lua_isnoneornil(L, 3)) {
        dentry **slot;
        twp_mutex_lock(&d->mu);
        slot = find_slot(d, k, kn, h);
        if (*slot != NULL) {
            remove_entry(d, slot);
        }
        twp_mutex_unlock(&d->mu);
        lua_pushboolean(L, 1);
        return 1;
    }
    check_value(L, 3, &v);
    expires = check_ttl(L, 4);
    twp_mutex_lock(&d->mu);
    rc = store(d, k, kn, h, &v, expires);
    twp_mutex_unlock(&d->mu);
    if (rc != 0) {
        luaL_pushfail(L);
        lua_pushliteral(L, "no memory");
        return 2;
    }
    lua_pushboolean(L, 1);
    return 1;
}

/* d:add(key, value [, ttl]) -> true, or nil, "exists" */
static int l_add(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    dval v;
    int64_t expires;
    int rc;
    memset(&v, 0, sizeof(v));
    check_value(L, 3, &v);
    expires = check_ttl(L, 4);
    twp_mutex_lock(&d->mu);
    if (lookup(d, k, kn, h, 0) != NULL) {
        twp_mutex_unlock(&d->mu);
        luaL_pushfail(L);
        lua_pushliteral(L, "exists");
        return 2;
    }
    rc = store(d, k, kn, h, &v, expires);
    twp_mutex_unlock(&d->mu);
    if (rc != 0) {
        luaL_pushfail(L);
        lua_pushliteral(L, "no memory");
        return 2;
    }
    lua_pushboolean(L, 1);
    return 1;
}

/* d:incr(key, delta [, init [, init_ttl]]) -> new value, or nil, err. Without init a missing key is an error. */
static int l_incr(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    int has_init = !lua_isnoneornil(L, 4);
    int delta_int = lua_isinteger(L, 3);
    lua_Number delta = luaL_checknumber(L, 3);
    lua_Integer idelta = delta_int ? lua_tointeger(L, 3) : 0;
    dval init, out;
    int64_t expires = 0;
    dentry *e;
    const char *err = NULL;
    memset(&init, 0, sizeof(init));
    memset(&out, 0, sizeof(out));
    if (has_init) {
        luaL_checknumber(L, 4);
        check_value(L, 4, &init);
        expires = check_ttl(L, 5);
    }
    twp_mutex_lock(&d->mu);
    e = lookup(d, k, kn, h, 1);
    if (e == NULL) {
        if (!has_init) {
            err = "not found";
        } else {
            if (init.vtype == V_INT && delta_int) {
                init.i = (int64_t)((uint64_t)init.i + (uint64_t)idelta);
            } else {
                double base = init.vtype == V_INT ? (double)init.i : init.num;
                init.vtype = V_NUM;
                init.num = base + delta;
            }
            if (store(d, k, kn, h, &init, expires) != 0) {
                err = "no memory";
            }
            out = init;
        }
    } else if (e->vtype == V_INT && delta_int) {
        e->i = (int64_t)((uint64_t)e->i + (uint64_t)idelta);
        out.vtype = V_INT;
        out.i = e->i;
    } else if (e->vtype == V_INT || e->vtype == V_NUM) {
        double base = e->vtype == V_INT ? (double)e->i : e->num;
        e->vtype = V_NUM;
        e->num = base + delta;
        out.vtype = V_NUM;
        out.num = e->num;
    } else {
        err = "not a number";
    }
    twp_mutex_unlock(&d->mu);
    if (err != NULL) {
        luaL_pushfail(L);
        lua_pushstring(L, err);
        return 2;
    }
    push_scalar(L, &out);
    return 1;
}

/* d:delete(key) -> true */
static int l_delete(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    dentry **slot;
    twp_mutex_lock(&d->mu);
    slot = find_slot(d, k, kn, h);
    if (*slot != NULL) {
        remove_entry(d, slot);
    }
    twp_mutex_unlock(&d->mu);
    lua_pushboolean(L, 1);
    return 1;
}

/* d:ttl(key) -> seconds left (0 = no expiry), or nil when missing */
static int l_ttl(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t kn;
    const char *k = check_key(L, &kn);
    uint32_t h = fnv1a(k, kn);
    dentry *e;
    int64_t left = -1;
    twp_mutex_lock(&d->mu);
    e = lookup(d, k, kn, h, 0);
    if (e != NULL) {
        left = e->expires_ms == 0 ? 0 : e->expires_ms - now_ms();
    }
    twp_mutex_unlock(&d->mu);
    if (left < 0) {
        lua_pushnil(L);
    } else {
        lua_pushnumber(L, (lua_Number)left / 1000.0);
    }
    return 1;
}

static int l_count(lua_State *L)
{
    ddict *d = check_dict(L);
    size_t c;
    twp_mutex_lock(&d->mu);
    c = d->count;
    twp_mutex_unlock(&d->mu);
    lua_pushinteger(L, (lua_Integer)c);
    return 1;
}

/* shared.dict(name) -> handle */
static int l_dict(lua_State *L)
{
    size_t n;
    const char *name = luaL_checklstring(L, 1, &n);
    ddict *d;
    ddict **ud;
    if (!valid_name(name, n)) {
        return luaL_argerror(L, 1, "dict name must be 1 to 64 of [A-Za-z0-9_.-]");
    }
    ud = (ddict **)lua_newuserdatauv(L, sizeof(ddict *), 0);
    d = dict_get(name, n, 1, 0, 0);
    if (d == NULL) {
        return luaL_error(L, "shared.dict: cannot create '%s' (at most %d dictionaries)", name, DICT_MAX);
    }
    *ud = d;
    luaL_setmetatable(L, DICT_MT);
    return 1;
}

void twp_open_shared(lua_State *L)
{
    static const luaL_Reg methods[] = {
        {"get", l_get},
        {"set", l_set},
        {"add", l_add},
        {"incr", l_incr},
        {"delete", l_delete},
        {"ttl", l_ttl},
        {"count", l_count},
        {NULL, NULL},
    };
    static const luaL_Reg fns[] = {
        {"dict", l_dict},
        {NULL, NULL},
    };
    luaL_newmetatable(L, DICT_MT);
    lua_newtable(L);
    luaL_setfuncs(L, methods, 0);
    lua_setfield(L, -2, "__index");
    lua_pushliteral(L, "locked");
    lua_setfield(L, -2, "__metatable");
    lua_pop(L, 1);
    luaL_newlib(L, fns);
    lua_setglobal(L, "shared");
}
