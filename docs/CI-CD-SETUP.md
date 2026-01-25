# CI/CD Setup Guide

This project uses GitHub Actions with a **self-hosted runner** on thedarktower for automatic deployment.

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────────┐
│  Developer PC   │────▶│     GitHub       │────▶│    thedarktower     │
│   (git push)    │     │ (triggers runner)│     │ (self-hosted runner)│
└─────────────────┘     └──────────────────┘     └─────────────────────┘
                                │                         │
                                │                         ▼
                                │                ┌─────────────────────┐
                                │                │  Docker Containers  │
                                ▼                │  - familyapp-app    │
                        ┌──────────────┐         │  - familyapp-postgres│
                        │   Discord    │         └─────────────────────┘
                        │ (notifications)│
                        └──────────────┘
```

**Benefits:**
- No SSH ports exposed to internet
- Unlimited free CI/CD minutes  
- Direct access to Docker and filesystem
- Secrets managed with `pass` (GPG-encrypted)
- Discord notifications on deploy

---

## Secrets Management

Secrets are stored using `pass` (password-store) on thedarktower, encrypted with GPG.

```bash
# View stored secrets
ssh thedarktower "pass ls"

# Output:
# Password Store
# └── familyapp
#     ├── google-client-id
#     ├── google-client-secret
#     └── postgres-password
```

### Retrieving Secrets
```bash
ssh thedarktower
pass familyapp/postgres-password  # Prompts for GPG passphrase
```

### Where Secrets Live

| Secret | Location | Notes |
|--------|----------|-------|
| DB password | `pass familyapp/postgres-password` | GPG encrypted |
| Google OAuth | `pass familyapp/google-*` | GPG encrypted |
| Discord webhook | GitHub Secrets | `DISCORD_WEBHOOK_URL` |
| Runtime config | `thedarktower:~/familyapp/.env` | Read by docker-compose |

### Regenerating .env from pass

If `.env` is lost or corrupted:
```bash
ssh thedarktower
cat > ~/familyapp/.env << EOF
POSTGRES_USER=familyapp
POSTGRES_PASSWORD=$(pass familyapp/postgres-password)
POSTGRES_DB=familyapp
GOOGLE_CLIENT_ID=$(pass familyapp/google-client-id)
GOOGLE_CLIENT_SECRET=$(pass familyapp/google-client-secret)
ASPNETCORE_ENVIRONMENT=Production
SITE_ADMIN_EMAILS=jmheath@gmail.com
POSTGRES_DATA_PATH=/home/sirm/familyapp-data/postgres
APP_UPLOADS_PATH=/home/sirm/familyapp-data/uploads
APP_LOGS_PATH=/home/sirm/familyapp-data/logs
APP_DATAPROTECTION_PATH=/home/sirm/familyapp-data/dataprotection
EOF
```

---

## GitHub Configuration

### Required Secrets

Go to **Repository → Settings → Secrets and variables → Actions**

| Secret | Purpose |
|--------|---------|
| `DISCORD_WEBHOOK_URL` | Deploy notifications to Discord |

### Deploy Key

Go to **Repository → Settings → Deploy keys**

| Title | Key | Write Access |
|-------|-----|--------------|
| `thedarktower-familyapp` | `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFfD...` | ❌ No |

---

## Runner Setup (One-Time)

### 1. Install Runner
```bash
ssh thedarktower
mkdir -p ~/actions-runner && cd ~/actions-runner
curl -o actions-runner.tar.gz -L https://github.com/actions/runner/releases/download/v2.321.0/actions-runner-linux-x64-2.321.0.tar.gz
tar xzf actions-runner.tar.gz
```

### 2. Configure Runner
Get token from **Repository → Settings → Actions → Runners → New self-hosted runner**

```bash
./config.sh --url https://github.com/sirmaelstrom/family-coordination-app --token YOUR_TOKEN
sudo ./svc.sh install
sudo ./svc.sh start
```

### 3. Verify
- **GitHub:** Repository → Settings → Actions → Runners → should show "Idle"
- **Server:** `systemctl status actions.runner.sirmaelstrom-family-coordination-app.thedarktower`

---

## How Deployment Works

1. **Push** to `main` or `master`
2. **GitHub** triggers the workflow
3. **Runner** on thedarktower executes:
   - `git fetch && git reset --hard origin/main`
   - `docker build -t familyapp:latest .`
   - `docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d`
   - Health check (polls container for up to 60s)
   - `docker image prune` (cleanup)
4. **Discord** notification sent (success/failure)

---

## Manual Operations

### Trigger Deploy Manually
**GitHub:** Actions → Deploy to Production → Run workflow

### Deploy via SSH
```bash
ssh thedarktower "cd ~/familyapp && git pull && docker build -t familyapp:latest . && docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d"
```

### Check Status
```bash
ssh thedarktower "docker ps --filter name=familyapp --format 'table {{.Names}}\t{{.Status}}'"
```

### View Logs
```bash
ssh thedarktower "docker logs familyapp-app --tail 100 -f"
```

---

## Runner Management

```bash
# Status
systemctl status actions.runner.sirmaelstrom-family-coordination-app.thedarktower

# Logs
journalctl -u actions.runner.sirmaelstrom-family-coordination-app.thedarktower -f

# Restart
sudo systemctl restart actions.runner.sirmaelstrom-family-coordination-app.thedarktower
```

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Runner offline | `sudo systemctl restart actions.runner.*` |
| Git pull fails | Check deploy key: `ssh -T git@github.com` |
| Container unhealthy | Check logs: `docker logs familyapp-app --tail 100` |
| Discord notification fails | Verify `DISCORD_WEBHOOK_URL` in GitHub Secrets |
| Missing .env | Regenerate from pass (see above) |

---

## Security Notes

- ✅ Runner runs as `sirm` user (not root)
- ✅ Secrets encrypted with GPG in `pass`
- ✅ No secrets in git repository
- ✅ Deploy key is read-only
- ✅ `.env` only exists on production server
- ✅ Discord webhook stored in GitHub Secrets (encrypted)
