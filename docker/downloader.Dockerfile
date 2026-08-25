# PlexRequests downloader worker (headless). Build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PlexRequests.Downloader/PlexRequests.Downloader.csproj PlexRequests.Downloader/
COPY Shared/PlexRequests.Shared.csproj Shared/
RUN dotnet restore PlexRequests.Downloader/PlexRequests.Downloader.csproj

COPY PlexRequests.Downloader/ PlexRequests.Downloader/
COPY Shared/ Shared/
RUN dotnet publish PlexRequests.Downloader/PlexRequests.Downloader.csproj -c Release -o /app --no-restore

# Pinned Deno binary for yt-dlp's YouTube player challenge solver. Extract it in a disposable stage so
# neither unzip nor its package metadata is carried into the worker image.
FROM debian:bookworm-slim AS deno
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates unzip \
    && rm -rf /var/lib/apt/lists/*
ADD --checksum=sha256:8b010a3b1a4a0188a67cdb8a7a27348b2a501af78aec7fc74f2ace167368d530 \
    https://github.com/denoland/deno/releases/download/v2.9.5/deno-x86_64-unknown-linux-gnu.zip \
    /tmp/deno.zip
RUN unzip /tmp/deno.zip -d /out

# Worker Service -> runtime image (no ASP.NET). libc (for hardlink) is present in the base image.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# Pinned official yt-dlp nightly standalone. The checksum makes rebuilds deterministic and fails closed if
# the upstream asset ever changes. This binary carries its Python dependencies.
ADD --chmod=0755 --checksum=sha256:f0181635131bb01a120994bc0e18d80b3da81ca43fc368c2002179e9f8cdc3f2 \
    https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/download/2026.08.19.233000/yt-dlp_linux \
    /usr/local/bin/yt-dlp
COPY --from=deno --chmod=0755 /out/deno /usr/local/bin/deno
# cifs-utils / nfs-common: mount.cifs & mount.nfs helpers so the worker can mount admin-configured NAS
# shares (Admin > Library > Network Drives) read-write to place library files. Needs CAP_SYS_ADMIN at runtime
# (granted in docker-compose.yml); harmless if the feature is unused. The headless MediaInfo CLI proves
# strict audio/subtitle requirements from real streams before a file is allowed into any Plex root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends cifs-utils nfs-common ca-certificates mediainfo \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PlexRequests.Downloader.dll"]
