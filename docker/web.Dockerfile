# PlexRequests web app (Blazor Server). Build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against just the csproj files first for better layer caching.
COPY PlexRequestsHosted.csproj .
COPY Shared/PlexRequests.Shared.csproj Shared/
RUN dotnet restore PlexRequestsHosted.csproj

# Copy the rest and publish. The web csproj globs exclude the sibling Downloader project.
COPY . .
RUN dotnet publish PlexRequestsHosted.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# app.db and DataProtection keys are expected on mounted volumes (see docker-compose.yml).
ENTRYPOINT ["dotnet", "PlexRequestsHosted.dll"]
