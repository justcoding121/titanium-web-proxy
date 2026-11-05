/*
 * regex.match / regex.find / regex.test: a byte-oriented regular expression engine run as a Pike VM, so time grows
 * with input length times program size and never backtracks. Patterns are capped at 1024 bytes, programs at 4000
 * instructions, input at 1 MiB, and each call at TWP_REGEX_STEPS steps (charged to the instruction budget).
 *
 * Syntax: literals, ., [...] and [^...] with ranges and \d\w\s, \d \D \w \W \s \S \b \B \n \r \t \f \v \xHH,
 * escaped punctuation, ^ $, (...) captures (up to 9), (?:...), |, and * + ? {m} {m,} {m,n} (m, n <= 1000) with
 * a trailing ? for the lazy form. Flags: "i" (ASCII case-insensitive).
 */
#include "twp_internal.h"

#include <ctype.h>
#include <string.h>

#define RX_PATTERN_MAX 1024
#define RX_INPUT_MAX (1024 * 1024)
#define RX_PROG_MAX 4000
#define RX_NODE_MAX 2048
#define RX_CLASS_MAX 128
#define RX_GROUP_MAX 9
#define RX_REPEAT_MAX 1000
#define TWP_REGEX_STEPS 20000000

enum { R_CHAR, R_CHARI, R_ANY, R_CLASS, R_SPLIT, R_JMP, R_SAVE, R_BOL, R_EOL, R_WORDB, R_NWORDB, R_MATCH };
enum { N_EMPTY, N_CHAR, N_ANY, N_CLASS, N_BOL, N_EOL, N_WORDB, N_NWORDB, N_CAT, N_ALT, N_STAR, N_PLUS, N_QUEST, N_REP, N_GROUP };

typedef struct rinst {
    unsigned char op;
    unsigned char c;
    int x;
    int y;
} rinst;

typedef struct rnode {
    int type;
    int c;
    int greedy;
    int min;
    int max;
    struct rnode *a;
    struct rnode *b;
} rnode;

typedef struct rclass {
    unsigned char bits[32];
} rclass;

typedef struct rx {
    lua_State *L;
    const char *p;
    const char *end;
    int icase;
    rnode nodes[RX_NODE_MAX];
    int nnodes;
    rclass classes[RX_CLASS_MAX];
    int nclasses;
    int ngroups;
    rinst prog[RX_PROG_MAX];
    int nprog;
} rx;

static void rx_fail(rx *r, const char *what)
{
    luaL_error(r->L, "regex: %s", what);
}

static rnode *node(rx *r, int type)
{
    rnode *n;
    if (r->nnodes >= RX_NODE_MAX) {
        rx_fail(r, "pattern is too complex");
    }
    n = &r->nodes[r->nnodes++];
    memset(n, 0, sizeof(*n));
    n->type = type;
    n->greedy = 1;
    return n;
}

static int new_class(rx *r)
{
    if (r->nclasses >= RX_CLASS_MAX) {
        rx_fail(r, "too many character classes");
    }
    memset(&r->classes[r->nclasses], 0, sizeof(rclass));
    return r->nclasses++;
}

static void cls_set(rclass *c, int ch) { c->bits[ch >> 3] |= (unsigned char)(1 << (ch & 7)); }
static int cls_has(const rclass *c, int ch) { return (c->bits[ch >> 3] >> (ch & 7)) & 1; }

static void cls_add_named(rclass *c, int kind)
{
    int ch;
    for (ch = 0; ch < 256; ch++) {
        int in;
        switch (tolower(kind)) {
        case 'd': in = ch >= '0' && ch <= '9'; break;
        case 'w': in = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '_'; break;
        default: in = ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f' || ch == '\v'; break;
        }
        if (isupper(kind)) {
            in = !in;
        }
        if (in) {
            cls_set(c, ch);
        }
    }
}

