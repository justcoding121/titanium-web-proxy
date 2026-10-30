/*
 * Guarded string functions. The pattern matcher is the Lua 5.4 lstrlib matcher (MIT, Copyright (C) 1994-2024
 * Lua.org, PUC-Rio) with a step budget: backtracking runs in C where the instruction hook cannot see it, so each
 * call counts its steps, stops at TWP_PATTERN_STEPS, and charges what it used to the instruction budget.
 * string.rep and string.format refuse results past TWP_MAX_STRING_BUILD; string.dump is removed.
 */
#include "twp_internal.h"

#include <ctype.h>
#include <string.h>

#define TWP_PATTERN_STEPS 5000000
#define TWP_PATTERN_MAX 4096
#define TWP_FORMAT_MAX 8192

#define uchar(c) ((unsigned char)(c))
#define L_ESC '%'
#define SPECIALS "^$*+?.([%-"
#define CAP_UNFINISHED (-1)
#define CAP_POSITION (-2)
#define TWP_MAXCAPTURES 32
#define TWP_MAXCCALLS 200

typedef struct MatchState {
    const char *src_init;
    const char *src_end;
    const char *p_end;
    lua_State *L;
    int matchdepth;
    unsigned char level;
    int64_t steps;
    struct {
        const char *init;
        ptrdiff_t len;
    } capture[TWP_MAXCAPTURES];
} MatchState;

#define STEP(ms) \
    do { \
        if (++(ms)->steps > TWP_PATTERN_STEPS) \
            luaL_error((ms)->L, "pattern too complex (more than %d matching steps)", TWP_PATTERN_STEPS); \
    } while (0)

static const char *match(MatchState *ms, const char *s, const char *p);

static int check_capture(MatchState *ms, int l)
{
    l -= '1';
    if (l < 0 || l >= ms->level || ms->capture[l].len == CAP_UNFINISHED) {
        return luaL_error(ms->L, "invalid capture index %%%d", l + 1);
    }
    return l;
}

static int capture_to_close(MatchState *ms)
{
    int level = ms->level;
    for (level--; level >= 0; level--) {
        if (ms->capture[level].len == CAP_UNFINISHED) {
            return level;
        }
    }
    return luaL_error(ms->L, "invalid pattern capture");
}

static const char *classEnd(MatchState *ms, const char *p)
{
    switch (*p++) {
    case L_ESC:
        if (p == ms->p_end) {
            luaL_error(ms->L, "malformed pattern (ends with '%%')");
        }
        return p + 1;
    case '[':
        if (*p == '^') {
            p++;
        }
        do {
            if (p == ms->p_end) {
                luaL_error(ms->L, "malformed pattern (missing ']')");
            }
            if (*(p++) == L_ESC && p < ms->p_end) {
                p++;
            }
        } while (*p != ']');
        return p + 1;
    default:
        return p;
    }
}

static int match_class(int c, int cl)
{
    int res;
    switch (tolower(cl)) {
    case 'a': res = isalpha(c); break;
    case 'c': res = iscntrl(c); break;
    case 'd': res = isdigit(c); break;
    case 'g': res = isgraph(c); break;
    case 'l': res = islower(c); break;
    case 'p': res = ispunct(c); break;
    case 's': res = isspace(c); break;
    case 'u': res = isupper(c); break;
    case 'w': res = isalnum(c); break;
    case 'x': res = isxdigit(c); break;
    default: return (cl == c);
    }
    if (isupper(cl)) {
        res = !res;
    }
    return res;
}

static int matchbracketclass(int c, const char *p, const char *ec)
{
    int sig = 1;
    if (*(p + 1) == '^') {
        sig = 0;
        p++;
    }
    while (++p < ec) {
        if (*p == L_ESC) {
            p++;
            if (match_class(c, uchar(*p))) {
                return sig;
            }
        } else if (*(p + 1) == '-' && (p + 2 < ec)) {
            p += 2;
            if (uchar(*(p - 2)) <= c && c <= uchar(*p)) {
                return sig;
            }
        } else if (uchar(*p) == c) {
            return sig;
        }
    }
    return !sig;
}

static int singlematch(MatchState *ms, const char *s, const char *p, const char *ep)
{
    if (s >= ms->src_end) {
        return 0;
    }
    switch (*p) {
    case '.': return 1;
    case L_ESC: return match_class(uchar(*s), uchar(*(p + 1)));
    case '[': return matchbracketclass(uchar(*s), p, ep - 1);
    default: return (uchar(*p) == uchar(*s));
    }
}

