/*
 * State lifecycle, limits, sandbox, ctx, and the run loop of the Titanium Lua shim.
 *
 * Limits live in the engine: a capped allocator refuses growth past the memory ceiling, and a count hook charges
 * every TWP_HOOK_STEP instructions against the call's budget and checks its deadline. Once a limit trips, the hook
 * fires on every instruction and raises again, so a script cannot pcall its way past the limit.
 *
 * Every touch of a lua_State from an exported function goes through lua_pcall or lua_resume. The panic handler
 * long-jumps back to the exported function as a last resort and marks the state broken; the jump never leaves
 * this library.
 */
#include "twp_internal.h"

#include <stdlib.h>
#include <string.h>

static const char CALLS_KEY = 'c';
static const char CUR_CMDS_KEY = 'm';
static const char SETTINGS_KEY = 's';

twp_state *twp_from(lua_State *L) { return *(twp_state **)lua_getextraspace(L); }

static void *twp_alloc(void *ud, void *ptr, size_t osize, size_t nsize)
{
    twp_state *S = (twp_state *)ud;
    void *grown;
    if (ptr == NULL) {
        osize = 0;
    }
    if (nsize == 0) {
        free(ptr);
        S->mem_used -= osize;
        return NULL;
    }
    if (nsize > osize && S->mem_used + (nsize - osize) > S->mem_limit) {
        S->mem_refused = 1;
        return NULL;
    }
    grown = realloc(ptr, nsize);
    if (grown == NULL) {
        return NULL;
    }
    S->mem_used = S->mem_used - osize + nsize;
    return grown;
}

static void twp_hook(lua_State *L, lua_Debug *ar);

static void raise_tripped(lua_State *L, twp_state *S)
{
    lua_sethook(L, twp_hook, LUA_MASKCOUNT, 1);
    luaL_error(L, S->tripped == TWP_TRIP_INSTRUCTIONS ? "titanium: instruction limit exceeded" : "titanium: time limit exceeded");
}

static void twp_hook(lua_State *L, lua_Debug *ar)
{
    twp_state *S = twp_from(L);
    (void)ar;
    if (S->tripped == TWP_TRIP_NONE) {
        S->used += TWP_HOOK_STEP;
        if (S->used > S->budget) {
            S->tripped = TWP_TRIP_INSTRUCTIONS;
        } else if (S->deadline_us != 0 && twp_mono_us() > S->deadline_us) {
            S->tripped = TWP_TRIP_DEADLINE;
        } else {
            return;
        }
    }
    raise_tripped(L, S);
}

void twp_charge(lua_State *L, int64_t units)
{
    twp_state *S = twp_from(L);
    if (S->tripped == TWP_TRIP_NONE) {
        S->used += units < 0 ? 0 : units;
        if (S->used > S->budget) {
            S->tripped = TWP_TRIP_INSTRUCTIONS;
        } else if (S->deadline_us != 0 && units >= TWP_HOOK_STEP && twp_mono_us() > S->deadline_us) {
            S->tripped = TWP_TRIP_DEADLINE;
        } else {
            return;
        }
    }
    raise_tripped(L, S);
}

void twp_check_can_wait(lua_State *L, const char *what)
{
    twp_state *S = twp_from(L);
    if (S->phase_co == NULL || L != S->phase_co) {
        luaL_error(L, "%s can only be called from a phase or timer function, not at load time or inside a coroutine", what);
    }
    if (!lua_isyieldable(L)) {
        luaL_error(L, "%s cannot wait here (inside a metamethod, iterator, or sort comparator)", what);
    }
    S->our_yield = 1;
}

void twp_emit(lua_State *L, int n)
{
    int i;
    lua_Integer len;
    luaL_checkstack(L, 4, "queueing a command");
    if (lua_rawgetp(L, LUA_REGISTRYINDEX, &CUR_CMDS_KEY) != LUA_TTABLE) {
        lua_pop(L, n + 1);
        return;
    }
    len = (lua_Integer)lua_rawlen(L, -1);
    if (len >= TWP_MAX_COMMANDS) {
        luaL_error(L, "a call may queue at most %d commands", TWP_MAX_COMMANDS);
    }
    lua_insert(L, -(n + 1));
    lua_createtable(L, n, 0);
    lua_insert(L, -(n + 1));
    for (i = n; i >= 1; i--) {
        lua_rawseti(L, -(i + 1), i);
    }
    lua_rawseti(L, -2, len + 1);
    lua_pop(L, 1);
}

static int twp_panic(lua_State *L)
{
    twp_state *S = twp_from(L);
    S->broken = 1;
    if (S->panic_jmp != NULL) {
        longjmp(*S->panic_jmp, 1);
    }
    return 0;
}

/* ---- sandbox --------------------------------------------------------------------------------------------------- */

