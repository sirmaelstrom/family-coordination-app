// Decides the npm half of the dependency audit from `npm audit --json`.
// Pure (no I/O) so scripts/npm-audit-filter.test.mjs can hold its rules to fixtures.
//
// Rules:
// - Moderate, High and Critical advisories fail, unless their exact GHSA ID (and package)
//   is in ACCEPTED_ADVISORIES. Low advisories are reported and never fail.
// - Anything the filter cannot read fails closed: unparseable output, an `error` object
//   (the audit service failed), a missing `vulnerabilities` map, an advisory with no GHSA
//   ID or an unknown severity, or a Moderate+ entry that no advisory explains.
// - An accepted ID that the audit no longer reports fails as stale, so the list can't rot:
//   remove the entry (and its docs/dependency-security.md row) when that happens.

/**
 * Accepted Moderate+ advisories. Exact GHSA IDs only; each needs a reachability row
 * with a `Revisit if:` line in docs/dependency-security.md.
 */
export const ACCEPTED_ADVISORIES = [
  {
    id: 'GHSA-fc5q-39xc-vjpf',
    package: 'picomatch',
    why: 'no patched release; build tooling only, on checked-in globs (docs/dependency-security.md)',
  },
];

const BLOCKING = new Set(['moderate', 'high', 'critical']);
const KNOWN = new Set(['info', 'low', ...BLOCKING]);

/** @param {unknown} url */
function ghsaId(url) {
  const m = /GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4}/i.exec(String(url ?? ''));
  return m ? m[0] : null;
}

/** Parse and shape-check the report; returns the vulnerabilities map or null (problem recorded). */
function readVulnerabilities(rawJson, problems) {
  let report;
  try {
    report = JSON.parse(rawJson);
  } catch {
    problems.push('npm audit output is not JSON (fail closed)');
    return null;
  }
  if (report?.error) {
    const e = report.error;
    problems.push(`npm audit reported an error (fail closed): ${e.code ?? ''} ${e.summary ?? JSON.stringify(e)}`.trim());
    return null;
  }
  const vulns = report?.vulnerabilities;
  if (!vulns || typeof vulns !== 'object') {
    problems.push('npm audit output has no vulnerabilities map (fail closed)');
    return null;
  }
  return vulns;
}

/** One advisory object from a package's `via`, or null (problem recorded) when unreadable. */
function readAdvisory(name, via, problems) {
  const id = ghsaId(via.url);
  const severity = String(via.severity ?? '').toLowerCase();
  if (!id) {
    problems.push(`${name}: advisory without a GHSA ID (${via.url ?? via.title}) (fail closed)`);
    return null;
  }
  if (!KNOWN.has(severity)) {
    problems.push(`${name}: ${id} has unknown severity "${via.severity}" (fail closed)`);
    return null;
  }
  return { id, package: via.name ?? name, severity, title: via.title ?? '', range: via.range ?? '' };
}

/** One row per (advisory, package): advisory objects sit in `via` of the package they hit. */
function collectAdvisories(vulns, problems) {
  const advisories = new Map();
  for (const [name, entry] of Object.entries(vulns)) {
    const objects = (entry.via ?? []).filter((via) => typeof via === 'object' && via !== null);
    for (const a of objects.map((via) => readAdvisory(name, via, problems)).filter(Boolean)) {
      advisories.set(`${a.id}|${a.package}`, a);
    }
  }
  return [...advisories.values()];
}

/** Advisory IDs a package carries, following `via` package references (memoized, cycle-safe). */
function advisoryIdsOf(vulns, name, memo, seen = new Set()) {
  if (memo.has(name)) return memo.get(name);
  if (seen.has(name)) return new Set();
  seen.add(name);
  const ids = new Set();
  for (const via of vulns[name]?.via ?? []) {
    const found = typeof via === 'string' ? advisoryIdsOf(vulns, via, memo, seen) : [ghsaId(via?.url)];
    found.forEach((id) => id && ids.add(id));
  }
  memo.set(name, ids);
  return ids;
}

/** advisory ID -> every package entry that carries it; flags Moderate+ entries nothing explains. */
function collectCarriers(vulns, problems) {
  const memo = new Map();
  const carriers = {};
  for (const [name, entry] of Object.entries(vulns)) {
    const ids = advisoryIdsOf(vulns, name, memo);
    if (ids.size === 0 && BLOCKING.has(String(entry.severity).toLowerCase())) {
      problems.push(`${name}: ${entry.severity} with no advisory behind it (fail closed)`);
    }
    for (const id of ids) (carriers[id] ??= []).push(name);
  }
  return carriers;
}

/**
 * @param {string} rawJson stdout of `npm audit --json`
 * @param {{id: string, package: string}[]} accepted
 */
export function evaluateNpmAudit(rawJson, accepted = ACCEPTED_ADVISORIES) {
  const problems = [];
  const vulns = readVulnerabilities(rawJson, problems);
  if (!vulns) return { ok: false, problems, advisories: [], stale: [], carriers: {} };

  const isAccepted = (a) => accepted.some((x) => x.id === a.id && x.package === a.package);
  const advisories = collectAdvisories(vulns, problems).map((a) => {
    const blocking = BLOCKING.has(a.severity);
    return { ...a, blocking, accepted: blocking && isAccepted(a) };
  });
  const carriers = collectCarriers(vulns, problems);

  for (const a of advisories) {
    if (a.blocking && !a.accepted) problems.push(`${a.id} (${a.severity}, ${a.package} ${a.range}): ${a.title}`);
  }
  const stale = accepted
    .filter((x) => !advisories.some((a) => a.id === x.id && a.package === x.package))
    .map((x) => x.id);
  for (const id of stale) {
    problems.push(`${id} is on the accepted list but npm audit no longer reports it: remove it from ACCEPTED_ADVISORIES and its docs row`);
  }
  return { ok: problems.length === 0, problems, advisories, stale, carriers };
}
