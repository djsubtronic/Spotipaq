/*
 * spq_audio.c - audio playback for SpotipaqNative.
 *
 * Spotify delivers an AES-128-CTR encrypted Ogg Vorbis file. .NET downloads it in chunks and pushes the encrypted
 * bytes in with spq_audio_feed() (decrypted here); a decoder thread demuxes Ogg, decodes Vorbis (spq_vorbis.c,
 * x87 floats only - no SSE, this runs on a Pentium II) and plays through waveOut. One stream at a time.
 *
 * The decoder runs up to 1.5 s ahead into a ring of 16-bit samples; only about 0.1 s of that is handed to waveOut
 * at any time. Volume and fades are applied on the way from the ring to the device, so they are heard within
 * that 0.1 s: pause / resume fade over 333 ms, a stream starts with a 166 ms fade-in and a stopped one fades out
 * over 166 ms (on its own, after spq_audio_stop has already returned - the next track can be opening meanwhile).
 */
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0501
#include <windows.h>
#include <mmsystem.h>
#include <stdlib.h>
#include <string.h>
#include <openssl/aes.h>
#include "spq_vorbis.h"

#define SPQ_API __declspec(dllexport)

#define SPOTIFY_HEADER 0xa7            /* Spotify's own first bytes, skipped before the Ogg stream */
#define RING_FRAMES 65536              /* decoded audio waiting to be played (a power of two): 1.5 s at 44.1 kHz */
#define RING_MASK (RING_FRAMES - 1)
#define MAX_OUT 4096                   /* most frames one Vorbis packet can produce */
#define PACE_FRAMES 16384              /* with this much decoded, decode at the pace the device plays (no CPU bursts) */
#define NBUF 8
#define OUT_FRAMES 1024                /* one waveOut buffer: 23 ms */
#define QUEUE 6                        /* buffers kept queued in the device: about 0.14 s */
#define FADE_ONE (1 << 24)
#define FADE_PAUSE_MS 333
#define FADE_TRACK_MS 166

enum { ST_NONE = 0, ST_BUFFERING = 1, ST_PLAYING = 2, ST_PAUSED = 3, ST_ENDED = 4, ST_ERROR = -1 };

typedef struct { size_t off; int64_t gran; int hdr; } PageIdx;

typedef struct {
    uint8_t* buf; size_t total; volatile size_t filled;
    AES_KEY aes; uint8_t iv[16], ecount[16]; unsigned int num;

    HANDLE thread, ev_data, ev_cmd, ev_wave, ev_faded;
    volatile LONG stop, paused, seek_ms, buffering, ended, error, volume, start_ms, pos_base_ms;
    volatile LONG fade_to, fade_ms;          /* 1 = audible, 0 = silent; how long the way there takes */
    volatile LONG dying;                     /* stopped: fade out, then the reaper frees the session */

    HWAVEOUT hwo; WAVEHDR hdr[NBUF]; short* pcm[NBUF]; int busy[NBUF]; int queued;
    short* ring; int rr, rcount;             /* interleaved stereo; read index and frames waiting */
    int fade;                                /* current fade gain, 0 .. FADE_ONE */
    int vg;                                  /* current volume gain (Q15), gliding towards the set volume; -1 = not set yet */
    int eof;                                 /* the decoder has reached the end of the stream */
    int rate, channels;

    ogg_reader og; vorbis_dec* vd;
    PageIdx* pages; int npages, cappages; size_t scan_pos;
    int64_t discard;                         /* decoder output frames still to drop after a seek */
} Sess;

static CRITICAL_SECTION g_cs;
static volatile LONG g_cs_init = 0;
static Sess* g_sess = NULL;

static void lock_init(void)
{
    if (InterlockedCompareExchange(&g_cs_init, 1, 0) == 0) { InitializeCriticalSection(&g_cs); g_cs_init = 2; }
    else while (g_cs_init != 2) Sleep(1);
}

/* ------------------------------------------------------------------ helpers */
static size_t avail_bytes(Sess* s) { size_t f = s->filled; return f > SPOTIFY_HEADER ? f - SPOTIFY_HEADER : 0; }

static int interrupted(Sess* s) { return s->stop || s->dying || s->seek_ms >= 0; }