static const char *matchbalance(MatchState *ms, const char *s, const char *p)
{
    if (p >= ms->p_end - 1) {
        luaL_error(ms->L, "malformed pattern (missing arguments to '%%b')");
    }
    if (s >= ms->src_end || *s != *p) {
        return NULL;
    } else {
        int b = *p;
        int e = *(p + 1);
        int cont = 1;
        while (++s < ms->src_end) {
            STEP(ms);
            if (*s == e) {
                if (--cont == 0) {
                    return s + 1;
                }
            } else if (*s == b) {
                cont++;
            }
        }
    }
    return NULL;
}

static const char *max_expand(MatchState *ms, const char *s, const char *p, const char *ep)
{
    ptrdiff_t i = 0;
    while (singlematch(ms, s + i, p, ep)) {
        STEP(ms);
        i++;
    }
    while (i >= 0) {
        const char *res = match(ms, (s + i), ep + 1);
        if (res) {
            return res;
        }
        i--;
    }
    return NULL;
}

static const char *min_expand(MatchState *ms, const char *s, const char *p, const char *ep)
{
    for (;;) {
        const char *res = match(ms, s, ep + 1);
        if (res != NULL) {
            return res;
        } else if (singlematch(ms, s, p, ep)) {
            s++;
        } else {
            return NULL;
        }
    }
}

static const char *start_capture(MatchState *ms, const char *s, const char *p, int what)
{
    const char *res;
    int level = ms->level;
    if (level >= TWP_MAXCAPTURES) {
        luaL_error(ms->L, "too many captures");
    }
    ms->capture[level].init = s;
    ms->capture[level].len = what;
    ms->level = (unsigned char)(level + 1);
    if ((res = match(ms, s, p)) == NULL) {
        ms->level--;
    }
    return res;
}

static const char *end_capture(MatchState *ms, const char *s, const char *p)
{
    int l = capture_to_close(ms);
    const char *res;
    ms->capture[l].len = s - ms->capture[l].init;
    if ((res = match(ms, s, p)) == NULL) {
        ms->capture[l].len = CAP_UNFINISHED;
    }
    return res;
}

static const char *match_capture(MatchState *ms, const char *s, int l)
{
    size_t len;
    l = check_capture(ms, l);
    len = (size_t)ms->capture[l].len;
    if ((size_t)(ms->src_end - s) >= len && memcmp(ms->capture[l].init, s, len) == 0) {
        return s + len;
    }
    return NULL;
}

static const char *match(MatchState *ms, const char *s, const char *p)
{
    if (ms->matchdepth-- == 0) {
        luaL_error(ms->L, "pattern too complex");
    }
init:
    STEP(ms);
    if (p != ms->p_end) {
        switch (*p) {
        case '(':
            if (*(p + 1) == ')') {
                s = start_capture(ms, s, p + 2, CAP_POSITION);
            } else {
                s = start_capture(ms, s, p + 1, CAP_UNFINISHED);
            }
            break;
        case ')':
            s = end_capture(ms, s, p + 1);
            break;
        case '$':
            if ((p + 1) != ms->p_end) {
                goto dflt;
            }
            s = (s == ms->src_end) ? s : NULL;
            break;
        case L_ESC:
            switch (*(p + 1)) {
            case 'b':
                s = matchbalance(ms, s, p + 2);
                if (s != NULL) {
                    p += 4;
                    goto init;
                }
                break;
            case 'f': {
                const char *ep;
                char previous;
                p += 2;
                if (*p != '[') {
                    luaL_error(ms->L, "missing '[' after '%%f' in pattern");
                }
                ep = classEnd(ms, p);
                previous = (s == ms->src_init) ? '\0' : *(s - 1);
                if (!matchbracketclass(uchar(previous), p, ep - 1) &&
                    matchbracketclass(s < ms->src_end ? uchar(*s) : 0, p, ep - 1)) {
                    p = ep;
                    goto init;
                }
                s = NULL;
                break;
            }
            case '0': case '1': case '2': case '3': case '4':
            case '5': case '6': case '7': case '8': case '9':
                s = match_capture(ms, s, uchar(*(p + 1)));
                if (s != NULL) {
                    p += 2;
                    goto init;
                }
                break;
            default:
                goto dflt;
            }
            break;
        default:
        dflt: {
            const char *ep = classEnd(ms, p);
            if (!singlematch(ms, s, p, ep)) {
                if (*ep == '*' || *ep == '?' || *ep == '-') {
                    p = ep + 1;
                    goto init;
                }
                s = NULL;
            } else {
                switch (*ep) {
                case '?': {
                    const char *res;
                    if ((res = match(ms, s + 1, ep + 1)) != NULL) {
                        s = res;
                    } else {
                        p = ep + 1;
                        goto init;
                    }
                    break;
                }
                case '+':
                    s++;
                    /* fall through */
                case '*':
                    s = max_expand(ms, s, p, ep);
                    break;
                case '-':
                    s = min_expand(ms, s, p, ep);
                    break;
                default:
                    s++;
                    p = ep;
                    goto init;
                }
            }
            break;
        }
        }
    }
    ms->matchdepth++;
    return s;
}

