/*
 * spq_audio.c - audio playback for SpotipaqNative.
 *
 * Spotify delivers an AES-128-CTR encrypted Ogg Vorbis file. .NET downloads it in chunks and pushes the encrypted
 * bytes in with spq_audio_feed() (decrypted here); a decoder thread demuxes Ogg, decodes Vorbis (spq_vorbis.c,
 * x87 floats only - no SSE, this runs on a Pentium II) and plays through waveOut. One stream at a time.
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
#define NBUF 32
#define BUF_FRAMES 2048

enum { ST_NONE = 0, ST_BUFFERING = 1, ST_PLAYING = 2, ST_PAUSED = 3, ST_ENDED = 4, ST_ERROR = -1 };

typedef struct { size_t off; int64_t gran; int hdr; } PageIdx;

typedef struct {
    uint8_t* buf; size_t total; volatile size_t filled;
    AES_KEY aes; uint8_t iv[16], ecount[16]; unsigned int num;

    HANDLE thread, ev_data, ev_cmd, ev_wave;
    volatile LONG stop, paused, seek_ms, buffering, ended, error, volume, start_ms, pos_base_ms;

    HWAVEOUT hwo; WAVEHDR hdr[NBUF]; short* pcm[NBUF]; int busy[NBUF]; int next;
    int cur, cur_fill;                       /* buffer being filled, frames in it */
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

static int interrupted(Sess* s) { return s->stop || s->seek_ms >= 0; }

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

/* ------------------------------------------------------------------ wave output */
static int acquire(Sess* s)                      /* free buffer index, or -1 when interrupted */
{
    for (;;) {
        int i = s->next;
        if (!s->busy[i] || (s->hdr[i].dwFlags & WHDR_DONE)) { s->busy[i] = 0; s->next = (i + 1) % NBUF; return i; }
        WaitForSingleObject(s->ev_wave, 50);
        if (interrupted(s)) return -1;
    }
}

static void submit(Sess* s)
{
    int i = s->cur;
    if (i < 0) return;
    if (s->cur_fill < BUF_FRAMES) memset(s->pcm[i] + s->cur_fill * 2, 0, (BUF_FRAMES - s->cur_fill) * 4);
    s->hdr[i].dwBufferLength = BUF_FRAMES * 4;
    s->hdr[i].dwFlags &= ~WHDR_DONE;
    s->busy[i] = 1;
    waveOutWrite(s->hwo, &s->hdr[i], sizeof(WAVEHDR));
    s->cur = -1; s->cur_fill = 0;
}

static __inline short to_s16(float v)
{
    union { float f; int i; } u;
    u.f = v + 12582912.0f;                       /* round-to-nearest trick, no slow float->int conversion */
    { int r = u.i - 0x4B400000; return (short)(r > 32767 ? 32767 : r < -32768 ? -32768 : r); }
}