static int safe_collectgarbage(lua_State *L)
{
    static const char *const opts[] = {"collect", "count", "step", "isrunning", NULL};
    int o = luaL_checkoption(L, 1, "collect", opts);
    twp_state *S = twp_from(L);
    switch (o) {
    case 0:
        twp_charge(L, 100000 + (int64_t)(S->mem_used / 64));
        lua_gc(L, LUA_GCCOLLECT);
        lua_pushinteger(L, 0);
        return 1;
    case 1: {
        int k = lua_gc(L, LUA_GCCOUNT);
        int b = lua_gc(L, LUA_GCCOUNTB);
        lua_pushnumber(L, (lua_Number)k + ((lua_Number)b / 1024));
        return 1;
    }
    case 2:
        twp_charge(L, 10000);
        lua_pushboolean(L, lua_gc(L, LUA_GCSTEP, 0));
        return 1;
    default:
        lua_pushboolean(L, lua_gc(L, LUA_GCISRUNNING));
        return 1;
    }
}

static int safe_running(lua_State *L)
{
    twp_state *S = twp_from(L);
    if (L == S->phase_co) {
        /* The phase coroutine belongs to the host; scripts never get a handle on it. */
        lua_pushnil(L);
        lua_pushboolean(L, 0);
        return 2;
    }
    lua_pushthread(L);
    lua_pushboolean(L, lua_pushthread(L));
    lua_remove(L, -2);
    return 2;
}

static int open_sandbox(lua_State *L)
{
    static const luaL_Reg libs[] = {
        {LUA_GNAME, luaopen_base},
        {LUA_STRLIBNAME, luaopen_string},
        {LUA_TABLIBNAME, luaopen_table},
        {LUA_MATHLIBNAME, luaopen_math},
        {LUA_UTF8LIBNAME, luaopen_utf8},
        {LUA_COLIBNAME, luaopen_coroutine},
        {NULL, NULL},
    };
    static const char *const strip[] = {"dofile", "loadfile", "load", "loadstring", "require", "module", "print", NULL};
    static const char *const os_keep[] = {"time", "clock", "date", "difftime", NULL};
    const luaL_Reg *lib;
    int i;

    for (lib = libs; lib->func != NULL; lib++) {
        luaL_requiref(L, lib->name, lib->func, 1);
        lua_pop(L, 1);
    }

    lua_pushglobaltable(L);
    for (i = 0; strip[i] != NULL; i++) {
        lua_pushnil(L);
        lua_setfield(L, -2, strip[i]);
    }
    lua_pushcfunction(L, safe_collectgarbage);
    lua_setfield(L, -2, "collectgarbage");

    /* os: only the clock and calendar functions. No execute, exit, getenv, remove, rename, tmpname, setlocale. */
    luaopen_os(L);
    lua_newtable(L);
    for (i = 0; os_keep[i] != NULL; i++) {
        lua_getfield(L, -2, os_keep[i]);
        lua_setfield(L, -2, os_keep[i]);
    }
    lua_setfield(L, -3, "os");
    lua_pop(L, 1);

    lua_getfield(L, -1, "coroutine");
    lua_pushcfunction(L, safe_running);
    lua_setfield(L, -2, "running");
    lua_pop(L, 1);
    lua_pop(L, 1);

    twp_open_strguard(L);

    lua_newtable(L);
    lua_rawsetp(L, LUA_REGISTRYINDEX, &CALLS_KEY);
    lua_newtable(L);
    lua_pushvalue(L, -1);
    lua_rawsetp(L, LUA_REGISTRYINDEX, &SETTINGS_KEY);
    lua_setglobal(L, "settings");
    return 0;
}

/* ---- ctx ------------------------------------------------------------------------------------------------------- */

static void ctx_self(lua_State *L)
{
    if (lua_gettop(L) >= 1 && lua_rawequal(L, 1, lua_upvalueindex(1))) {
        lua_remove(L, 1);
    }
}

static int is_tchar(unsigned char c)
{
    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) {
        return 1;
    }
    return strchr("!#$%&'*+-.^_`|~", c) != NULL && c != 0;
}

static const char *check_header_name(lua_State *L, int arg, size_t *len)
{
    size_t i, n;
    const char *s = luaL_checklstring(L, arg, &n);
    if (n == 0 || n > 256) {
        luaL_argerror(L, arg, "header name must be 1 to 256 characters");
    }
    for (i = 0; i < n; i++) {
        if (!is_tchar((unsigned char)s[i])) {
            luaL_argerror(L, arg, "header name has a character that is not allowed");
        }
    }
    *len = n;
    return s;
}

static void push_lower(lua_State *L, const char *s, size_t n)
{
    luaL_Buffer b;
    size_t i;
    char *p = luaL_buffinitsize(L, &b, n);
    for (i = 0; i < n; i++) {
        unsigned char c = (unsigned char)s[i];
        p[i] = (char)(c >= 'A' && c <= 'Z' ? c + 32 : c);
    }
    luaL_pushresultsize(&b, n);
}

