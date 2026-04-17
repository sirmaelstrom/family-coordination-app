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
ISLAND_DIR="./frontend/shopping-list"

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

# Step 2: Build Svelte shopping-list island.
# The MSBuild target CopyShoppingListIsland picks up the dist output during
# `dotnet publish` and copies it into wwwroot/islands/shopping-list/, which
# then lands in the publish output and gets baked into the runtime image.
echo "[2/4] Building shopping-list island..."
if [ -d "$ISLAND_DIR" ]; then
    pushd "$ISLAND_DIR" > /dev/null
    if [ -f package-lock.json ]; then
        npm ci
    else
        npm install --no-audit --no-fund
    fi
    npm run build
    popd > /dev/null
    echo "✓ Island built"
else
    echo "⚠ $ISLAND_DIR not found — skipping island build"
fi
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

# Step 4: Build Docker image
echo "[4/4] Building Docker image..."
echo "Command: docker build -f $DOCKERFILE -t $IMAGE_NAME:$TAG ."
sudo docker build -f "$DOCKERFILE" -t "$IMAGE_NAME:$TAG" .

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
