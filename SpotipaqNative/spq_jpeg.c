/*
 * spq_jpeg.c - SpotipaqNative.dll: a small baseline JPEG decoder that decodes straight to 1/1, 1/2, 1/4 or 1/8 of
 * the picture's size.
 *
 * Spotipaq's artwork is shown far smaller than it is downloaded (a 300 px cover in a 28 px row), and GDI+ can only
 * decode at full size and then shrink - most of the work thrown away, and both steps slow on the Pentium II. A JPEG
 * is stored as 8x8 blocks of frequencies, so a smaller picture can be had by using only each block's lowest ones:
 * at 1/8 a block is just its average (no transform at all), at 1/4 a 2x2 transform, at 1/2 a 4x4 one.
 *
 *   spq_jpeg_decode  decodes data[len] to 32-bit B,G,R,A (A = 255), at the smallest of those sizes that is still at
 *                    least needW x needH once cropped to that shape (as ImageCache.Scale crops: "cover"). needW/needH
 *                    <= 0 = full size. out is malloc'd (free with spq_free), *stride = *w * 4.
 *                    Returns 1, or 0 when the file isn't a JPEG this decoder handles (progressive, arithmetic coded,
 *                    12-bit, CMYK...) or is damaged - the caller then decodes it another way.
 *
 * Plain C, no SSE (floating point via the x87 FPU in the transform). Thread-safe: no shared state.
 */
#define WIN32_LEAN_AND_MEAN
#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifdef _MSC_VER
#define SPQ_API __declspec(dllexport)
#define SPQ_CDECL __cdecl
#else
#define SPQ_API
#define SPQ_CDECL
#endif

/* ------------------------------------------------------------------------------------------ tables */
static const unsigned char kZigzag[64] = {
     0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
    12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
    35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
    58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63 };

typedef struct
{
    unsigned char  lookBits[512];   /* first 9 bits -> code length (0 = longer than 9) */
    unsigned char  lookSym[512];    /* ...and its symbol */
    int            maxCode[18];     /* largest code of each length, -1 = none */
    int            valOffset[17];   /* symbol index = code + valOffset[length] */
    unsigned char  vals[256];
    int            present;
} huff_t;

typedef struct
{
    int id, h, v, tq, td, ta;
    int bw, bh;                     /* blocks across / down in this component's plane (whole MCUs) */
    int nx, ny;                     /* each block decodes to nx x ny samples */
    int pw, ph;                     /* plane size in samples */
    unsigned char* plane;
    int dcPred;
} comp_t;

typedef struct
{
    const unsigned char* p;
    const unsigned char* end;
    unsigned int bits;              /* bit buffer, msb first */
    int nbits;
    int hitMarker;                  /* a marker was reached: feed zeros from here */
} bits_t;

typedef struct
{
    unsigned short qt[4][64];       /* in natural (not zigzag) order */
    int qtPresent[4];
    huff_t dc[4], ac[4];
    comp_t comp[3];
    int ncomp, width, height, hmax, vmax, restart, adobe, adobeTransform;
    int progressive;
} jpeg_t;

/* ---------------------------------------------------------------------------------------- huffman */
static int build_huff(huff_t* h, const unsigned char* counts, const unsigned char* vals, int nvals)
{
    int code = 0, k = 0, len, i;
    memset(h, 0, sizeof(*h));
    if (nvals > 256) return 0;
    memcpy(h->vals, vals, nvals);
    for (len = 1; len <= 16; len++)
    {
        h->valOffset[len] = k - code;
        for (i = 0; i < counts[len - 1]; i++)
        {
            if (len <= 9)
            {
                int fill = 1 << (9 - len), base = code << (9 - len), j;
                for (j = 0; j < fill; j++) { h->lookBits[base + j] = (unsigned char)len; h->lookSym[base + j] = vals[k]; }
            }
            code++; k++;
        }
        h->maxCode[len] = counts[len - 1] ? code - 1 : -1;
        if (code > (1 << len)) return 0;      /* more codes than this length allows: a broken table */
        code <<= 1;
    }
    h->maxCode[17] = 0x7FFFFFFF;
    h->present = 1;
    return k == nvals;
}

static void fill_bits(bits_t* b)
{
    while (b->nbits <= 24)
    {
        unsigned int c = 0;
        if (!b->hitMarker && b->p < b->end)
        {
            c = *b->p;
            if (c == 0xFF)
            {
                unsigned int n = b->p + 1 < b->end ? b->p[1] : 0;
                if (n == 0x00) b->p += 2;         /* a stuffed 0xFF data byte */
                else { b->hitMarker = 1; c = 0; } /* a marker: the data stops here */
            }
            else b->p++;
        }
        b->bits |= c << (24 - b->nbits);
        b->nbits += 8;
    }
}

