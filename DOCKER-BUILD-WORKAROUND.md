# Docker Build Workaround for .NET 10.0.102 SDK Bug

## Problem

The .NET SDK 10.0.102 has a known bug (MSB3552) that causes Docker builds to fail with:
```
error MSB3552: Resource file "**/*.resx" cannot be found
```

This is caused by a glob pattern expansion bug in MSBuild when file paths exceed 260 characters in containerized environments.

## Root Cause

- .NET SDK 8.0 in Docker worked fine
- .NET SDK 10.0.100 and 10.0.102 both have this regression bug
- The bug prevents glob patterns (`**/*.cs`, `**/*.razor`, `**/*.resx`) from expanding correctly during `dotnet publish` in Docker
- Local builds with `dotnet build` work fine

## Workaround Solution

### Automated Build Script (Recommended)

Use the provided build script from the project root:

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

The script automatically:
1. Cleans previous publish output
2. Publishes the application locally
3. Builds the Docker image using Dockerfile.runtime-only
4. Cleans up temporary files

### Manual Build Process

If you prefer to run the steps manually:

1. **Publish locally** (outside Docker):
   ```bash
   dotnet publish src/FamilyCoordinationApp/FamilyCoordinationApp.csproj -c Release -o ./publish-output
   ```

2. **Build runtime-only Docker image**:
   ```bash
   docker build -f Dockerfile.runtime-only -t familyapp:latest .
   ```

3. **Clean up**:
   ```bash
   rm -rf ./publish-output
   ```

## Files

- `Dockerfile` - Original multi-stage build (currently broken due to SDK bug)
- `Dockerfile.runtime-only` - Runtime-only build that uses pre-published artifacts (working)

## When This Can Be Removed

This workaround can be removed once Microsoft releases a .NET SDK update (likely 10.0.3+) that fixes the MSB3552 glob pattern bug.

## References

- [GitHub Issue: dotnet/sdk#8239](https://github.com/dotnet/sdk/issues/8239)
- [MSBuild Issue: dotnet/msbuild#12546](https://github.com/dotnet/msbuild/issues/12546)
- [.NET Core Issue: dotnet/core#10204](https://github.com/dotnet/core/issues/10204)

## Timeline

- 2026-01-22: Project upgraded from .NET SDK 8.0 to 10.0 in Docker (commit 4d28b24)
- 2026-01-24: Bug discovered when Docker cache invalidated by bugfix commits
- 2026-01-24: Workaround implemented using runtime-only Dockerfile