/* Waits a little for more data / a command. Returns 0 when we should abort (stop or seek request pending). */
static int wait_data(Sess* s)
{
    HANDLE h[2]; h[0] = s->ev_data; h[1] = s->ev_cmd;
    if (interrupted(s)) return 0;
    InterlockedExchange(&s->buffering, 1);
    WaitForMultipleObjects(2, h, FALSE, 100);
    return !interrupted(s);
}

static void scan_pages(Sess* s)
{
    const uint8_t* base = s->buf + SPOTIFY_HEADER;
    size_t avail = avail_bytes(s);
    for (;;) {
        const uint8_t* p; int nsegs, i; size_t total = 0;
        if (s->scan_pos + 27 > avail) return;
        p = base + s->scan_pos;
        if (memcmp(p, "OggS", 4) != 0) return;
        nsegs = p[26];
        if (s->scan_pos + 27 + nsegs > avail) return;
        for (i = 0; i < nsegs; i++) total += p[27 + i];
        if (s->scan_pos + 27 + nsegs + total > avail) return;
        if (s->npages == s->cappages) { s->cappages = s->cappages ? s->cappages * 2 : 512; s->pages = (PageIdx*)realloc(s->pages, sizeof(PageIdx) * s->cappages); }
        s->pages[s->npages].off = s->scan_pos;
        s->pages[s->npages].hdr = p[5];
        s->pages[s->npages].gran = (int64_t)((uint64_t)p[6] | ((uint64_t)p[7] << 8) | ((uint64_t)p[8] << 16) | ((uint64_t)p[9] << 24) |
            ((uint64_t)p[10] << 32) | ((uint64_t)p[11] << 40) | ((uint64_t)p[12] << 48) | ((uint64_t)p[13] << 56));
        s->npages++;
        s->scan_pos += 27 + nsegs + total;
    }
}

/* ------------------------------------------------------------------ ring + wave output */
static __inline short to_s16(float v)
{
    union { float f; int i; } u;
    u.f = v + 12582912.0f;                       /* round-to-nearest trick, no slow float->int conversion */
    { int r = u.i - 0x4B400000; return (short)(r > 32767 ? 32767 : r < -32768 ? -32768 : r); }
}

/* Appends frames [from, n) of the decoded block to the ring (the caller has made sure there is room). */
static void ring_write(Sess* s, float** pcm, int from, int n)
{
    const float* l = pcm[0], * r = s->channels > 1 ? pcm[1] : pcm[0];
    int w = (s->rr + s->rcount) & RING_MASK, i;
    for (i = from; i < n; i++) {
        short* o = s->ring + w * 2;
        o[0] = to_s16(l[i] * 32767.0f); o[1] = to_s16(r[i] * 32767.0f);
        w = (w + 1) & RING_MASK;
    }
    s->rcount += n - from;
}

static void reclaim(Sess* s)
{
    int i;
    for (i = 0; i < NBUF; i++) if (s->busy[i] && (s->hdr[i].dwFlags & WHDR_DONE)) { s->busy[i] = 0; s->queued--; }
}

/* Fully faded out (paused, or a stopped stream that has finished its fade): nothing more goes to the device. */
static int held(Sess* s) { return s->fade_to == 0 && s->fade == 0; }

/* Moves up to `take` frames from the ring into `out`, applying the volume and the fade. Returns how many it
 * produced: fewer than asked when a fade-out reaches silence, so the rest stays in the ring for the resume.
 * A volume change glides over about 30 ms instead of stepping (a step is a click). */