static int get_bits(bits_t* b, int n)
{
    unsigned int v;
    if (n == 0) return 0;
    if (b->nbits < n) fill_bits(b);
    v = b->bits >> (32 - n);
    b->bits <<= n; b->nbits -= n;
    return (int)v;
}

/* a value of n bits, in JPEG's sign convention */
static int extend(int v, int n) { return v < (1 << (n - 1)) ? v - (1 << n) + 1 : v; }

static int decode_huff(bits_t* b, const huff_t* h)
{
    int look, len, code;
    if (b->nbits < 16) fill_bits(b);
    look = (int)(b->bits >> 23);
    len = h->lookBits[look];
    if (len) { b->bits <<= len; b->nbits -= len; return h->lookSym[look]; }
    code = (int)(b->bits >> 22); len = 10;              /* longer codes, one length at a time */
    while (len <= 16 && code > h->maxCode[len]) { len++; code = (int)(b->bits >> (32 - len)); }
    if (len > 16) return -1;
    b->bits <<= len; b->nbits -= len;
    code += h->valOffset[len];
    return code >= 0 && code < 256 ? h->vals[code] : -1;
}

/* ------------------------------------------------------------------------------- scaled transform */
/* One block's samples at nx x ny (each 8, 4, 2 or 1) from its lowest frequencies: the usual inverse DCT with fewer
 * points than 8, and the 8-point normalisation (so 1x1 is the block's average). Colour planes stored at half
 * resolution decode at twice the brightness plane's scale, so they still come out at the full output resolution. */
typedef struct { float t[8][8]; } idct_tab;

static void make_tab(idct_tab* tab, int n)
{
    int x, u;
    for (x = 0; x < n; x++)
        for (u = 0; u < n; u++)
            tab->t[x][u] = (float)(0.5 * (u == 0 ? 0.70710678118654752 : 1.0) * cos((2 * x + 1) * u * 3.14159265358979323846 / (2 * n)));
}

static void idct_block(const int* coef, int maxU, int maxV, int nx, int ny, const idct_tab* tx, const idct_tab* ty,
                       unsigned char* out, int stride)
{
    float tmp[8][8];
    int x, y, u, v;
    if (maxU == 0 && maxV == 0)
    {
        int val = (int)floor(coef[0] * 0.125f + 128.5f);  /* only the average: a flat block */
        unsigned char c = (unsigned char)(val < 0 ? 0 : val > 255 ? 255 : val);
        for (y = 0; y < ny; y++) memset(out + y * stride, c, nx);
        return;
    }
    if (maxU >= nx) maxU = nx - 1;
    if (maxV >= ny) maxV = ny - 1;
    /* columns: tmp[y][u] = sum over v of ty[y][v] * coef[v][u] */
    for (u = 0; u <= maxU; u++)
        for (y = 0; y < ny; y++)
        {
            float s = 0;
            for (v = 0; v <= maxV; v++) s += ty->t[y][v] * (float)coef[v * 8 + u];
            tmp[y][u] = s;
        }
    /* rows */
    for (y = 0; y < ny; y++)
    {
        unsigned char* o = out + y * stride;
        for (x = 0; x < nx; x++)
        {
            float s = 128.5f;
            int val;
            for (u = 0; u <= maxU; u++) s += tx->t[x][u] * tmp[y][u];
            val = (int)floor(s);
            o[x] = (unsigned char)(val < 0 ? 0 : val > 255 ? 255 : val);
        }
    }
}

/* ------------------------------------------------------------------------------------------ parse */
static int u16(const unsigned char* p) { return (p[0] << 8) | p[1]; }

