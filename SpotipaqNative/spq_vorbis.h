/* spq_vorbis.h - small Vorbis I decoder + Ogg demuxer for SpotipaqNative.
 * Plain C, float maths on the x87 FPU only (no SSE), so it runs on a Pentium II. */
#ifndef SPQ_VORBIS_H
#define SPQ_VORBIS_H
#include <stddef.h>
#include <stdint.h>

typedef struct vorbis_dec vorbis_dec;

vorbis_dec *vd_create(void);
void        vd_destroy(vorbis_dec *d);
/* Feed the three header packets in order (index 0,1,2). Returns 0 on success. */
int         vd_header(vorbis_dec *d, const uint8_t *pkt, int len, int index);
int         vd_channels(const vorbis_dec *d);
int         vd_rate(const vorbis_dec *d);
/* Forget the overlap state (after a seek). The next decoded packet produces no output. */
void        vd_reset(vorbis_dec *d);
/* Decode one audio packet. On success returns 0 and *out points at per-channel float arrays
 * (valid until the next call) holding *n samples each (n may be 0). Negative on error. */
int         vd_decode(vorbis_dec *d, const uint8_t *pkt, int len, float ***out, int *n);

/* ---- Ogg demuxer over a (growing) memory buffer ---- */
typedef struct {
    const uint8_t *data;   /* start of the Ogg stream */
    size_t avail;          /* bytes valid so far */
    size_t pos;            /* next page to read */
    int in_page, nsegs, seg_idx;
    uint8_t lacing[255];
    size_t body_off;
    int64_t granule;
    int hdr_type;
    uint8_t *pk; int pk_len, pk_cap; int pk_ready;
} ogg_reader;

void og_init(ogg_reader *o, const uint8_t *data);
void og_free(ogg_reader *o);
/* 1 = packet returned, 0 = need more data, -1 = lost sync. *gran is the page granule when this packet is
 * the last one finished on its page (else -1). */
int  og_next(ogg_reader *o, const uint8_t **pkt, int *len, int64_t *gran, int *eos);

#endif