/* Upvalue 1 is ctx. The headers map is ctx.headers, keyed by lower-case name. */
static int ctx_get_header(lua_State *L)
{
    size_t n;
    const char *name;
    ctx_self(L);
    name = luaL_checklstring(L, 1, &n);
    lua_getfield(L, lua_upvalueindex(1), "headers");
    if (!lua_istable(L, -1)) {
        lua_pushnil(L);
        return 1;
    }
    push_lower(L, name, n);
    lua_rawget(L, -2);
    return 1;
}

static int ctx_set_header(lua_State *L)
{
    size_t n, vn, i;
    const char *name, *value;
    ctx_self(L);
    name = check_header_name(L, 1, &n);
    value = luaL_checklstring(L, 2, &vn);
    if (vn > 16384) {
        luaL_argerror(L, 2, "header value is longer than 16384 bytes");
    }
    for (i = 0; i < vn; i++) {
        if (value[i] == '\r' || value[i] == '\n' || value[i] == '\0') {
            luaL_argerror(L, 2, "header value must not contain CR, LF, or NUL");
        }
    }
    lua_getfield(L, lua_upvalueindex(1), "headers");
    if (lua_istable(L, -1)) {
        push_lower(L, name, n);
        lua_pushvalue(L, 2);
        lua_rawset(L, -3);
    }
    lua_pop(L, 1);
    lua_pushliteral(L, "set_header");
    lua_pushvalue(L, 1);
    lua_pushvalue(L, 2);
    twp_emit(L, 3);
    return 0;
}

static int ctx_remove_header(lua_State *L)
{
    size_t n;
    const char *name;
    ctx_self(L);
    name = check_header_name(L, 1, &n);
    lua_getfield(L, lua_upvalueindex(1), "headers");
    if (lua_istable(L, -1)) {
        push_lower(L, name, n);
        lua_pushnil(L);
        lua_rawset(L, -3);
    }
    lua_pop(L, 1);
    lua_pushliteral(L, "remove_header");
    lua_pushvalue(L, 1);
    twp_emit(L, 2);
    return 0;
}

static int ctx_set_body(lua_State *L)
{
    size_t n;
    ctx_self(L);
    luaL_checklstring(L, 1, &n);
    lua_pushliteral(L, "set_body");
    lua_pushvalue(L, 1);
    twp_emit(L, 2);
    return 0;
}

static int ctx_deny(lua_State *L)
{
    lua_Integer status;
    ctx_self(L);
    status = luaL_optinteger(L, 1, 403);
    if (status < 100 || status > 599) {
        luaL_argerror(L, 1, "status must be 100 to 599");
    }
    if (!lua_isnoneornil(L, 2)) {
        luaL_checkstring(L, 2);
    }
    if (!lua_isnoneornil(L, 3)) {
        luaL_checktype(L, 3, LUA_TTABLE);
    }
    lua_settop(L, 3);
    lua_pushliteral(L, "deny");
    lua_pushinteger(L, status);
    lua_pushvalue(L, 2);
    lua_pushvalue(L, 3);
    twp_emit(L, 4);
    return 0;
}

static int ctx_choose_cluster(lua_State *L)
{
    size_t n;
    ctx_self(L);
    luaL_checklstring(L, 1, &n);
    if (n == 0 || n > 256) {
        luaL_argerror(L, 1, "cluster id must be 1 to 256 characters");
    }
    lua_pushliteral(L, "cluster");
    lua_pushvalue(L, 1);
    twp_emit(L, 2);
    return 0;
}

static int ctx_continue(lua_State *L)
{
    (void)L;
    return 0;
}

static int ctx_log(lua_State *L)
{
    ctx_self(L);
    lua_getglobal(L, "log");
    if (lua_istable(L, -1)) {
        lua_getfield(L, -1, "info");
        lua_insert(L, 1);
        lua_pop(L, 1);
        lua_call(L, lua_gettop(L) - 1, 0);
    }
    return 0;
}