/* one block's coefficients, dequantised; reports the highest row / column holding a non-zero one */
static int decode_block(bits_t* b, jpeg_t* j, comp_t* c, int* coef, int* maxU, int* maxV)
{
    const huff_t* dc = &j->dc[c->td];
    const huff_t* ac = &j->ac[c->ta];
    const unsigned short* q = j->qt[c->tq];
    int s, k, mu = 0, mv = 0;
    memset(coef, 0, 64 * sizeof(int));
    s = decode_huff(b, dc);
    if (s < 0 || s > 11) return 0;
    c->dcPred += s ? extend(get_bits(b, s), s) : 0;
    coef[0] = c->dcPred * q[0];
    for (k = 1; k < 64; )
    {
        int rs = decode_huff(b, ac), r, z;
        if (rs < 0) return 0;
        r = rs >> 4; s = rs & 15;
        if (s == 0)
        {
            if (r != 15) break;                 /* end of block */
            k += 16; continue;
        }
        k += r;
        if (k > 63) return 0;
        z = kZigzag[k];
        coef[z] = extend(get_bits(b, s), s) * q[z];
        if ((z & 7) > mu) mu = z & 7;
        if ((z >> 3) > mv) mv = z >> 3;
        k++;
    }
    *maxU = mu; *maxV = mv;
    return 1;
}

static int read_headers(jpeg_t* j, const unsigned char* d, int len, const unsigned char** scan)
{
    int pos = 2, i;
    if (len < 4 || d[0] != 0xFF || d[1] != 0xD8) return 0;
    while (pos + 4 <= len)
    {
        int marker, seglen;
        const unsigned char* s;
        if (d[pos] != 0xFF) return 0;
        marker = d[pos + 1];
        if (marker == 0xFF) { pos++; continue; }               /* fill bytes */
        if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { pos += 2; continue; }
        seglen = u16(d + pos + 2);
        if (seglen < 2 || pos + 2 + seglen > len) return 0;
        s = d + pos + 4;
        switch (marker)
        {
        case 0xC0: case 0xC1:                                   /* baseline / extended, Huffman */
            if (s[0] != 8) return 0;                            /* 8-bit samples only */
            j->height = u16(s + 1); j->width = u16(s + 3); j->ncomp = s[5];
            if (j->width <= 0 || j->height <= 0 || (j->ncomp != 1 && j->ncomp != 3) || seglen < 8 + 3 * j->ncomp) return 0;
            j->hmax = j->vmax = 1;
            for (i = 0; i < j->ncomp; i++)
            {
                comp_t* c = &j->comp[i];
                c->id = s[6 + i * 3]; c->h = s[7 + i * 3] >> 4; c->v = s[7 + i * 3] & 15; c->tq = s[8 + i * 3];
                if (c->h < 1 || c->h > 2 || c->v < 1 || c->v > 2 || c->tq > 3) return 0;
                if (c->h > j->hmax) j->hmax = c->h;
                if (c->v > j->vmax) j->vmax = c->v;
            }
            break;
        case 0xC2: case 0xC3: case 0xC5: case 0xC6: case 0xC7: case 0xC9: case 0xCA: case 0xCB:
        case 0xCD: case 0xCE: case 0xCF:
            return 0;                                           /* progressive, lossless, arithmetic: not handled */
        case 0xC4:                                              /* Huffman tables */
        {
            const unsigned char* e = d + pos + 2 + seglen;
            while (s < e)
            {
                int tc = s[0] >> 4, th = s[0] & 15, n = 0;
                if (s + 17 > e || tc > 1 || th > 3) return 0;
                for (i = 0; i < 16; i++) n += s[1 + i];
                if (s + 17 + n > e) return 0;
                if (!build_huff(tc ? &j->ac[th] : &j->dc[th], s + 1, s + 17, n)) return 0;
                s += 17 + n;
            }
            break;
        }
        case 0xDB:                                              /* quantisation tables */
        {
            const unsigned char* e = d + pos + 2 + seglen;
            while (s < e)
            {
                int pq = s[0] >> 4, tq = s[0] & 15;
                if (tq > 3 || s + 1 + (pq ? 128 : 64) > e) return 0;
                for (i = 0; i < 64; i++) j->qt[tq][kZigzag[i]] = (unsigned short)(pq ? u16(s + 1 + i * 2) : s[1 + i]);
                j->qtPresent[tq] = 1;
                s += 1 + (pq ? 128 : 64);
            }
            break;
        }
        case 0xDD:                                              /* restart interval */
            if (seglen < 4) return 0;
            j->restart = u16(s);
            break;
        case 0xEE:                                              /* Adobe: says whether 3 components are RGB */
            if (seglen >= 14 && memcmp(s, "Adobe", 5) == 0) { j->adobe = 1; j->adobeTransform = s[11]; }
            break;
        case 0xDA:                                              /* start of scan */
        {
            int ns = s[0];
            if (!j->width || ns != j->ncomp || seglen < 6 + 2 * ns) return 0;      /* one scan with every component */
            for (i = 0; i < ns; i++)
            {
                int id = s[1 + i * 2], k;
                for (k = 0; k < j->ncomp; k++)
                    if (j->comp[k].id == id)
                    {
                        j->comp[k].td = s[2 + i * 2] >> 4; j->comp[k].ta = s[2 + i * 2] & 15;
                        if (j->comp[k].td > 3 || j->comp[k].ta > 3) return 0;
                        if (!j->dc[j->comp[k].td].present || !j->ac[j->comp[k].ta].present || !j->qtPresent[j->comp[k].tq]) return 0;
                        break;
                    }
                if (k == j->ncomp) return 0;
            }
            *scan = d + pos + 2 + seglen;
            return 1;
        }
        default:
            break;                                              /* APPn, COM...: skipped */
        }
        pos += 2 + seglen;
    }
    return 0;
}

