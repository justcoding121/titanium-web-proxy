This PC, 2026-11-27. Windows 11 (10.0.26300), AMD Ryzen 5 7640HS (6 cores, 12 threads), 13.8 GiB RAM, .NET SDK 10.0.404, runtime 10.0.15. BenchmarkDotNet 0.14.0 switched the power plan to High performance for the timed job and restored Balanced afterwards. One warmup, 10 iterations, one launch.

The JSON files are the full exporter output. `tools/tls/compare-perf-baseline.ps1` prints median ratios against a fresh export. Medians are nanoseconds.

The loopback bulk file is the run after the in-memory pipe stopped copying one byte at a time. Handshake, resumed, and small-exchange files are the earlier run on the same tree. The X25519 ceiling test stayed out of the suite. macmini and linuxmini baselines are not in this folder yet.
