/* spq_vorbis.c - Vorbis I decoder (floor 1, residue 0/1/2, mapping 0) written from the Vorbis I spec. */
#include "spq_vorbis.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "spq_vorbis_db.inc"

#define PI_D 3.14159265358979323846
#define FAST_BITS 10
#define MAXBLOCK 8192

/* ------------------------------------------------------------------ bit reader */
typedef struct { const uint8_t *d; int nbytes; int pos; int eop; } BR;

static uint32_t br_peek(BR *b, int n)            /* n <= 24 */
{
    int byte = b->pos >> 3, sh = b->pos & 7;
    uint32_t v = 0;
    if (byte + 3 < b->nbytes) v = (uint32_t)b->d[byte] | ((uint32_t)b->d[byte+1] << 8) | ((uint32_t)b->d[byte+2] << 16) | ((uint32_t)b->d[byte+3] << 24);
    else { int i; for (i = 0; i < 4; i++) if (byte + i < b->nbytes) v |= (uint32_t)b->d[byte+i] << (8*i); }
    return (v >> sh) & ((1u << n) - 1);
}
static uint32_t br_peek32(BR *b)                 /* 32 bits, zero padded */
{
    int byte = b->pos >> 3, sh = b->pos & 7, i;
    uint32_t lo = 0, hi = 0;
    for (i = 0; i < 4; i++) if (byte + i < b->nbytes) lo |= (uint32_t)b->d[byte+i] << (8*i);
    if (byte + 4 < b->nbytes) hi = b->d[byte+4];
    if (sh == 0) return lo;
    return (lo >> sh) | (hi << (32 - sh));
}
static void br_skip(BR *b, int n) { b->pos += n; if (b->pos > b->nbytes * 8) b->eop = 1; }
static uint32_t br_read(BR *b, int n)            /* n <= 32 */
{
    uint32_t v;
    if (n == 0) return 0;
    if (n <= 24) v = br_peek(b, n);
    else { uint32_t lo = br_peek(b, 16); br_skip(b, 16); v = lo | (br_peek(b, n - 16) << 16); br_skip(b, n - 16); return v; }
    br_skip(b, n);
    return v;
}

static int ilog(unsigned x) { int n = 0; while (x) { n++; x >>= 1; } return n; }

static uint32_t bitrev32(uint32_t v)
{
    v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
    v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
    v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
    v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
    return (v >> 16) | (v << 16);
}

static float float32_unpack(uint32_t x)
{
    double mant = (double)(x & 0x1fffff);
    int expo = (int)((x & 0x7fe00000u) >> 21);
    if (x & 0x80000000u) mant = -mant;
    return (float)ldexp(mant, expo - 788);
}

/* ------------------------------------------------------------------ codebooks */
typedef struct {
    int dim, entries;
    uint8_t *lens;
    int16_t fast[1 << FAST_BITS];          /* entry or -1 */
    uint8_t fastlen[1 << FAST_BITS];
    int nsorted;
    uint32_t *sorted_codes;                /* left aligned, ascending */
    int *sorted_entry;
    uint8_t *sorted_len;
    float *vecs;                           /* entries * dim, or NULL (no lookup) */
} Codebook;

static void cb_free(Codebook *c) { free(c->lens); free(c->sorted_codes); free(c->sorted_entry); free(c->sorted_len); free(c->vecs); memset(c, 0, sizeof *c); }

static int lookup1_values(int entries, int dim)
{
    int r = (int)floor(exp(log((double)entries) / dim));
    for (;;) {
        double p = 1; int i;
        for (i = 0; i < dim; i++) p *= (r + 1);
        if (p <= entries) r++; else break;
    }
    for (;;) {
        double p = 1; int i;
        for (i = 0; i < dim; i++) p *= r;
        if (p > entries && r > 1) r--; else break;
    }
    return r;
}

