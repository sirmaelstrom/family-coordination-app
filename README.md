# Family Coordination App

A Blazor Server application for family meal planning, recipe management, and shopping list coordination.

## Building and Running

### Local Development

Run the application locally using the .NET SDK:

```bash
dotnet run --project src/FamilyCoordinationApp/FamilyCoordinationApp.csproj
```

The application will be available at `http://localhost:5000` (HTTP) or `https://localhost:5001` (HTTPS).

### Docker Build

Due to a known bug in .NET SDK 10.0.102 that prevents standard Docker builds, use the provided build script:

```bash
./docker-build.sh [tag]
```

Examples:
```bash
# Build with default 'latest' tag
./docker-build.sh

# Build with specific version tag
./docker-build.sh v1.0.0
```

For more details about the Docker build workaround, see [DOCKER-BUILD-WORKAROUND.md](DOCKER-BUILD-WORKAROUND.md).

### Running with Docker

After building the Docker image:

```bash
# Run standalone
docker run -d -p 8080:8080 --name familyapp familyapp:latest

# Or with Docker Compose
docker-compose up -d
```

## Project Structure

- `src/FamilyCoordinationApp/` - Main application code
- `.planning/` - Project planning documents, roadmaps, and phase plans
- `Dockerfile.runtime-only` - Docker build configuration (workaround for SDK bug)
- `docker-build.sh` - Automated Docker build script

## Technology Stack

- .NET 10.0
- Blazor Server
- PostgreSQL (via Npgsql)
- Entity Framework Core 10.0
- MudBlazor UI components
- Google OAuth authentication

## Development Status

This project is under active development. See `.planning/ROADMAP.md` for feature roadmap and `.planning/STATE.md` for current progress.

## License

Private/Personal Project