/* Builds ctx from the decoded snapshot at the top of the stack and replaces it. */
static void build_ctx(lua_State *L)
{
    static const luaL_Reg methods[] = {
        {"get_header", ctx_get_header},
        {"set_header", ctx_set_header},
        {"remove_header", ctx_remove_header},
        {"set_body", ctx_set_body},
        {"set_data", ctx_set_body},
        {"deny", ctx_deny},
        {"respond", ctx_deny},
        {"choose_cluster", ctx_choose_cluster},
        {"continue", ctx_continue},
        {"log", ctx_log},
        {NULL, NULL},
    };
    const luaL_Reg *m;
    int snap = lua_gettop(L);
    int ctx, map;
    luaL_checkstack(L, 8, "building ctx");
    if (!lua_istable(L, snap)) {
        lua_pop(L, 1);
        lua_newtable(L);
    }
    lua_newtable(L);
    ctx = lua_gettop(L);

    lua_pushnil(L);
    while (lua_next(L, snap) != 0) {
        lua_pushvalue(L, -2);
        lua_insert(L, -2);
        lua_rawset(L, ctx);
    }

    /* headers: the snapshot carries a list of {name, value}; ctx.headers maps lower-case name to value. */
    lua_newtable(L);
    map = lua_gettop(L);
    if (lua_getfield(L, snap, "headers") == LUA_TTABLE) {
        lua_Integer i, n = (lua_Integer)lua_rawlen(L, -1);
        lua_pushvalue(L, -1);
        lua_setfield(L, ctx, "header_list");
        for (i = 1; i <= n; i++) {
            size_t kn;
            const char *k;
            if (lua_rawgeti(L, -1, i) != LUA_TTABLE) {
                lua_pop(L, 1);
                continue;
            }
            lua_rawgeti(L, -1, 1);
            lua_rawgeti(L, -2, 2);
            k = lua_type(L, -2) == LUA_TSTRING ? lua_tolstring(L, -2, &kn) : NULL;
            if (k != NULL && lua_type(L, -1) == LUA_TSTRING) {
                push_lower(L, k, kn);       /* entry name value lname */
                lua_pushvalue(L, -1);       /* ... lname lname */
                if (lua_rawget(L, map) == LUA_TSTRING) {
                    lua_pushliteral(L, ", ");
                    lua_pushvalue(L, -4);
                    lua_concat(L, 3);       /* ... lname "old, value" */
                } else {
                    lua_pop(L, 1);
                    lua_pushvalue(L, -2);   /* ... lname value */
                }
                lua_rawset(L, map);
            }
            lua_pop(L, 3);
        }
    }
    lua_pop(L, 1);
    lua_setfield(L, ctx, "headers");

    if (lua_getfield(L, ctx, "vars") != LUA_TTABLE) {
        lua_pop(L, 1);
        lua_newtable(L);
        lua_setfield(L, ctx, "vars");
    } else {
        lua_pop(L, 1);
    }

    lua_rawgetp(L, LUA_REGISTRYINDEX, &SETTINGS_KEY);
    lua_setfield(L, ctx, "settings");

    for (m = methods; m->name != NULL; m++) {
        lua_pushvalue(L, ctx);
        lua_pushcclosure(L, m->func, 1);
        lua_setfield(L, ctx, m->name);
    }

    lua_remove(L, snap);
}

/* ---- exported API ---------------------------------------------------------------------------------------------- */

static void copy_err(const char *msg, char *err, int errlen)
{
    size_t n;
    if (err == NULL || errlen <= 0) {
        return;
    }
    if (msg == NULL) {
        msg = "unknown error";
    }
    n = strlen(msg);
    if (n >= (size_t)errlen) {
        n = (size_t)errlen - 1;
    }
    memcpy(err, msg, n);
    err[n] = '\0';
}

static void begin_segment(twp_state *S, int64_t budget, int64_t max_us)
{
    S->tripped = TWP_TRIP_NONE;
    S->used = 0;
    S->budget = budget > 0 ? budget : 1;
    S->deadline_us = max_us > 0 ? twp_mono_us() + max_us : 0;
    S->our_yield = 0;
    S->mem_refused = 0;
    lua_sethook(S->L, twp_hook, LUA_MASKCOUNT, TWP_HOOK_STEP);
}

static int limit_code(twp_state *S, int status)
{
    if (S->tripped == TWP_TRIP_INSTRUCTIONS) {
        return TWP_ERR_INSTRUCTIONS;
    }
    if (S->tripped == TWP_TRIP_DEADLINE) {
        return TWP_ERR_DEADLINE;
    }
    if (status == LUA_ERRMEM || S->mem_refused) {
        return TWP_ERR_MEMORY;
    }
    return TWP_ERR_RUNTIME;
}

TWP_API int twp_abi_version(void) { return TWP_ABI_VERSION; }

TWP_API const char *twp_lua_version(void) { return LUA_RELEASE; }

