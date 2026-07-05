#!/bin/bash
set -e

# Docker Build Script for Family Coordination App
# Workaround for .NET 10.0.102 SDK MSB3552 bug
#
# Usage: ./docker-build.sh [tag]
#   tag: Optional Docker image tag (default: latest)
#
# Example:
#   ./docker-build.sh           # Builds familyapp:latest
#   ./docker-build.sh v1.0.0    # Builds familyapp:v1.0.0

# Configuration
PROJECT_PATH="src/FamilyCoordinationApp/FamilyCoordinationApp.csproj"
PUBLISH_DIR="./publish-output"
DOCKERFILE="Dockerfile.runtime-only"
IMAGE_NAME="familyapp"
TAG="${1:-latest}"

echo "======================================"
echo "Family Coordination App - Docker Build"
echo "======================================"
echo ""

# Step 1: Clean previous publish output
echo "[1/4] Cleaning previous publish output..."
if [ -d "$PUBLISH_DIR" ]; then
    rm -rf "$PUBLISH_DIR"
    echo "✓ Cleaned $PUBLISH_DIR"
else
    echo "✓ No previous output to clean"
fi
echo ""

# Step 2: Build the SvelteKit SPA (de-Blazor WP-12: the app's entire UI).
# SvelteKit emits ./build/, which the CopyAppSpa MSBuild target copies into
# wwwroot/ (site root) during the publish step below. It MUST be built here,
# or the Copy target is skipped silently and the app serves no UI.
echo "[2/4] Building the SvelteKit SPA..."
build_frontend() {
    local dir="$1"
    local name="$2"
    if [ -d "$dir" ]; then
        echo "  → $name"
        pushd "$dir" > /dev/null
        if [ -f package-lock.json ]; then
            npm ci
        else
            npm install --no-audit --no-fund
        fi
        npm run build
        popd > /dev/null
        echo "  ✓ $name built"
    else
        echo "  ✗ $dir not found — the app has no UI without it"
        exit 1
    fi
}
build_frontend "./frontend/app" "spa-shell"
echo ""

# Step 3: Publish locally
echo "[3/4] Publishing application locally..."
echo "Command: dotnet publish $PROJECT_PATH -c Release -o $PUBLISH_DIR"
dotnet publish "$PROJECT_PATH" -c Release -o "$PUBLISH_DIR"

if [ $? -ne 0 ]; then
    echo "✗ Publish failed"
    exit 1
fi
echo "✓ Published to $PUBLISH_DIR"
echo ""

# Step 4: Build Docker image (sudo only where docker needs it — prod host yes, local Windows no)
DOCKER="docker"
if ! docker info >/dev/null 2>&1 && command -v sudo >/dev/null 2>&1; then
    DOCKER="sudo docker"
fi
echo "[4/4] Building Docker image..."
echo "Command: $DOCKER build -f $DOCKERFILE -t $IMAGE_NAME:$TAG ."
$DOCKER build -f "$DOCKERFILE" -t "$IMAGE_NAME:$TAG" .

if [ $? -ne 0 ]; then
    echo "✗ Docker build failed"
    exit 1
fi
echo "✓ Built $IMAGE_NAME:$TAG"
echo ""

# Cleanup publish output
echo "Cleaning up publish output..."
rm -rf "$PUBLISH_DIR"
echo "✓ Cleaned $PUBLISH_DIR"
echo ""

# Success
echo "======================================"
echo "✓ Build completed successfully!"
echo "======================================"
echo ""
echo "Image: $IMAGE_NAME:$TAG"
echo ""
echo "To run the container:"
echo "  docker run -d -p 8080:8080 --name familyapp $IMAGE_NAME:$TAG"
echo ""
echo "With Docker Compose:"
echo "  docker-compose up -d"
echo ""
