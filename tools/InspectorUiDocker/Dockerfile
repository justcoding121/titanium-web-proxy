# Linux image for portable Inspector E2E-UI / Avalonia Headless.
# Local use from Windows (or any host with Docker) — CI still uses ubuntu-latest.
# Build: docker build -f tools/InspectorUiDocker/Dockerfile -t twp-inspector-ui .
FROM mcr.microsoft.com/dotnet/sdk:10.0

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        fontconfig \
        fonts-dejavu-core \
        libice6 \
        libsm6 \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
ENTRYPOINT ["/bin/bash"]