static ptrdiff_t get_onecapture(MatchState *ms, int i, const char *s, const char *e, const char **cap)
{
    if (i >= ms->level) {
        if (i != 0) {
            luaL_error(ms->L, "invalid capture index %%%d", i + 1);
        }
        *cap = s;
        return e - s;
    } else {
        ptrdiff_t capl = ms->capture[i].len;
        *cap = ms->capture[i].init;
        if (capl == CAP_UNFINISHED) {
            luaL_error(ms->L, "unfinished capture");
        } else if (capl == CAP_POSITION) {
            lua_pushinteger(ms->L, (ms->capture[i].init - ms->src_init) + 1);
        }
        return capl;
    }
}

static void push_onecapture(MatchState *ms, int i, const char *s, const char *e)
{
    const char *cap;
    ptrdiff_t l = get_onecapture(ms, i, s, e, &cap);
    if (l != CAP_POSITION) {
        lua_pushlstring(ms->L, cap, (size_t)l);
    }
}

static int push_captures(MatchState *ms, const char *s, const char *e)
{
    int i;
    int nlevels = (ms->level == 0 && s) ? 1 : ms->level;
    luaL_checkstack(ms->L, nlevels, "too many captures");
    for (i = 0; i < nlevels; i++) {
        push_onecapture(ms, i, s, e);
    }
    return nlevels;
}

static int nospecials(const char *p, size_t l)
{
    size_t upto = 0;
    do {
        if (strpbrk(p + upto, SPECIALS)) {
            return 0;
        }
        upto += strlen(p + upto) + 1;
    } while (upto <= l);
    return 1;
}

static void prepstate(MatchState *ms, lua_State *L, const char *s, size_t ls, const char *p, size_t lp)
{
    ms->L = L;
    ms->matchdepth = TWP_MAXCCALLS;
    ms->src_init = s;
    ms->src_end = s + ls;
    ms->p_end = p + lp;
    ms->steps = 0;
}

static void reprepstate(MatchState *ms)
{
    ms->level = 0;
    ms->matchdepth = TWP_MAXCCALLS;
}

static void charge_steps(MatchState *ms)
{
    twp_charge(ms->L, ms->steps / 2);
    ms->steps = 0;
}

static size_t posrelatI(lua_Integer pos, size_t len)
{
    if (pos > 0) {
        return (size_t)pos;
    } else if (pos == 0) {
        return 1;
    } else if (pos < -(lua_Integer)len) {
        return 1;
    }
    return len + (size_t)pos + 1;
}

static const char *check_pattern(lua_State *L, int arg, size_t *lp)
{
    const char *p = luaL_checklstring(L, arg, lp);
    if (*lp > TWP_PATTERN_MAX) {
        luaL_argerror(L, arg, "pattern is longer than 4096 bytes");
    }
    return p;
}

static const char *lmemfind(MatchState *ms, const char *s1, size_t l1, const char *s2, size_t l2)
{
    if (l2 == 0) {
        return s1;
    } else if (l2 > l1) {
        return NULL;
    } else {
        const char *init;
        l2--;
        l1 = l1 - l2;
        while (l1 > 0 && (init = (const char *)memchr(s1, *s2, l1)) != NULL) {
            ms->steps += 1 + (int64_t)(l2 / 16);
            if (ms->steps > TWP_PATTERN_STEPS) {
                luaL_error(ms->L, "find too complex (more than %d matching steps)", TWP_PATTERN_STEPS);
            }
            init++;
            if (memcmp(init, s2 + 1, l2) == 0) {
                return init - 1;
            }
            l1 -= (size_t)(init - s1);
            s1 = init;
        }
        return NULL;
    }
}