static int cb_parse(Codebook *c, BR *b)
{
    int i, ordered, nused = 0;
    uint32_t avail[33];
    uint32_t *codes;
    if (br_read(b, 24) != 0x564342) return -1;
    c->dim = (int)br_read(b, 16);
    c->entries = (int)br_read(b, 24);
    if (c->dim <= 0 || c->entries <= 0 || c->entries > (1 << 22)) return -1;
    c->lens = (uint8_t *)calloc(c->entries, 1);
    ordered = (int)br_read(b, 1);
    if (!ordered) {
        int sparse = (int)br_read(b, 1);
        for (i = 0; i < c->entries; i++) {
            if (sparse) { if (br_read(b, 1)) { c->lens[i] = (uint8_t)(br_read(b, 5) + 1); nused++; } }
            else { c->lens[i] = (uint8_t)(br_read(b, 5) + 1); nused++; }
        }
    } else {
        int cur = 0, curlen = (int)br_read(b, 5) + 1;
        while (cur < c->entries) {
            int num = (int)br_read(b, ilog(c->entries - cur)), k;
            if (cur + num > c->entries || curlen > 32) return -1;
            for (k = 0; k < num; k++) c->lens[cur + k] = (uint8_t)curlen;
            cur += num; nused += num; curlen++;
            if (b->eop) return -1;
        }
    }
    if (b->eop) return -1;

    /* assign codewords (Vorbis rule: lowest free codeword of the requested length) */
    codes = (uint32_t *)calloc(c->entries, sizeof(uint32_t));
    memset(avail, 0, sizeof avail);
    {
        int k = 0, first = 1;
        for (i = 0; i < c->entries; i++) {
            int z = c->lens[i], y; uint32_t res;
            if (!z) continue;
            if (first) {
                first = 0; codes[i] = 0;
                for (y = 1; y <= z; y++) avail[y] = 1u << (32 - y);
                k++; continue;
            }
            while (z > 0 && !avail[z]) --z;
            if (z == 0) { free(codes); return -1; }
            res = avail[z]; avail[z] = 0; codes[i] = res;
            if (z != c->lens[i]) for (y = c->lens[i]; y > z; --y) avail[y] = res + (1u << (32 - y));
            k++;
        }
    }
    /* fast table + sorted list */
    for (i = 0; i < (1 << FAST_BITS); i++) c->fast[i] = -1;
    c->nsorted = nused;
    c->sorted_codes = (uint32_t *)malloc(sizeof(uint32_t) * (nused ? nused : 1));
    c->sorted_entry = (int *)malloc(sizeof(int) * (nused ? nused : 1));
    c->sorted_len = (uint8_t *)malloc(nused ? nused : 1);
    {
        int n = 0;
        for (i = 0; i < c->entries; i++) {
            int L = c->lens[i];
            if (!L) continue;
            if (L <= FAST_BITS) {
                uint32_t rev = bitrev32(codes[i]) ;               /* first bit read = MSB of the code */
                uint32_t base = rev & ((1u << L) - 1), step = 1u << L, idx;
                for (idx = base; idx < (1u << FAST_BITS); idx += step) { c->fast[idx] = (int16_t)i; c->fastlen[idx] = (uint8_t)L; }
            }
            c->sorted_codes[n] = codes[i]; c->sorted_entry[n] = i; c->sorted_len[n] = (uint8_t)L; n++;
        }
        /* insertion sort is too slow for big books; use simple shell sort */
        {
            int gap, j;
            for (gap = n / 2; gap > 0; gap /= 2)
                for (i = gap; i < n; i++) {
                    uint32_t tc = c->sorted_codes[i]; int te = c->sorted_entry[i]; uint8_t tl = c->sorted_len[i];
                    for (j = i; j >= gap && c->sorted_codes[j - gap] > tc; j -= gap) {
                        c->sorted_codes[j] = c->sorted_codes[j - gap]; c->sorted_entry[j] = c->sorted_entry[j - gap]; c->sorted_len[j] = c->sorted_len[j - gap];
                    }
                    c->sorted_codes[j] = tc; c->sorted_entry[j] = te; c->sorted_len[j] = tl;
                }
        }
    }
    free(codes);

    /* vector lookup */
    {
        int lt = (int)br_read(b, 4);
        if (lt > 2) return -1;
        if (lt) {
            float minv = float32_unpack(br_read(b, 32)), delta = float32_unpack(br_read(b, 32));
            int vbits = (int)br_read(b, 4) + 1, seq = (int)br_read(b, 1);
            int nvals = lt == 1 ? lookup1_values(c->entries, c->dim) : c->entries * c->dim;
            uint16_t *mult = (uint16_t *)malloc(sizeof(uint16_t) * nvals);
            int e, k;
            if (nvals <= 0 || (double)c->entries * c->dim > 8.0e6) { free(mult); return -1; }
            for (i = 0; i < nvals; i++) mult[i] = (uint16_t)br_read(b, vbits);
            if (b->eop) { free(mult); return -1; }
            c->vecs = (float *)malloc(sizeof(float) * c->entries * c->dim);
            for (e = 0; e < c->entries; e++) {
                float last = 0; int div = 1;
                for (k = 0; k < c->dim; k++) {
                    int moff = lt == 1 ? (e / div) % nvals : e * c->dim + k;
                    float v = (float)mult[moff] * delta + minv + last;
                    c->vecs[e * c->dim + k] = v;
                    if (seq) last = v;
                    if (lt == 1) div *= nvals;
                }
            }
            free(mult);
        }
    }
    return b->eop ? -1 : 0;
}

