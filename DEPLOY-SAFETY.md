# Deploy Safety — Protecting the Production Database

This documents how a production deploy can **silently destroy the database**, the
guardrails now in `deploy.sh` that prevent it, and how to recover if it ever happens.

## The failure mode

A deploy tears down and **force-recreates** every container, including postgres:

```sh
sudo docker rm -f familyapp-app familyapp-nginx familyapp-postgres
$COMPOSE down --remove-orphans
$COMPOSE up -d --force-recreate
```

Postgres has no data of its own — it mounts a directory. In production that's a
**bind mount** from `docker-compose.prod.yml`:

```yaml
postgres:
  volumes:
    - ${POSTGRES_DATA_PATH}:/var/lib/postgresql/data   # prod overlay (bind mount)
```

`${POSTGRES_DATA_PATH}` comes from `.env` (generated each deploy from the
darktower-only `.env.local`). The danger: **if that path is ever unset, empty,
wrong, or an empty directory, postgres runs `initdb` and starts a brand-new empty
cluster with no error.** The old data still exists on disk at the *real* path, but
the app is now talking to an empty database — so it looks like "all data is gone."

This is a *silent* failure: postgres treats an empty data dir as "first run," not
as an error. Nothing fails; the app just starts fresh.

### How we found it

While standing up the chores feature **locally**, a `docker compose up -d`
recreated postgres onto a different (project-prefixed) named volume than the one
the running container had been using, because the running container predated a
change to the compose volume config. The data wasn't destroyed — it was stranded
in the old, now-unmounted volume — but the app showed an empty database. Prod uses
a bind mount rather than a named volume, so it's less exposed to *that* exact
mismatch, but the same silent "fresh empty DB" outcome is reachable any time
`POSTGRES_DATA_PATH` doesn't resolve to the populated cluster.

## The guardrails (in `deploy.sh`, before teardown)

1. **Require `POSTGRES_DATA_PATH`.** If it's unset/empty in the generated `.env`,
   the deploy aborts instead of letting postgres invent a fresh DB.

2. **Back up the live DB before any teardown.** A `pg_dump` of the running
   `familyapp-postgres` is written to `~/data/db-backups/familyapp-<timestamp>.sql.gz`
   (14-day retention). If the backup fails, the deploy **aborts** — so we never
   tear down an un-backed-up database. Override with `ALLOW_NO_BACKUP=1` only when
   you knowingly accept the risk.

3. **Refuse to start on an uninitialized data dir.** If `POSTGRES_DATA_PATH`
   exists but has no `PG_VERSION` (i.e. it's not a real cluster), the deploy aborts
   rather than triggering a fresh `initdb`. Override with `ALLOW_FRESH_DB=1`.

Net effect: the silent "fresh empty DB" outcome becomes a **loud abort**, and every
deploy leaves a **restorable snapshot** behind.

## Intentional fresh install (first deploy)

The very first deploy on a new host has no data dir yet, which is legitimate:

```sh
ALLOW_FRESH_DB=1 ./deploy.sh --build --no-pull
```

(There's also no running DB to back up on a first deploy — the backup step is
skipped automatically in that case.)

## Recovery — restoring from a backup

Backups live at `~/data/db-backups/` on the darktower. To restore one into the
running container:

```sh
# pick the snapshot you want
ls -lt ~/data/db-backups/

# restore (drops & recreates objects from the dump)
gunzip -c ~/data/db-backups/familyapp-<timestamp>.sql.gz | \
  sudo docker exec -i familyapp-postgres sh -c \
    'PGPASSWORD=$(cat /run/secrets/postgres_password) psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
```

If the data was *stranded* rather than lost (a volume/path mismatch), the cleaner
fix is to point `POSTGRES_DATA_PATH` back at the populated directory and redeploy —
no restore needed. Inspect candidate dirs for a `PG_VERSION` file and recent
mtimes to identify the live one.

## Recommended (optional) structural hardening

The base `docker-compose.yml` declares a **named volume** for postgres and the prod
overlay **also** mounts a **bind mount** at the same container path; prod relies on
Compose's list-merge letting the bind mount "win." That works today but is fragile —
two mounts targeting the same path is ambiguous. A cleaner long-term structure is to
define the DB storage in exactly one place per environment (e.g. a dev overlay with
the named volume, the prod overlay with the bind mount, and no DB volume in the
shared base). Not required — the guardrails above make a misresolution fail loudly
and recoverably regardless — but it removes the footgun at the source.

## Note on image builds

Production builds via the multi-stage `Dockerfile` (`deploy.sh` runs
`docker build -t familyapp:latest .`), which builds **both** Svelte islands
(shopping-list + chores) and bakes them into `wwwroot/islands/`. The
`docker-build.sh` + `Dockerfile.runtime-only` path is the **local Windows**
workaround for the .NET SDK MSB3552 multi-stage bug and also builds both islands.
