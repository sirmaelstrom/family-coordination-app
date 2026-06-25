# Stage 1: Build the Svelte shopping-list island.
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/shopping-list/.
FROM node:20-alpine AS node-build
WORKDIR /frontend
COPY frontend/shopping-list/package.json frontend/shopping-list/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/shopping-list/ ./
RUN npm run build

# Stage 1b: Build the Svelte chores island (parallel to node-build; MN7 — added,
# not modifying the shopping-list stage). Emits ./dist/ which the .NET stage
# copies into wwwroot/islands/chores/.
FROM node:20-alpine AS chores-node-build
WORKDIR /frontend
COPY frontend/chores/package.json frontend/chores/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/chores/ ./
RUN npm run build

# Stage 1c: Build the Svelte meal-plan island (strangler; parallel to the others).
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/meal-plan/.
FROM node:20-alpine AS mealplan-node-build
WORKDIR /frontend
COPY frontend/meal-plan/package.json frontend/meal-plan/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/meal-plan/ ./
RUN npm run build

# Stage 1d: Build the Svelte recipes island (strangler; parallel to the others).
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/recipes/.
FROM node:20-alpine AS recipes-node-build
WORKDIR /frontend
COPY frontend/recipes/package.json frontend/recipes/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/recipes/ ./
RUN npm run build

# Stage 1e: Build the Svelte dashboard island (strangler; parallel to the others).
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/dashboard/.
FROM node:20-alpine AS dashboard-node-build
WORKDIR /frontend
COPY frontend/dashboard/package.json frontend/dashboard/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/dashboard/ ./
RUN npm run build

# Stage 1f: Build the Svelte settings island (cluster A; parallel to the others).
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/settings/.
FROM node:20-alpine AS settings-node-build
WORKDIR /frontend
COPY frontend/settings/package.json frontend/settings/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/settings/ ./
RUN npm run build

# Stage 1g: Build the Svelte connections island (settings cluster B; parallel to the others).
# Emits ./dist/ which the .NET stage copies into wwwroot/islands/connections/.
FROM node:20-alpine AS connections-node-build
WORKDIR /frontend
COPY frontend/connections/package.json frontend/connections/package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci; else npm install --no-audit --no-fund; fi
COPY frontend/connections/ ./
RUN npm run build

# Stage 2: Build the .NET app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY src/FamilyCoordinationApp/FamilyCoordinationApp.csproj ./FamilyCoordinationApp/
RUN dotnet restore FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Copy source and island build output
COPY src/FamilyCoordinationApp/ ./FamilyCoordinationApp/
COPY --from=node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/shopping-list/
COPY --from=chores-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/chores/
COPY --from=mealplan-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/meal-plan/
COPY --from=recipes-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/recipes/
COPY --from=dashboard-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/dashboard/
COPY --from=settings-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/settings/
COPY --from=connections-node-build /frontend/dist/ ./FamilyCoordinationApp/wwwroot/islands/connections/

# Explicitly set working directory to project folder before publish
WORKDIR /src/FamilyCoordinationApp
RUN dotnet publish \
    -c Release \
    -o /app/publish
RUN echo "=== Checking publish output ===" && \
    ls -la /app/publish/wwwroot/ && \
    echo "=== Checking for _framework ===" && \
    ls -la /app/publish/wwwroot/_framework/ || echo "WARN: _framework directory not found" && \
    echo "=== Checking island ===" && \
    ls -la /app/publish/wwwroot/islands/shopping-list/ || echo "WARN: island not found"

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
