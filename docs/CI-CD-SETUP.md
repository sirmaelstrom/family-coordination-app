# CI/CD Setup Guide

This project uses GitHub Actions with a **self-hosted runner** on thedarktower for automatic deployment.

## Architecture

```
GitHub (push to main)
    ↓ (webhook)
Self-hosted runner on thedarktower (polls GitHub)
    ↓
git pull → docker build → docker compose up
    ↓
Health check verification
```

**Benefits:**
- No SSH ports exposed to internet
- Unlimited free CI/CD minutes
- Direct access to Docker and filesystem
- No secrets needed in GitHub (runner has local .env)

---

## Setup Instructions

### 1. Install GitHub Actions Runner on thedarktower

```bash
# Create runner directory
mkdir -p ~/actions-runner && cd ~/actions-runner

# Download latest runner (x64)
curl -o actions-runner.tar.gz -L https://github.com/actions/runner/releases/download/v2.321.0/actions-runner-linux-x64-2.321.0.tar.gz
tar xzf actions-runner.tar.gz
```

### 2. Get Runner Token from GitHub

1. Go to: **Repository → Settings → Actions → Runners**
2. Click **"New self-hosted runner"**
3. Copy the token from the `./config.sh` command shown

### 3. Configure the Runner

```bash
cd ~/actions-runner

# Configure (paste your token)
./config.sh --url https://github.com/sirmaelstrom/family-coordination-app --token YOUR_TOKEN_HERE

# Install as system service
sudo ./svc.sh install

# Start the service
sudo ./svc.sh start

# Check status
sudo ./svc.sh status
```

### 4. Verify Runner is Connected

Go to **Repository → Settings → Actions → Runners** — you should see the runner listed as "Idle".

---

## Git Credentials on thedarktower

The runner needs to pull from GitHub. We use an SSH deploy key (read-only):

**Already configured:**
- SSH key: `~/.ssh/github-deploy`
- SSH config: Points to the key for github.com
- Remote: `git@github.com:sirmaelstrom/family-coordination-app.git`

**To add the deploy key to GitHub:**
1. Go to: **Repository → Settings → Deploy keys → Add deploy key**
2. Title: `thedarktower-familyapp`
3. Key:
```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFfDnKtIT4m6TAHzI3YdenGKgd6alPB60/kAAQ3NzPKm thedarktower-familyapp
```
4. Leave "Allow write access" **unchecked**

---

## How Deployment Works

1. Push to `main` or `master` branch
2. GitHub sends webhook to runner
3. Runner executes workflow:
   - `git fetch && git reset --hard origin/main`
   - `docker build -t familyapp:latest .`
   - `docker compose up -d`
   - Health check (waits up to 60s)
   - Cleanup old Docker images

---

## Manual Deployment

**Via GitHub:**
- Go to **Actions → Deploy to Production → Run workflow**

**Via SSH (if needed):**
```bash
ssh thedarktower "cd ~/familyapp && git pull && docker build -t familyapp:latest . && docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d"
```

---

## Runner Management

```bash
# Check status
sudo ~/actions-runner/svc.sh status

# Restart runner
sudo ~/actions-runner/svc.sh stop
sudo ~/actions-runner/svc.sh start

# View runner logs
journalctl -u actions.runner.sirmaelstrom-family-coordination-app.thedarktower -f

# Update runner (when new version available)
cd ~/actions-runner
sudo ./svc.sh stop
curl -o actions-runner.tar.gz -L https://github.com/actions/runner/releases/latest/...
tar xzf actions-runner.tar.gz
sudo ./svc.sh start
```

---

## Troubleshooting

### Runner shows "Offline"
```bash
sudo ~/actions-runner/svc.sh status
sudo ~/actions-runner/svc.sh start
```

### Git pull fails
```bash
# Test SSH to GitHub
ssh -T git@github.com

# Should see: "Hi sirmaelstrom/family-coordination-app! You've successfully authenticated..."
# If not, check deploy key is added to repo
```

### Docker build fails
```bash
# Check disk space
df -h

# Check Docker
docker info
docker logs familyapp-app --tail 100
```

---

## Security Notes

- Runner runs as `sirm` user (not root)
- No GitHub secrets needed — uses local .env file
- Deploy key is read-only (can't push to repo)
- Runner only executes workflows from this repository