static int str_find_aux(lua_State *L, int find)
{
    size_t ls, lp;
    const char *s = luaL_checklstring(L, 1, &ls);
    const char *p = check_pattern(L, 2, &lp);
    size_t init = posrelatI(luaL_optinteger(L, 3, 1), ls) - 1;
    MatchState ms;
    if (init > ls) {
        luaL_pushfail(L);
        return 1;
    }
    prepstate(&ms, L, s, ls, p, lp);
    if (find && (lua_toboolean(L, 4) || nospecials(p, lp))) {
        const char *s2 = lmemfind(&ms, s + init, ls - init, p, lp);
        charge_steps(&ms);
        if (s2) {
            lua_pushinteger(L, (s2 - s) + 1);
            lua_pushinteger(L, (lua_Integer)((s2 - s) + lp));
            return 2;
        }
    } else {
        const char *s1 = s + init;
        int anchor = (*p == '^');
        if (anchor) {
            p++;
            lp--;
        }
        prepstate(&ms, L, s, ls, p, lp);
        do {
            const char *res;
            reprepstate(&ms);
            if ((res = match(&ms, s1, p)) != NULL) {
                charge_steps(&ms);
                if (find) {
                    lua_pushinteger(L, (s1 - s) + 1);
                    lua_pushinteger(L, res - s);
                    return push_captures(&ms, NULL, 0) + 2;
                }
                return push_captures(&ms, s1, res);
            }
        } while (s1++ < ms.src_end && !anchor);
        charge_steps(&ms);
    }
    luaL_pushfail(L);
    return 1;
}

static int str_find(lua_State *L) { return str_find_aux(L, 1); }
static int str_match(lua_State *L) { return str_find_aux(L, 0); }

typedef struct GMatchState {
    const char *src;
    const char *p;
    const char *lastmatch;
    MatchState ms;
} GMatchState;

static int gmatch_aux(lua_State *L)
{
    GMatchState *gm = (GMatchState *)lua_touserdata(L, lua_upvalueindex(3));
    const char *src;
    gm->ms.L = L;
    gm->ms.steps = 0;
    for (src = gm->src; src <= gm->ms.src_end; src++) {
        const char *e;
        reprepstate(&gm->ms);
        if ((e = match(&gm->ms, src, gm->p)) != NULL && e != gm->lastmatch) {
            gm->src = gm->lastmatch = e;
            charge_steps(&gm->ms);
            return push_captures(&gm->ms, src, e);
        }
    }
    gm->src = gm->ms.src_end + 1;
    charge_steps(&gm->ms);
    return 0;
}

static int str_gmatch(lua_State *L)
{
    size_t ls, lp;
    const char *s = luaL_checklstring(L, 1, &ls);
    const char *p = check_pattern(L, 2, &lp);
    size_t init = posrelatI(luaL_optinteger(L, 3, 1), ls) - 1;
    GMatchState *gm;
    lua_settop(L, 2);
    gm = (GMatchState *)lua_newuserdatauv(L, sizeof(GMatchState), 0);
    if (init > ls) {
        init = ls + 1;
    }
    prepstate(&gm->ms, L, s, ls, p, lp);
    gm->src = s + init;
    gm->p = p;
    gm->lastmatch = NULL;
    lua_pushcclosure(L, gmatch_aux, 3);
    return 1;
}

static void add_s(MatchState *ms, luaL_Buffer *b, const char *s, const char *e)
{
    size_t l;
    lua_State *L = ms->L;
    const char *news = lua_tolstring(L, 3, &l);
    const char *p;
    while ((p = (const char *)memchr(news, L_ESC, l)) != NULL) {
        luaL_addlstring(b, news, (size_t)(p - news));
        p++;
        if (*p == L_ESC) {
            luaL_addchar(b, *p);
        } else if (*p == '0') {
            luaL_addlstring(b, s, (size_t)(e - s));
        } else if (isdigit(uchar(*p))) {
            const char *cap;
            ptrdiff_t resl = get_onecapture(ms, *p - '1', s, e, &cap);
            if (resl == CAP_POSITION) {
                luaL_addvalue(b);
            } else {
                luaL_addlstring(b, cap, (size_t)resl);
            }
        } else {
            luaL_error(L, "invalid use of '%c' in replacement string", L_ESC);
        }
        l -= (size_t)(p + 1 - news);
        news = p + 1;
    }
    luaL_addlstring(b, news, l);
}

static int add_value(MatchState *ms, luaL_Buffer *b, const char *s, const char *e, int tr)
{
    lua_State *L = ms->L;
    switch (tr) {
    case LUA_TFUNCTION: {
        int n;
        lua_pushvalue(L, 3);
        n = push_captures(ms, s, e);
        lua_call(L, n, 1);
        break;
    }
    case LUA_TTABLE:
        push_onecapture(ms, 0, s, e);
        lua_gettable(L, 3);
        break;
    default:
        add_s(ms, b, s, e);
        return 1;
    }
    if (!lua_toboolean(L, -1)) {
        lua_pop(L, 1);
        luaL_addlstring(b, s, (size_t)(e - s));
        return 0;
    } else if (!lua_isstring(L, -1)) {
        return luaL_error(L, "invalid replacement value (a %s)", luaL_typename(L, -1));
    }
    luaL_addvalue(b);
    return 1;
}