static int hexd(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* Reads one escaped literal after '\'. Returns the byte, or -1 for a class escape left in *named. */
static int read_escape(rx *r, int *named)
{
    int c;
    *named = 0;
    if (r->p >= r->end) {
        rx_fail(r, "pattern ends with '\\'");
    }
    c = (unsigned char)*r->p++;
    switch (c) {
    case 'd': case 'D': case 'w': case 'W': case 's': case 'S':
        *named = c;
        return -1;
    case 'n': return '\n';
    case 'r': return '\r';
    case 't': return '\t';
    case 'f': return '\f';
    case 'v': return '\v';
    case '0': return 0;
    case 'x': {
        int h = 0, l = 0;
        if (r->end - r->p < 2 || (h = hexd(r->p[0])) < 0 || (l = hexd(r->p[1])) < 0) {
            rx_fail(r, "\\x needs two hex digits");
        }
        r->p += 2;
        return (h << 4) | l;
    }
    default:
        if (isalnum(c)) {
            rx_fail(r, "unknown escape");
        }
        return c;
    }
}

static void cls_add_char(rx *r, rclass *c, int ch)
{
    cls_set(c, ch);
    if (r->icase && isalpha(ch)) {
        cls_set(c, tolower(ch));
        cls_set(c, toupper(ch));
    }
}

static rnode *parse_class(rx *r)
{
    int idx = new_class(r), negate = 0, first = 1, i;
    rclass *c = &r->classes[idx];
    rnode *n;
    if (r->p < r->end && *r->p == '^') {
        negate = 1;
        r->p++;
    }
    for (;;) {
        int lo, named = 0;
        if (r->p >= r->end) {
            rx_fail(r, "missing ']'");
        }
        if (*r->p == ']' && !first) {
            r->p++;
            break;
        }
        first = 0;
        lo = (unsigned char)*r->p++;
        if (lo == '\\') {
            lo = read_escape(r, &named);
            if (named) {
                cls_add_named(c, named);
                continue;
            }
        }
        if (r->end - r->p >= 2 && r->p[0] == '-' && r->p[1] != ']') {
            int hi, ch;
            r->p++;
            hi = (unsigned char)*r->p++;
            if (hi == '\\') {
                hi = read_escape(r, &named);
                if (named) {
                    rx_fail(r, "a class escape cannot end a range");
                }
            }
            if (hi < lo) {
                rx_fail(r, "range out of order");
            }
            for (ch = lo; ch <= hi; ch++) {
                cls_add_char(r, c, ch);
            }
        } else {
            cls_add_char(r, c, lo);
        }
    }
    if (negate) {
        for (i = 0; i < 32; i++) {
            c->bits[i] = (unsigned char)~c->bits[i];
        }
    }
    n = node(r, N_CLASS);
    n->c = idx;
    return n;
}

static rnode *parse_alt(rx *r, int depth);

static rnode *parse_atom(rx *r, int depth)
{
    int c = (unsigned char)*r->p++;
    rnode *n;
    switch (c) {
    case '(': {
        int capture = 1, group = 0;
        if (r->end - r->p >= 2 && r->p[0] == '?' && r->p[1] == ':') {
            capture = 0;
            r->p += 2;
        } else if (r->p < r->end && *r->p == '?') {
            rx_fail(r, "only (?:...) groups are supported");
        }
        if (capture) {
            if (r->ngroups >= RX_GROUP_MAX) {
                rx_fail(r, "more than 9 capture groups");
            }
            group = ++r->ngroups;
        }
        n = parse_alt(r, depth + 1);
        if (r->p >= r->end || *r->p != ')') {
            rx_fail(r, "missing ')'");
        }
        r->p++;
        if (capture) {
            rnode *g = node(r, N_GROUP);
            g->c = group;
            g->a = n;
            return g;
        }
        return n;
    }
    case '[':
        return parse_class(r);
    case '.':
        return node(r, N_ANY);
    case '^':
        return node(r, N_BOL);
    case '$':
        return node(r, N_EOL);
    case '\\': {
        int named;
        if (r->p < r->end && (*r->p == 'b' || *r->p == 'B')) {
            return node(r, *r->p++ == 'b' ? N_WORDB : N_NWORDB);
        }
        c = read_escape(r, &named);
        if (named) {
            int idx = new_class(r);
            cls_add_named(&r->classes[idx], named);
            n = node(r, N_CLASS);
            n->c = idx;
            return n;
        }
        break;
    }
    case '*': case '+': case '?':
        rx_fail(r, "nothing to repeat");
        break;
    default:
        break;
    }
    n = node(r, N_CHAR);
    n->c = c;
    return n;
}

/* Parses {m}, {m,}, {m,n} at r->p. Returns 1 and fills min/max (max -1 = unbounded), or 0 to treat '{' as a literal. */
static int parse_braces(rx *r, int *min, int *max)
{
    const char *q = r->p + 1;
    int m = 0, n = -1, digits = 0;
    while (q < r->end && *q >= '0' && *q <= '9') {
        m = m * 10 + (*q++ - '0');
        if (++digits > 4) return 0;
    }
    if (digits == 0) return 0;
    if (q < r->end && *q == ',') {
        q++;
        digits = 0;
        n = 0;
        while (q < r->end && *q >= '0' && *q <= '9') {
            n = n * 10 + (*q++ - '0');
            if (++digits > 4) return 0;
        }
        if (digits == 0) n = -1;
    } else {
        n = m;
    }
    if (q >= r->end || *q != '}') return 0;
    if (m > RX_REPEAT_MAX || n > RX_REPEAT_MAX || (n >= 0 && n < m)) {
        rx_fail(r, "repeat count out of range (0 to 1000, min <= max)");
    }
    r->p = q + 1;
    *min = m;
    *max = n;
    return 1;
}

static rnode *parse_repeat(rx *r, int depth)
{
    rnode *a = parse_atom(r, depth);
    if (r->p < r->end) {
        int c = *r->p, min = 0, max = 0;
        rnode *q = NULL;
        if (c == '*' || c == '+' || c == '?') {
            r->p++;
            q = node(r, c == '*' ? N_STAR : c == '+' ? N_PLUS : N_QUEST);
        } else if (c == '{' && parse_braces(r, &min, &max)) {
            q = node(r, N_REP);
            q->min = min;
            q->max = max;
        }
        if (q != NULL) {
            q->a = a;
            if (r->p < r->end && *r->p == '?') {
                q->greedy = 0;
                r->p++;
            }
            if (r->p < r->end && (*r->p == '*' || *r->p == '+' || (*r->p == '?'))) {
                rx_fail(r, "nested quantifier");
            }
            return q;
        }
    }
    return a;
}

static rnode *parse_cat(rx *r, int depth)
{
    rnode *left = NULL;
    while (r->p < r->end && *r->p != '|' && *r->p != ')') {
        rnode *n = parse_repeat(r, depth);
        if (left == NULL) {
            left = n;
        } else {
            rnode *c = node(r, N_CAT);
            c->a = left;
            c->b = n;
            left = c;
        }
    }
    return left != NULL ? left : node(r, N_EMPTY);
}

static rnode *parse_alt(rx *r, int depth)
{
    rnode *left;
    if (depth > 64) {
        rx_fail(r, "groups nested too deeply");
    }
    left = parse_cat(r, depth);
    while (r->p < r->end && *r->p == '|') {
        rnode *alt;
        r->p++;
        alt = node(r, N_ALT);
        alt->a = left;
        alt->b = parse_cat(r, depth);
        left = alt;
    }
    return left;
}

static int emit(rx *r, int op, int c, int x, int y)
{
    rinst *i;
    if (r->nprog >= RX_PROG_MAX) {
        rx_fail(r, "pattern compiles to too many instructions");
    }
    i = &r->prog[r->nprog];
    i->op = (unsigned char)op;
    i->c = (unsigned char)c;
    i->x = x;
    i->y = y;
    return r->nprog++;
}

static void compile(rx *r, rnode *n, int depth);

static void compile_star(rx *r, rnode *a, int greedy, int depth)
{
    int split = emit(r, R_SPLIT, 0, 0, 0), jmp;
    compile(r, a, depth + 1);
    jmp = emit(r, R_JMP, 0, split, 0);
    (void)jmp;
    if (greedy) {
        r->prog[split].x = split + 1;
        r->prog[split].y = r->nprog;
    } else {
        r->prog[split].x = r->nprog;
        r->prog[split].y = split + 1;
    }
}

static void compile_quest(rx *r, rnode *a, int greedy, int depth)
{
    int split = emit(r, R_SPLIT, 0, 0, 0);
    compile(r, a, depth + 1);
    if (greedy) {
        r->prog[split].x = split + 1;
        r->prog[split].y = r->nprog;
    } else {
        r->prog[split].x = r->nprog;
        r->prog[split].y = split + 1;
    }
}

static void compile(rx *r, rnode *n, int depth)
{
    if (depth > 200) {
        rx_fail(r, "pattern is too complex");
    }
    switch (n->type) {
    case N_EMPTY:
        return;
    case N_CHAR:
        if (r->icase && isalpha(n->c)) {
            emit(r, R_CHARI, tolower(n->c), 0, 0);
        } else {
            emit(r, R_CHAR, n->c, 0, 0);
        }
        return;
    case N_ANY: emit(r, R_ANY, 0, 0, 0); return;
    case N_CLASS: emit(r, R_CLASS, 0, n->c, 0); return;
    case N_BOL: emit(r, R_BOL, 0, 0, 0); return;
    case N_EOL: emit(r, R_EOL, 0, 0, 0); return;
    case N_WORDB: emit(r, R_WORDB, 0, 0, 0); return;
    case N_NWORDB: emit(r, R_NWORDB, 0, 0, 0); return;
    case N_CAT:
        compile(r, n->a, depth + 1);
        compile(r, n->b, depth + 1);
        return;
    case N_ALT: {
        int split = emit(r, R_SPLIT, 0, 0, 0), jmp;
        compile(r, n->a, depth + 1);
        jmp = emit(r, R_JMP, 0, 0, 0);
        r->prog[split].x = split + 1;
        r->prog[split].y = r->nprog;
        compile(r, n->b, depth + 1);
        r->prog[jmp].x = r->nprog;
        return;
    }
    case N_STAR:
        compile_star(r, n->a, n->greedy, depth);
        return;
    case N_PLUS: {
        int start = r->nprog, split;
        compile(r, n->a, depth + 1);
        split = emit(r, R_SPLIT, 0, 0, 0);
        if (n->greedy) {
            r->prog[split].x = start;
            r->prog[split].y = r->nprog;
        } else {
            r->prog[split].x = r->nprog;
            r->prog[split].y = start;
        }
        return;
    }
    case N_QUEST:
        compile_quest(r, n->a, n->greedy, depth);
        return;
    case N_REP: {
        int i;
        for (i = 0; i < n->min; i++) {
            compile(r, n->a, depth + 1);
        }
        if (n->max < 0) {
            compile_star(r, n->a, n->greedy, depth);
        } else {
            for (i = n->min; i < n->max; i++) {
                compile_quest(r, n->a, n->greedy, depth);
            }
        }
        return;
    }
    case N_GROUP:
        emit(r, R_SAVE, 0, n->c * 2, 0);
        compile(r, n->a, depth + 1);
        emit(r, R_SAVE, 0, n->c * 2 + 1, 0);
        return;
    default:
        rx_fail(r, "internal error");
    }
}

/* ---- Pike VM --------------------------------------------------------------------------------------------------- */

typedef struct vm {
    lua_State *L;
    const rx *r;
    const unsigned char *s;
    int len;
    int nslots;
    int *mark;
    int gen;
    int *stack; /* pairs: (pc or -slot-1, value) */
    int64_t steps;
} vm;

typedef struct tlist {
    int n;
    int *pcs;
    int *caps;
} tlist;

static int is_word(int c)
{
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
}

static void vm_step(vm *m)
{
    if (++m->steps > TWP_REGEX_STEPS) {
        luaL_error(m->L, "regex: too much work (more than %d steps)", TWP_REGEX_STEPS);
    }
}

/* Follows JMP / SPLIT / SAVE / assertions from pc at position sp and adds the threads it reaches, in priority order. */
static void addthread(vm *m, tlist *l, int pc0, int *caps, int sp)
{
    int top = 0;
    m->stack[top++] = pc0;
    m->stack[top++] = 0;
    while (top > 0) {
        int val = m->stack[--top];
        int pc = m->stack[--top];
        if (pc < 0) {
            caps[-pc - 1] = val;
            continue;
        }
        for (;;) {
            const rinst *in;
            vm_step(m);
            if (m->mark[pc] == m->gen) {
                break;
            }
            m->mark[pc] = m->gen;
            in = &m->r->prog[pc];
            if (in->op == R_JMP) {
                pc = in->x;
                continue;
            }
            if (in->op == R_SPLIT) {
                m->stack[top++] = in->y;
                m->stack[top++] = 0;
                pc = in->x;
                continue;
            }
            if (in->op == R_SAVE) {
                m->stack[top++] = -in->x - 1;
                m->stack[top++] = caps[in->x];
                caps[in->x] = sp;
                pc++;
                continue;
            }
            if (in->op == R_BOL) {
                if (sp != 0) break;
                pc++;
                continue;
            }
            if (in->op == R_EOL) {
                if (sp != m->len) break;
                pc++;
                continue;
            }
            if (in->op == R_WORDB || in->op == R_NWORDB) {
                int before = sp > 0 && is_word(m->s[sp - 1]);
                int after = sp < m->len && is_word(m->s[sp]);
                if ((before != after) != (in->op == R_WORDB)) break;
                pc++;
                continue;
            }
            l->pcs[l->n] = pc;
            memcpy(l->caps + (size_t)l->n * (size_t)m->nslots, caps, sizeof(int) * (size_t)m->nslots);
            l->n++;
            break;
        }
    }
}

/* Runs the program from position start. Returns 1 and fills out[] (nslots ints) on a match. */
static int vm_run(vm *m, tlist *cl, tlist *nl, int *scratch, int *out, int start)
{
    const rx *r = m->r;
    int sp, matched = 0, i;
    cl->n = 0;
    m->gen++;
    for (sp = start;; sp++) {
        int c;
        tlist *t;
        if (!matched) {
            for (i = 0; i < m->nslots; i++) {
                scratch[i] = -1;
            }
            addthread(m, cl, 0, scratch, sp);
        }
        if (cl->n == 0) {
            if (matched || sp >= m->len) {
                break;
            }
            /* Nothing survives at this position (an assertion failed); try the next one with fresh marks. */
            m->gen++;
            continue;
        }
        c = sp < m->len ? m->s[sp] : -1;
        m->gen++;
        nl->n = 0;
        for (i = 0; i < cl->n; i++) {
            int pc = cl->pcs[i];
            const rinst *in = &r->prog[pc];
            const int *tc = cl->caps + (size_t)i * (size_t)m->nslots;
            int ok = 0;
            vm_step(m);
            switch (in->op) {
            case R_CHAR: ok = c == in->c; break;
            case R_CHARI: ok = c >= 0 && tolower(c) == in->c; break;
            case R_ANY: ok = c >= 0 && c != '\n'; break;
            case R_CLASS: ok = c >= 0 && cls_has(&r->classes[in->x], c); break;
            case R_MATCH:
                matched = 1;
                memcpy(out, tc, sizeof(int) * (size_t)m->nslots);
                i = cl->n; /* lower-priority threads are cut */
                continue;
            default: break;
            }
            if (ok) {
                memcpy(scratch, tc, sizeof(int) * (size_t)m->nslots);
                addthread(m, nl, pc + 1, scratch, sp + 1);
            }
        }
        t = cl;
        cl = nl;
        nl = t;
        if (sp >= m->len) {
            break;
        }
    }
    return matched;
}

/* Compiles pattern (arg 1) and matches subject (arg 2) with flags (arg 3). Pushes nothing; fills caps. */
static int run_regex(lua_State *L, int *caps, int *nslots_out, const char **subject)
{
    size_t pl, sl, fl = 0;
    const char *pat = luaL_checklstring(L, 1, &pl);
    const char *s = luaL_checklstring(L, 2, &sl);
    const char *flags = luaL_optlstring(L, 3, "", &fl);
    rx *r;
    rnode *root;
    vm m;
    tlist a, b;
    int *mem, nslots, matched;
    size_t ints;
    if (pl > RX_PATTERN_MAX) {
        luaL_argerror(L, 1, "pattern is longer than 1024 bytes");
    }
    if (sl > RX_INPUT_MAX) {
        luaL_argerror(L, 2, "input is longer than 1 MiB");
    }
    r = (rx *)lua_newuserdatauv(L, sizeof(rx), 0);
    memset(r, 0, offsetof(rx, nodes));
    r->nnodes = 0;
    r->nclasses = 0;
    r->ngroups = 0;
    r->nprog = 0;
    r->L = L;
    r->p = pat;
    r->end = pat + pl;
    r->icase = memchr(flags, 'i', fl) != NULL;
    root = parse_alt(r, 0);
    if (r->p != r->end) {
        rx_fail(r, "unmatched ')'");
    }
    emit(r, R_SAVE, 0, 0, 0);
    compile(r, root, 0);
    emit(r, R_SAVE, 0, 1, 0);
    emit(r, R_MATCH, 0, 0, 0);
    twp_charge(L, (int64_t)r->nprog);

    nslots = (r->ngroups + 1) * 2;
    ints = (size_t)r->nprog /* mark */ + (size_t)r->nprog * 4 /* stack */ + 2 * (size_t)r->nprog /* pcs */ +
        2 * (size_t)r->nprog * (size_t)nslots /* caps */ + (size_t)nslots /* scratch */;
    mem = (int *)lua_newuserdatauv(L, ints * sizeof(int), 0);
    memset(mem, 0, (size_t)r->nprog * sizeof(int));
    m.L = L;
    m.r = r;
    m.s = (const unsigned char *)s;
    m.len = (int)sl;
    m.nslots = nslots;
    m.mark = mem;
    m.gen = 0;
    m.stack = mem + r->nprog;
    m.steps = 0;
    a.pcs = m.stack + (size_t)r->nprog * 4;
    b.pcs = a.pcs + r->nprog;
    a.caps = b.pcs + r->nprog;
    b.caps = a.caps + (size_t)r->nprog * (size_t)nslots;
    a.n = b.n = 0;
    matched = vm_run(&m, &a, &b, b.caps + (size_t)r->nprog * (size_t)nslots, caps, 0);
    twp_charge(L, m.steps / 4);
    lua_pop(L, 2);
    *nslots_out = nslots;
    *subject = s;
    return matched;
}

static void push_group(lua_State *L, const char *s, const int *caps, int g)
{
    if (caps[g * 2] < 0 || caps[g * 2 + 1] < 0) {
        luaL_pushfail(L);
    } else {
        lua_pushlstring(L, s + caps[g * 2], (size_t)(caps[g * 2 + 1] - caps[g * 2]));
    }
}

/* regex.match(pattern, subject [, flags]) -> the groups, or the whole match when there are none; nil when none. */
static int l_match(lua_State *L)
{
    int caps[(RX_GROUP_MAX + 1) * 2], nslots, g;
    const char *s;
    if (!run_regex(L, caps, &nslots, &s)) {
        luaL_pushfail(L);
        return 1;
    }
    if (nslots == 2) {
        push_group(L, s, caps, 0);
        return 1;
    }
    luaL_checkstack(L, nslots / 2, "regex captures");
    for (g = 1; g < nslots / 2; g++) {
        push_group(L, s, caps, g);
    }
    return nslots / 2 - 1;
}

/* regex.find(pattern, subject [, flags]) -> start, end (1-based, inclusive), groups...; nil when none. */
static int l_find(lua_State *L)
{
    int caps[(RX_GROUP_MAX + 1) * 2], nslots, g;
    const char *s;
    if (!run_regex(L, caps, &nslots, &s)) {
        luaL_pushfail(L);
        return 1;
    }
    luaL_checkstack(L, nslots / 2 + 2, "regex captures");
    lua_pushinteger(L, caps[0] + 1);
    lua_pushinteger(L, caps[1]);
    for (g = 1; g < nslots / 2; g++) {
        push_group(L, s, caps, g);
    }
    return 2 + nslots / 2 - 1;
}

static int l_test(lua_State *L)
{
    int caps[(RX_GROUP_MAX + 1) * 2], nslots;
    const char *s;
    lua_pushboolean(L, run_regex(L, caps, &nslots, &s));
    return 1;
}

void twp_open_regex(lua_State *L)
{
    static const luaL_Reg fns[] = {
        {"match", l_match},
        {"find", l_find},
        {"test", l_test},
        {NULL, NULL},
    };
    luaL_newlib(L, fns);
    lua_setglobal(L, "regex");
}
