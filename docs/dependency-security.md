# Dependency audit and remaining risks

Run `node scripts/audit-dependencies.mjs` from any directory. It checks both .NET
projects, including transitive packages, and the complete npm lockfile, including
development dependencies. Moderate, High, Critical, and audit-service errors fail
the command. Low findings remain in its output; they are not suppressed.

`.github/workflows/dependency-audit.yml` runs this command on every PR to master/main,
on pushes to those branches, every Monday at 09:17 UTC, and on manual dispatch.
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

Keep the supported SvelteKit 2 / Vite 6 / Vitest 4 majors. Direct minimums become
SvelteKit 2.70.2, Vite 6.4.5, and Vitest 4.1.12. Vite 6.4.4 is insufficient:
[GHSA-8g5g-mxqh-grgj](https://github.com/advisories/GHSA-8g5g-mxqh-grgj) fixes a
dev-server file-denial bypass in 6.4.5. Vitest's mock-server fixes require 4.1.12.
Transitive overrides pin the first fixed compatible releases: devalue 5.9.3,
nanoid 3.3.18, PostCSS 8.5.23, and source-map-js 1.2.2. They remain within their
parents' declared ranges. The scoped cookie 0.7.0 override crosses SvelteKit's
0.6 range to take the validation fix while preserving its parse/serialize API;
FCA's production server uses ASP.NET cookies, not SvelteKit's cookie server.

## Accepted Low risks (2026-11-12)

These are explicit residual findings, not a claim of zero npm advisories. The
parent approved documenting remaining Low risks after assessing reachability.
Five affected npm package entries represent three underlying Low advisories;
some entries inherit their finding from dependencies or peer dependencies.

| npm entry | Reachability and decision | Revisit if: |
| --- | --- | --- |
| `deepmerge` 4.3.1 | [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4) has no patched release. The Svelte Vite plugin merges checked-in compiler/plugin configuration with it. It runs during builds/dev, and is absent from the static app runtime. Accept this Low build-tooling risk; do not downgrade below its parent's supported range. | An upstream fix ships, or build configuration starts accepting untrusted objects. |
| `@sveltejs/vite-plugin-svelte` | Inherits the deepmerge finding (and inspector peer relationship). Compiler/dev tooling only; the plugin is not shipped in the ASP.NET runtime image. Accept the inherited Low risk. | The deepmerge fix ships or a compatible plugin removes it. |
| `@sveltejs/vite-plugin-svelte-inspector` | Inherits the plugin's Low finding through its peer dependency. Its inspector runs in the Vite dev server, not the production static bundle. Accept the inherited Low risk. | A compatible plugin/inspector release removes the affected dependency. |
| `@sveltejs/kit` 2.70.2 | Kit does contribute client routing code, so its `devDependency` label alone does not prove safety. [GHSA-pfhg-q3qv-9q6r](https://github.com/advisories/GHSA-pfhg-q3qv-9q6r) needs enhanced form actions on double-slash paths; FCA imports no `$app/forms` or `use:enhance`. [GHSA-27mw-82g8-phjj](https://github.com/advisories/GHSA-27mw-82g8-phjj) needs untrusted cache-control passed to `setHeaders` while prerendering; FCA has no `setHeaders`, and `+layout.ts` sets `ssr=false` and `prerender=false`. Both are fixed in Kit 3. Accept the Low findings with these paths unused, rather than force a framework major migration. | FCA adds enhanced form actions, prerendering/untrusted headers, or a supported Kit 2 backport ships; also reassess at the Kit 3 migration. |
| `@sveltejs/adapter-static` 3.0.10 | Inherits Kit's Low findings via its peer dependency; it generates static build output and does not serve requests in production. Accept the inherited Low risk under the same Kit feature constraints. | Kit is upgraded or its affected features become reachable. |

The final Docker image copies the ASP.NET publish directory and static SPA files,
not the Node build stage's `node_modules`. This distinction does not exclude
compiled browser code: the Kit row above explicitly assesses its shipped client
behavior. Audit counts describe affected package entries, not exploit counts.
