/* crypto.md5 / sha1 / sha256 / sha512 / hmac / equals. Digests are returned as raw bytes; use hex.encode to print. */
#include "twp_internal.h"

#include <string.h>

/* ---- MD5 (RFC 1321) -------------------------------------------------------------------------------------------- */

typedef struct { uint32_t h[4]; uint64_t n; unsigned char buf[64]; size_t used; } md5_ctx;

#define ROTL32(x, c) (((x) << (c)) | ((x) >> (32 - (c))))

static void md5_block(md5_ctx *c, const unsigned char *p)
{
    static const uint32_t K[64] = {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391};
    static const int R[64] = {7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20,
        5, 9, 14, 20, 5, 9, 14, 20, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10,
        15, 21, 6, 10, 15, 21, 6, 10, 15, 21};
    uint32_t w[16], a = c->h[0], b = c->h[1], cc = c->h[2], d = c->h[3];
    int i;
    for (i = 0; i < 16; i++) {
        w[i] = (uint32_t)p[i * 4] | ((uint32_t)p[i * 4 + 1] << 8) | ((uint32_t)p[i * 4 + 2] << 16) |
            ((uint32_t)p[i * 4 + 3] << 24);
    }
    for (i = 0; i < 64; i++) {
        uint32_t f, t;
        int g;
        if (i < 16) {
            f = (b & cc) | (~b & d);
            g = i;
        } else if (i < 32) {
            f = (d & b) | (~d & cc);
            g = (5 * i + 1) % 16;
        } else if (i < 48) {
            f = b ^ cc ^ d;
            g = (3 * i + 5) % 16;
        } else {
            f = cc ^ (b | ~d);
            g = (7 * i) % 16;
        }
        t = d;
        d = cc;
        cc = b;
        b = b + ROTL32(a + f + K[i] + w[g], R[i]);
        a = t;
    }
    c->h[0] += a;
    c->h[1] += b;
    c->h[2] += cc;
    c->h[3] += d;
}

static void md5(const unsigned char *p, size_t n, unsigned char out[16])
{
    md5_ctx c;
    unsigned char tail[128];
    size_t rem, i, padlen;
    uint64_t bits = (uint64_t)n * 8;
    c.h[0] = 0x67452301;
    c.h[1] = 0xefcdab89;
    c.h[2] = 0x98badcfe;
    c.h[3] = 0x10325476;
    while (n >= 64) {
        md5_block(&c, p);
        p += 64;
        n -= 64;
    }
    rem = n;
    memcpy(tail, p, rem);
    tail[rem] = 0x80;
    padlen = rem < 56 ? 64 : 128;
    memset(tail + rem + 1, 0, padlen - rem - 1);
    for (i = 0; i < 8; i++) {
        tail[padlen - 8 + i] = (unsigned char)(bits >> (8 * i));
    }
    md5_block(&c, tail);
    if (padlen == 128) {
        md5_block(&c, tail + 64);
    }
    for (i = 0; i < 4; i++) {
        out[i * 4] = (unsigned char)c.h[i];
        out[i * 4 + 1] = (unsigned char)(c.h[i] >> 8);
        out[i * 4 + 2] = (unsigned char)(c.h[i] >> 16);
        out[i * 4 + 3] = (unsigned char)(c.h[i] >> 24);
    }
}

/* ---- SHA-1 and SHA-256 (FIPS 180-4) ---------------------------------------------------------------------------- */

