# Offsite Postgres backups → Cloudflare R2

Nightly `pg_dump` of the production `familyapp` database, age-encrypted and shipped
offsite to a Cloudflare R2 bucket. This closes the one unrecoverable failure mode of
the self-hosted setup: **the family's data dying with the single home server.**

- **What runs:** [`pg-backup-r2.sh`](./pg-backup-r2.sh) as a **`sirm` user-cron** on thedarktower, nightly **03:30 local**.
- **Why user-cron (not root):** `sirm` is in the `docker` group, so the dump runs via `docker exec` with no sudo. pg auth is local-socket `trust` (see `docker/postgres/pg_hba.conf`), so no DB password is handled.
- **Cost:** ~$0 — dumps are ~125 KB; R2 free tier is 10 GB + generous ops.
- **Not** point-in-time / RPO-minutes. It's a nightly durability net. PITR would come from a managed Postgres if the app ever migrates off the home server (separate, parked decision).

## Where things live (on thedarktower)

| Path | What |
|---|---|
| `~/familyapp-backup/pg-backup-r2.sh` | the script (canonical source: repo `infra/backup/`) |
| `~/familyapp-backup/backup.env` | config + secrets, mode `600` (R2 keys, `AGE_RECIPIENT`, retention, heartbeat URL) |
| `~/familyapp-backup/backup.log` | run log (appended by cron) |
| user crontab (`crontab -l`) | the `30 3 * * *` entry |

R2 object layout (bucket `familyapp-backups`, account `157bf441cc44ecffbcb46b0d42339157`):

| key | what |
|---|---|
| `daily/familyapp_<UTC-ISO>.dump.age`   | every night, newest `RETAIN_DAILY` (14) kept |
| `monthly/familyapp_<UTC-ISO>.dump.age` | copy taken on the 1st, newest `RETAIN_MONTHLY` (6) kept |

`.age` = age-encrypted (asymmetric). The host holds only the **public** recipient
(`AGE_RECIPIENT` in `backup.env`). The **private** key is off-host — Bitwarden Secrets
`FAMILYAPP_BACKUP_AGE_KEY` — and is required only at restore time.

## Encryption keys

- **Public** (`age1…`): in `backup.env` as `AGE_RECIPIENT`. Encrypt-only; safe on the host.
- **Private** (`AGE-SECRET-KEY-1…`): Bitwarden Secrets `FAMILYAPP_BACKUP_AGE_KEY` (+ keep an offline copy). **Losing it makes every backup unreadable.** It must NOT live on the host — if `~/familyapp-backup/age-key.txt` exists, move it into bws and `shred -u` it.

## Restore / recovery procedure

A backup that has never been restored is a hope. This exact procedure was run green at
install (restored counts matched prod). Run it to recover, or to re-verify anytime.

```bash
ssh thedarktower
set -a; . ~/familyapp-backup/backup.env; set +a
export RCLONE_CONFIG=/dev/null RCLONE_CONFIG_R2_TYPE=s3 RCLONE_CONFIG_R2_PROVIDER=Cloudflare \
  RCLONE_CONFIG_R2_ACCESS_KEY_ID=$R2_ACCESS_KEY_ID RCLONE_CONFIG_R2_SECRET_ACCESS_KEY=$R2_SECRET_ACCESS_KEY \
  RCLONE_CONFIG_R2_ENDPOINT=https://$R2_ACCOUNT_ID.r2.cloudflarestorage.com \
  RCLONE_CONFIG_R2_REGION=auto RCLONE_CONFIG_R2_NO_CHECK_BUCKET=true

# 1. pull the latest daily object
LATEST=$(rclone lsf R2:familyapp-backups/daily/ | sort | tail -1); echo "$LATEST"
rclone copyto "R2:familyapp-backups/daily/$LATEST" "/tmp/$LATEST"

# 2. decrypt (needs the private key from bws — write it to a temp file first)
#    bws secret get <id-of-FAMILYAPP_BACKUP_AGE_KEY> ... > /tmp/age.key
age -d -i /tmp/age.key -o /tmp/restore.dump "/tmp/$LATEST"

# 3. restore into a throwaway container and sanity-check row counts
docker run -d --name fa-restore-test -e POSTGRES_USER=familyapp -e POSTGRES_DB=familyapp \
  -e POSTGRES_PASSWORD=restoretest postgres:17
sleep 8
docker cp /tmp/restore.dump fa-restore-test:/tmp/restore.dump
docker exec fa-restore-test pg_restore -U familyapp -d familyapp --no-owner --no-privileges /tmp/restore.dump
docker exec fa-restore-test psql -U familyapp -d familyapp -c \
  'SELECT (SELECT count(*) FROM "Recipes") recipes,(SELECT count(*) FROM "Households") households;'
docker exec familyapp-postgres  psql -U familyapp -d familyapp -c \
  'SELECT (SELECT count(*) FROM "Recipes") recipes,(SELECT count(*) FROM "Households") households;'   # compare

# 4. cleanup
docker rm -f fa-restore-test; rm -f /tmp/restore.dump /tmp/age.key "/tmp/$LATEST"
```

**Real recovery** into prod restores into a freshly-recreated `familyapp` database on the
live container (drop + create empty first — the app rebuilds schema on boot, so restoring
over a live DB throws "already exists"). See `data/memory/INFRASTRUCTURE.md` for the
drop-recreate-restore shape used for the observatory DB.

## Operations

- **Logs:** `~/familyapp-backup/backup.log`. Each run appends ~6 lines.
- **Manual run:** `~/familyapp-backup/pg-backup-r2.sh` (or via cron env: `env -i HOME=/home/sirm PATH=/usr/bin:/bin ~/familyapp-backup/pg-backup-r2.sh`).
- **Health / dead-man alert:** healthchecks.io. Set `HEALTHCHECK_URL` in `backup.env` to a check's ping URL; the script pings `/start`, `/fail` on error, and success. A missed nightly run → healthchecks nags. If it goes red, read the log. Common causes: R2 token rotated, `familyapp-postgres` renamed, disk full in `/tmp`.
- **Update the script:** edit repo `infra/backup/pg-backup-r2.sh`, then
  `scp infra/backup/pg-backup-r2.sh thedarktower:~/familyapp-backup/ && ssh thedarktower chmod 755 ~/familyapp-backup/pg-backup-r2.sh`.
- **Disable:** `crontab -e` and remove the `pg-backup-r2.sh` line.

## Rebuild-from-scratch (if the host is replaced)

1. `sudo usermod -aG docker sirm` (backup runs as a docker-group user cron).
2. `brew install rclone age`.
3. Recreate `~/familyapp-backup/backup.env` from [`familyapp-backup.env.example`](./familyapp-backup.env.example): R2 keys (bws `R2_S3_Access_Key_ID` / `R2_S3_Secret_Access_Key`), account id `157bf441cc44ecffbcb46b0d42339157`, bucket `familyapp-backups`, `AGE_RECIPIENT` = the public key paired with bws `FAMILYAPP_BACKUP_AGE_KEY`, `HEALTHCHECK_URL`.
4. `scp` the script into `~/familyapp-backup/`, `chmod 755`, add the `30 3 * * *` crontab line.
5. Run once, then the restore test above.