static int render(Sess* s, short* out, int take)
{
    int vol = (int)s->volume, vg = vol * vol * 32768 / 10000;       /* Q15, squared for a natural volume curve */
    int target = s->fade_to ? FADE_ONE : 0, done = 0;
    if (s->vg < 0) s->vg = vg;                                      /* first buffer: start at the set volume */
    while (done < take) {
        int seg = RING_FRAMES - s->rr, used = 0, k;
        const short* in = s->ring + s->rr * 2;
        short* o = out + done * 2;
        if (seg > take - done) seg = take - done;
        if (s->fade == target && s->vg == vg) {                     /* steady: the usual case */
            if (target == 0) break;
            if (vg == 32768) memcpy(o, in, (size_t)seg * 4);
            else for (k = 0; k < seg * 2; k++) o[k] = (short)((in[k] * vg) >> 15);
            used = seg;
        }
        else {
            int ms = (int)s->fade_ms, frames, step, f = s->fade, v = s->vg, vstep = 32768 * 34 / (s->rate > 0 ? s->rate : 44100) + 1;
            if (ms < 1) ms = 1;
            frames = (int)((int64_t)s->rate * ms / 1000); if (frames < 1) frames = 1;
            step = FADE_ONE / frames; if (step < 1) step = 1;
            while (used < seg && (f != target || v != vg)) {
                int g;
                if (f < target) { f += step; if (f > target) f = target; }
                else if (f > target) { f -= step; if (f < target) f = target; }
                if (v < vg) { v += vstep; if (v > vg) v = vg; }
                else if (v > vg) { v -= vstep; if (v < vg) v = vg; }
                g = (v * (f >> 9)) >> 15;                           /* Q15 */
                o[2 * used] = (short)((in[2 * used] * g) >> 15); o[2 * used + 1] = (short)((in[2 * used + 1] * g) >> 15);
                used++;
                if (f == 0 && target == 0) break;                   /* silent: stop here, whatever the volume is doing */
            }
            s->fade = f; s->vg = v;
            if (f == 0 && target == 0) s->vg = vg;
        }
        s->rr = (s->rr + used) & RING_MASK; s->rcount -= used; done += used;
    }
    return done;
}

/* Tops the device queue up from the ring. Returns 1 if it handed anything over. */
static int pump(Sess* s)
{
    int wrote = 0;
    if (!s->hwo) return 0;
    reclaim(s);
    while (s->queued < QUEUE && s->rcount > 0 && !held(s)) {
        int i, n, take = s->rcount < OUT_FRAMES ? s->rcount : OUT_FRAMES;
        if (take < OUT_FRAMES && !s->eof && s->fade_to) break;      /* wait for a full buffer, except at the end or while fading out */
        for (i = 0; i < NBUF; i++) if (!s->busy[i]) break;
        if (i == NBUF) break;
        n = render(s, s->pcm[i], take);
        if (n <= 0) break;
        s->hdr[i].dwBufferLength = (DWORD)n * 4;
        s->hdr[i].dwFlags &= ~WHDR_DONE;
        s->busy[i] = 1; s->queued++;
        waveOutWrite(s->hwo, &s->hdr[i], sizeof(WAVEHDR));
        wrote = 1;
    }
    return wrote;
}

/* ------------------------------------------------------------------ seeking */
static void set_reader(Sess* s, size_t off)
{
    s->og.pos = off; s->og.in_page = 0; s->og.pk_len = 0; s->og.pk_ready = 0;
}

static void do_seek(Sess* s, int ms)
{
    int64_t T = (int64_t)ms * s->rate / 1000, base = 0;
    int i, best, j;
    if (s->hwo) waveOutReset(s->hwo);
    for (i = 0; i < NBUF; i++) s->busy[i] = 0;
    s->queued = 0; s->rr = 0; s->rcount = 0; s->eof = 0;
    s->fade = 0;                                         /* comes back in with a fade instead of a click (or stays silent, if paused) */
    InterlockedExchange(&s->ended, 0);
    InterlockedExchange(&s->pos_base_ms, ms);
    for (;;) {
        int beyond = 0;
        scan_pages(s);
        best = -1;
        for (i = 0; i < s->npages; i++) {
            if (s->pages[i].gran < 0) continue;
            if (s->pages[i].gran <= T - 4096) best = i; else beyond = 1;
        }
        j = best + 1;
        while (j < s->npages && (s->pages[j].hdr & 1)) j++;
        if (beyond&& j < s->npages) break;
        if (s->filled >= s->total) {                     /* whole file here */
            if (j >= s->npages) { s->eof = 1; return; }      /* past the end: nothing to play, the main loop reports it */
            break;
        }
        if (!wait_data(s)) return;                       /* new command arrived; caller re-dispatches */
    }
    base = best >= 0 ? s->pages[best].gran : 0;
    set_reader(s, s->pages[j].off);
    vd_reset(s->vd);
    s->discard = T > base ? T - base : 0;
}

