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
echo "[1/3] Cleaning previous publish output..."
if [ -d "$PUBLISH_DIR" ]; then
    rm -rf "$PUBLISH_DIR"
    echo "✓ Cleaned $PUBLISH_DIR"
else
    echo "✓ No previous output to clean"
fi
echo ""

# Step 2: Publish locally
echo "[2/3] Publishing application locally..."
echo "Command: dotnet publish $PROJECT_PATH -c Release -o $PUBLISH_DIR"
dotnet publish "$PROJECT_PATH" -c Release -o "$PUBLISH_DIR"

if [ $? -ne 0 ]; then
    echo "✗ Publish failed"
    exit 1
fi
echo "✓ Published to $PUBLISH_DIR"
echo ""

# Step 3: Build Docker image
echo "[3/3] Building Docker image..."
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
