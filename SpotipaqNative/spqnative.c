/*
 * spqnative.c - SpotipaqNative.dll: the bits .NET 2.0 can't do on Windows XP.
 *
 *   spq_http    - HTTPS (TLS 1.2 via our static OpenSSL 1.0.2u) GET/POST, whole response in memory
 *   spq_modexp  - big-number modular exponentiation (Diffie-Hellman for the Spotify access point)
 *   spq_free    - frees memory returned by spq_http
 *
 * Build: build.cmd (VS2017 14.16, x86, /arch:IA32 /MT, subsystem 5.01) - same toolchain as NativePlayer.
 * Everything is cdecl and thread-safe (OpenSSL locking callbacks are installed on first use).
 */
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0501
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/bn.h>
#include <openssl/crypto.h>

#define SPQ_API __declspec(dllexport)

 /* ------------------------------------------------------------------------------------------ init */
static volatile LONG g_initState = 0;          /* 0 = not yet, 1 = in progress, 2 = done */
static CRITICAL_SECTION* g_locks = NULL;
static SSL_CTX* g_ctx = NULL;

static void locking_cb(int mode, int n, const char* file, int line)
{
    (void)file; (void)line;
    if (mode & CRYPTO_LOCK) EnterCriticalSection(&g_locks[n]);
    else LeaveCriticalSection(&g_locks[n]);
}

static unsigned long id_cb(void) { return (unsigned long)GetCurrentThreadId(); }

static int spq_init(void)
{
    if (g_initState == 2) return g_ctx != NULL;
    if (InterlockedCompareExchange(&g_initState, 1, 0) == 0)
    {
        WSADATA wsa;
        int i, n;
        WSAStartup(MAKEWORD(2, 2), &wsa);
        n = CRYPTO_num_locks();
        g_locks = (CRITICAL_SECTION*)malloc(sizeof(CRITICAL_SECTION) * n);
        for (i = 0; i < n; i++) InitializeCriticalSection(&g_locks[i]);
        CRYPTO_set_id_callback(id_cb);
        CRYPTO_set_locking_callback(locking_cb);
        SSL_library_init();
        SSL_load_error_strings();
        g_ctx = SSL_CTX_new(SSLv23_client_method());
        if (g_ctx)
        {
            SSL_CTX_set_options(g_ctx, SSL_OP_NO_SSLv2 | SSL_OP_NO_SSLv3 | SSL_OP_NO_COMPRESSION);
            SSL_CTX_set_verify(g_ctx, SSL_VERIFY_NONE, NULL);   /* TODO: pin/verify against a CA bundle */
            SSL_CTX_set_mode(g_ctx, SSL_MODE_AUTO_RETRY);
        }
        InterlockedExchange(&g_initState, 2);
    }
    else
    {
        while (g_initState != 2) Sleep(1);
    }
    return g_ctx != NULL;
}

/* -------------------------------------------------------------------------------------- helpers */
typedef struct { unsigned char* p; int len, cap; } buf_t;

static int buf_add(buf_t* b, const void* data, int n)
{
    if (b->len + n + 1 > b->cap)
    {
        int cap = b->cap ? b->cap : 16384;
        unsigned char* np;
        while (cap < b->len + n + 1) cap *= 2;
        np = (unsigned char*)realloc(b->p, cap);
        if (!np) return 0;
        b->p = np; b->cap = cap;
    }
    memcpy(b->p + b->len, data, n);
    b->len += n;
    b->p[b->len] = 0;
    return 1;
}

static void set_err(char* err, int errCap, const char* fmt, const char* arg)
{
    if (!err || errCap <= 0) return;
    _snprintf(err, errCap - 1, fmt, arg ? arg : "");
    err[errCap - 1] = 0;
}

static void ssl_err(char* err, int errCap, const char* what)
{
    char tmp[256];
    unsigned long e = ERR_get_error();
    if (e) ERR_error_string_n(e, tmp, sizeof(tmp)); else strcpy(tmp, "no detail");
    if (err && errCap > 0) { _snprintf(err, errCap - 1, "%s: %s", what, tmp); err[errCap - 1] = 0; }
}

/* case-insensitive search for a header line "name:" in the raw header block; returns pointer to value */
static const char* find_header(const char* headers, const char* name)
{
    size_t n = strlen(name);
    const char* p = headers;
    while (p && *p)
    {
        if (_strnicmp(p, name, n) == 0 && p[n] == ':')
        {
            p += n + 1;
            while (*p == ' ' || *p == '\t') p++;
            return p;
        }
        p = strstr(p, "\r\n");
        if (p) p += 2;
    }
    return NULL;
}