/* Writes frames [from, n) of the decoded block into wave buffers. Returns 0 if interrupted. */
static int emit(Sess* s, float** pcm, int from, int n)
{
    float gain = (float)s->volume / 100.0f; float scale;
    int i;
    gain = gain * gain; scale = 32767.0f * gain; (void)scale;
    for (i = from; i < n; ) {
        int room, take, k;
        if (s->cur < 0) { s->cur = acquire(s); s->cur_fill = 0; if (s->cur < 0) return 0; }
        room = BUF_FRAMES - s->cur_fill; take = n - i < room ? n - i : room;
        {
            short* o = s->pcm[s->cur] + s->cur_fill * 2;
            const float* l = pcm[0] + i, * r = s->channels > 1 ? pcm[1] + i : pcm[0] + i;
            for (k = 0; k < take; k++) { o[2 * k] = to_s16(l[k] * gain * 32767.0f); o[2 * k + 1] = to_s16(r[k] * gain * 32767.0f); }
        }
        s->cur_fill += take; i += take;
        if (s->cur_fill >= BUF_FRAMES) submit(s);
    }
    return 1;
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
    s->cur = -1; s->cur_fill = 0; s->next = 0;
    InterlockedExchange(&s->ended, 0);
    InterlockedExchange(&s->pos_base_ms, ms);
    if (s->paused) { /* stay paused: waveOutReset leaves the device paused */ }
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
            if (j >= s->npages) { InterlockedExchange(&s->ended, 1); return; }
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
static void finish_stream(Sess* s)
{
    int i;
    if (s->cur >= 0 && s->cur_fill > 0) submit(s);
    s->cur = -1;
    for (;;) {
        int any = 0;
        for (i = 0; i < NBUF; i++) if (s->busy[i] && !(s->hdr[i].dwFlags & WHDR_DONE)) any = 1;
        if (!any) break;
        WaitForSingleObject(s->ev_wave, 50);
        if (interrupted(s)) return;
    }
    InterlockedExchange(&s->ended, 1);
}

static int open_wave(Sess* s)
{
    WAVEFORMATEX wf; int i;
    memset(&wf, 0, sizeof wf);
    wf.wFormatTag = WAVE_FORMAT_PCM; wf.nChannels = 2; wf.nSamplesPerSec = s->rate; wf.wBitsPerSample = 16;
    wf.nBlockAlign = 4; wf.nAvgBytesPerSec = s->rate * 4;
    if (waveOutOpen(&s->hwo, WAVE_MAPPER, &wf, (DWORD_PTR)s->ev_wave, 0, CALLBACK_EVENT) != MMSYSERR_NOERROR) { s->hwo = NULL; return 0; }
    for (i = 0; i < NBUF; i++) {
        s->pcm[i] = (short*)calloc(BUF_FRAMES * 2, sizeof(short));
        memset(&s->hdr[i], 0, sizeof(WAVEHDR));
        s->hdr[i].lpData = (LPSTR)s->pcm[i]; s->hdr[i].dwBufferLength = BUF_FRAMES * 4;
        waveOutPrepareHeader(s->hwo, &s->hdr[i], sizeof(WAVEHDR));
        s->busy[i] = 0;
    }
    if (s->paused) waveOutPause(s->hwo);
    return 1;
}

static DWORD WINAPI dec_thread(LPVOID arg)
{
    Sess* s = (Sess*)arg;
    int idx = 0, errors = 0;
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

    og_init(&s->og, s->buf + SPOTIFY_HEADER);
    s->vd = vd_create();
    s->cur = -1;
    InterlockedExchange(&s->buffering, 1);

    /* the three header packets */
    while (idx < 3) {
        const uint8_t* p; int len, eos, r; int64_t g;
        if (s->stop) goto done;
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

    while (!s->stop) {
        const uint8_t* p; int len, eos, r; int64_t g;
        float** pcm; int n;

        if (s->seek_ms >= 0) {
            LONG ms = InterlockedExchange(&s->seek_ms, -1);
            do_seek(s, (int)ms);
            InterlockedExchange(&s->buffering, 0);
            errors = 0;
            continue;
        }
        if (s->ended) { WaitForSingleObject(s->ev_cmd, 100); continue; }

        s->og.avail = avail_bytes(s);
        r = og_next(&s->og, &p, &len, &g, &eos);
        if (r == 0) {
            if (s->filled >= s->total) { finish_stream(s); continue; }
            wait_data(s);
            continue;
        }
        InterlockedExchange(&s->buffering, 0);
        if (r < 0) { finish_stream(s); continue; }
        if (vd_decode(s->vd, p, len, &pcm, &n) != 0) { if (++errors > 200) { InterlockedExchange(&s->error, 1); break; } continue; }
        errors = 0;
        if (n > 0) {
            int skip = 0;
            if (s->discard > 0) { skip = s->discard >= n ? n : (int)s->discard; s->discard -= skip; }
            if (skip < n && !emit(s, pcm, skip, n)) continue;      /* interrupted: loop handles seek/stop */
        }
        if (eos) finish_stream(s);
    }

done:
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
    free(s->pages); free(s->buf);
    if (s->ev_data) CloseHandle(s->ev_data);
    if (s->ev_cmd) CloseHandle(s->ev_cmd);
    if (s->ev_wave) CloseHandle(s->ev_wave);
    free(s);
}

SPQ_API void __cdecl spq_audio_stop(void)
{
    Sess* s;
    lock_init();
    EnterCriticalSection(&g_cs); s = g_sess; g_sess = NULL; LeaveCriticalSection(&g_cs);
    sess_destroy(s);
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
    s->volume = 100; s->seek_ms = -1; s->start_ms = 0;
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
    if (s) {
        InterlockedExchange(&s->paused, paused ? 1 : 0);
        if (s->hwo) { if (paused) waveOutPause(s->hwo); else waveOutRestart(s->hwo); }
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
