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