/* decode a chunked body in place; returns the new length or -1 */
static int dechunk(unsigned char* data, int len)
{
    int in = 0, out = 0;
    for (;;)
    {
        long size;
        char* end;
        if (in >= len) return -1;
        size = strtol((const char*)data + in, &end, 16);
        if ((unsigned char*)end == data + in) return -1;
        while (in < len && !(data[in] == '\r' && in + 1 < len && data[in + 1] == '\n')) in++;
        in += 2;
        if (size == 0) return out;
        if (in + size > len) return -1;
        memmove(data + out, data + in, size);
        out += size;
        in += size + 2;
    }
}

/* --------------------------------------------------------------------------------------- spq_http */
/*
 * method   "GET" / "POST" / ...
 * url      https://host[:port]/path?query   (http:// also accepted, without TLS)
 * headers  extra request headers, each "Name: value\r\n" (may be NULL)
 * body     request body (may be NULL), bodyLen bytes
 * status   out: HTTP status code
 * outBody  out: response body (malloc'd, NUL-terminated, free with spq_free), outLen its length
 * outHeaders out (optional): raw response header block (malloc'd), free with spq_free
 * err      out: error text when the function returns 0
 * returns 1 on success (any HTTP status), 0 on network/TLS failure
 */
static char g_timing[160];
static DWORD g_tick(void) { return GetTickCount(); }

/* Connect with a short per-address timeout (XP waits ~21 s per dead address otherwise). */
static int connect_timeout(SOCKET s, const struct sockaddr* a, int alen, int ms)
{
    u_long nb = 1, blk = 0;
    fd_set wf, ef;
    struct timeval tv;
    int r;
    ioctlsocket(s, FIONBIO, &nb);
    r = connect(s, a, alen);
    if (r != 0 && WSAGetLastError() != WSAEWOULDBLOCK) return -1;
    if (r != 0)
    {
        FD_ZERO(&wf); FD_ZERO(&ef); FD_SET(s, &wf); FD_SET(s, &ef);
        tv.tv_sec = ms / 1000; tv.tv_usec = (ms % 1000) * 1000;
        if (select(0, NULL, &wf, &ef, &tv) <= 0 || !FD_ISSET(s, &wf)) return -1;
    }
    ioctlsocket(s, FIONBIO, &blk);
    return 0;
}

SPQ_API void __cdecl spq_last_timing(char* buf, int cap)
{
    if (!buf || cap <= 0) return;
    _snprintf(buf, cap - 1, "%s", g_timing); buf[cap - 1] = 0;
}