TWP_API twp_state *twp_create(int64_t mem_limit, int64_t out_limit, char *err, int errlen)
{
    jmp_buf jb;
    twp_state *S = (twp_state *)calloc(1, sizeof(twp_state));
    lua_State *L;
    if (S == NULL) {
        copy_err("out of memory", err, errlen);
        return NULL;
    }
    S->mem_limit = mem_limit > 0 ? (size_t)mem_limit : (size_t)64 * 1024 * 1024;
    S->out_limit = out_limit > 0 ? (size_t)out_limit : (size_t)4 * 1024 * 1024;
    L = lua_newstate(twp_alloc, S);
    if (L == NULL) {
        free(S);
        copy_err("could not create a Lua state within the memory limit", err, errlen);
        return NULL;
    }
    S->L = L;
    *(twp_state **)lua_getextraspace(L) = S;
    lua_atpanic(L, twp_panic);
    begin_segment(S, 50000000, 0);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        copy_err("the Lua state failed while it was being set up", err, errlen);
        return NULL; /* leaked on purpose: a panicked state cannot be closed safely */
    }
    lua_pushcfunction(L, open_sandbox);
    if (lua_pcall(L, 0, 0, 0) != LUA_OK) {
        copy_err(lua_tostring(L, -1), err, errlen);
        S->panic_jmp = NULL;
        lua_close(L);
        free(S);
        return NULL;
    }
    S->panic_jmp = NULL;
    lua_settop(L, 0);
    return S;
}

TWP_API void twp_destroy(twp_state *S)
{
    jmp_buf jb;
    if (S == NULL) {
        return;
    }
    if (!S->broken) {
        /* Finalizers that run during close hit the tripped hook at their first instruction. */
        S->tripped = TWP_TRIP_INSTRUCTIONS;
        S->phase_co = NULL;
        lua_sethook(S->L, twp_hook, LUA_MASKCOUNT, 1);
        S->panic_jmp = &jb;
        if (setjmp(jb) == 0) {
            lua_close(S->L);
            S->panic_jmp = NULL;
            free(S);
        }
        return;
    }
}

TWP_API int64_t twp_memory_used(twp_state *S) { return S == NULL ? 0 : (int64_t)S->mem_used; }

TWP_API void twp_buffer_free(unsigned char *p) { free(p); }

typedef struct load_args {
    const char *src;
    size_t len;
    const char *name;
} load_args;

static int do_load(lua_State *L)
{
    load_args *a = (load_args *)lua_touserdata(L, 1);
    int status = luaL_loadbufferx(L, a->src, a->len, a->name, "t");
    if (status != LUA_OK) {
        return lua_error(L);
    }
    lua_call(L, 0, 0);
    return 0;
}

TWP_API int twp_load(twp_state *S, const char *src, size_t len, const char *chunkname, int64_t budget, int64_t max_us,
    char *err, int errlen)
{
    jmp_buf jb;
    load_args a;
    int status, code;
    lua_State *L;
    if (S == NULL || S->broken) {
        copy_err("the Lua state is not usable", err, errlen);
        return TWP_ERR_BROKEN;
    }
    L = S->L;
    a.src = src;
    a.len = len;
    a.name = chunkname != NULL ? chunkname : "=script";
    begin_segment(S, budget, max_us);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        copy_err("the Lua state failed", err, errlen);
        return TWP_ERR_BROKEN;
    }
    lua_settop(L, 0);
    lua_pushcfunction(L, do_load);
    lua_pushlightuserdata(L, &a);
    status = lua_pcall(L, 1, 0, 0);
    code = TWP_OK;
    if (status != LUA_OK) {
        code = limit_code(S, status);
        copy_err(lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "the script raised a non-string error", err, errlen);
    }
    lua_settop(L, 0);
    S->panic_jmp = NULL;
    return code;
}

typedef struct bytes_args {
    const unsigned char *p;
    size_t len;
    const char *name;
    int found;
} bytes_args;

static int do_set_settings(lua_State *L)
{
    bytes_args *a = (bytes_args *)lua_touserdata(L, 1);
    lua_rawgetp(L, LUA_REGISTRYINDEX, &SETTINGS_KEY);
    twp_decode(L, a->p, a->len, 0);
    if (lua_istable(L, -1)) {
        lua_pushnil(L);
        while (lua_next(L, -2) != 0) {
            lua_pushvalue(L, -2);
            lua_insert(L, -2);
            lua_rawset(L, -5);
        }
    }
    return 0;
}

TWP_API int twp_set_settings(twp_state *S, const unsigned char *p, size_t len)
{
    jmp_buf jb;
    bytes_args a;
    int status;
    if (S == NULL || S->broken) {
        return TWP_ERR_BROKEN;
    }
    a.p = p;
    a.len = len;
    begin_segment(S, 1000000, 0);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        return TWP_ERR_BROKEN;
    }
    lua_settop(S->L, 0);
    lua_pushcfunction(S->L, do_set_settings);
    lua_pushlightuserdata(S->L, &a);
    status = lua_pcall(S->L, 1, 0, 0);
    lua_settop(S->L, 0);
    S->panic_jmp = NULL;
    return status == LUA_OK ? TWP_OK : TWP_ERR_RUNTIME;
}

static int do_has_function(lua_State *L)
{
    bytes_args *a = (bytes_args *)lua_touserdata(L, 1);
    lua_pushglobaltable(L);
    lua_pushstring(L, a->name);
    a->found = lua_rawget(L, -2) == LUA_TFUNCTION;
    return 0;
}