/* ------------------------------------------------------------------ decoder thread */
static int open_wave(Sess* s)
{
    WAVEFORMATEX wf; int i;
    memset(&wf, 0, sizeof wf);
    wf.wFormatTag = WAVE_FORMAT_PCM; wf.nChannels = 2; wf.nSamplesPerSec = s->rate; wf.wBitsPerSample = 16;
    wf.nBlockAlign = 4; wf.nAvgBytesPerSec = s->rate * 4;
    if (waveOutOpen(&s->hwo, WAVE_MAPPER, &wf, (DWORD_PTR)s->ev_wave, 0, CALLBACK_EVENT) != MMSYSERR_NOERROR) { s->hwo = NULL; return 0; }
    for (i = 0; i < NBUF; i++) {
        s->pcm[i] = (short*)calloc(OUT_FRAMES * 2, sizeof(short));
        memset(&s->hdr[i], 0, sizeof(WAVEHDR));
        s->hdr[i].lpData = (LPSTR)s->pcm[i]; s->hdr[i].dwBufferLength = OUT_FRAMES * 4;
        waveOutPrepareHeader(s->hwo, &s->hdr[i], sizeof(WAVEHDR));
        s->busy[i] = 0;
    }
    s->queued = 0;
    return 1;
}

static DWORD WINAPI dec_thread(LPVOID arg)
{
    Sess* s = (Sess*)arg;
    int idx = 0, errors = 0;
    HANDLE ev[3];
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

    og_init(&s->og, s->buf + SPOTIFY_HEADER);
    s->vd = vd_create();
    InterlockedExchange(&s->buffering, 1);

    /* the three header packets */
    while (idx < 3) {
        const uint8_t* p; int len, eos, r; int64_t g;
        if (s->stop || s->dying) goto done;
        s->og.avail = avail_bytes(s);
        r = og_next(&s->og, &p, &len, &g, &eos);
        if (r == 1) { if (vd_header(s->vd, p, len, idx) != 0) { InterlockedExchange(&s->error, 1); goto done; } idx++; }
        else if (r == 0) { if (s->filled >= s->total) { InterlockedExchange(&s->error, 1); goto done; } wait_data(s); }
        else { InterlockedExchange(&s->error, 1); goto done; }
    }
    s->rate = vd_rate(s->vd); s->channels = vd_channels(s->vd);
    if (!open_wave(s)) { InterlockedExchange(&s->error, 1); goto done; }
    s->scan_pos = s->og.pos;                             /* first audio page */
    if (s->start_ms > 0) { InterlockedExchange(&s->seek_ms, s->start_ms); SetEvent(s->ev_cmd); }
    InterlockedExchange(&s->buffering, 0);
    ev[0] = s->ev_wave; ev[1] = s->ev_cmd; ev[2] = s->ev_data;

    while (!s->stop) {
        int progressed, idle = 0;

        if (s->seek_ms >= 0 && !s->dying) {
            LONG ms = InterlockedExchange(&s->seek_ms, -1);
            do_seek(s, (int)ms);
            InterlockedExchange(&s->buffering, 0);
            errors = 0;
            continue;
        }

        progressed = pump(s);                            /* ring -> device, with volume and fades */
        if (!s->fade_to && s->fade && s->rcount == 0 && s->queued == 0) s->fade = 0;      /* paused with nothing left to fade: silent already */

        if (s->dying) {                                  /* stopped: once the fade-out has played, tell the reaper */
            if (s->queued == 0 && (held(s) || s->rcount == 0)) { SetEvent(s->ev_faded); idle = 1; }
        }
        else if (s->eof) {
            if (s->rcount == 0 && s->queued == 0) { if (!s->ended) InterlockedExchange(&s->ended, 1); idle = 1; }
        }
        else if (RING_FRAMES - s->rcount >= MAX_OUT) {   /* room for one more packet: decode -> ring */
            const uint8_t* p; int len, eos, r, n; int64_t g; float** pcm;
            s->og.avail = avail_bytes(s);
            r = og_next(&s->og, &p, &len, &g, &eos);
            if (r == 0) {
                if (s->filled >= s->total) s->eof = 1;                                /* whole file decoded */
                else if (s->rcount == 0 && s->queued == 0) InterlockedExchange(&s->buffering, 1);   /* run dry: waiting for the download */
            }
            else if (r < 0) s->eof = 1;
            else {
                InterlockedExchange(&s->buffering, 0);
                if (vd_decode(s->vd, p, len, &pcm, &n) != 0) { if (++errors > 200) { InterlockedExchange(&s->error, 1); break; } }
                else {
                    errors = 0;
                    if (n > 0) {
                        int skip = 0;
                        if (s->discard > 0) { skip = s->discard >= n ? n : (int)s->discard; s->discard -= skip; }
                        if (skip < n) ring_write(s, pcm, skip, n);
                    }
                    if (eos) s->eof = 1;
                }
                /* Far enough ahead: one packet per device buffer is plenty, so wait for the device instead of
                 * racing to fill the ring (that burst is what made the UI stutter when a track started). */
                if (s->rcount < PACE_FRAMES) progressed = 1;
            }
        }
        if (held(s) && !s->dying) idle = 1;              /* paused */

        if (!progressed) WaitForMultipleObjects(3, ev, FALSE, idle ? 200 : 40);
    }

done:
    if (s->ev_faded) SetEvent(s->ev_faded);
    return 0;
}

