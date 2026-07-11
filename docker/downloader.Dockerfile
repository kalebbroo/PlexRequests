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
COPY --from=build /app .
ENTRYPOINT ["dotnet", "PlexRequests.Downloader.dll"]
