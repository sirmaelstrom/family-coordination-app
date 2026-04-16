FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY src/FamilyCoordinationApp/FamilyCoordinationApp.csproj ./FamilyCoordinationApp/
RUN dotnet restore FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Copy source and build
COPY src/FamilyCoordinationApp/ ./FamilyCoordinationApp/
# Explicitly set working directory to project folder before publish
WORKDIR /src/FamilyCoordinationApp
RUN dotnet publish \
    -c Release \
    -o /app/publish
RUN echo "=== Checking publish output ===" && \
    ls -la /app/publish/wwwroot/ && \
    echo "=== Checking for _framework ===" && \
    ls -la /app/publish/wwwroot/_framework/ || echo "WARN: _framework directory not found"

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Create directories for logs, uploads, and data protection keys
RUN mkdir -p /app/logs /app/wwwroot/uploads /root/.aspnet/DataProtection-Keys

# Install yt-dlp standalone binary for YouTube recipe extraction.
# Pinned for reproducibility — bump the ARG to update. curl is installed
# temporarily and removed in the same layer to keep the image slim.
ARG YTDLP_VERSION=2025.03.31
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && curl -fsSL -o /usr/local/bin/yt-dlp \
       "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
    && chmod +x /usr/local/bin/yt-dlp \
    && apt-get purge -y --auto-remove curl \
    && rm -rf /var/lib/apt/lists/*

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
