# PlexRequests downloader worker (headless). Build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PlexRequests.Downloader/PlexRequests.Downloader.csproj PlexRequests.Downloader/
COPY Shared/PlexRequests.Shared.csproj Shared/
RUN dotnet restore PlexRequests.Downloader/PlexRequests.Downloader.csproj

COPY PlexRequests.Downloader/ PlexRequests.Downloader/
COPY Shared/ Shared/
RUN dotnet publish PlexRequests.Downloader/PlexRequests.Downloader.csproj -c Release -o /app --no-restore

# Worker Service -> runtime image (no ASP.NET). libc (for hardlink) is present in the base image.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# cifs-utils / nfs-common: mount.cifs & mount.nfs helpers so the worker can mount admin-configured NAS
# shares (Admin > Library > Network Drives) read-write to place library files. Needs CAP_SYS_ADMIN at runtime
# (granted in docker-compose.yml); harmless if the feature is unused.
# cifs/nfs helpers as above, plus chromium for the Cloudflare challenge solver.
#
# The browser is the only way past a JS interstitial — it is what FlareSolverr is, and why Jackett
# deployments end up running one. Including it here avoids a second service. It is used ONCE per site per
# clearance lifetime to earn a cf_clearance cookie; every search after that is plain HTTP carrying the
# cookie, so the cost is a larger image rather than a slower search.
#
# INSTALL_BROWSER=false builds without it: the solver then reports itself unavailable and blocked indexers
# are surfaced as blocked, which is the pre-existing behaviour rather than a regression.
ARG INSTALL_BROWSER=true
RUN apt-get update \
    && apt-get install -y --no-install-recommends cifs-utils nfs-common \
    && if [ "$INSTALL_BROWSER" = "true" ]; then \
         apt-get install -y --no-install-recommends chromium fonts-liberation ca-certificates; \
       fi \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PlexRequests.Downloader.dll"]