static int cb_scalar(const Codebook *c, BR *b)
{
    uint32_t pk = br_peek(b, FAST_BITS);
    int e = c->fast[pk];
    if (e >= 0) { br_skip(b, c->fastlen[pk]); return e; }
    {
        uint32_t v = bitrev32(br_peek32(b));
        int lo = 0, hi = c->nsorted - 1, best = -1;
        while (lo <= hi) {
            int mid = (lo + hi) >> 1;
            if (c->sorted_codes[mid] <= v) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (best < 0) { b->eop = 1; return -1; }
        br_skip(b, c->sorted_len[best]);
        return c->sorted_entry[best];
    }
}

/* ------------------------------------------------------------------ floor 1 */
typedef struct {
    int partitions, nclasses, multiplier, rangebits, nvalues;
    int part_class[32];
    int cdim[16], csub[16], cmaster[16], cbook[16][8];
    int X[66], order[66], lowN[66], highN[66];
} Floor1;

static int floor1_parse(Floor1 *f, BR *b)
{
    int i, j, maxc = -1;
    f->partitions = (int)br_read(b, 5);
    for (i = 0; i < f->partitions; i++) { f->part_class[i] = (int)br_read(b, 4); if (f->part_class[i] > maxc) maxc = f->part_class[i]; }
    f->nclasses = maxc + 1;
    for (i = 0; i < f->nclasses; i++) {
        f->cdim[i] = (int)br_read(b, 3) + 1;
        f->csub[i] = (int)br_read(b, 2);
        f->cmaster[i] = f->csub[i] ? (int)br_read(b, 8) : 0;
        for (j = 0; j < (1 << f->csub[i]); j++) f->cbook[i][j] = (int)br_read(b, 8) - 1;
    }
    f->multiplier = (int)br_read(b, 2) + 1;
    f->rangebits = (int)br_read(b, 4);
    f->X[0] = 0; f->X[1] = 1 << f->rangebits; f->nvalues = 2;
    for (i = 0; i < f->partitions; i++) {
        int c = f->part_class[i];
        for (j = 0; j < f->cdim[c]; j++) {
            if (f->nvalues >= 66) return -1;
            f->X[f->nvalues++] = (int)br_read(b, f->rangebits);
        }
    }
    /* sort + neighbours */
    for (i = 0; i < f->nvalues; i++) f->order[i] = i;
    for (i = 1; i < f->nvalues; i++) { int t = f->order[i]; for (j = i; j > 0 && f->X[f->order[j-1]] > f->X[t]; j--) f->order[j] = f->order[j-1]; f->order[j] = t; }
    for (i = 2; i < f->nvalues; i++) {
        int lo = 0, hi = 1, k;                      /* indices of closest lower / higher X among 0..i-1 */
        int lov = -1, hiv = 1 << 30;
        for (k = 0; k < i; k++) {
            if (f->X[k] < f->X[i] && f->X[k] > lov) { lov = f->X[k]; lo = k; }
            if (f->X[k] > f->X[i] && f->X[k] < hiv) { hiv = f->X[k]; hi = k; }
        }
        f->lowN[i] = lo; f->highN[i] = hi;
    }
    return b->eop ? -1 : 0;
}

static int render_point(int x0, int y0, int x1, int y1, int x)
{
    int dy = y1 - y0, adx = x1 - x0, ady = dy < 0 ? -dy : dy;
    int err = ady * (x - x0), off = adx ? err / adx : 0;
    return dy < 0 ? y0 - off : y0 + off;
}

static void render_line(int x0, int y0, int x1, int y1, float *out, int n)
{
    int dy = y1 - y0, adx = x1 - x0, ady = dy < 0 ? -dy : dy, base, x, y, err = 0, sy;
    if (adx <= 0) return;
    base = dy / adx;
    sy = dy < 0 ? base - 1 : base + 1;
    ady -= (base < 0 ? -base : base) * adx;
    x = x0; y = y0;
    if (x < n) out[x] = floor1_db[y < 0 ? 0 : y > 255 ? 255 : y];
    for (x = x0 + 1; x < x1; x++) {
        err += ady;
        if (err >= adx) { err -= adx; y += sy; } else y += base;
        if (x < n) out[x] = floor1_db[y < 0 ? 0 : y > 255 ? 255 : y];
    }
}

/* returns 1 = floor present (curve written to out[0..n-1]), 0 = unused, -1 = error */
static int floor1_decode(const Floor1 *f, const Codebook *books, int nbooks, BR *b, float *out, int n)
{
    static const int ranges[4] = { 256, 128, 86, 64 };
    int range = ranges[f->multiplier - 1];
    int Y[66], finalY[66], step2[66];
    int i, j, offset = 2;
    if (!br_read(b, 1)) return 0;
    Y[0] = (int)br_read(b, ilog(range - 1));
    Y[1] = (int)br_read(b, ilog(range - 1));
    for (i = 0; i < f->partitions; i++) {
        int cls = f->part_class[i], cdim = f->cdim[cls], cbits = f->csub[cls], csub = (1 << cbits) - 1, cval = 0;
        if (cbits) {
            if (f->cmaster[cls] >= nbooks) return -1;
            cval = cb_scalar(&books[f->cmaster[cls]], b);
            if (cval < 0) return 0;
        }
        for (j = 0; j < cdim; j++) {
            int bk = f->cbook[cls][cval & csub];
            cval >>= cbits;
            if (bk >= 0) {
                if (bk >= nbooks) return -1;
                Y[offset + j] = cb_scalar(&books[bk], b);
                if (Y[offset + j] < 0) return 0;
            } else Y[offset + j] = 0;
        }
        offset += cdim;
    }
    if (b->eop) return 0;
    step2[0] = step2[1] = 1; finalY[0] = Y[0]; finalY[1] = Y[1];
    for (i = 2; i < f->nvalues; i++) {
        int lo = f->lowN[i], hi = f->highN[i];
        int predicted = render_point(f->X[lo], finalY[lo], f->X[hi], finalY[hi], f->X[i]);
        int val = Y[i], highroom = range - predicted, lowroom = predicted, room = (highroom < lowroom ? highroom : lowroom) * 2;
        if (val) {
            step2[lo] = step2[hi] = step2[i] = 1;
            if (val >= room) finalY[i] = highroom > lowroom ? val - lowroom + predicted : predicted - val + highroom - 1;
            else finalY[i] = (val & 1) ? predicted - ((val + 1) >> 1) : predicted + (val >> 1);
        } else { step2[i] = 0; finalY[i] = predicted; }
    }
    {
        int lx = 0, hx = 0, ly = finalY[f->order[0]] * f->multiplier, hy = ly;
        for (i = 1; i < f->nvalues; i++) {
            int j2 = f->order[i];
            if (step2[j2]) {
                hx = f->X[j2]; hy = finalY[j2] * f->multiplier;
                render_line(lx, ly, hx, hy, out, n);
                lx = hx; ly = hy;
            }
        }
        for (i = hx; i < n; i++) out[i] = floor1_db[ly < 0 ? 0 : ly > 255 ? 255 : ly];
    }
    return 1;
}

/* ------------------------------------------------------------------ residue */
typedef struct {
    int type, begin, end, psize, nclass, classbook;
    int books[64][8];
} Residue;

static int residue_parse(Residue *r, BR *b, int type)
{
    int i, j; int cascade[64];
    r->type = type;
    r->begin = (int)br_read(b, 24); r->end = (int)br_read(b, 24);
    r->psize = (int)br_read(b, 24) + 1; r->nclass = (int)br_read(b, 6) + 1; r->classbook = (int)br_read(b, 8);
    for (i = 0; i < r->nclass; i++) {
        int low = (int)br_read(b, 3), flag = (int)br_read(b, 1), high = 0;
        if (flag) high = (int)br_read(b, 5);
        cascade[i] = high * 8 + low;
    }
    for (i = 0; i < r->nclass; i++)
        for (j = 0; j < 8; j++) r->books[i][j] = (cascade[i] & (1 << j)) ? (int)br_read(b, 8) : -1;
    return b->eop ? -1 : 0;
}

/* ------------------------------------------------------------------ mapping / mode */
typedef struct {
    int submaps, ncoupling;
    int mag[256], ang[256];
    int mux[8];
    int floor[16], residue[16];
} Mapping;
typedef struct { int blockflag, mapping; } Mode;

/* ------------------------------------------------------------------ IMDCT (via DCT-IV and a complex FFT) */
typedef struct {
    int M, h;
    float *pre_re, *pre_im, *post_re, *post_im, *tw_re, *tw_im;
    int *rev;
    float *vr, *vi, *C;
} Imdct;

static void imdct_init(Imdct *m, int N)
{
    int M = N / 2, h = M / 2, i, j, bits = 0;
    m->M = M; m->h = h;
    m->pre_re = (float *)malloc(sizeof(float) * h); m->pre_im = (float *)malloc(sizeof(float) * h);
    m->post_re = (float *)malloc(sizeof(float) * h); m->post_im = (float *)malloc(sizeof(float) * h);
    m->tw_re = (float *)malloc(sizeof(float) * h); m->tw_im = (float *)malloc(sizeof(float) * h);
    m->rev = (int *)malloc(sizeof(int) * h);
    m->vr = (float *)malloc(sizeof(float) * h); m->vi = (float *)malloc(sizeof(float) * h);
    m->C = (float *)malloc(sizeof(float) * M);
    for (i = 0; i < h; i++) {
        double a = -PI_D * (i + 0.25) / M, b2 = -PI_D * i / M, t = -2.0 * PI_D * i / h;
        m->pre_re[i] = (float)cos(a); m->pre_im[i] = (float)sin(a);
        m->post_re[i] = (float)cos(b2); m->post_im[i] = (float)sin(b2);
        m->tw_re[i] = (float)cos(t); m->tw_im[i] = (float)sin(t);
    }
    while ((1 << bits) < h) bits++;
    for (i = 0; i < h; i++) { int r = 0; for (j = 0; j < bits; j++) if (i & (1 << j)) r |= 1 << (bits - 1 - j); m->rev[i] = r; }
}
static void imdct_free(Imdct *m)
{
    free(m->pre_re); free(m->pre_im); free(m->post_re); free(m->post_im); free(m->tw_re); free(m->tw_im);
    free(m->rev); free(m->vr); free(m->vi); free(m->C); memset(m, 0, sizeof *m);
}

/* X: M coefficients -> y: N = 2M samples (unwindowed, unscaled) */
static void imdct_run(Imdct *m, const float *X, float *y, float scale)
{
    int M = m->M, h = m->h, j, p, len, k, half;
    float *vr = m->vr, *vi = m->vi, *C = m->C;
    for (j = 0; j < h; j++) {
        float re = X[2 * j], im = X[M - 1 - 2 * j];
        int r = m->rev[j];
        vr[r] = re * m->pre_re[j] - im * m->pre_im[j];
        vi[r] = re * m->pre_im[j] + im * m->pre_re[j];
    }
    for (len = 2; len <= h; len <<= 1) {
        half = len >> 1;
        int tstep = h / len;
        for (k = 0; k < h; k += len) {
            for (j = 0; j < half; j++) {
                float wr = m->tw_re[j * tstep], wi = m->tw_im[j * tstep];
                int a = k + j, bb = a + half;
                float tr = vr[bb] * wr - vi[bb] * wi, ti = vr[bb] * wi + vi[bb] * wr;
                vr[bb] = vr[a] - tr; vi[bb] = vi[a] - ti;
                vr[a] += tr; vi[a] += ti;
            }
        }
    }
    for (p = 0; p < h; p++) {
        float zr = vr[p] * m->post_re[p] - vi[p] * m->post_im[p];
        float zi = vr[p] * m->post_im[p] + vi[p] * m->post_re[p];
        C[2 * p] = zr * scale; C[M - 1 - 2 * p] = -zi * scale;
    }
    /* unfold DCT-IV output into the 2M IMDCT output */
    for (j = 0; j < M / 2; j++) y[j] = C[j + M / 2];
    for (j = M / 2; j < 3 * M / 2; j++) y[j] = -C[3 * M / 2 - 1 - j];
    for (j = 3 * M / 2; j < 2 * M; j++) y[j] = -C[j - 3 * M / 2];
}

/* ------------------------------------------------------------------ decoder */
struct vorbis_dec {
    int channels, rate, bs[2];
    int ncb; Codebook *cb;
    int nfloor; Floor1 *floors;
    int nres; Residue *res;
    int nmap; Mapping *maps;
    int nmode; Mode *modes;
    Imdct mdct[2];
    float *slope[2];                  /* rising power-sine slope for short / long */
    float *coef[2], *fl[2], *buf[2];  /* per channel work */
    float *prev[2]; int prev_n; int have_prev;
    float *outp[2];                   /* output arrays */
    int *cls; float *r2;
    int header_state;
};

vorbis_dec *vd_create(void) { return (vorbis_dec *)calloc(1, sizeof(vorbis_dec)); }

void vd_destroy(vorbis_dec *d)
{
    int i;
    if (!d) return;
    for (i = 0; i < d->ncb; i++) cb_free(&d->cb[i]);
    free(d->cb); free(d->floors); free(d->res); free(d->maps); free(d->modes);
    for (i = 0; i < 2; i++) { if (d->mdct[i].M) imdct_free(&d->mdct[i]); free(d->slope[i]); }
    for (i = 0; i < 2; i++) { free(d->coef[i]); free(d->fl[i]); free(d->buf[i]); free(d->prev[i]); free(d->outp[i]); }
    free(d->cls); free(d->r2);
    free(d);
}

int vd_channels(const vorbis_dec *d) { return d->channels; }
int vd_rate(const vorbis_dec *d) { return d->rate; }

void vd_reset(vorbis_dec *d) { d->have_prev = 0; d->prev_n = 0; }

static int parse_ident(vorbis_dec *d, BR *b)
{
    int i;
    if (br_read(b, 32) != 0) return -1;
    d->channels = (int)br_read(b, 8); d->rate = (int)br_read(b, 32);
    br_read(b, 32); br_read(b, 32); br_read(b, 32);
    d->bs[0] = 1 << br_read(b, 4); d->bs[1] = 1 << br_read(b, 4);
    if (d->channels < 1 || d->channels > 2 || d->bs[0] > d->bs[1] || d->bs[0] < 64 || d->bs[1] > MAXBLOCK) return -1;
    for (i = 0; i < 2; i++) {
        int n = d->bs[i], k;
        imdct_init(&d->mdct[i], n);
        d->slope[i] = (float *)malloc(sizeof(float) * (n / 2));
        for (k = 0; k < n / 2; k++) { double s = sin(PI_D * (k + 0.5) / n); d->slope[i][k] = (float)sin(PI_D / 2 * s * s); }
    }
    for (i = 0; i < d->channels; i++) {
        d->coef[i] = (float *)calloc(MAXBLOCK / 2, sizeof(float));
        d->fl[i] = (float *)calloc(MAXBLOCK / 2, sizeof(float));
        d->buf[i] = (float *)calloc(MAXBLOCK, sizeof(float));
        d->prev[i] = (float *)calloc(MAXBLOCK / 2, sizeof(float));
        d->outp[i] = (float *)calloc(MAXBLOCK, sizeof(float));
    }
    d->cls = (int *)malloc(sizeof(int) * d->channels * (MAXBLOCK / 2 + 64));
    d->r2 = (float *)calloc(MAXBLOCK * 2, sizeof(float));
    return b->eop ? -1 : 0;
}

static int parse_setup(vorbis_dec *d, BR *b)
{
    int i, j, n;
    d->ncb = (int)br_read(b, 8) + 1;
    d->cb = (Codebook *)calloc(d->ncb, sizeof(Codebook));
    for (i = 0; i < d->ncb; i++) if (cb_parse(&d->cb[i], b) < 0) return -1;
    n = (int)br_read(b, 6) + 1;
    for (i = 0; i < n; i++) if (br_read(b, 16) != 0) return -1;
    d->nfloor = (int)br_read(b, 6) + 1;
    d->floors = (Floor1 *)calloc(d->nfloor, sizeof(Floor1));
    for (i = 0; i < d->nfloor; i++) {
        if (br_read(b, 16) != 1) return -1;            /* only floor type 1 */
        if (floor1_parse(&d->floors[i], b) < 0) return -1;
    }
    d->nres = (int)br_read(b, 6) + 1;
    d->res = (Residue *)calloc(d->nres, sizeof(Residue));
    for (i = 0; i < d->nres; i++) {
        int t = (int)br_read(b, 16);
        if (t > 2) return -1;
        if (residue_parse(&d->res[i], b, t) < 0) return -1;
        if (d->res[i].classbook >= d->ncb) return -1;
    }
    d->nmap = (int)br_read(b, 6) + 1;
    d->maps = (Mapping *)calloc(d->nmap, sizeof(Mapping));
    for (i = 0; i < d->nmap; i++) {
        Mapping *m = &d->maps[i];
        if (br_read(b, 16) != 0) return -1;
        m->submaps = br_read(b, 1) ? (int)br_read(b, 4) + 1 : 1;
        if (br_read(b, 1)) {
            m->ncoupling = (int)br_read(b, 8) + 1;
            for (j = 0; j < m->ncoupling; j++) {
                m->mag[j] = (int)br_read(b, ilog(d->channels - 1)); m->ang[j] = (int)br_read(b, ilog(d->channels - 1));
                if (m->mag[j] == m->ang[j] || m->mag[j] >= d->channels || m->ang[j] >= d->channels) return -1;
            }
        }
        if (br_read(b, 2) != 0) return -1;
        if (m->submaps > 1) for (j = 0; j < d->channels; j++) { m->mux[j] = (int)br_read(b, 4); if (m->mux[j] >= m->submaps) return -1; }
        for (j = 0; j < m->submaps; j++) {
            br_read(b, 8);
            m->floor[j] = (int)br_read(b, 8); m->residue[j] = (int)br_read(b, 8);
            if (m->floor[j] >= d->nfloor || m->residue[j] >= d->nres) return -1;
        }
    }
    d->nmode = (int)br_read(b, 6) + 1;
    d->modes = (Mode *)calloc(d->nmode, sizeof(Mode));
    for (i = 0; i < d->nmode; i++) {
        d->modes[i].blockflag = (int)br_read(b, 1);
        br_read(b, 16); br_read(b, 16);
        d->modes[i].mapping = (int)br_read(b, 8);
        if (d->modes[i].mapping >= d->nmap) return -1;
    }
    return b->eop ? -1 : 0;
}

int vd_header(vorbis_dec *d, const uint8_t *pkt, int len, int index)
{
    BR b; int type;
    b.d = pkt; b.nbytes = len; b.pos = 0; b.eop = 0;
    if (len < 7) return -1;
    type = (int)br_read(&b, 8);
    if (memcmp(pkt + 1, "vorbis", 6) != 0) return -1;
    b.pos = 56;
    if (index == 0 && type == 1) { if (parse_ident(d, &b) < 0) return -1; d->header_state = 1; return 0; }
    if (index == 1 && type == 3) { if (d->header_state != 1) return -1; d->header_state = 2; return 0; }
    if (index == 2 && type == 5) { if (d->header_state != 2) return -1; if (parse_setup(d, &b) < 0) return -1; d->header_state = 3; return 0; }
    return -1;
}

static int residue_decode(vorbis_dec *d, const Residue *r, int ch, float **vecs, const uint8_t *dodec, int n, BR *b)
{
    int type = r->type, actual = type == 2 ? n * ch : n;
    int lb = r->begin < actual ? r->begin : actual, le = r->end < actual ? r->end : actual;
    int nread = le - lb, nparts, pass, nch = type == 2 ? 1 : ch;
    const Codebook *cbk = &d->cb[r->classbook];
    int cwords = cbk->dim, stride = n / 1 + 64 + 8;
    float *tgt[2];
    int j;
    (void)stride;
    if (nread <= 0) return 0;
    nparts = nread / r->psize;
    if (nparts <= 0) return 0;
    if (nparts + cwords > MAXBLOCK / 2 + 64) return -1;
    if (type == 2) { memset(d->r2, 0, sizeof(float) * actual); tgt[0] = d->r2; }
    else for (j = 0; j < ch; j++) tgt[j] = vecs[j];

    for (pass = 0; pass < 8; pass++) {
        int pc = 0;
        while (pc < nparts) {
            if (pass == 0) {
                for (j = 0; j < nch; j++) {
                    int temp, i;
                    if (type != 2 && !dodec[j]) continue;
                    temp = cb_scalar(cbk, b);
                    if (temp < 0) return 0;                       /* end of packet: keep what we have */
                    for (i = cwords - 1; i >= 0; i--) { d->cls[j * (MAXBLOCK / 2 + 64) + pc + i] = temp % r->nclass; temp /= r->nclass; }
                }
            }
            {
                int i;
                for (i = 0; i < cwords && pc < nparts; i++, pc++) {
                    for (j = 0; j < nch; j++) {
                        int c, bk, off, k, e;
                        const Codebook *vb;
                        if (type != 2 && !dodec[j]) continue;
                        c = d->cls[j * (MAXBLOCK / 2 + 64) + pc];
                        bk = r->books[c][pass];
                        if (bk < 0) continue;
                        if (bk >= d->ncb) return -1;
                        vb = &d->cb[bk];
                        if (!vb->vecs) return -1;
                        off = lb + pc * r->psize;
                        if (type == 0) {
                            int step = r->psize / vb->dim;
                            for (k = 0; k < step; k++) {
                                int q;
                                e = cb_scalar(vb, b);
                                if (e < 0) return 0;
                                for (q = 0; q < vb->dim; q++) tgt[j][off + k + q * step] += vb->vecs[e * vb->dim + q];
                            }
                        } else {
                            for (k = 0; k < r->psize; k += vb->dim) {
                                int q;
                                e = cb_scalar(vb, b);
                                if (e < 0) return 0;
                                for (q = 0; q < vb->dim && off + k + q < actual; q++) tgt[j][off + k + q] += vb->vecs[e * vb->dim + q];
                            }
                        }
                    }
                }
            }
            if (b->eop) return 0;
        }
    }
    if (type == 2) {
        int i, c;
        for (i = 0; i < n; i++) for (c = 0; c < ch; c++) vecs[c][i] = d->r2[i * ch + c];
    }
    return 0;
}

#define VORBIS_SCALE 1.0f

int vd_decode(vorbis_dec *d, const uint8_t *pkt, int len, float ***out, int *nout)
{
    BR b; int mode_i, bf, n, h, c, i, prev_long = 0, next_long = 0;
    const Mode *mode; const Mapping *map;
    uint8_t nonzero[2], dodec[2];
    float *vecs[2];
    int s;

    *nout = 0;
    if (d->header_state != 3) return -1;
    b.d = pkt; b.nbytes = len; b.pos = 0; b.eop = 0;
    if (br_read(&b, 1) != 0) return -2;                      /* not an audio packet */
    mode_i = (int)br_read(&b, ilog(d->nmode - 1));
    if (mode_i >= d->nmode) return -1;
    mode = &d->modes[mode_i]; map = &d->maps[mode->mapping];
    bf = mode->blockflag; n = d->bs[bf]; h = n / 2;
    if (bf) { prev_long = (int)br_read(&b, 1); next_long = (int)br_read(&b, 1); }
    if (b.eop) return -2;

    /* floors */
    for (c = 0; c < d->channels; c++) {
        int sm = map->submaps > 1 ? map->mux[c] : 0, r;
        (void)sm;
        r = floor1_decode(&d->floors[map->floor[map->submaps > 1 ? map->mux[c] : 0]], d->cb, d->ncb, &b, d->fl[c], h);
        if (r < 0) return -1;
        nonzero[c] = (uint8_t)r;
        memset(d->coef[c], 0, sizeof(float) * h);
    }
    for (i = 0; i < map->ncoupling; i++)
        if (nonzero[map->mag[i]] || nonzero[map->ang[i]]) nonzero[map->mag[i]] = nonzero[map->ang[i]] = 1;

    /* residues, per submap */
    for (s = 0; s < map->submaps; s++) {
        int k = 0, any = 0;
        for (c = 0; c < d->channels; c++) {
            if ((map->submaps > 1 ? map->mux[c] : 0) != s) continue;
            dodec[k] = nonzero[c]; vecs[k] = d->coef[c]; if (nonzero[c]) any = 1; k++;
        }
        if (any && residue_decode(d, &d->res[map->residue[s]], k, vecs, dodec, h, &b) < 0) return -1;
    }

    /* inverse coupling */
    for (i = map->ncoupling - 1; i >= 0; i--) {
        float *M = d->coef[map->mag[i]], *A = d->coef[map->ang[i]];
        int k;
        for (k = 0; k < h; k++) {
            float m = M[k], a = A[k], nm, na;
            if (m > 0) { if (a > 0) { nm = m; na = m - a; } else { na = m; nm = m + a; } }
            else { if (a > 0) { nm = m; na = m + a; } else { na = m; nm = m - a; } }
            M[k] = nm; A[k] = na;
        }
    }

    /* spectrum = floor * residue, IMDCT, window */
    for (c = 0; c < d->channels; c++) {
        float *y = d->buf[c];
        int k, ls, rs, lstart, rstart;
        if (!nonzero[c]) { memset(y, 0, sizeof(float) * n); }
        else {
            for (k = 0; k < h; k++) d->coef[c][k] *= d->fl[c][k];
            imdct_run(&d->mdct[bf], d->coef[c], y, VORBIS_SCALE);
        }
        /* window */
        ls = (bf && prev_long) || !bf ? 0 : 1;               /* ls = 1 -> short slope on the left of a long block */
        rs = (bf && next_long) || !bf ? 0 : 1;
        {
            int lslope = (!bf || ls) ? d->bs[0] / 2 : n / 2, rslope = (!bf || rs) ? d->bs[0] / 2 : n / 2;
            const float *lw = (lslope == d->bs[0] / 2) ? d->slope[0] : d->slope[1];
            const float *rw = (rslope == d->bs[0] / 2) ? d->slope[0] : d->slope[1];
            lstart = n / 4 - lslope / 2; rstart = n / 4 - rslope / 2;
            for (k = 0; k < lstart; k++) y[k] = 0;
            for (k = 0; k < lslope; k++) y[lstart + k] *= lw[k];
            /* right half: positions h + m */
            for (k = 0; k < rstart; k++) ;                      /* weight 1 */
            for (k = 0; k < rslope; k++) y[h + rstart + k] *= rw[rslope - 1 - k];
            for (k = h + rstart + rslope; k < n; k++) y[k] = 0;
        }
    }

    /* overlap-add */
    if (d->have_prev) {
        int outlen = d->prev_n / 4 + n / 4, a;
        for (c = 0; c < d->channels; c++) {
            float *o = d->outp[c], *y = d->buf[c], *pr = d->prev[c];
            for (a = 0; a < outlen; a++) {
                float v = a < d->prev_n / 2 ? pr[a] : 0.0f;
                int bi = a - d->prev_n / 4 + n / 4;
                if (bi >= 0 && bi < h) v += y[bi];
                o[a] = v;
            }
        }
        *nout = outlen;
    }
    for (c = 0; c < d->channels; c++) memcpy(d->prev[c], d->buf[c] + h, sizeof(float) * h);
    d->prev_n = n; d->have_prev = 1;
    *out = d->outp;
    return 0;
}

/* ------------------------------------------------------------------ Ogg demuxer */
void og_init(ogg_reader *o, const uint8_t *data) { memset(o, 0, sizeof *o); o->data = data; }
void og_free(ogg_reader *o) { free(o->pk); o->pk = NULL; o->pk_cap = 0; }

static void pk_append(ogg_reader *o, const uint8_t *p, int n)
{
    if (o->pk_len + n > o->pk_cap) { o->pk_cap = (o->pk_len + n) * 2 + 1024; o->pk = (uint8_t *)realloc(o->pk, o->pk_cap); }
    memcpy(o->pk + o->pk_len, p, n); o->pk_len += n;
}

int og_next(ogg_reader *o, const uint8_t **pkt, int *len, int64_t *gran, int *eos)
{
    if (o->pk_ready) { o->pk_len = 0; o->pk_ready = 0; }
    for (;;) {
        if (!o->in_page) {
            int i, total = 0; const uint8_t *p;
            if (o->pos + 27 > o->avail) return 0;
            p = o->data + o->pos;
            if (memcmp(p, "OggS", 4) != 0) return -1;
            o->nsegs = p[26];
            if (o->pos + 27 + o->nsegs > o->avail) return 0;
            for (i = 0; i < o->nsegs; i++) { o->lacing[i] = p[27 + i]; total += p[27 + i]; }
            if (o->pos + 27 + o->nsegs + total > o->avail) return 0;
            o->hdr_type = p[5];
            o->granule = (int64_t)((uint64_t)p[6] | ((uint64_t)p[7] << 8) | ((uint64_t)p[8] << 16) | ((uint64_t)p[9] << 24) |
                                   ((uint64_t)p[10] << 32) | ((uint64_t)p[11] << 40) | ((uint64_t)p[12] << 48) | ((uint64_t)p[13] << 56));
            o->body_off = o->pos + 27 + o->nsegs; o->seg_idx = 0; o->in_page = 1;
            if (!(o->hdr_type & 1)) o->pk_len = 0;
        }
        while (o->seg_idx < o->nsegs) {
            int l = o->lacing[o->seg_idx++];
            pk_append(o, o->data + o->body_off, l); o->body_off += l;
            if (l < 255) {
                int last = o->seg_idx >= o->nsegs;
                *pkt = o->pk; *len = o->pk_len; o->pk_ready = 1;
                *gran = last ? o->granule : -1;
                *eos = last && (o->hdr_type & 4);
                if (last) { o->in_page = 0; o->pos = o->body_off; }
                return 1;
            }
        }
        o->in_page = 0; o->pos = o->body_off;
    }
}