static uint32_t be32(const unsigned char *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static void sha1_block(uint32_t h[5], const unsigned char *p)
{
    uint32_t w[80], a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
    int i;
    for (i = 0; i < 16; i++) {
        w[i] = be32(p + i * 4);
    }
    for (i = 16; i < 80; i++) {
        w[i] = ROTL32(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
    }
    for (i = 0; i < 80; i++) {
        uint32_t f, k, t;
        if (i < 20) {
            f = (b & c) | (~b & d);
            k = 0x5a827999;
        } else if (i < 40) {
            f = b ^ c ^ d;
            k = 0x6ed9eba1;
        } else if (i < 60) {
            f = (b & c) | (b & d) | (c & d);
            k = 0x8f1bbcdc;
        } else {
            f = b ^ c ^ d;
            k = 0xca62c1d6;
        }
        t = ROTL32(a, 5) + f + e + k + w[i];
        e = d;
        d = c;
        c = ROTL32(b, 30);
        b = a;
        a = t;
    }
    h[0] += a;
    h[1] += b;
    h[2] += c;
    h[3] += d;
    h[4] += e;
}

#define ROTR32(x, n) (((x) >> (n)) | ((x) << (32 - (n))))

static void sha256_block(uint32_t h[8], const unsigned char *p)
{
    static const uint32_t K[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
    uint32_t w[64], a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    int i;
    for (i = 0; i < 16; i++) {
        w[i] = be32(p + i * 4);
    }
    for (i = 16; i < 64; i++) {
        uint32_t s0 = ROTR32(w[i - 15], 7) ^ ROTR32(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = ROTR32(w[i - 2], 17) ^ ROTR32(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    for (i = 0; i < 64; i++) {
        uint32_t S1 = ROTR32(e, 6) ^ ROTR32(e, 11) ^ ROTR32(e, 25);
        uint32_t ch = (e & f) ^ (~e & g);
        uint32_t t1 = hh + S1 + ch + K[i] + w[i];
        uint32_t S0 = ROTR32(a, 2) ^ ROTR32(a, 13) ^ ROTR32(a, 22);
        uint32_t mj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t t2 = S0 + mj;
        hh = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }
    h[0] += a;
    h[1] += b;
    h[2] += c;
    h[3] += d;
    h[4] += e;
    h[5] += f;
    h[6] += g;
    h[7] += hh;
}

/* Merkle-Damgard driver for the 64-byte block, big-endian length hashes (SHA-1, SHA-256). */
static void md_be64(void (*block)(uint32_t *, const unsigned char *), uint32_t *h, int words, const unsigned char *p,
    size_t n, unsigned char *out)
{
    unsigned char tail[128];
    size_t rem, padlen, i;
    uint64_t bits = (uint64_t)n * 8;
    while (n >= 64) {
        block(h, p);
        p += 64;
        n -= 64;
    }
    rem = n;
    memcpy(tail, p, rem);
    tail[rem] = 0x80;
    padlen = rem < 56 ? 64 : 128;
    memset(tail + rem + 1, 0, padlen - rem - 1);
    for (i = 0; i < 8; i++) {
        tail[padlen - 1 - i] = (unsigned char)(bits >> (8 * i));
    }
    block(h, tail);
    if (padlen == 128) {
        block(h, tail + 64);
    }
    for (i = 0; i < (size_t)words; i++) {
        out[i * 4] = (unsigned char)(h[i] >> 24);
        out[i * 4 + 1] = (unsigned char)(h[i] >> 16);
        out[i * 4 + 2] = (unsigned char)(h[i] >> 8);
        out[i * 4 + 3] = (unsigned char)h[i];
    }
}

static void sha1_block_v(uint32_t *h, const unsigned char *p) { sha1_block(h, p); }
static void sha256_block_v(uint32_t *h, const unsigned char *p) { sha256_block(h, p); }

/* ---- SHA-512 --------------------------------------------------------------------------------------------------- */

#define ROTR64(x, n) (((x) >> (n)) | ((x) << (64 - (n))))

static uint64_t be64(const unsigned char *p)
{
    uint64_t v = 0;
    int i;
    for (i = 0; i < 8; i++) {
        v = (v << 8) | p[i];
    }
    return v;
}

static void sha512_block(uint64_t h[8], const unsigned char *p)
{
    static const uint64_t K[80] = {
        0x428a2f98d728ae22ULL, 0x7137449123ef65cdULL, 0xb5c0fbcfec4d3b2fULL, 0xe9b5dba58189dbbcULL,
        0x3956c25bf348b538ULL, 0x59f111f1b605d019ULL, 0x923f82a4af194f9bULL, 0xab1c5ed5da6d8118ULL,
        0xd807aa98a3030242ULL, 0x12835b0145706fbeULL, 0x243185be4ee4b28cULL, 0x550c7dc3d5ffb4e2ULL,
        0x72be5d74f27b896fULL, 0x80deb1fe3b1696b1ULL, 0x9bdc06a725c71235ULL, 0xc19bf174cf692694ULL,
        0xe49b69c19ef14ad2ULL, 0xefbe4786384f25e3ULL, 0x0fc19dc68b8cd5b5ULL, 0x240ca1cc77ac9c65ULL,
        0x2de92c6f592b0275ULL, 0x4a7484aa6ea6e483ULL, 0x5cb0a9dcbd41fbd4ULL, 0x76f988da831153b5ULL,
        0x983e5152ee66dfabULL, 0xa831c66d2db43210ULL, 0xb00327c898fb213fULL, 0xbf597fc7beef0ee4ULL,
        0xc6e00bf33da88fc2ULL, 0xd5a79147930aa725ULL, 0x06ca6351e003826fULL, 0x142929670a0e6e70ULL,
        0x27b70a8546d22ffcULL, 0x2e1b21385c26c926ULL, 0x4d2c6dfc5ac42aedULL, 0x53380d139d95b3dfULL,
        0x650a73548baf63deULL, 0x766a0abb3c77b2a8ULL, 0x81c2c92e47edaee6ULL, 0x92722c851482353bULL,
        0xa2bfe8a14cf10364ULL, 0xa81a664bbc423001ULL, 0xc24b8b70d0f89791ULL, 0xc76c51a30654be30ULL,
        0xd192e819d6ef5218ULL, 0xd69906245565a910ULL, 0xf40e35855771202aULL, 0x106aa07032bbd1b8ULL,
        0x19a4c116b8d2d0c8ULL, 0x1e376c085141ab53ULL, 0x2748774cdf8eeb99ULL, 0x34b0bcb5e19b48a8ULL,
        0x391c0cb3c5c95a63ULL, 0x4ed8aa4ae3418acbULL, 0x5b9cca4f7763e373ULL, 0x682e6ff3d6b2b8a3ULL,
        0x748f82ee5defb2fcULL, 0x78a5636f43172f60ULL, 0x84c87814a1f0ab72ULL, 0x8cc702081a6439ecULL,
        0x90befffa23631e28ULL, 0xa4506cebde82bde9ULL, 0xbef9a3f7b2c67915ULL, 0xc67178f2e372532bULL,
        0xca273eceea26619cULL, 0xd186b8c721c0c207ULL, 0xeada7dd6cde0eb1eULL, 0xf57d4f7fee6ed178ULL,
        0x06f067aa72176fbaULL, 0x0a637dc5a2c898a6ULL, 0x113f9804bef90daeULL, 0x1b710b35131c471bULL,
        0x28db77f523047d84ULL, 0x32caab7b40c72493ULL, 0x3c9ebe0a15c9bebcULL, 0x431d67c49c100d4cULL,
        0x4cc5d4becb3e42b6ULL, 0x597f299cfc657e2aULL, 0x5fcb6fab3ad6faecULL, 0x6c44198c4a475817ULL};
    uint64_t w[80], a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    int i;
    for (i = 0; i < 16; i++) {
        w[i] = be64(p + i * 8);
    }
    for (i = 16; i < 80; i++) {
        uint64_t s0 = ROTR64(w[i - 15], 1) ^ ROTR64(w[i - 15], 8) ^ (w[i - 15] >> 7);
        uint64_t s1 = ROTR64(w[i - 2], 19) ^ ROTR64(w[i - 2], 61) ^ (w[i - 2] >> 6);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    for (i = 0; i < 80; i++) {
        uint64_t S1 = ROTR64(e, 14) ^ ROTR64(e, 18) ^ ROTR64(e, 41);
        uint64_t ch = (e & f) ^ (~e & g);
        uint64_t t1 = hh + S1 + ch + K[i] + w[i];
        uint64_t S0 = ROTR64(a, 28) ^ ROTR64(a, 34) ^ ROTR64(a, 39);
        uint64_t mj = (a & b) ^ (a & c) ^ (b & c);
        uint64_t t2 = S0 + mj;
        hh = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = t1 + t2;
    }
    h[0] += a;
    h[1] += b;
    h[2] += c;
    h[3] += d;
    h[4] += e;
    h[5] += f;
    h[6] += g;
    h[7] += hh;
}

static void sha512(const unsigned char *p, size_t n, unsigned char out[64])
{
    uint64_t h[8] = {0x6a09e667f3bcc908ULL, 0xbb67ae8584caa73bULL, 0x3c6ef372fe94f82bULL, 0xa54ff53a5f1d36f1ULL,
        0x510e527fade682d1ULL, 0x9b05688c2b3e6c1fULL, 0x1f83d9abfb41bd6bULL, 0x5be0cd19137e2179ULL};
    unsigned char tail[256];
    size_t rem, padlen, i;
    uint64_t bits = (uint64_t)n * 8;
    while (n >= 128) {
        sha512_block(h, p);
        p += 128;
        n -= 128;
    }
    rem = n;
    memcpy(tail, p, rem);
    tail[rem] = 0x80;
    padlen = rem < 112 ? 128 : 256;
    memset(tail + rem + 1, 0, padlen - rem - 1);
    for (i = 0; i < 8; i++) {
        tail[padlen - 1 - i] = (unsigned char)(bits >> (8 * i));
    }
    sha512_block(h, tail);
    if (padlen == 256) {
        sha512_block(h, tail + 128);
    }
    for (i = 0; i < 8; i++) {
        int j;
        for (j = 0; j < 8; j++) {
            out[i * 8 + j] = (unsigned char)(h[i] >> (56 - 8 * j));
        }
    }
}

size_t twp_hash(twp_hash_alg alg, const void *data, size_t len, unsigned char *out)
{
    const unsigned char *p = (const unsigned char *)data;
    switch (alg) {
    case TWP_MD5:
        md5(p, len, out);
        return 16;
    case TWP_SHA1: {
        uint32_t h[5] = {0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0};
        md_be64(sha1_block_v, h, 5, p, len, out);
        return 20;
    }
    case TWP_SHA256: {
        uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
        md_be64(sha256_block_v, h, 8, p, len, out);
        return 32;
    }
    default:
        sha512(p, len, out);
        return 64;
    }
}

static size_t block_size(twp_hash_alg alg) { return alg == TWP_SHA512 ? 128 : 64; }

static int parse_alg(lua_State *L, int arg)
{
    static const char *const names[] = {"md5", "sha1", "sha256", "sha512", NULL};
    return luaL_checkoption(L, arg, NULL, names);
}

static int l_digest(lua_State *L, twp_hash_alg alg)
{
    size_t n;
    unsigned char out[64];
    const char *s = luaL_checklstring(L, 1, &n);
    size_t len;
    twp_charge(L, (int64_t)(n / 16));
    len = twp_hash(alg, s, n, out);
    lua_pushlstring(L, (const char *)out, len);
    return 1;
}

static int l_md5(lua_State *L) { return l_digest(L, TWP_MD5); }
static int l_sha1(lua_State *L) { return l_digest(L, TWP_SHA1); }
static int l_sha256(lua_State *L) { return l_digest(L, TWP_SHA256); }
static int l_sha512(lua_State *L) { return l_digest(L, TWP_SHA512); }

/* crypto.hmac(alg, key, message) per RFC 2104. The message is concatenated with the inner pad in a Lua buffer. */
static int l_hmac(lua_State *L)
{
    twp_hash_alg alg = (twp_hash_alg)parse_alg(L, 1);
    size_t kn, mn, bs = block_size(alg), i, dl;
    const char *key = luaL_checklstring(L, 2, &kn);
    const char *msg = luaL_checklstring(L, 3, &mn);
    unsigned char k0[128], pad[128], inner[64], outer_in[128 + 64], out[64];
    luaL_Buffer b;
    char *ib;
    twp_charge(L, (int64_t)(mn / 16));
    memset(k0, 0, sizeof(k0));
    if (kn > bs) {
        twp_hash(alg, key, kn, k0);
    } else {
        memcpy(k0, key, kn);
    }
    for (i = 0; i < bs; i++) {
        pad[i] = k0[i] ^ 0x36;
    }
    ib = luaL_buffinitsize(L, &b, bs + mn);
    memcpy(ib, pad, bs);
    memcpy(ib + bs, msg, mn);
    dl = twp_hash(alg, ib, bs + mn, inner);
    for (i = 0; i < bs; i++) {
        outer_in[i] = k0[i] ^ 0x5c;
    }
    memcpy(outer_in + bs, inner, dl);
    dl = twp_hash(alg, outer_in, bs + dl, out);
    luaL_pushresultsize(&b, 0);
    lua_pop(L, 1);
    lua_pushlstring(L, (const char *)out, dl);
    return 1;
}

/* crypto.equals(a, b): time depends only on the lengths, not on where the strings differ. */
static int l_equals(lua_State *L)
{
    size_t an, bn, i;
    const unsigned char *a = (const unsigned char *)luaL_checklstring(L, 1, &an);
    const unsigned char *b = (const unsigned char *)luaL_checklstring(L, 2, &bn);
    unsigned int diff = (unsigned int)(an ^ bn);
    size_t n = an < bn ? an : bn;
    for (i = 0; i < n; i++) {
        diff |= (unsigned int)(a[i] ^ b[i]);
    }
    lua_pushboolean(L, diff == 0);
    return 1;
}

void twp_open_crypto(lua_State *L)
{
    static const luaL_Reg fns[] = {
        {"md5", l_md5},
        {"sha1", l_sha1},
        {"sha256", l_sha256},
        {"sha512", l_sha512},
        {"hmac", l_hmac},
        {"equals", l_equals},
        {NULL, NULL},
    };
    luaL_newlib(L, fns);
    lua_setglobal(L, "crypto");
}
