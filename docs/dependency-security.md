# Dependency audit and remaining risks

Run `node scripts/audit-dependencies.mjs` from any directory. It checks both .NET
projects, including transitive packages, and the complete npm lockfile, including
development dependencies. Moderate, High, Critical, and audit-service errors fail
the command. Low findings remain in its output; they are not suppressed.

The npm half reads `npm audit --json` and decides in `scripts/npm-audit-filter.mjs`.
Its `ACCEPTED_ADVISORIES` list names exact GHSA IDs (with their package) that may
pass at Moderate or above; every other Moderate+ advisory still fails. Anything the
filter cannot read fails closed: non-JSON output, an `error` object from the audit
service, an advisory without a GHSA ID or with an unknown severity. An accepted ID
the audit stops reporting also fails, so the list cannot rot: delete the entry and
its row below. Each accepted advisory prints the package entries that carry it.
`node --test scripts/npm-audit-filter.test.mjs` holds these rules to two real audit
captures (`scripts/fixtures/npm-audit/`) and synthetic cases; the workflow runs it
before the audit. Add an ID only with a reachability row in the accepted table below.

`.github/workflows/dependency-audit.yml` runs this command on every PR,
on pushes to master/main, every Monday at 09:17 UTC, and on manual dispatch.
The weekly run can fail when an advisory arrives without a source change. Check
the Dependency Audit workflow and GitHub's workflow-failure notifications.
The repo is public and uses a standard `ubuntu-latest` runner, for which
[GitHub charges no compute minutes](https://docs.github.com/en/billing/concepts/product-billing/github-actions).
The workflow uploads no artifacts and uses no secrets or self-hosted runner.

## NuGet fixes and reachability

- [HtmlSanitizer GHSA-j92c-7v7g-gj3f](https://github.com/advisories/GHSA-j92c-7v7g-gj3f)
  is fixed in 9.0.892 (or 9.1.893-beta). Its bypass requires allowing `template`;
  `MarkdownHelper.CreateSanitizer` does not allow it or `shadowrootmode`.
  The sanitizer itself runs on recipe instructions shown by the recipe drawer and
  meal-plan recipe sheet. The advisory's enabling configuration is absent.
- [AngleSharp GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg)
  is fixed in 1.5.0. External recipe HTML reaches `RecipeScraperService`'s parser;
  that method reads JSON-LD text, rather than returning the parsed page as HTML.
  AngleSharp also parses HTML for the recipe sanitizer. FCA disallows MathML,
  `annotation-xml`, and `template`, but the parser still needs the fix: do not
  treat a sanitizer allowlist as a substitute for a conforming parser.
- The chosen stable pair is **HtmlSanitizer 9.1.973 + AngleSharp 1.6.0**.
  HtmlSanitizer 9.0.x requires AngleSharp **0.17.1 exactly**, an affected version;
  9.1.973 is the first stable 9.1 release and declares AngleSharp >=1.6.0 and
  AngleSharp.Css >=1.0.0. This removes the old NU1608 dependency mismatch rather
  than forcing an unsupported parser under the minimum sanitizer patch.
  [Release notes](https://github.com/mganss/HtmlSanitizer/releases/tag/v9.1.973).
- Testcontainers pulls in SSH.NET. Its two High SCP advisories,
  [remote-path command execution](https://github.com/advisories/GHSA-mggc-4xg6-vcxf)
  and [recursive download traversal](https://github.com/advisories/GHSA-q939-rpr3-3284),
  are fixed in **2026.0.0**, pinned directly in the test project. FCA does not use
  SCP; this dependency is test tooling and does not enter the app's publish output.

## npm changes

The SPA runs on the SvelteKit 3 / Vite 8 / Vitest 4 majors (2026-11-17): SvelteKit
3.3.1, adapter-static 4.0.1, vite-plugin-svelte 7.3.1, Vite 8.3.8, Svelte 5.57.3,
TypeScript 6.0.3, Vitest 4.1.12. Kit 2 had no fix for
[GHSA-3r8g-jcqq-3p5j](https://github.com/advisories/GHSA-3r8g-jcqq-3p5j) (High) or
[GHSA-6cf5-gc4q-fm3x](https://github.com/advisories/GHSA-6cf5-gc4q-fm3x) (Moderate):
both cover `>=2.42.0 <=3.1.0` and Kit 3.1.1 is the first patched release, with no
Kit 2 backport. The upgrade also clears the Kit 2 Moderates
[GHSA-mw6w-2fmv-pvw3](https://github.com/advisories/GHSA-mw6w-2fmv-pvw3) and
[GHSA-rqp5-r3wg-vg35](https://github.com/advisories/GHSA-rqp5-r3wg-vg35), adapter-static
3's [GHSA-jf9x-2fg2-hx4w](https://github.com/advisories/GHSA-jf9x-2fg2-hx4w), and the
two Kit Lows this page used to accept,
[GHSA-pfhg-q3qv-9q6r](https://github.com/advisories/GHSA-pfhg-q3qv-9q6r) and
[GHSA-27mw-82g8-phjj](https://github.com/advisories/GHSA-27mw-82g8-phjj).
`scripts/fixtures/npm-audit/kit2-master.json` is the Kit 2 audit these replace.
Kit 3 sets the floors that follow: Node 22.17 (CI, the audit workflow and the
Dockerfile's SPA stage run Node 22), Vite 8, TypeScript 6 and vite-plugin-svelte 7.
Vitest stays on 4.1.12: it declares `vite ^6 || ^7 || ^8`, and it keeps the
mock-server fixes that needed 4.1.12.

There are no npm `overrides`. The five earlier pins (devalue 5.9.3, nanoid 3.3.18,
PostCSS 8.5.23, source-map-js 1.2.2, Kit-scoped cookie 0.7.0) are moot: the Kit 3
tree resolves devalue 5.9.4, nanoid 3.3.20, PostCSS 8.5.33, source-map-js 1.2.2 and
cookie 2.0.2, each at or past its pinned fix, and Kit 3 requires devalue `^5.9.4`
and cookie `^2.0.1`, so the old devalue and cookie pins would now conflict. The
audit, not a pin, catches a regression.

## Accepted Moderate+ risks

These pass the audit only because their exact GHSA ID is in
`ACCEPTED_ADVISORIES` (`scripts/npm-audit-filter.mjs`).

None as of 2026-11-19. GitHub withdrew the one entry this table held, picomatch
[GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf), on
2026-11-18 (`withdrawn_at` 15:38:56Z). The audit then failed on the stale ID, as
designed, so the entry was deleted. The withdrawn row is kept below for the record.
A new acceptance adds a live row to a table like it, with the same columns.

| Advisory (withdrawn) | Reachability and decision | Revisit if: |
| --- | --- | --- |
| [GHSA-fc5q-39xc-vjpf](https://github.com/advisories/GHSA-fc5q-39xc-vjpf) `picomatch` <=4.0.7, High (accepted 2026-11-17; withdrawn by GitHub 2026-11-18, entry deleted 2026-11-19) | ReDoS from single-branch repeated extglobs such as `+(*)`: the attacker must supply the **glob pattern**. No patched release exists. picomatch is build tooling only: Vite (config globs, `import.meta.glob`), tinyglobby/fdir (file discovery for Vite and svelte-check) and Vitest (`include` globs). Every pattern they compile is checked in (`vite.config.ts`, `vitest.config.ts`, two `import.meta.glob` calls in test files); none comes from a request, a user or the network. The production image holds the ASP.NET publish output and the static SPA files, not the Node stage's `node_modules`, and the shipped client bundle does not include picomatch. The audit prints the 9 entries that carry it (picomatch, plus fdir, tinyglobby, vite, vitest, svelte-check, Kit, adapter-static and vite-plugin-svelte by dependency). | An upstream picomatch fix ships (take it, then the audit fails on the stale ID until the entry is deleted), or a build or dev step starts compiling globs from untrusted input. |

## Accepted Low risks

None as of 2026-11-17. The Low rows this page used to hold are gone: Kit 3 fixes
both Kit Lows (and so adapter-static's inherited ones), and the deepmerge advisory
[GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4) that
vite-plugin-svelte and its inspector inherited was withdrawn by GitHub on
2026-11-16. vite-plugin-svelte 7 also no longer depends on the inspector.
deepmerge 4.3.1 is still in the tree, unflagged. A new Low prints in the audit
output without failing it; add a row here when one appears.

The final Docker image copies the ASP.NET publish directory and static SPA files,
not the Node build stage's `node_modules`. This distinction does not exclude
compiled browser code: a Kit advisory, for instance, must be assessed against the
client routing code Kit ships. Audit counts describe affected package entries, not
exploit counts.
