/* Clock, CSPRNG, and mutex for the Titanium Lua shim. */
#include "twp_internal.h"

#include <string.h>
#include <time.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

int64_t twp_mono_us(void)
{
    static LARGE_INTEGER freq;
    LARGE_INTEGER now;
    if (freq.QuadPart == 0) {
        QueryPerformanceFrequency(&freq);
    }
    QueryPerformanceCounter(&now);
    return (int64_t)((now.QuadPart / freq.QuadPart) * 1000000 + (now.QuadPart % freq.QuadPart) * 1000000 / freq.QuadPart);
}

int64_t twp_unix_ms(void)
{
    FILETIME ft;
    ULARGE_INTEGER t;
    GetSystemTimeAsFileTime(&ft);
    t.LowPart = ft.dwLowDateTime;
    t.HighPart = ft.dwHighDateTime;
    return (int64_t)((t.QuadPart - 116444736000000000ULL) / 10000ULL);
}

typedef LONG(WINAPI *twp_bcrypt_gen_random)(void *, unsigned char *, ULONG, ULONG);

int twp_random_bytes(void *buf, size_t n)
{
    /* Loaded on first use so neither MSVC nor MinGW needs an extra import library. */
    static twp_bcrypt_gen_random gen;
    if (gen == NULL) {
        HMODULE mod = LoadLibraryA("bcrypt.dll");
        if (mod == NULL) {
            return -1;
        }
        gen = (twp_bcrypt_gen_random)(void *)GetProcAddress(mod, "BCryptGenRandom");
        if (gen == NULL) {
            return -1;
        }
    }
    /* BCRYPT_USE_SYSTEM_PREFERRED_RNG */
    return gen(NULL, (unsigned char *)buf, (ULONG)n, 0x00000002) == 0 ? 0 : -1;
}

void twp_mutex_init(twp_mutex *m) { InitializeSRWLock((PSRWLOCK)&m->srw); }
void twp_mutex_lock(twp_mutex *m) { AcquireSRWLockExclusive((PSRWLOCK)&m->srw); }
void twp_mutex_unlock(twp_mutex *m) { ReleaseSRWLockExclusive((PSRWLOCK)&m->srw); }

static SRWLOCK twp_global = SRWLOCK_INIT;
void twp_global_lock(void) { AcquireSRWLockExclusive(&twp_global); }
void twp_global_unlock(void) { ReleaseSRWLockExclusive(&twp_global); }

#else
#include <fcntl.h>
#include <pthread.h>
#include <unistd.h>
#include <errno.h>

int64_t twp_mono_us(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000 + ts.tv_nsec / 1000;
}

int64_t twp_unix_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
}

int twp_random_bytes(void *buf, size_t n)
{
    unsigned char *p = (unsigned char *)buf;
    int fd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        return -1;
    }
    while (n > 0) {
        ssize_t r = read(fd, p, n);
        if (r < 0 && errno == EINTR) {
            continue;
        }
        if (r <= 0) {
            close(fd);
            return -1;
        }
        p += r;
        n -= (size_t)r;
    }
    close(fd);
    return 0;
}

void twp_mutex_init(twp_mutex *m) { pthread_mutex_init(&m->mu, NULL); }
void twp_mutex_lock(twp_mutex *m) { pthread_mutex_lock(&m->mu); }
void twp_mutex_unlock(twp_mutex *m) { pthread_mutex_unlock(&m->mu); }

static pthread_mutex_t twp_global = PTHREAD_MUTEX_INITIALIZER;
void twp_global_lock(void) { pthread_mutex_lock(&twp_global); }
void twp_global_unlock(void) { pthread_mutex_unlock(&twp_global); }
#endif