TWP_API int twp_has_function(twp_state *S, const char *name)
{
    jmp_buf jb;
    bytes_args a;
    if (S == NULL || S->broken || name == NULL) {
        return 0;
    }
    a.name = name;
    a.found = 0;
    begin_segment(S, 1000000, 0);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        return 0;
    }
    lua_settop(S->L, 0);
    lua_pushcfunction(S->L, do_has_function);
    lua_pushlightuserdata(S->L, &a);
    if (lua_pcall(S->L, 1, 0, 0) != LUA_OK) {
        a.found = 0;
    }
    lua_settop(S->L, 0);
    S->panic_jmp = NULL;
    return a.found;
}

/* ---- calls ----------------------------------------------------------------------------------------------------- */

typedef struct call_args {
    const char *fn;
    const unsigned char *p;
    size_t len;
    int id;
    int nargs;
    lua_State *co;
    int missing;
    int status;
    int nres;
    int code;
    twp_buf *out;
} call_args;

/* Leaves the call record of a->id on the stack top. */
static void push_record(lua_State *L, call_args *a)
{
    lua_rawgetp(L, LUA_REGISTRYINDEX, &CALLS_KEY);
    if (lua_rawgeti(L, -1, a->id) != LUA_TTABLE) {
        luaL_error(L, "no suspended call %d", a->id);
    }
    lua_remove(L, -2);
}

static int do_start(lua_State *L)
{
    call_args *a = (call_args *)lua_touserdata(L, 1);
    lua_State *co;
    int rec;
    lua_settop(L, 0);
    lua_rawgetp(L, LUA_REGISTRYINDEX, &CALLS_KEY);
    lua_newtable(L);
    rec = lua_gettop(L);

    lua_pushglobaltable(L);
    lua_pushstring(L, a->fn);
    if (lua_rawget(L, -2) != LUA_TFUNCTION) {
        a->missing = 1;
        return luaL_error(L, "the script does not define %s", a->fn);
    }
    lua_remove(L, -2);

    co = lua_newthread(L);
    lua_pushvalue(L, -1);
    lua_setfield(L, rec, "co");
    lua_pop(L, 1);

    lua_newtable(L);
    lua_pushvalue(L, -1);
    lua_setfield(L, rec, "cmds");
    lua_rawsetp(L, LUA_REGISTRYINDEX, &CUR_CMDS_KEY);

    twp_decode(L, a->p, a->len, 0);
    build_ctx(L);
    lua_pushvalue(L, -1);
    lua_setfield(L, rec, "ctx");

    lua_pushvalue(L, rec);
    a->id = luaL_ref(L, 1);
    if (!lua_checkstack(co, 4)) {
        return luaL_error(L, "out of memory");
    }
    lua_xmove(L, co, 2); /* function, ctx */
    a->co = co;
    a->nargs = 1;
    return 0;
}

static int do_prepare_resume(lua_State *L)
{
    call_args *a = (call_args *)lua_touserdata(L, 1);
    lua_State *co;
    lua_Integer i, n;
    lua_settop(L, 0);
    push_record(L, a);
    lua_getfield(L, -1, "co");
    co = lua_tothread(L, -1);
    if (co == NULL || lua_status(co) != LUA_YIELD) {
        a->missing = 1;
        return luaL_error(L, "call %d is not waiting", a->id);
    }
    lua_pop(L, 1);
    lua_getfield(L, -1, "cmds");
    lua_rawsetp(L, LUA_REGISTRYINDEX, &CUR_CMDS_KEY);

    twp_decode(L, a->p, a->len, 0);
    n = lua_istable(L, -1) ? (lua_Integer)lua_rawlen(L, -1) : 0;
    if (n > 16) {
        n = 16;
    }
    if (!lua_checkstack(co, (int)n + 4)) {
        return luaL_error(L, "out of memory");
    }
    for (i = 1; i <= n; i++) {
        lua_rawgeti(L, -1, i);
        lua_xmove(L, co, 1);
    }
    a->co = co;
    a->nargs = (int)n;
    return 0;
}

static int do_cancel(lua_State *L)
{
    call_args *a = (call_args *)lua_touserdata(L, 1);
    lua_settop(L, 0);
    lua_rawgetp(L, LUA_REGISTRYINDEX, &CALLS_KEY);
    luaL_unref(L, 1, a->id);
    lua_pushnil(L);
    lua_rawsetp(L, LUA_REGISTRYINDEX, &CUR_CMDS_KEY);
    return 0;
}