SPQ_API int __cdecl spq_http(const char* method, const char* url, const char* headers,
    const unsigned char* body, int bodyLen,
    int* status, unsigned char** outBody, int* outLen, char** outHeaders,
    char* err, int errCap)
{
    char host[256], port[8], path[4096];
    const char* p, * hostStart, * hostEnd, * pathStart;
    int tls, ok = 0, n;
    struct addrinfo hints, * res = NULL, * ai;
    SOCKET s = INVALID_SOCKET;
    SSL* ssl = NULL;
    buf_t req = { 0 }, resp = { 0 };
    char line[512];
    DWORD timeout = 30000;
    int audioCdn = 0;
    DWORD t0 = g_tick(), tDns = 0, tConn = 0, tTls = 0;
    char ipstr[24] = "";

    if (status) *status = 0;
    if (outBody) *outBody = NULL;
    if (outLen) *outLen = 0;
    if (outHeaders) *outHeaders = NULL;
    if (!spq_init()) { set_err(err, errCap, "OpenSSL init failed%s", NULL); return 0; }

    /* ---- parse URL ---- */
    if (_strnicmp(url, "https://", 8) == 0) { tls = 1; hostStart = url + 8; strcpy(port, "443"); }
    else if (_strnicmp(url, "http://", 7) == 0) { tls = 0; hostStart = url + 7; strcpy(port, "80"); }
    else { set_err(err, errCap, "Bad URL: %s", url); return 0; }
    pathStart = strchr(hostStart, '/');
    if (!pathStart) pathStart = hostStart + strlen(hostStart);
    hostEnd = pathStart;
    for (p = hostStart; p < pathStart; p++)
        if (*p == ':') { hostEnd = p; _snprintf(port, sizeof(port) - 1, "%.*s", (int)(pathStart - p - 1), p + 1); port[sizeof(port) - 1] = 0; break; }
    if (hostEnd - hostStart >= (int)sizeof(host)) { set_err(err, errCap, "Host too long%s", NULL); return 0; }
    memcpy(host, hostStart, hostEnd - hostStart); host[hostEnd - hostStart] = 0;
    _snprintf(path, sizeof(path) - 1, "%s", *pathStart ? pathStart : "/"); path[sizeof(path) - 1] = 0;
    /* A stalled CDN range must not starve the decoder for 30 seconds.
       Keep the existing timeout for authentication, metadata and artwork. */
    if (strstr(path, "/audio/") != NULL && strstr(host, ".scdn.co") != NULL)
    {
        audioCdn = 1;
        timeout = 5000;
    }

    /* ---- connect ---- */
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    if (getaddrinfo(host, port, &hints, &res) != 0 || !res) { set_err(err, errCap, "Could not resolve %s", host); return 0; }
    tDns = g_tick();
    for (ai = res; ai; ai = ai->ai_next)
    {
        s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (s == INVALID_SOCKET) continue;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));
        setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char*)&timeout, sizeof(timeout));
        if (connect_timeout(s, ai->ai_addr, (int)ai->ai_addrlen, 5000) == 0)
        {
            _snprintf(ipstr, sizeof(ipstr) - 1, "%s", inet_ntoa(((struct sockaddr_in*)ai->ai_addr)->sin_addr));
            break;
        }
        closesocket(s); s = INVALID_SOCKET;
    }
    freeaddrinfo(res);
    if (s == INVALID_SOCKET) { set_err(err, errCap, "Could not connect to %s", host); return 0; }

    tConn = g_tick();
    if (tls)
    {
        ssl = SSL_new(g_ctx);
        if (!ssl) { ssl_err(err, errCap, "SSL_new"); goto done; }
        SSL_set_fd(ssl, (int)s);
        SSL_set_tlsext_host_name(ssl, host);
        if (SSL_connect(ssl) != 1) { ssl_err(err, errCap, "TLS handshake"); goto done; }
    }
    tTls = g_tick();

    /* ---- request ---- */
    _snprintf(line, sizeof(line) - 1, "%s ", method); line[sizeof(line) - 1] = 0;
    buf_add(&req, line, (int)strlen(line));
    buf_add(&req, path, (int)strlen(path));
    _snprintf(line, sizeof(line) - 1, " HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nUser-Agent: Spotipaq/0.1\r\n", host); line[sizeof(line) - 1] = 0;
    buf_add(&req, line, (int)strlen(line));
    if (headers && *headers) buf_add(&req, headers, (int)strlen(headers));
    if ((body && bodyLen > 0) || _stricmp(method, "POST") == 0 || _stricmp(method, "PUT") == 0)
    {
        _snprintf(line, sizeof(line) - 1, "Content-Length: %d\r\n", body ? bodyLen : 0); line[sizeof(line) - 1] = 0;
        buf_add(&req, line, (int)strlen(line));
    }
    buf_add(&req, "\r\n", 2);
    if (body && bodyLen > 0) buf_add(&req, body, bodyLen);

    {
        int sent = 0;
        while (sent < req.len)
        {
            n = tls ? SSL_write(ssl, req.p + sent, req.len - sent) : send(s, (const char*)req.p + sent, req.len - sent, 0);
            if (n <= 0) { ssl_err(err, errCap, "Send failed"); goto done; }
            sent += n;
        }
    }

    /* ---- response: read until the body is complete (Content-Length / chunked end) or the server closes ---- */
    {
        int hdrDone = 0, bodyStart0 = 0, wantLen = -1, isChunked = 0, noBody = 0;
        for (;;)
        {
            char chunk[16384];
            n = tls ? SSL_read(ssl, chunk, sizeof(chunk)) : recv(s, chunk, sizeof(chunk), 0);
            if (n <= 0)
            {
                if (audioCdn)
                {
                    int code = tls ? SSL_get_error(ssl, n) : 0;
                    _snprintf(line, sizeof(line) - 1, "CDN receive interrupted (TLS=%d, socket=%d, received=%d bytes)",
                        code, WSAGetLastError(), resp.len);
                    line[sizeof(line) - 1] = 0;
                    set_err(err, errCap, "%s", line);
                    goto done;
                }
                break;
            }
            if (!buf_add(&resp, chunk, n)) { set_err(err, errCap, "Out of memory%s", NULL); goto done; }
            if (!hdrDone)
            {
                char* he = strstr((char*)resp.p, "\r\n\r\n");
                if (he)
                {
                    int hl = (int)(he - (char*)resp.p) + 2, st = 0;
                    char saved = (char)resp.p[hl];
                    const char* te2, * cl2;
                    resp.p[hl] = 0;
                    sscanf((char*)resp.p, "HTTP/%*s %d", &st);
                    te2 = find_header((char*)resp.p, "Transfer-Encoding");
                    cl2 = find_header((char*)resp.p, "Content-Length");
                    isChunked = te2 && _strnicmp(te2, "chunked", 7) == 0;
                    if (!isChunked && cl2) wantLen = atoi(cl2);
                    noBody = (st == 204 || st == 304 || (st >= 100 && st < 200));
                    resp.p[hl] = (unsigned char)saved;
                    hdrDone = 1; bodyStart0 = hl + 2;
                }
            }
            if (hdrDone)
            {
                if (noBody) break;
                if (wantLen >= 0 && resp.len - bodyStart0 >= wantLen) break;      /* all of it is here: don't wait for the server to hang up */
                if (isChunked && resp.len - bodyStart0 >= 5 &&
                    memcmp(resp.p + resp.len - 5, "0\r\n\r\n", 5) == 0 &&
                    (resp.len - bodyStart0 == 5 || memcmp(resp.p + resp.len - 7, "\r\n0\r\n\r\n", 7) == 0)) break;
            }
        }
    }
    if (resp.len == 0) { ssl_err(err, errCap, "Empty response"); goto done; }

    {
        char* hdrEnd = strstr((char*)resp.p, "\r\n\r\n");
        int headLen, bodyStart, blen;
        const char* te, * cl;
        if (!hdrEnd) { set_err(err, errCap, "Malformed HTTP response%s", NULL); goto done; }
        headLen = (int)(hdrEnd - (char*)resp.p) + 2;
        bodyStart = headLen + 2;
        if (status) sscanf((char*)resp.p, "HTTP/%*s %d", status);
        resp.p[headLen] = 0;                          /* terminate the header block (overwrites the 2nd CRLF) */
        te = find_header((char*)resp.p, "Transfer-Encoding");
        cl = find_header((char*)resp.p, "Content-Length");
        blen = resp.len - bodyStart;
        if (te && _strnicmp(te, "chunked", 7) == 0)
        {
            blen = dechunk(resp.p + bodyStart, blen);
            if (blen < 0) { set_err(err, errCap, "Bad chunked encoding%s", NULL); goto done; }
        }
        else if (cl)
        {
            int want = atoi(cl);
            if (want >= 0 && want < blen) blen = want;
        }
        if (outHeaders)
        {
            *outHeaders = (char*)malloc(headLen + 1);
            if (*outHeaders) { memcpy(*outHeaders, resp.p, headLen); (*outHeaders)[headLen] = 0; }
        }
        if (outBody)
        {
            *outBody = (unsigned char*)malloc(blen + 1);
            if (!*outBody) { set_err(err, errCap, "Out of memory%s", NULL); goto done; }
            memcpy(*outBody, resp.p + bodyStart, blen);
            (*outBody)[blen] = 0;
        }
        if (outLen) *outLen = blen;
        ok = 1;
    }

