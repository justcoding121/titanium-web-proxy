FROM martenseemann/quic-network-simulator-endpoint:latest
COPY publish/ /endpoint/
COPY run_endpoint.sh /run_endpoint.sh
RUN sed -i 's/\r$//' /run_endpoint.sh && chmod +x /run_endpoint.sh /endpoint/QuicInteropHost
ENTRYPOINT ["/run_endpoint.sh"]
