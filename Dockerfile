# syntax=docker/dockerfile:1.7

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Map docker TARGETARCH to .NET RID.
RUN case "${TARGETARCH}" in \
        amd64) echo "linux-x64"  > /tmp/rid ;; \
        arm64) echo "linux-arm64"> /tmp/rid ;; \
        arm)   echo "linux-arm"  > /tmp/rid ;; \
        *)     echo "linux-x64"  > /tmp/rid ;; \
    esac

# Restore first for better layer caching.
COPY Everboot.sln ./
COPY src/Everboot/Everboot.csproj src/Everboot/
RUN dotnet restore Everboot.sln -r "$(cat /tmp/rid)"

# Copy the rest and publish a single-file, self-contained binary.
COPY . .
RUN RID="$(cat /tmp/rid)" && \
    dotnet publish src/Everboot/Everboot.csproj \
        -c Release \
        -r "${RID}" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none \
        -p:DebugSymbols=false \
        -o /app/publish

# ---------------------------------------------------------------------------
# Runtime stage — small, dependency-free image since we ship self-contained.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

RUN mkdir -p /var/log/everboot /data/isos /data/tftp

COPY --from=build /app/publish/ /app/

# Runs as root because TFTP (69/UDP) and DHCP proxy (67/UDP) need privileged
# ports for PXE clients to reach them. If you want non-root, run with
# `--cap-add=NET_BIND_SERVICE` and `USER everboot` instead.
USER root

ENV DOTNET_ENVIRONMENT=Production \
    EVERBOOT_LOG_DIR=/var/log/everboot \
    Everboot__DataDirectory=/data

# Boot payloads live here - mount your ISO library in at runtime.
VOLUME ["/data"]

# DHCP proxy (67/68 UDP), TFTP (69 UDP), HTTP file server (8080 TCP).
EXPOSE 67/udp 68/udp 69/udp 8080/tcp

ENTRYPOINT ["/app/Everboot"]
