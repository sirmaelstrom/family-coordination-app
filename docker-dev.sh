#!/bin/bash
set -e

# Docker Dev Script for Family Coordination App
# Handles local development environment setup and common issues
#
# Usage: ./docker-dev.sh [command]
#   up      - Start local dev environment (default)
#   down    - Stop and remove containers
#   reset   - Nuclear option: wipe volumes and data, start fresh
#   logs    - Follow app logs
#   status  - Show container status
#
# Example:
#   ./docker-dev.sh           # Start dev environment
#   ./docker-dev.sh reset     # Fresh start (wipes local DB!)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

ENV_FILE=".env.local"
DATA_DIR="./data"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

# Ensure .env.local exists
ensure_env() {
    if [ ! -f "$ENV_FILE" ]; then
        log_warn ".env.local not found, creating from .env.example"
        if [ -f ".env.example" ]; then
            cp .env.example "$ENV_FILE"
            log_info "Created $ENV_FILE - update with your settings if needed"
        else
            log_error ".env.example not found!"
            exit 1
        fi
    fi
}

# Ensure data directories exist
ensure_data_dirs() {
    mkdir -p "$DATA_DIR/postgres" "$DATA_DIR/dataprotection" "$DATA_DIR/uploads" "$DATA_DIR/logs"
    mkdir -p "./secrets"
}

# Ensure Docker secrets file exists for local dev
ensure_secrets() {
    if [ ! -f "./secrets/postgres_password" ]; then
        echo "localdevpassword123" > "./secrets/postgres_password"
        chmod 600 "./secrets/postgres_password"
        log_info "Created secrets/postgres_password for local dev"
    fi
}

# Clean data directories using Docker (avoids sudo for root-owned postgres files)
clean_data() {
    log_info "Cleaning data directories..."
    docker run --rm -v "$(pwd)/data:/data" alpine sh -c "rm -rf /data/postgres/* /data/dataprotection/* /data/logs/* /data/uploads/*" 2>/dev/null || true
}

# Start dev environment
cmd_up() {
    log_info "Starting local dev environment..."
    ensure_env
    ensure_data_dirs
    ensure_secrets
    docker compose --env-file "$ENV_FILE" up -d
    log_info "Waiting for services to be healthy..."
    sleep 5
    docker ps --format "table {{.Names}}\t{{.Status}}" | grep familyapp
    echo ""
    log_info "Site available at: https://localhost/"
    log_warn "You'll need to accept the self-signed certificate warning"
}

# Stop dev environment
cmd_down() {
    log_info "Stopping dev environment..."
    docker compose --env-file "$ENV_FILE" down
}

# Nuclear reset - wipe everything and start fresh
cmd_reset() {
    log_warn "This will DELETE all local data (database, uploads, etc.)"
    read -p "Are you sure? (y/N) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        log_info "Stopping containers and removing volumes..."
        docker compose --env-file "$ENV_FILE" down -v 2>/dev/null || true
        clean_data
        log_info "Starting fresh..."
        cmd_up
    else
        log_info "Cancelled"
    fi
}

# Show logs
cmd_logs() {
    docker compose --env-file "$ENV_FILE" logs -f app
}

# Show status
cmd_status() {
    docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" | grep -E "^NAMES|familyapp"
}

# Main
case "${1:-up}" in
    up)     cmd_up ;;
    down)   cmd_down ;;
    reset)  cmd_reset ;;
    logs)   cmd_logs ;;
    status) cmd_status ;;
    *)
        echo "Usage: $0 {up|down|reset|logs|status}"
        exit 1
        ;;
esac
