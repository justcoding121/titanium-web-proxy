# Titanium.Quic.UdpOffload

Socket batching for Titanium.Quic. This project is the only place that calls `sendmmsg` and `recvmmsg`. Equal-sized datagrams can carry a segmentation control message on that send. Cryptography stays out. `TITANIUM_UDP_OFFLOAD=0` forces the portable socket path. The batch calls are used only after a loopback probe accepts them.