/* ------------------------------------------------------------------ session lifetime */
static void sess_destroy(Sess* s)
{
    int i;
    if (!s) return;
    InterlockedExchange(&s->stop, 1);
    if (s->ev_cmd) SetEvent(s->ev_cmd);
    if (s->ev_data) SetEvent(s->ev_data);
    if (s->ev_wave) SetEvent(s->ev_wave);
    if (s->thread) { WaitForSingleObject(s->thread, 5000); CloseHandle(s->thread); }
    if (s->hwo) {
        waveOutReset(s->hwo);
        for (i = 0; i < NBUF; i++) waveOutUnprepareHeader(s->hwo, &s->hdr[i], sizeof(WAVEHDR));
        waveOutClose(s->hwo);
    }
    for (i = 0; i < NBUF; i++) free(s->pcm[i]);
    if (s->vd) vd_destroy(s->vd);
    og_free(&s->og);
    free(s->pages); free(s->buf); free(s->ring);
    if (s->ev_faded) CloseHandle(s->ev_faded);
    if (s->ev_data) CloseHandle(s->ev_data);
    if (s->ev_cmd) CloseHandle(s->ev_cmd);
    if (s->ev_wave) CloseHandle(s->ev_wave);
    free(s);
}

/* Lets a stopped stream finish its fade-out (it tells us when it has, or we stop waiting), then frees it. */
static DWORD WINAPI reaper(LPVOID arg)
{
    Sess* s = (Sess*)arg;
    WaitForSingleObject(s->ev_faded, FADE_TRACK_MS + 450);
    sess_destroy(s);
    return 0;
}

/* Returns at once. What was playing fades out on its own; closing the device and freeing the stream also happen
 * off the caller's thread, so the UI never waits for the audio device. */
SPQ_API void __cdecl spq_audio_stop(void)
{
    Sess* s; HANDLE h;
    lock_init();
    EnterCriticalSection(&g_cs); s = g_sess; g_sess = NULL; LeaveCriticalSection(&g_cs);
    if (!s) return;
    if (!s->thread) { sess_destroy(s); return; }         /* never started: nothing to wait for */
    InterlockedExchange(&s->fade_ms, FADE_TRACK_MS);
    InterlockedExchange(&s->fade_to, 0);
    InterlockedExchange(&s->dying, 1);
    SetEvent(s->ev_cmd);
    h = CreateThread(NULL, 0, reaper, s, 0, NULL);
    if (h) CloseHandle(h); else sess_destroy(s);
}

/* key: 16-byte AES key. total: size of the whole file in bytes (a few MB). */
SPQ_API int __cdecl spq_audio_open(const unsigned char* key, int total)
{
    static const unsigned char IV[16] = { 0x72,0xe0,0x67,0xfb,0xdd,0xcb,0xcf,0x77,0xeb,0xe8,0xbc,0x64,0x3f,0x63,0x0d,0x93 };
    Sess* s;
    spq_audio_stop();
    if (total < SPOTIFY_HEADER + 64 || total > 256 * 1024 * 1024) return 0;
    s = (Sess*)calloc(1, sizeof(Sess));
    s->buf = (uint8_t*)malloc(total);
    if (!s->buf) { free(s); return 0; }
    s->total = (size_t)total;
    AES_set_encrypt_key(key, 128, &s->aes);
    memcpy(s->iv, IV, 16);
    s->ring = (short*)malloc(RING_FRAMES * 2 * sizeof(short));
    if (!s->ring) { free(s->buf); free(s); return 0; }
    s->volume = 100; s->seek_ms = -1; s->start_ms = 0;
    s->fade = 0; s->fade_to = 1; s->fade_ms = FADE_TRACK_MS;     /* a new track fades in */
    s->vg = -1;
    s->ev_faded = CreateEvent(NULL, FALSE, FALSE, NULL);
    s->ev_data = CreateEvent(NULL, FALSE, FALSE, NULL);
    s->ev_cmd = CreateEvent(NULL, FALSE, FALSE, NULL);
    s->ev_wave = CreateEvent(NULL, FALSE, FALSE, NULL);
    EnterCriticalSection(&g_cs); g_sess = s; LeaveCriticalSection(&g_cs);
    return 1;
}