/* Encodes the outcome of a segment into a->out. Runs protected on the main thread. */
static int do_finish(lua_State *L)
{
    call_args *a = (call_args *)lua_touserdata(L, 1);
    lua_State *co = a->co;
    twp_buf *b = a->out;
    lua_settop(L, 0);
    luaL_checkstack(L, 8, "finishing a call");

    twp_buf_byte(L, b, 'T');
    if (a->code == TWP_YIELD) {
        twp_buf_u32(L, b, 1);
        twp_buf_str(L, b, "op", 2);
        if (a->nres >= 1) {
            lua_xmove(co, L, 1);
            twp_encode(L, -1, b, 1);
            lua_pop(L, 1);
        } else {
            twp_buf_byte(L, b, 'n');
        }
        if (a->nres > 1) {
            lua_pop(co, a->nres - 1);
        }
        return 0;
    }

    push_record(L, a);
    twp_buf_u32(L, b, a->code == TWP_OK ? 3 : 2);
    twp_buf_str(L, b, "cmds", 4);
    lua_getfield(L, -1, "cmds");
    twp_encode(L, -1, b, 1);
    lua_pop(L, 1);
    if (a->code == TWP_OK) {
        twp_buf_str(L, b, "vars", 4);
        lua_getfield(L, -1, "ctx");
        lua_getfield(L, -1, "vars");
        twp_encode(L, -1, b, 1);
        lua_pop(L, 2);
        twp_buf_str(L, b, "ret", 3);
        if (a->nres >= 1 && lua_status(co) == LUA_OK) {
            int t;
            lua_settop(co, 1);
            lua_xmove(co, L, 1);
            t = lua_type(L, -1);
            if (t == LUA_TFUNCTION || t == LUA_TUSERDATA || t == LUA_TTHREAD) {
                lua_pop(L, 1);
                lua_pushnil(L);
            }
            twp_encode(L, -1, b, 1);
            lua_pop(L, 1);
        } else {
            twp_buf_byte(L, b, 'n');
        }
    } else {
        twp_buf_str(L, b, "error", 5);
        if (lua_type(co, -1) == LUA_TSTRING) {
            size_t n;
            const char *m = lua_tolstring(co, -1, &n);
            twp_buf_str(L, b, m, n > 4096 ? 4096 : n);
        } else {
            twp_buf_str(L, b, "the script raised a non-string error", 36);
        }
    }
    lua_rawgetp(L, LUA_REGISTRYINDEX, &CALLS_KEY);
    luaL_unref(L, -1, a->id);
    lua_pushnil(L);
    lua_rawsetp(L, LUA_REGISTRYINDEX, &CUR_CMDS_KEY);
    return 0;
}

/* Writes {error = msg} for a failure before the coroutine ran. */
static void write_plain_error(twp_buf *b, const char *msg)
{
    size_t n = msg == NULL ? 0 : strlen(msg);
    size_t need = 1 + 4 + 1 + 4 + 5 + 1 + 4 + n;
    if (n > 4096) {
        n = 4096;
    }
    b->data = (unsigned char *)malloc(need);
    if (b->data == NULL) {
        return;
    }
    b->len = 0;
    b->data[b->len++] = 'T';
    b->data[b->len++] = 1;
    b->data[b->len++] = 0;
    b->data[b->len++] = 0;
    b->data[b->len++] = 0;
    b->data[b->len++] = 's';
    b->data[b->len++] = 5;
    b->data[b->len++] = 0;
    b->data[b->len++] = 0;
    b->data[b->len++] = 0;
    memcpy(b->data + b->len, "error", 5);
    b->len += 5;
    b->data[b->len++] = 's';
    b->data[b->len++] = (unsigned char)n;
    b->data[b->len++] = (unsigned char)(n >> 8);
    b->data[b->len++] = 0;
    b->data[b->len++] = 0;
    memcpy(b->data + b->len, msg, n);
    b->len += n;
}

static int run_segment(twp_state *S, call_args *a, unsigned char **out, size_t *outlen, int64_t *used)
{
    lua_State *L = S->L;
    twp_buf b;
    int status;
    memset(&b, 0, sizeof(b));
    b.limit = S->out_limit;

    lua_sethook(a->co, twp_hook, LUA_MASKCOUNT, TWP_HOOK_STEP);
    S->phase_co = a->co;
    status = lua_resume(a->co, L, a->nargs, &a->nres);
    S->phase_co = NULL;
    a->status = status;
    if (status == LUA_OK) {
        a->code = TWP_OK;
    } else if (status == LUA_YIELD) {
        if (S->our_yield) {
            a->code = TWP_YIELD;
        } else {
            lua_settop(a->co, 0);
            lua_pushliteral(a->co, "coroutine.yield cannot be used at phase level; use it inside your own coroutine");
            a->code = TWP_ERR_RUNTIME;
        }
    } else {
        a->code = limit_code(S, status);
    }
    if (used != NULL) {
        *used = S->used;
    }

    a->out = &b;
    lua_settop(L, 0);
    lua_pushcfunction(L, do_finish);
    lua_pushlightuserdata(L, a);
    /* Encoding never runs script code, but it may allocate; give it room past a tripped limit. */
    S->tripped = TWP_TRIP_NONE;
    S->budget = INT64_MAX;
    S->deadline_us = 0;
    if (lua_pcall(L, 1, 0, 0) != LUA_OK) {
        const char *msg = lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "could not encode the result";
        free(b.data);
        memset(&b, 0, sizeof(b));
        write_plain_error(&b, msg);
        lua_settop(L, 0);
        lua_pushcfunction(L, do_cancel);
        lua_pushlightuserdata(L, a);
        lua_pcall(L, 1, 0, 0);
        if (a->code == TWP_OK || a->code == TWP_YIELD) {
            a->code = TWP_ERR_OUTPUT;
        }
    }
    lua_settop(L, 0);
    *out = b.data;
    *outlen = b.len;
    return a->code;
}

