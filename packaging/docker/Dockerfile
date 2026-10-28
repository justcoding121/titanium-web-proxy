# syntax=docker/dockerfile:1.7
#
# Titanium Web Proxy CLI image (linux/amd64 and linux/arm64).
#
# Build from the repository root:
#   docker buildx build -f packaging/docker/Dockerfile -t titanium-web-proxy:dev .
#   docker buildx build --platform linux/amd64,linux/arm64 -f packaging/docker/Dockerfile .
#
# Run with a config mounted at /etc/titanium/twp.yaml (a directory mount, so Kubernetes ConfigMap updates are seen):
#   docker run --rm -p 8080:8080 -v ./conf:/etc/titanium:ro --read-only --tmpfs /tmp titanium-web-proxy:dev
#
# The runtime stage is a chiseled, distroless-style base: no shell, no package manager, non-root user (UID 1654).
# HTTP/3 needs libmsquic, which this image does not carry; use the release zip or a custom base for QUIC listeners.

ARG DOTNET_VERSION=10.0

# Build on the host architecture and cross-publish for the target one (no emulation of the .NET SDK).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
ARG COMMIT=unknown
WORKDIR /src

COPY global.json NuGet.config ./
COPY src/ src/
RUN case "${TARGETARCH}" in \
      amd64) RID=linux-x64 ;; \
      arm64) RID=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/Titanium.Cli/Titanium.Cli.csproj \
         -c Release -r "${RID}" --self-contained true \
         -p:Version="${VERSION}" -p:SourceRevisionId="${COMMIT}" \
         -p:DebugType=None -p:DebugSymbols=false \
         -o /out

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
ARG VERSION=0.0.0-dev
ARG COMMIT=unknown
LABEL org.opencontainers.image.title="Titanium Web Proxy" \
      org.opencontainers.image.description="Titanium Web Proxy CLI: reverse and forward proxy driven by a YAML config" \
      org.opencontainers.image.source="https://github.com/justcoding121/titanium-web-proxy" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}"

WORKDIR /app
COPY --from=build --chown=1654:1654 /out/ /app/

# Read-only root friendly: nothing is written under /app. Scratch files go to /tmp (mount an emptyDir or tmpfs).
# Diagnostics are off because the .NET diagnostic IPC socket would otherwise need a writable /tmp.
ENV PATH="/app:${PATH}" \
    HOME=/tmp \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

USER 1654:1654

# Proxy listeners are chosen by the config; 8080 is the chart's default. 9901 is the conventional admin port.
EXPOSE 8080 9901

# SIGTERM flips /readyz to 503, waits server.admin.preStopDelaySeconds, then drains in-flight requests.
STOPSIGNAL SIGTERM

# Exec form only: the image has no shell. The probe reads server.admin from the config and calls /healthz;
# it passes without probing when the admin listener is off or uses port 0.
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
    CMD ["titanium", "healthcheck", "--config", "/etc/titanium/twp.yaml"]

ENTRYPOINT ["titanium", "run", "--config", "/etc/titanium/twp.yaml"]
# Default argument: reload when the mounted config changes. Override it by passing other arguments to `docker run`.
CMD ["--watch"]