/* Output position i -> a plane's samples, when the plane has num/den samples per output pixel: the two nearest
 * sample centres and the second's weight (0..256). num == den: the same position, weight 0. */
static void map_sample(int i, int num, int den, int size, int* m)
{
    int pos = (int)((((long long)(2 * i + 1) * num - den) * 256) / (2 * den));    /* centre, in 1/256 samples */
    int i0;
    if (num == den) { m[0] = i < size ? i : size - 1; m[1] = m[0]; m[2] = 0; return; }
    if (pos < 0) pos = 0;
    i0 = pos >> 8;
    if (i0 >= size - 1) { m[0] = m[1] = size - 1; m[2] = 0; return; }
    m[0] = i0; m[1] = i0 + 1; m[2] = pos & 255;
}

/* ----------------------------------------------------------------------------------------- decode */
SPQ_API int SPQ_CDECL spq_jpeg_decode(const unsigned char* data, int len, int needW, int needH,
                                      unsigned char** out, int* w, int* h, int* stride)
{
    jpeg_t* j;
    const unsigned char* scan = NULL;
    bits_t b;
    idct_tab tabs[9];               /* by size: 1, 2, 4, 8 */
    int n = 8, denom = 1, mcusX, mcusY, mx, my, ci, ok = 0, ow, oh, x, y, mcuCount = 0;
    int coef[64];
    int* xmap = NULL;               /* per component and output column: two plane samples and the second's weight */
    unsigned char* px = NULL;
    if (out) *out = NULL;
    if (!data || len < 4 || !out || !w || !h || !stride) return 0;
    j = (jpeg_t*)calloc(1, sizeof(jpeg_t));
    if (!j) return 0;
    if (!read_headers(j, data, len, &scan)) goto done;

    /* the scale: the smallest of 1/8, 1/4, 1/2 that still covers what is asked for */
    if (needW > 0 && needH > 0)
    {
        double f = (double)needW / j->width, fh = (double)needH / j->height;
        int d;
        if (fh > f) f = fh;
        for (d = 8; d > 1; d >>= 1)
        {
            int dw = (j->width + d - 1) / d, dh = (j->height + d - 1) / d;
            if (dw >= (int)ceil(j->width * f - 1e-9) && dh >= (int)ceil(j->height * f - 1e-9)) break;
        }
        denom = d;
    }
    n = 8 / denom;
    make_tab(&tabs[1], 1); make_tab(&tabs[2], 2); make_tab(&tabs[4], 4); make_tab(&tabs[8], 8);

    mcusX = (j->width + 8 * j->hmax - 1) / (8 * j->hmax);
    mcusY = (j->height + 8 * j->vmax - 1) / (8 * j->vmax);
    for (ci = 0; ci < j->ncomp; ci++)
    {
        comp_t* c = &j->comp[ci];
        c->bw = mcusX * c->h; c->bh = mcusY * c->v;
        c->nx = n * j->hmax / c->h; if (c->nx > 8) c->nx = 8;      /* half-resolution planes: twice the scale */
        c->ny = n * j->vmax / c->v; if (c->ny > 8) c->ny = 8;
        c->pw = c->bw * c->nx; c->ph = c->bh * c->ny;
        c->plane = (unsigned char*)malloc((size_t)c->pw * c->ph);
        if (!c->plane) goto done;
        c->dcPred = 0;
    }

    memset(&b, 0, sizeof(b));
    b.p = scan; b.end = data + len;
    for (my = 0; my < mcusY; my++)
        for (mx = 0; mx < mcusX; mx++)
        {
            if (j->restart && mcuCount > 0 && mcuCount % j->restart == 0)
            {
                /* restart marker: drop the bit buffer, find RSTn, reset the DC predictions */
                b.bits = 0; b.nbits = 0; b.hitMarker = 0;
                while (b.p + 1 < b.end && !(b.p[0] == 0xFF && b.p[1] >= 0xD0 && b.p[1] <= 0xD7)) b.p++;
                if (b.p + 1 >= b.end) goto done;
                b.p += 2;
                for (ci = 0; ci < j->ncomp; ci++) j->comp[ci].dcPred = 0;
            }
            for (ci = 0; ci < j->ncomp; ci++)
            {
                comp_t* c = &j->comp[ci];
                int bx, by;
                for (by = 0; by < c->v; by++)
                    for (bx = 0; bx < c->h; bx++)
                    {
                        int mu, mv, row = my * c->v + by, col = mx * c->h + bx;
                        if (!decode_block(&b, j, c, coef, &mu, &mv)) goto done;
                        idct_block(coef, mu, mv, c->nx, c->ny, &tabs[c->nx], &tabs[c->ny],
                                   c->plane + (size_t)row * c->ny * c->pw + col * c->nx, c->pw);
                    }
            }
            mcuCount++;
        }

    /* the output: colour converted. A plane at lower resolution than the output (colour, at full size) is
       stretched by blending its two nearest samples each way, as libjpeg does - not by repeating them. */
    ow = (j->width + denom - 1) / denom; oh = (j->height + denom - 1) / denom;
    px = (unsigned char*)malloc((size_t)ow * oh * 4);
    xmap = (int*)malloc(sizeof(int) * ow * 3 * 3);
    if (!px || !xmap) goto done;
    for (ci = 0; ci < j->ncomp; ci++)
    {
        comp_t* c = &j->comp[ci];
        int* m = xmap + ci * ow * 3;
        for (x = 0; x < ow; x++) map_sample(x, c->h * c->nx, j->hmax * n, c->pw, &m[x * 3]);
    }
    {
        int rgb = j->ncomp == 3 && j->adobe && j->adobeTransform == 0;     /* stored as RGB, not YCbCr */
        for (y = 0; y < oh; y++)
        {
            unsigned char* o = px + (size_t)y * ow * 4;
            const unsigned char* ra[3];
            const unsigned char* rb[3];
            int wy[3];
            for (ci = 0; ci < j->ncomp; ci++)
            {
                comp_t* c = &j->comp[ci];
                int ym[3];
                map_sample(y, c->v * c->ny, j->vmax * n, c->ph, ym);
                ra[ci] = c->plane + (size_t)ym[0] * c->pw;
                rb[ci] = c->plane + (size_t)ym[1] * c->pw;
                wy[ci] = ym[2];
            }
            for (x = 0; x < ow; x++)
            {
                int v[3];
                for (ci = 0; ci < j->ncomp; ci++)
                {
                    const int* m = xmap + ci * ow * 3 + x * 3;
                    if (m[2] == 0 && wy[ci] == 0) v[ci] = ra[ci][m[0]];
                    else
                    {
                        int top = ra[ci][m[0]] * (256 - m[2]) + ra[ci][m[1]] * m[2];
                        int bot = rb[ci][m[0]] * (256 - m[2]) + rb[ci][m[1]] * m[2];
                        v[ci] = (top * (256 - wy[ci]) + bot * wy[ci] + 32768) >> 16;
                    }
                }
                if (j->ncomp == 1) { o[0] = o[1] = o[2] = (unsigned char)v[0]; }
                else if (rgb) { o[0] = (unsigned char)v[2]; o[1] = (unsigned char)v[1]; o[2] = (unsigned char)v[0]; }
                else
                {
                    /* JFIF YCbCr -> RGB, 16.16 fixed point */
                    int R, G, B, Y = v[0], db = v[1] - 128, dr = v[2] - 128;
                    R = Y + ((91881 * dr + 32768) >> 16);
                    G = Y - ((22554 * db + 46802 * dr + 32768) >> 16);
                    B = Y + ((116130 * db + 32768) >> 16);
                    o[0] = (unsigned char)(B < 0 ? 0 : B > 255 ? 255 : B);
                    o[1] = (unsigned char)(G < 0 ? 0 : G > 255 ? 255 : G);
                    o[2] = (unsigned char)(R < 0 ? 0 : R > 255 ? 255 : R);
                }
                o[3] = 255;
                o += 4;
            }
        }
    }
    *out = px; px = NULL;
    *w = ow; *h = oh; *stride = ow * 4;
    ok = 1;

done:
    free(px);
    free(xmap);
    for (ci = 0; ci < 3; ci++) free(j->comp[ci].plane);
    free(j);
    return ok;
}
