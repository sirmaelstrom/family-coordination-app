FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY src/FamilyCoordinationApp/FamilyCoordinationApp.csproj ./FamilyCoordinationApp/
RUN dotnet restore FamilyCoordinationApp/FamilyCoordinationApp.csproj

# Copy source and build
COPY src/FamilyCoordinationApp/ ./FamilyCoordinationApp/
RUN dotnet publish FamilyCoordinationApp/FamilyCoordinationApp.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Create directories for logs and uploads
RUN mkdir -p /app/logs /app/uploads

# Copy published app
COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "FamilyCoordinationApp.dll"]