static int str_gsub(lua_State *L)
{
    size_t srcl, lp;
    const char *src = luaL_checklstring(L, 1, &srcl);
    const char *p = check_pattern(L, 2, &lp);
    const char *lastmatch = NULL;
    int tr = lua_type(L, 3);
    lua_Integer max_s = luaL_optinteger(L, 4, (lua_Integer)srcl + 1);
    int anchor = (*p == '^');
    lua_Integer n = 0;
    int changed = 0;
    MatchState ms;
    luaL_Buffer b;
    luaL_argexpected(L, tr == LUA_TNUMBER || tr == LUA_TSTRING || tr == LUA_TFUNCTION || tr == LUA_TTABLE, 3,
        "string/function/table");
    luaL_buffinit(L, &b);
    if (anchor) {
        p++;
        lp--;
    }
    prepstate(&ms, L, src, srcl, p, lp);
    while (n < max_s) {
        const char *e;
        reprepstate(&ms);
        if ((e = match(&ms, src, p)) != NULL && e != lastmatch) {
            n++;
            changed = add_value(&ms, &b, src, e, tr) | changed;
            src = lastmatch = e;
        } else if (src < ms.src_end) {
            luaL_addchar(&b, *src++);
        } else {
            break;
        }
        if (anchor) {
            break;
        }
    }
    charge_steps(&ms);
    if (!changed) {
        lua_pushvalue(L, 1);
    } else {
        luaL_addlstring(&b, src, (size_t)(ms.src_end - src));
        luaL_pushresult(&b);
    }
    lua_pushinteger(L, n);
    return 2;
}

/* string.rep: refuse a result past the build limit before the original allocates it. */
static int str_rep(lua_State *L)
{
    size_t l, lsep;
    lua_Integer n;
    luaL_checklstring(L, 1, &l);
    n = luaL_checkinteger(L, 2);
    luaL_optlstring(L, 3, "", &lsep);
    if (n > 0) {
        double total = (double)n * (double)l + (double)(n - 1) * (double)lsep;
        if (total > (double)TWP_MAX_STRING_BUILD) {
            return luaL_error(L, "string.rep would build %f bytes; the limit is %d", (lua_Number)total, TWP_MAX_STRING_BUILD);
        }
        twp_charge(L, (int64_t)(total / 16));
    }
    lua_pushvalue(L, lua_upvalueindex(1));
    lua_insert(L, 1);
    lua_call(L, lua_gettop(L) - 1, 1);
    return 1;
}

/* string.format: bound the format string and the result. Lua already caps each width and precision at 99. */
static int str_format(lua_State *L)
{
    size_t fl, rl;
    luaL_checklstring(L, 1, &fl);
    if (fl > TWP_FORMAT_MAX) {
        return luaL_argerror(L, 1, "format string is longer than 8192 bytes");
    }
    lua_pushvalue(L, lua_upvalueindex(1));
    lua_insert(L, 1);
    lua_call(L, lua_gettop(L) - 1, 1);
    lua_tolstring(L, -1, &rl);
    if (rl > TWP_MAX_STRING_BUILD) {
        return luaL_error(L, "string.format built more than %d bytes", TWP_MAX_STRING_BUILD);
    }
    twp_charge(L, (int64_t)(rl / 16));
    return 1;
}

void twp_open_strguard(lua_State *L)
{
    static const luaL_Reg patterns[] = {
        {"find", str_find},
        {"match", str_match},
        {"gmatch", str_gmatch},
        {"gsub", str_gsub},
        {NULL, NULL},
    };
    const luaL_Reg *r;
    lua_getglobal(L, "string");
    for (r = patterns; r->name != NULL; r++) {
        lua_pushcfunction(L, r->func);
        lua_setfield(L, -2, r->name);
    }
    lua_getfield(L, -1, "rep");
    lua_pushcclosure(L, str_rep, 1);
    lua_setfield(L, -2, "rep");
    lua_getfield(L, -1, "format");
    lua_pushcclosure(L, str_format, 1);
    lua_setfield(L, -2, "format");
    lua_pushnil(L);
    lua_setfield(L, -2, "dump");
    lua_pop(L, 1);
}
