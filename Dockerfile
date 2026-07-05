# Stage 1: Build the SvelteKit SPA shell (de-Blazor WP-12: the app's entire UI).
# A full SvelteKit app (adapter-static) that emits ./build/ (SPA fallback index.html +
# fingerprinted _app/ assets + static/ manifest + service worker), which the .NET stage
# copies into wwwroot/ so ASP.NET serves it at the site root.
FROM node:20-alpine AS app-spa-node-build
WORKDIR /frontend
COPY frontend/app/package.json frontend/app/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/app/ ./
RUN npm run build

# Stage 2: Build the .NET app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY src/FamilyCoordinationApp/FamilyCoordinationApp.csproj ./FamilyCoordinationApp/
RUN dotnet restore FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Copy source and the SPA build output (served at root)
COPY src/FamilyCoordinationApp/ ./FamilyCoordinationApp/
COPY --from=app-spa-node-build /frontend/build/ ./FamilyCoordinationApp/wwwroot/

# Explicitly set working directory to project folder before publish
WORKDIR /src/FamilyCoordinationApp
RUN dotnet publish \
    -c Release \
    -o /app/publish
RUN echo "=== Checking publish output ===" && \
    ls -la /app/publish/wwwroot/ && \
    echo "=== Checking for the SPA shell ===" && \
    ls -la /app/publish/wwwroot/_app/ >/dev/null && test -f /app/publish/wwwroot/index.html \
    || (echo "ERROR: SPA build output missing from wwwroot" && exit 1)

# yt-dlp download stage. Uses alpine so we avoid the Debian base image's
# apt sources, which have been flaky on the trixie transition (libcurl4t64
# unmet deps). Alpine's apk is reliable and we just need the binary.
FROM alpine:3.20 AS ytdlp-downloader
RUN apk add --no-cache curl ca-certificates \
    && curl -fsSL -o /yt-dlp \
       "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux" \
    && chmod +x /yt-dlp

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Create directories for logs, uploads, and data protection keys
RUN mkdir -p /app/logs /app/wwwroot/uploads /root/.aspnet/DataProtection-Keys

# Pull in yt-dlp from the alpine downloader. Tracks latest release —
# YouTube backend changes frequently break older versions, so pinning
# creates silent decay. Rebuild the image to pick up fixes.
COPY --from=ytdlp-downloader /yt-dlp /usr/local/bin/yt-dlp

# Copy published app
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose port
EXPOSE 8080

# Health check — uses /dev/tcp to avoid curl/wget dependency in slim images
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD bash -c '</dev/tcp/localhost/8080' || exit 1

ENTRYPOINT ["dotnet", "FamilyCoordinationApp.dll"]
