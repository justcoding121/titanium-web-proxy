linuxmini, 2026-11-27. Ubuntu 26.04.1, AMD Ryzen 5 7640HS (6 cores, 12 threads), 12.9 GiB RAM, .NET SDK 10.0.404, runtime 10.0.15. BenchmarkDotNet 0.14.0 switched the CPU governor to performance for the timed job and restored powersave afterwards. One warmup, 10 iterations, one invocation, one launch.

The JSON files are the full exporter output. Medians are nanoseconds. A ratio is the platform median divided by the managed median. The Error column in the run is half the width of the 99.9% confidence interval of the mean.

Handshake medians are 1.558 ms platform and 4.384 ms managed, ratio 0.355, gate 0.90 missed. The small-exchange medians are 34.94 µs and 114.1 µs, ratio 0.306, gate 0.90 missed. The bulk medians are 155.2 µs and 13.11 ms for 64 KiB, ratio 0.012, recorded with no portable numeric gate. The stream window is 1 MiB and is not raised, so the repeated bulk cell is 64 KiB. At that run `Titanium.Quic.UdpOffload` did not exist, so the 0.80 Linux offload gate was not measured.

The same 64 KiB cell was timed again after the batch probe accepted `sendmmsg`. The exporter output for that run was cleaned on the machine. Platform median 124.8 µs, Error 22.79 µs. Managed median 12.613 ms, Error 5.563 ms. Ratio 0.010. The 0.80 gate is missed and is not lowered. The managed row allocated about 139 MiB. The governor was restored to powersave.