/* Appends the next bytes of the (encrypted) file. Must be called in file order. Returns bytes accepted. */
SPQ_API int __cdecl spq_audio_feed(const unsigned char* data, int len)
{
    Sess* s; int n = 0;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s && len > 0) {
        size_t room = s->total - s->filled;
        n = (size_t)len > room ? (int)room : len;
        AES_ctr128_encrypt(data, s->buf + s->filled, (size_t)n, &s->aes, s->iv, s->ecount, &s->num);
        MemoryBarrier();
        s->filled += n;
        SetEvent(s->ev_data);
    }
    LeaveCriticalSection(&g_cs);
    return n;
}

/* Starts the decoder thread; playback begins as soon as enough data has arrived. */
SPQ_API int __cdecl spq_audio_play(int start_ms)
{
    Sess* s; int ok = 0;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s && !s->thread) {
        s->start_ms = start_ms > 0 ? start_ms : 0;
        s->thread = CreateThread(NULL, 0, dec_thread, s, 0, NULL);
        ok = s->thread != NULL;
    }
    LeaveCriticalSection(&g_cs);
    return ok;
}

SPQ_API void __cdecl spq_audio_pause(int paused)
{
    Sess* s;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) {                                             /* fades out, then stops feeding the device; and back */
        InterlockedExchange(&s->paused, paused ? 1 : 0);
        InterlockedExchange(&s->fade_ms, FADE_PAUSE_MS);
        InterlockedExchange(&s->fade_to, paused ? 0 : 1);
        SetEvent(s->ev_cmd);
    }
    LeaveCriticalSection(&g_cs);
}

SPQ_API int __cdecl spq_audio_seek(int ms)
{
    Sess* s; int ok = 0;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) { InterlockedExchange(&s->seek_ms, ms < 0 ? 0 : ms); SetEvent(s->ev_cmd); ok = 1; }
    LeaveCriticalSection(&g_cs);
    return ok;
}

SPQ_API void __cdecl spq_audio_volume(int v)
{
    Sess* s;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) InterlockedExchange(&s->volume, v < 0 ? 0 : v > 100 ? 100 : v);
    LeaveCriticalSection(&g_cs);
}

/* Current playback position in milliseconds. */
SPQ_API int __cdecl spq_audio_position(void)
{
    Sess* s; int ms = 0;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) {
        ms = s->pos_base_ms;
        if (s->hwo && s->rate > 0) {
            MMTIME t; memset(&t, 0, sizeof t); t.wType = TIME_SAMPLES;
            if (waveOutGetPosition(s->hwo, &t, sizeof t) == MMSYSERR_NOERROR) {
                double frames = 0;
                if (t.wType == TIME_SAMPLES) frames = (double)t.u.sample;
                else if (t.wType == TIME_BYTES) frames = (double)t.u.cb / 4.0;
                ms += (int)(frames * 1000.0 / s->rate);
            }
        }
    }
    LeaveCriticalSection(&g_cs);
    return ms;
}

SPQ_API int __cdecl spq_audio_state(void)
{
    Sess* s; int st = ST_NONE;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) {
        if (s->error) st = ST_ERROR;
        else if (s->ended) st = ST_ENDED;
        else if (s->buffering) st = ST_BUFFERING;
        else if (s->paused) st = ST_PAUSED;
        else st = ST_PLAYING;
    }
    LeaveCriticalSection(&g_cs);
    return st;
}

SPQ_API int __cdecl spq_audio_filled(void)
{
    Sess* s; int n = 0;
    lock_init();
    EnterCriticalSection(&g_cs);
    s = g_sess;
    if (s) n = (int)s->filled;
    LeaveCriticalSection(&g_cs);
    return n;
}
