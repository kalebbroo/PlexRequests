# PlexRequests web app (Blazor Server). Build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the csproj files first for better layer caching (populates the NuGet cache).
COPY PlexRequestsHosted.csproj .
COPY Shared/PlexRequests.Shared.csproj Shared/
RUN dotnet restore PlexRequestsHosted.csproj

# Copy the rest and publish. The web csproj globs exclude the sibling Downloader project.
# NOTE: do NOT pass --no-restore here. With the csproj-only restore above, a --no-restore publish
# silently omits the Blazor framework static assets (wwwroot/_framework/blazor.web.js), which breaks
# all client interactivity (buttons do nothing). Letting publish restore with full source present
# regenerates them correctly; packages are already cached from the restore layer, so it stays fast.
COPY . .
RUN dotnet publish PlexRequestsHosted.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# app.db and DataProtection keys are expected on mounted volumes (see docker-compose.yml).
ENTRYPOINT ["dotnet", "PlexRequestsHosted.dll"]