done:
    _snprintf(g_timing, sizeof(g_timing) - 1, "ip=%s dns=%lums connect=%lums tls=%lums transfer=%lums", ipstr, tDns - t0, tConn - tDns, tTls - tConn, g_tick() - tTls);
    if (ssl) { SSL_shutdown(ssl); SSL_free(ssl); }
    if (s != INVALID_SOCKET) closesocket(s);
    free(req.p);
    free(resp.p);
    ERR_remove_state(0);
    return ok;
}

SPQ_API void __cdecl spq_free(void* p) { free(p); }

/* ------------------------------------------------------------------------------------- spq_modexp */
/* result = base^exp mod m, big-endian unsigned byte arrays. Returns result length, or -1. */
SPQ_API int __cdecl spq_modexp(const unsigned char* base, int baseLen, const unsigned char* exp, int expLen,
    const unsigned char* mod, int modLen, unsigned char* out, int outCap)
{
    BN_CTX* ctx;
    BIGNUM* b, * e, * m, * r;
    int len = -1;
    if (!spq_init()) return -1;
    ctx = BN_CTX_new();
    b = BN_bin2bn(base, baseLen, NULL);
    e = BN_bin2bn(exp, expLen, NULL);
    m = BN_bin2bn(mod, modLen, NULL);
    r = BN_new();
    if (ctx && b && e && m && r && BN_mod_exp(r, b, e, m, ctx))
    {
        len = BN_num_bytes(r);
        if (len <= outCap) BN_bn2bin(r, out); else len = -1;
    }
    BN_free(b); BN_free(e); BN_free(m); BN_free(r);
    if (ctx) BN_CTX_free(ctx);
    return len;
}

SPQ_API int __cdecl spq_version(void) { return 2; }
