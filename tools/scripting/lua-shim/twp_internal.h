/*
 * Titanium Lua host shim. Compiled together with the Lua 5.4 core into one shared library
 * (lua54.dll / liblua54.so / liblua54.dylib). MIT licensed, same as Titanium Web Proxy.
 *
 * The managed host never registers a callback into Lua. It hands a snapshot in, Lua runs, and the shim hands a
 * command buffer back. Lua errors (longjmp) stay inside lua_pcall / lua_resume frames of this library.
 */
#ifndef TWP_INTERNAL_H
#define TWP_INTERNAL_H

#include <setjmp.h>
#include <stddef.h>
#include <stdint.h>

/* The shim is linked into the same library as the Lua core, so the Lua API is local, not imported. */
#ifndef LUA_LIB
#define LUA_LIB
#endif
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"

#if defined(_WIN32)
#define TWP_API __declspec(dllexport)
#else
#define TWP_API __attribute__((visibility("default")))
#endif

#define TWP_ABI_VERSION 1

/* Return codes of the exported entry points. Kept in sync with LuaNative.cs. */
#define TWP_OK 0
#define TWP_YIELD 1
#define TWP_ERR_RUNTIME (-1)
#define TWP_ERR_INSTRUCTIONS (-2)
#define TWP_ERR_MEMORY (-3)
#define TWP_ERR_DEADLINE (-4)
#define TWP_ERR_ARGUMENT (-5)
#define TWP_ERR_OUTPUT (-6)
#define TWP_ERR_BROKEN (-7)

#define TWP_TRIP_NONE 0
#define TWP_TRIP_INSTRUCTIONS 1
#define TWP_TRIP_DEADLINE 2

/* Instructions between two hook calls. The hook also checks the deadline. */
#define TWP_HOOK_STEP 1000

/* Most commands one call may queue. */
#define TWP_MAX_COMMANDS 4096

/* Largest string the guarded string.rep, string.format, and the libraries will build. */
#define TWP_MAX_STRING_BUILD (16 * 1024 * 1024)

typedef struct twp_state {
    lua_State *L;
    size_t mem_used;
    size_t mem_limit;
    int mem_refused;
    int64_t budget;
    int64_t used;
    int64_t deadline_us;
    int tripped;
    lua_State *phase_co;
    int our_yield;
    size_t out_limit;
    int broken;
    jmp_buf *panic_jmp;
} twp_state;

twp_state *twp_from(lua_State *L);

/* Charges native work against the instruction budget and the deadline; raises when either is spent. */
void twp_charge(lua_State *L, int64_t units);

/* Raises unless L is the running phase coroutine and can yield, then marks the coming yield as the host's. */
void twp_check_can_wait(lua_State *L, const char *what);

/* Appends the top n values, as one command array, to the current call's command list. No-op outside a call. */
void twp_emit(lua_State *L, int n);

/* Platform */
int64_t twp_mono_us(void);
int64_t twp_unix_ms(void);
int twp_random_bytes(void *buf, size_t n);

#if defined(_WIN32)
typedef struct twp_mutex { void *srw; } twp_mutex; /* an SRWLOCK: one pointer, zero means unlocked */
#else
#include <pthread.h>
typedef struct twp_mutex { pthread_mutex_t mu; } twp_mutex;
#endif
void twp_mutex_init(twp_mutex *m);
void twp_mutex_lock(twp_mutex *m);
void twp_mutex_unlock(twp_mutex *m);
void twp_global_lock(void);
void twp_global_unlock(void);

/* Codec: a tagged binary form of nil / boolean / integer / float / string / table. */
typedef struct twp_buf {
    unsigned char *data;
    size_t len;
    size_t cap;
    size_t limit;
} twp_buf;

void twp_encode(lua_State *L, int idx, twp_buf *b, int depth);
void twp_buf_put(lua_State *L, twp_buf *b, const void *p, size_t n);
void twp_buf_byte(lua_State *L, twp_buf *b, unsigned char c);
void twp_buf_u32(lua_State *L, twp_buf *b, uint32_t v);
void twp_buf_str(lua_State *L, twp_buf *b, const char *s, size_t n);
/* Decodes one value and pushes it. Returns bytes consumed. Raises on malformed input. */
size_t twp_decode(lua_State *L, const unsigned char *p, size_t len, int depth);

/* Libraries */
void twp_open_strguard(lua_State *L);
void twp_open_libs(lua_State *L);
void twp_open_crypto(lua_State *L);
void twp_open_json(lua_State *L);
void twp_open_regex(lua_State *L);
void twp_open_shared(lua_State *L);

/* Crypto primitives shared by the HMAC and UUID code. */
typedef enum { TWP_MD5, TWP_SHA1, TWP_SHA256, TWP_SHA512 } twp_hash_alg;
size_t twp_hash(twp_hash_alg alg, const void *data, size_t len, unsigned char *out);

#endif
