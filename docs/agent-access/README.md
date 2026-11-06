# Agent access: the family app's MCP endpoint

AI agents such as Claude Code and Codex can read and plan the household's recipes, weekly meal plan and shopping lists through `/mcp`. They connect with a personal access token that a member creates in **Settings → Agent access**. Quest `8bfa42cf`; the design is `D:\Development\data\outputs\research\family-mcp-2026-11-05\design.md`.

## What an agent can do

| Tool | Access | What it does |
| --- | --- | --- |
| `search_recipes` | read | Search the household's recipes, paged |
| `get_recipe` | read | One recipe: ingredients, servings, version |
| `preview_recipe_import` | read | Extract a recipe from a public web page without saving it (no YouTube, no model calls) |
| `get_meal_plan` | read | One Monday–Sunday week |
| `get_shopping_lists` | read | List summaries, or one list's items |
| `create_recipe` | write | Add a new recipe |
| `add_meal` | write | Plan one meal. Insert-only: an existing identical meal is left unchanged |
| `set_meal_servings` | write | How many people one meal is cooked for (needs its current version) |
| `generate_shopping_list` | write | A new list from one week, scaled to servings, with no pantry subtraction |
| `add_shopping_item` | write | One item on an active list |

There are no deletes, no edits of existing recipes or lists, no bulk changes and no settings. Every write takes a `requestId`, so a retry never writes twice, and a `dryRun` flag that previews without saving. A read-only token cannot call any write tool and doesn't see one.

## Token rules

- **One token per client.** Name it after the machine and client, for example "Claude Code on flagg".
- **Shown once.** The app stores only a SHA-256 fingerprint. A lost token can't be recovered: revoke it and create another.
- **Expiry is absolute:** 30, 90 or 365 days, or a date up to a year out. It never slides.
- **Revoking takes effect on the next request**, including from a client that is already connected.
- **Bound to the member and their household.** If the member is disabled or moves household, the token stops working.
- **Creating a token needs a sign-in within the last 15 minutes.** Otherwise the page asks you to sign in again. Revoking never does.
- **A token opens nothing on `/api`**, so it can't create, list or revoke tokens. The browser session opens nothing on `/mcp`.

## Connect a client

Keep the token in the `FAMILYAPP_TOKEN` environment variable on the machine that runs the agent, using a user environment variable or a secret manager. Never put it in a repo file, a command line or shell history.

**Codex.** Copy [`codex.config.toml.example`](codex.config.toml.example) into `~/.codex/config.toml`. Codex reads the token from `bearer_token_env_var`. Sign in with ChatGPT; `codex login status` should say `Logged in using ChatGPT`. Leave `OPENAI_API_KEY` unset so usage runs on the subscription.

**Claude Code.** Merge [`claude-code.mcp.json.example`](claude-code.mcp.json.example) into the user-scope MCP config, or register the server with:

```sh
claude mcp add --transport http --scope user family https://family.heathdev.me/mcp --header 'Authorization: Bearer ${FAMILYAPP_TOKEN}'
```

Use single quotes so `${FAMILYAPP_TOKEN}` is expanded by Claude Code at connect time, not by the shell. Sign in with the Claude subscription; `claude auth status` should show `"authMethod": "claude.ai"`. Leave `ANTHROPIC_API_KEY` unset.

Both templates were exercised by the acceptance run below, pointed at the local staging URL.

## Run it locally (isolated staging)

`scripts/mcp-staging.sh` builds this checkout into an image and runs it on its own compose project (`familyapp-mcp`). It has its own Postgres volume, listens on `127.0.0.1` only, has `/mcp` enabled, and signs in through the Development auth bypass as the seeded member. It never touches the dev stack or production.

```sh
scripts/mcp-staging.sh up 8095          # build HEAD, start, seed; http://127.0.0.1:8095
scripts/mcp-staging.sh as other@dev.local   # restart signed in as another seeded member
scripts/mcp-staging.sh psql "SELECT 1"  # SQL in the stack's database
scripts/mcp-staging.sh down             # remove containers, volume and image
```

Then open `http://127.0.0.1:8095/settings/agent-access`, create a token, and point a client at `http://127.0.0.1:8095/mcp`.

## Limits and logs

- **Per token, each minute:** 120 calls, 30 writes and 10 web-page fetches. Dry runs count as calls, not writes. Over the limit, the tool error says to wait and retry with the same requestId.
- **Logging:** each call logs one line, `Agent tool <tool> by token <id> (user, household) request <requestId>: <outcome> in <ms> [<correlationId>]`. A refused token logs `Agent token refused: <reason> (token id <id>)`. Neither logs argument content or the token.

## Production

Merging to `master` deploys. `/mcp` stays unmapped until the app container gets `Mcp__Enabled=true`. `docker-compose.yml` maps it from `MCP_ENABLED`, which defaults to `false`. Turning it on:

1. Merge the stack (#163, #164, #165, and this PR) under Justin's go. Two additive migrations apply at start: `PersonalAccessTokens` and `AgentOperations`.
2. Add a dedicated `location /mcp` to the shared nginx `server` block for `family.heathdev.me` (the `nginx` container on the production host, `/etc/nginx/conf.d/family.heathdev.me.conf`). Use the same upstream as its `location /`, pass `Authorization`, and turn off buffering and caching. Back up the file, then `nginx -t`, then `nginx -s reload`.

   ```nginx
   location /mcp {
       proxy_pass http://<same upstream as location />;
       proxy_http_version 1.1;
       proxy_set_header Host $host;
       proxy_set_header Authorization $http_authorization;
       proxy_set_header X-Real-IP $remote_addr;
       proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
       proxy_set_header X-Forwarded-Proto https;
       proxy_buffering off;
       proxy_cache off;
       add_header Cache-Control "no-store" always;
       proxy_read_timeout 300s;
       proxy_send_timeout 300s;
   }
   ```

3. Set `MCP_ENABLED=true`. On the production host, back up `~/familyapp/.env.local`, add the line `MCP_ENABLED=true`, and recreate the app:

   ```sh
   cp .env.local .env.local.bak-$(date +%Y%m%d%H%M%S) && echo 'MCP_ENABLED=true' >> .env.local
   ./deploy.sh --no-pull   # rebuilds .env from .env.local + secrets, recreates the containers
   ```

   `deploy.sh` copies `.env.local` into `.env` on every deploy, so the setting survives later merges. To turn the endpoint off, remove the line and run `./deploy.sh --no-pull` again.
4. Run a bounded smoke test with a token Justin creates in Settings: `initialize`, `tools/list`, one read, and one dry-run write through `https://family.heathdev.me/mcp`.
   - If the smoke-test token is a throwaway, revoke it afterwards and confirm a 401.
   - If it is the token the client will keep, don't revoke it: the acceptance run already proved revocation.

## Acceptance evidence

The real-client run is recorded in `D:\Development\data\outputs\verifications\fca-mcp-e2e-final\` (`verdict.md`). An earlier run, on `060e576`, is in `...\fca-mcp-e2e\`; it found the dry-run rate-limit issue. The final run includes:

- traces from a Claude Code session and a Codex session, both on subscription auth;
- screenshots of the app showing the recipe, the week and the list;
- an API readback;
- the server audit lines;
- the read-only and revoked-token refusals.
