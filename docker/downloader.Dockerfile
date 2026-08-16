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
# cifs/nfs helpers as above, plus a real browser for the Cloudflare challenge solver.
#
# The browser is the only way past a JS interstitial — it is what FlareSolverr is, and why Jackett
# deployments end up running one. Including it here avoids a second service. 1337x keeps one persistent,
# manually verifiable profile and uses that browser as its transport; other challenged scrapers can still
# use the lightweight one-shot clearance solver.
#
# Google Chrome rather than chromium, because this base is Ubuntu noble: apt has no real `chromium`
# package there (candidate: none), and `chromium-browser` is a stub that refuses to run and tells you to
# install the snap. Shipping that stub looked like success — the binary existed, so the solver believed it
# had a browser — while every solve would have failed. Debian-based dotnet runtime tags that do carry a
# genuine chromium (bookworm/trixie) are not published for .NET 10.
#
# INSTALL_BROWSER=false builds without it: the solver reports itself unavailable and blocked indexers are
# surfaced as blocked, which is the pre-existing behaviour rather than a regression.
ARG INSTALL_BROWSER=true
RUN apt-get update \
    && apt-get install -y --no-install-recommends cifs-utils nfs-common ca-certificates curl gnupg \
    && if [ "$INSTALL_BROWSER" = "true" ]; then \
         curl -fsSL https://dl.google.com/linux/linux_signing_key.pub \
           | gpg --dearmor -o /usr/share/keyrings/google-chrome.gpg \
         && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main" \
              > /etc/apt/sources.list.d/google-chrome.list \
         && apt-get update \
         && apt-get install -y --no-install-recommends google-chrome-stable fonts-liberation xvfb; \
       fi \
    && apt-get purge -y curl gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PlexRequests.Downloader.dll"]