TWP_API int twp_start(twp_state *S, const char *fn, const unsigned char *snap, size_t len, int64_t budget, int64_t max_us,
    int32_t *call_id, unsigned char **out, size_t *outlen, int64_t *used)
{
    jmp_buf jb;
    call_args a;
    int code;
    lua_State *L;
    *out = NULL;
    *outlen = 0;
    *call_id = 0;
    if (used != NULL) {
        *used = 0;
    }
    if (S == NULL || S->broken) {
        return TWP_ERR_BROKEN;
    }
    L = S->L;
    memset(&a, 0, sizeof(a));
    a.fn = fn;
    a.p = snap;
    a.len = len;
    begin_segment(S, budget, max_us);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        return TWP_ERR_BROKEN;
    }
    lua_settop(L, 0);
    lua_pushcfunction(L, do_start);
    lua_pushlightuserdata(L, &a);
    if (lua_pcall(L, 1, 0, 0) != LUA_OK) {
        twp_buf b;
        memset(&b, 0, sizeof(b));
        write_plain_error(&b, lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "could not start the call");
        code = a.missing ? TWP_ERR_ARGUMENT : limit_code(S, LUA_ERRRUN);
        lua_settop(L, 0);
        S->panic_jmp = NULL;
        *out = b.data;
        *outlen = b.len;
        return code;
    }
    lua_settop(L, 0);
    *call_id = a.id;
    code = run_segment(S, &a, out, outlen, used);
    S->panic_jmp = NULL;
    return code;
}

TWP_API int twp_resume(twp_state *S, int32_t call_id, const unsigned char *vals, size_t len, int64_t budget, int64_t max_us,
    unsigned char **out, size_t *outlen, int64_t *used)
{
    jmp_buf jb;
    call_args a;
    int code;
    lua_State *L;
    *out = NULL;
    *outlen = 0;
    if (used != NULL) {
        *used = 0;
    }
    if (S == NULL || S->broken) {
        return TWP_ERR_BROKEN;
    }
    L = S->L;
    memset(&a, 0, sizeof(a));
    a.id = call_id;
    a.p = vals;
    a.len = len;
    begin_segment(S, budget, max_us);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        return TWP_ERR_BROKEN;
    }
    lua_settop(L, 0);
    lua_pushcfunction(L, do_prepare_resume);
    lua_pushlightuserdata(L, &a);
    if (lua_pcall(L, 1, 0, 0) != LUA_OK) {
        twp_buf b;
        memset(&b, 0, sizeof(b));
        write_plain_error(&b, lua_type(L, -1) == LUA_TSTRING ? lua_tostring(L, -1) : "could not resume the call");
        code = a.missing ? TWP_ERR_ARGUMENT : limit_code(S, LUA_ERRRUN);
        lua_settop(L, 0);
        lua_pushcfunction(L, do_cancel);
        lua_pushlightuserdata(L, &a);
        lua_pcall(L, 1, 0, 0);
        lua_settop(L, 0);
        S->panic_jmp = NULL;
        *out = b.data;
        *outlen = b.len;
        return code;
    }
    lua_settop(L, 0);
    code = run_segment(S, &a, out, outlen, used);
    S->panic_jmp = NULL;
    return code;
}

TWP_API void twp_cancel(twp_state *S, int32_t call_id)
{
    jmp_buf jb;
    call_args a;
    if (S == NULL || S->broken) {
        return;
    }
    memset(&a, 0, sizeof(a));
    a.id = call_id;
    begin_segment(S, 1000000, 0);
    S->panic_jmp = &jb;
    if (setjmp(jb) != 0) {
        S->panic_jmp = NULL;
        return;
    }
    lua_settop(S->L, 0);
    lua_pushcfunction(S->L, do_cancel);
    lua_pushlightuserdata(S->L, &a);
    lua_pcall(S->L, 1, 0, 0);
    lua_settop(S->L, 0);
    S->panic_jmp = NULL;
}
