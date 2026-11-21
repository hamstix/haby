import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, lstatSync, realpathSync, writeFileSync } from 'node:fs';
import { resolve, relative, isAbsolute, join } from 'node:path';
import { git, immutableRoute } from './ci-change-routing.mjs';

const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value) && !/^0+$/.test(value);
class QualificationFailure extends Error {
  constructor(message, details = {}) { super(message); this.details = details; }
}
const requireThat = (value, message, details) => { if (!value) throw new QualificationFailure(message, details); };
function boundedFailure(error, stage) {
  const detail = error instanceof QualificationFailure ? error.details : { code: 'api-or-data-unavailable' };
  return { primary: { code: detail.code ?? 'contract-mismatch', stage: detail.stage ?? stage,
    gate: detail.gate ?? null, member: detail.member ?? null, outcome: detail.outcome ?? 'failure', deadline: null,
    ...(detail.api ? { api: detail.api } : {}) },
  cleanup: detail.cleanup ?? 'not-established' };
}
function artifactRequestDiagnostic(error) {
  const status = error?.status;
  const httpStatus = Number.isInteger(status) && status >= 100 && status <= 599 ? status : null;
  const transportCodes = ['ECONNRESET', 'ETIMEDOUT', 'ENOTFOUND', 'EAI_AGAIN', 'ECONNREFUSED', 'EPIPE'];
  const category = httpStatus === 401 ? 'authentication' : httpStatus === 403 ? 'forbidden' :
    httpStatus === 404 ? 'not-found' : httpStatus === 429 ? 'rate-limited' :
      httpStatus >= 500 ? 'server-error' : httpStatus !== null ? 'other-http' :
        transportCodes.includes(error?.code) ? 'transport' : 'unknown';
  return { operation: 'actions.getArtifact', httpStatus, category };
}
// Internal timing seam keeps the fixed visibility policy deterministic in tests.
const artifactTiming = {
  now: () => performance.now(),
  wait: ms => new Promise(resolve => setTimeout(resolve, ms)),
  setTimer: (callback, ms) => setTimeout(callback, ms),
  clearTimer: timer => clearTimeout(timer),
};
async function readControlArtifact(github, owner, repo, artifactId, member, timing) {
  const started = timing.now(); const deadline = started + 15000;
  const elapsed = () => Math.max(0, Math.floor(timing.now() - started));
  let attempts = 0;
  const unavailable = (error, code = 'api-or-data-unavailable') => new QualificationFailure('Control artifact request failed.', {
    code, gate: 'control', member,
    api: { ...artifactRequestDiagnostic(error), attempts, elapsedMs: elapsed() },
  });
  for (const delay of [0, 1000, 2000, 4000]) {
    if (timing.now() + delay >= deadline) throw unavailable(null, 'control-artifact-deadline');
    if (delay) await timing.wait(delay);
    const remaining = deadline - timing.now();
    if (remaining <= 0) throw unavailable(null, 'control-artifact-deadline');
    const controller = new AbortController(); let timer;
    attempts++;
    try {
      // Race as well as abort: an unresponsive transport must not hold publication open.
      const timeout = new Promise((_, reject) => {
        timer = timing.setTimer(() => {
          controller.abort(); reject(unavailable(null, 'control-artifact-deadline'));
        }, remaining);
      });
      const response = await Promise.race([
        timeout,
        github.rest.actions.getArtifact({ owner, repo, artifact_id: artifactId,
          request: { timeout: remaining, signal: controller.signal } }),
      ]);
      if (timing.now() >= deadline) throw unavailable(null, 'control-artifact-deadline');
      return { response, diagnostic: { attempts, httpStatus: 200, elapsedMs: elapsed() } };
    } catch (error) {
      if (error instanceof QualificationFailure) throw error;
      // Only the structured HTTP status is retryable, never a message or transport code.
      if (error?.status !== 404 || attempts === 4) throw unavailable(error);
      if (timing.now() >= deadline) throw unavailable(error, 'control-artifact-deadline');
    } finally { timing.clearTimer(timer); }
  }
}
const outcome = value => ['failure', 'cancelled', 'skipped', 'timed_out', 'action_required', 'neutral'].includes(value) ? value : 'missing-or-incomplete';

export function validateRequest(environment, input) {
  requireThat(environment.event === 'workflow_dispatch' && environment.ref === 'refs/heads/main', 'Installed main dispatch required.');
  requireThat(environment.workflowRef === `${environment.repository}/.github/workflows/final-qualification.yml@refs/heads/main`, 'Unexpected control workflow.');
  requireThat(/^[1-9][0-9]{0,8}$/.test(input.pr ?? ''), 'Numeric PR required.');
  for (const key of ['head', 'base', 'merge', 'tree']) requireThat(sha(input[key]), 'Immutable expected identities required.');
  requireThat(sha(environment.control) && input.base === environment.control, 'Control must be installed at the expected main base.');
  return { repository: environment.repository, control: environment.control, pr: Number(input.pr), head: input.head, base: input.base, merge: input.merge, tree: input.tree };
}

export function assertCurrent(identity, pr, commit, main) {
  requireThat(main === identity.base, 'Current main no longer matches the expected base.',
    { code: 'stale-main', stage: 'freshness', gate: 'candidate', member: 'refs/heads/main', outcome: 'stale' });
  requireThat(pr.state === 'open' && pr.number === identity.pr && pr.head?.sha === identity.head &&
    pr.base?.ref === 'main' && pr.base?.repo?.full_name === identity.repository &&
    pr.merge_commit_sha === identity.merge, 'Candidate is stale or outside the expected PR/base.',
  { code: 'stale-candidate', stage: 'freshness', gate: 'candidate', member: 'HEAD/base/merge/tree', outcome: 'stale' });
  requireThat(commit.sha === identity.merge && commit.tree?.sha === identity.tree && commit.parents?.length === 2 &&
    commit.parents[0].sha === identity.base && commit.parents[1].sha === identity.head, 'Tested merge/tree identity mismatch.',
  { code: 'tested-tree-mismatch', stage: 'freshness', gate: 'candidate', member: 'merge-parents/tree', outcome: 'stale' });
}

export function assertCandidate(identity, pr, commit, permission, main) {
  requireThat(['write', 'maintain', 'admin'].includes(permission), 'Maintainer dispatch permission required.');
  assertCurrent(identity, pr, commit, main);
}

// Only prepare derives this record, using installed code and immutable Git data.
// It is never accepted from workflow_dispatch inputs or runtime artifacts.
export function classifyCandidate(root, identity) {
  try {
    requireThat(typeof root === 'string' && isAbsolute(root), 'Trusted workspace required.');
    const parents = git(root, 'show', '-s', '--format=%P', identity.merge).trim().split(' ');
    requireThat(parents.length === 2 && parents[0] === identity.base && parents[1] === identity.head &&
      git(root, 'rev-parse', `${identity.merge}^{tree}`).trim() === identity.tree, 'Local tested merge identity mismatch.');
    const classification = { ...immutableRoute(root, identity.base, identity.merge), tree: identity.tree, documentation: 'passed' };
    return { ...identity, scope: classification.route, classification };
  } catch {
    throw new QualificationFailure('Trusted classification unavailable or failed.',
      { code: 'classification-failed', stage: 'classification', gate: 'control', member: 'base-to-tested-merge' });
  }
}
function assertClassification(identity) {
  const value = identity.classification;
  requireThat(['full', 'docs-only'].includes(identity.scope) && value && value.route === identity.scope &&
    value.before === identity.base && value.after === identity.merge && value.tree === identity.tree &&
    Number.isSafeInteger(value.changedEntries) && value.changedEntries >= (identity.scope === 'docs-only' ? 1 : 0) &&
    value.changedEntries <= 1000000 && value.documentation === 'passed', 'Trusted classification missing or mismatched.',
  { code: 'classification-incomplete', stage: 'classification', gate: 'control', member: 'bound-classification' });
}
function assertDocsResults(results, jobs) {
  for (const key of ['build', 'api', 'workloads']) requireThat(results[key] === 'skipped', 'Docs runtime groups must be skipped.',
    { code: 'unexpected-docs-runtime', gate: key, member: 'workflow-result', outcome: outcome(results[key]) });
  for (const key of ['prepare', 'start']) requireThat(jobs.filter(job => job.name === key && job.status === 'completed' && job.conclusion === 'success').length === 1,
    'Docs admission incomplete.', { code: 'docs-admission-incomplete', gate: 'control', member: key });
  requireThat(jobs.every(job => ['prepare', 'start', 'publish'].includes(job.name) ||
    (['build', 'api', 'workloads'].includes(job.name) && job.status === 'completed' && job.conclusion === 'skipped')),
  'Unexpected docs execution.', { code: 'unexpected-docs-runtime', gate: 'control', member: 'job-results' });
}
const docsChecks = ['complete-change-classification', 'added-whitespace', 'local-markdown-links'];

export function assertResults(results) {
  const missing = ['build', 'api', 'workloads'].find(name => results[name] !== 'success');
  requireThat(!missing, 'Full qualification jobs failed, were canceled, skipped or are missing.',
    { code: 'workflow-outcome', gate: missing, member: 'workflow-result', outcome: outcome(results[missing]) });
}

export function assertJobs(jobs) {
  for (const name of ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
    'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel']) {
    requireThat(jobs.filter(job => job.name === name && job.status === 'completed' && job.conclusion === 'success').length === 1, 'Required job is missing, canceled, skipped or failed.',
      { code: 'job-outcome', gate: name, member: 'job-result', outcome: outcome(jobs.find(job => job.name === name)?.conclusion) });
  }
}

export function artifactNames(identity, attempt) {
  requireThat(Number.isSafeInteger(attempt) && attempt > 0, 'Invalid run attempt.');
  const ref = identity.merge;
  return {
    fast: `fast-inventory-${ref}-${attempt}`,
    windows: `native-inventory-win-x64-${ref}-${attempt}`,
    linux: `native-inventory-linux-x64-${ref}-${attempt}`,
    postgresql: `postgresql-timing-${ref}-${attempt}`,
    composition: `operator-composition-sample-${ref}-${attempt}`,
    provider: 'kubernetes-qualification',
    configured: `kubernetes-configured-deployment-${ref}`,
    durable: `kubernetes-durable-actions-${ref}-${attempt}`,
    kernel: `kubernetes-deployment-kernel-${ref}`,
  };
}

function json(files, path) {
  const detail = { code: 'missing-member', gate: files.qualificationGate, member: path };
  requireThat(files.has(path), 'Required evidence member is missing.', detail);
  const text = files.get(path);
  requireThat(Buffer.byteLength(text) <= 8 * 1024 * 1024, 'Evidence member exceeds bound.', { ...detail, code: 'member-bound' });
  try {
    const value = JSON.parse(text.toString());
    requireThat(value !== null && typeof value === 'object', 'Invalid evidence JSON shape.', { ...detail, code: 'invalid-member' });
    return value;
  }
  catch { throw new QualificationFailure('Invalid evidence JSON.', { ...detail, code: 'invalid-member' }); }
}
const array = value => Array.isArray(value) ? value : [value];
function inventory(expected, actual, detail = {}) {
  const context = { code: 'coverage-incomplete', ...detail };
  requireThat(expected.length > 0 && actual.length === expected.length && actual.every(row => row && typeof row.method === 'string' && typeof row.case === 'string'), 'Incomplete gate coverage.', context);
  const keys = rows => rows.map(row => `${row.method}#${row.case}`).sort();
  requireThat(JSON.stringify(keys(expected)) === JSON.stringify(keys(actual)) && actual.every(row => row.outcome === 'Passed'), 'Failed, skipped or different gate cases.', context);
}
function counts(files, prefix, expected) {
  const value = json(files, `${prefix}counts.json`);
  requireThat(value.total === expected && value.passed === expected && value.failed === 0 && value.skipped === 0, 'Gate counts are incomplete.', { code: 'counts-incomplete', gate: files.qualificationGate, member: `${prefix}counts.json` });
  requireThat(json(files, `${prefix}failure-evidence.json`).failedCases === 0, 'Primary test failure present.', { code: 'test-failure', gate: files.qualificationGate, member: `${prefix}failure-evidence.json` });
  requireThat(files.has(`${prefix}results.trx`), 'Sanitized test results missing.', { code: 'missing-member', gate: files.qualificationGate, member: `${prefix}results.trx` });
}
function gate(files, prefix, identity, name, expected) {
  const value = json(files, `${prefix}gate.json`);
  requireThat(value.source === identity.merge && value.status === 'complete' && value.gate === name &&
    value.cases === expected.length && value.dirty !== true, 'Gate source/completeness mismatch.', { code: 'gate-incomplete', gate: name, member: `${prefix}gate.json` });
  inventory(expected, array(json(files, `${prefix}cases.json`)), { gate: name, member: `${prefix}cases.json` });
  return value;
}
function resources(value, member = 'resources.json') {
  requireThat(value && Number.isInteger(value.limit) && value.limit >= 2 && value.limit <= 8 &&
    value.active === 0 && value.cleanupFailed === 0 && value.readyActive === 0 && value.hostGroupsActive === 0 &&
    value.startupAttempts === value.cleaned && value.catalogDatabasesCreated === value.catalogDatabasesDropped &&
    value.peakReservedServers <= value.limit && value.peakReadyServers <= value.limit &&
    value.hostGroupLimit >= 1 && value.hostGroupLimit < value.limit && value.peakHostGroups <= value.hostGroupLimit, 'PostgreSQL cleanup/resource accounting incomplete.',
  { code: 'cleanup-incomplete', gate: 'postgresql-integration', member, cleanup: 'incomplete' });
}
function cleanup(text, identity, marker, gate = 'kubernetes-provider-access', member = 'evidence.txt') {
  const lines = text.toString().split(/\r?\n/);
  requireThat(lines.includes(`Source: ${identity.merge}`) && lines.includes('Dirty: False') &&
    lines.includes('Harness: PASS') && lines.includes('Run-owned private directory removed.') &&
    lines.filter(line => /^Run-owned container and [0-9]+ anonymous volumes absent\.$/.test(line)).length === 1 &&
    !lines.some(line => /^FAIL |^Failure:|cleanup: FAIL|stop: FAIL|Evidence write: FAIL/.test(line)), 'Kubernetes outcome/source/cleanup incomplete.',
  { code: 'kubernetes-evidence-incomplete', gate, member, cleanup: 'not-confirmed' });
  for (const item of marker) requireThat(lines.includes(item), 'Required Kubernetes lifecycle member missing.', { code: 'missing-lifecycle-member', gate, member: item, cleanup: 'not-confirmed' });
}

// Read only the compact, existing per-gate reports and the candidate's existing
// coverage plan. Never import, execute or print candidate artifacts.
export function validateEvidence(identity, plan, artifacts, attempt) {
  requireThat(Array.isArray(plan.entries) && plan.entries.length > 0, 'Coverage plan is missing.', { code: 'coverage-plan-incomplete', gate: 'control', member: 'suite-gate-map.json' });
  const names = artifactNames(identity, attempt);
  const gates = { fast: 'validate', windows: 'Native AOT (win-x64)', linux: 'Native AOT (linux-x64)', postgresql: 'postgresql-integration', composition: 'postgresql-integration', provider: 'kubernetes-provider-access', configured: 'kubernetes-configured-deployment', durable: 'kubernetes-durable-actions', kernel: 'kubernetes-deployment-kernel' };
  const files = key => {
    requireThat(artifacts.has(names[key]), 'Required qualification artifact is missing.', { code: 'missing-artifact', gate: gates[key], member: names[key] });
    const content = artifacts.get(names[key]); content.qualificationGate = gates[key]; return content;
  };
  const expected = (name, suite) => plan.entries.filter(row => row.gate === name && (!suite || row.suite === suite));
  const summary = [];
  for (const [key, name, prefix] of [['fast', 'validate', ''], ['windows', 'Native AOT (win-x64)', 'safe/'], ['linux', 'Native AOT (linux-x64)', 'safe/']]) {
    const rows = expected(name); const content = files(key);
    const value = gate(content, prefix, identity, name, rows);
    counts(content, prefix, rows.length);
    if (key !== 'fast') {
      requireThat(value.requiredAot === true && /^[0-9a-f]{64}$/.test(value.nativeSha256) && /^[0-9a-f]{64}$/.test(value.runtimeConsumerSha256), 'Published native evidence missing.',
        { code: 'published-native-incomplete', gate: name, member: `${prefix}gate.json` });
      const compiler = json(content, 'compiler.json');
      requireThat(compiler.source === identity.merge && compiler.rid === (key === 'windows' ? 'win-x64' : 'linux-x64') && compiler.packs?.length === 2, 'Native compiler identity missing.',
        { code: 'native-compiler-incomplete', gate: name, member: 'compiler.json' });
    }
    summary.push({ gate: name, cases: rows.length, status: 'complete' });
  }
  const pg = files('postgresql'); const rows = expected('postgresql-integration');
  inventory(rows, array(json(pg, 'cases.json')), { gate: 'postgresql-integration', member: 'cases.json' }); counts(pg, '', rows.length);
  const coverage = json(pg, 'coverage.json'); const measurement = json(pg, 'measurement.json');
  requireThat(coverage.status === 'complete' && coverage.selection === 'full' && coverage.discovered === rows.length && coverage.executed === rows.length, 'Focused or partial PostgreSQL evidence cannot qualify.',
    { code: 'coverage-incomplete', gate: 'postgresql-integration', member: 'coverage.json' });
  requireThat(measurement.source === identity.merge && measurement.sourceChangedDuringRun === false && measurement.untrackedFileCountAtStart === 0 &&
    measurement.requiredAot === true && measurement.testExitCode === 0 && measurement.reportingFailed === false && measurement.selection === 'full', 'PostgreSQL source or outcome incomplete.',
    { code: 'postgresql-execution-incomplete', gate: 'postgresql-integration', member: 'measurement.json', cleanup: measurement.preflightCleanupExitCode === 0 ? 'not-established' : 'incomplete' });
  requireThat(measurement.preflightCleanupExitCode === 0, 'PostgreSQL preflight cleanup incomplete.',
    { code: 'preflight-cleanup-incomplete', gate: 'postgresql-integration', member: 'measurement.json', cleanup: 'incomplete' });
  requireThat(measurement.groups?.length === 2 && ['functional', 'infrastructure-qualification'].every(suite => measurement.groups.filter(group => group.suite === suite && group.phase === 'complete' && group.testExitCode === 0).length === 1), 'Complete PostgreSQL suites missing.',
    { code: 'suite-coverage-incomplete', gate: 'postgresql-integration', member: 'measurement.json' });
  resources(json(pg, 'resources.json'));
  const preflight = json(pg, 'preflight.json');
  requireThat(['prerequisites', 'ownedServer'].every(key => preflight[key]?.exitCode === 0 && preflight[key]?.cleanup === 'ok'), 'PostgreSQL preflight/cleanup evidence incomplete.',
    { code: 'preflight-incomplete', gate: 'postgresql-integration', member: 'preflight.json', cleanup: ['prerequisites', 'ownedServer'].every(key => preflight[key]?.cleanup === 'ok') ? 'not-established' : 'incomplete' });
  for (const suite of ['functional', 'infrastructure-qualification']) {
    inventory(expected('postgresql-integration', suite), array(json(pg, `${suite}/cases.json`)), { gate: 'postgresql-integration', member: `${suite}/cases.json` });
    resources(json(pg, `${suite}/resources.json`), `${suite}/resources.json`);
    requireThat(json(pg, `${suite}/failure-evidence.json`).failedCases === 0, 'PostgreSQL suite failure present.',
      { code: 'test-failure', gate: 'postgresql-integration', member: `${suite}/failure-evidence.json` });
  }
  const precheck = json(pg, 'anonymous-reflection-precheck/precheck.json');
  requireThat(precheck.source === identity.merge && precheck.status === 'passed' && precheck.exitCode === 0 && precheck.testExitCode === 0 && precheck.reportingFailed === false && precheck.cleanup === 'confirmed-by-named-test-lifecycle', 'Named reflection precheck incomplete.',
    { code: 'precheck-incomplete', gate: 'postgresql-integration', member: 'anonymous-reflection-precheck/precheck.json', cleanup: precheck.cleanup === 'confirmed-by-named-test-lifecycle' ? 'not-established' : 'not-confirmed' });
  const composition = files('composition');
  requireThat(composition.has('Haby.Operator.Composition.Sample.dll') && createHash('sha256').update(composition.get('Haby.Operator.Composition.Sample.dll')).digest('hex') === measurement.compositionSampleSha256, 'Tested composition executable missing or different.',
    { code: 'composition-incomplete', gate: 'postgresql-integration', member: 'Haby.Operator.Composition.Sample.dll' });
  summary.push({ gate: 'postgresql-integration', cases: rows.length, status: 'complete', cleanup: 'confirmed' });
  for (const [key, name, markers] of [
    ['kernel', 'kubernetes-deployment-kernel', ['PASS kernel-cleanup-ownership', 'PASS kernel-namespace-absence-after-UID-delete']],
    ['durable', 'kubernetes-durable-actions', ['PASS durable-owned-catalog-cleanup']],
    ['configured', 'kubernetes-configured-deployment', ['Diagnostic workload: configured retirement DrainComplete', 'PASS configured-owned-namespace-absent', 'PASS configured-owned-catalog-cleanup']],
  ]) {
    const content = files(key); const members = expected(name);
    gate(content, '', identity, name, members);
    cleanup(content.get('evidence.txt') ?? '', identity, markers, name);
    summary.push({ gate: name, cases: members.length, status: 'complete', cleanup: 'confirmed' });
  }
  const provider = files('provider'); const members = expected('kubernetes-provider-access');
  gate(provider, 'provider-access/', identity, 'kubernetes-provider-access', members);
  const scenarios = ['success', 'stock', 'port-collision', 'readiness-failure'];
  for (const scenario of scenarios) {
    const member = `qualification-${scenario}.txt`;
    const matching = [...provider.keys()].filter(path => path.endsWith(`/qualification-${scenario}.txt`));
    requireThat(matching.length === 1, 'API rehearsal member missing or ambiguous.', { code: 'missing-rehearsal-member', gate: 'kubernetes-provider-access', member });
    const lines = provider.get(matching[0]).toString().split(/\r?\n/);
    requireThat(lines.includes(`Source: ${identity.merge}`) && lines.includes('Dirty: False') && lines.includes('Run-owned private directory removed.') &&
      lines.filter(line => /^Run-owned container and [0-9]+ anonymous volumes absent\.$/.test(line) || (scenario === 'port-collision' && line === 'Run-owned container absent after attempted launch.')).length === 1 &&
      !lines.some(line => /cleanup: FAIL|stop: FAIL|Evidence write: FAIL/.test(line)), 'API rehearsal cleanup/source incomplete.',
    { code: 'provider-evidence-incomplete', gate: 'kubernetes-provider-access', member, cleanup: 'not-confirmed' });
    if (scenario === 'success') cleanup(provider.get(matching[0]), identity, [], 'kubernetes-provider-access', member);
    else {
      const failure = scenario === 'stock' ? 'stage=harness; reason=nonzero' : scenario === 'port-collision' ? 'stage=launch; reason=nonzero' : 'stage=readiness; reason=deadline';
      requireThat(lines.filter(line => line.startsWith(`Failure: ${failure}; category=`)).length === 1, 'Expected failure rehearsal outcome missing.',
        { code: 'rehearsal-outcome-incomplete', gate: 'kubernetes-provider-access', member });
    }
  }
  summary.push({ gate: 'kubernetes-provider-access', cases: members.length, status: 'complete', cleanup: 'confirmed' });
  return summary;
}

export async function current(github, identity) {
  const [owner, repo] = identity.repository.split('/');
  const pr = (await github.rest.pulls.get({ owner, repo, pull_number: identity.pr })).data;
  const commit = (await github.rest.git.getCommit({ owner, repo, commit_sha: identity.merge })).data;
  const main = (await github.rest.repos.getBranch({ owner, repo, branch: 'main' })).data.commit?.sha;
  assertCurrent(identity, pr, commit, main);
  return { pr, commit, main };
}

export async function prepare(github, environment, input, actor, root = process.env.GITHUB_WORKSPACE) {
  const identity = validateRequest(environment, input);
  const [owner, repo] = identity.repository.split('/');
  const { pr, commit, main } = await current(github, identity);
  const permission = (await github.rest.repos.getCollaboratorPermissionLevel({ owner, repo, username: actor })).data.permission;
  assertCandidate(identity, pr, commit, permission, main);
  try {
    requireThat(typeof root === 'string' && isAbsolute(root) && git(root, 'rev-parse', 'HEAD').trim() === identity.control,
      'Installed control checkout required.');
    // Fetch Git objects only. Never check out or import candidate source in control.
    git(root, 'fetch', '--no-tags', '--no-recurse-submodules', 'origin', identity.base, identity.merge);
    return classifyCandidate(root, identity);
  } catch {
    // Git/doc diagnostics can contain untrusted paths or Markdown content.
    throw new QualificationFailure('Trusted classification unavailable or failed.',
      { code: 'classification-failed', stage: 'classification', gate: 'control', member: 'base-to-tested-merge' });
  }
}

export function readArtifacts(directory, names) {
  const result = new Map();
  for (const name of Object.values(names)) {
    const root = resolve(directory, name); const files = new Map();
    requireThat(lstatSync(root).isDirectory() && !lstatSync(root).isSymbolicLink(), 'Artifact directory missing.');
    const walk = path => {
      for (const entry of readdirSync(path, { withFileTypes: true })) {
        const file = join(path, entry.name); const stat = lstatSync(file);
        const rel = relative(root, realpathSync(file));
        requireThat(!stat.isSymbolicLink() && rel !== '..' && !rel.startsWith(`..${process.platform === 'win32' ? '\\' : '/'}`) && !isAbsolute(rel), 'Unsafe artifact path.');
        if (stat.isDirectory()) walk(file);
        else { requireThat(stat.isFile() && stat.size <= 16 * 1024 * 1024, 'Artifact member exceeds bound.'); files.set(relative(root, file).replaceAll('\\', '/'), readFileSync(file)); }
      }
    };
    walk(root); result.set(name, files);
  }
  return result;
}

export async function reduce(github, identity, results, directory, runId, attempt, summaryPath = 'qualification-summary.json') {
  const [owner, repo] = identity.repository.split('/');
  let verified = false; let report = []; let failure = null; let stage = 'classification'; let cleanupState = 'not-established';
  let scope = 'unknown'; let runtimeExecuted = null;
  try {
    assertClassification(identity);
    stage = 'workflow-results';
    if (identity.scope === 'full') assertResults(results);
    stage = 'job-results';
    const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRunAttempt, { owner, repo, run_id: runId, attempt_number: attempt, per_page: 100 });
    if (identity.scope === 'docs-only') {
      assertDocsResults(results, jobs);
      scope = 'docs-only'; runtimeExecuted = false; cleanupState = 'not-applicable';
      report = [{ gate: 'documentation', status: 'complete', changedEntries: identity.classification.changedEntries, checks: docsChecks }];
    } else {
      assertJobs(jobs);
      stage = 'artifacts';
      const actualArtifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, { owner, repo, run_id: runId, per_page: 100 });
      for (const [key, name] of Object.entries(artifactNames(identity, attempt))) requireThat(actualArtifacts.filter(item => item.name === name && !item.expired && item.size_in_bytes > 0).length === 1, 'Run artifact missing, expired or ambiguous.',
        { code: 'missing-artifact', gate: key, member: name });
      stage = 'coverage-plan';
      const planResponse = await github.request('GET /repos/{owner}/{repo}/contents/{path}', { owner, repo, path: 'tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json', ref: identity.merge, headers: { accept: 'application/vnd.github.raw+json' } });
      const plan = typeof planResponse.data === 'string' ? JSON.parse(planResponse.data) : planResponse.data;
      requireThat(plan && Buffer.byteLength(JSON.stringify(plan)) <= 4 * 1024 * 1024, 'Coverage plan unavailable or exceeds bound.');
      stage = 'evidence';
      report = validateEvidence(identity, plan, readArtifacts(directory, artifactNames(identity, attempt)), attempt);
      cleanupState = 'confirmed-by-required-gates';
      scope = 'full'; runtimeExecuted = true;
    }
    stage = 'freshness';
    await current(github, identity);
    verified = true;
  } catch (error) {
    // Never echo API/parser messages or artifact data, which may contain secrets.
    failure = boundedFailure(error, stage);
    if (failure.cleanup === 'not-established') failure.cleanup = cleanupState;
  }
  const reduction = { ...identity, requestedScope: identity.scope ?? null, scope, runtimeExecuted, runId, attempt, outcome: verified ? 'verified' : 'failure', gates: report,
    primary: failure?.primary ?? null, cleanup: failure?.cleanup ?? cleanupState };
  writeFileSync(summaryPath, JSON.stringify(reduction, null, 2) + '\n');
  return reduction;
}

export async function publish(github, identity, runId, attempt, checkId, summaryPath, publication, timing = artifactTiming) {
  const [owner, repo] = identity.repository.split('/');
  let conclusion = 'failure'; let summary = 'Qualification failed, is stale or has incomplete publication.'; let failure = null; let stage = 'control-summary'; let cleanupState = 'not-established';
  try {
    assertClassification(identity);
    const reduction = JSON.parse(readFileSync(summaryPath, 'utf8'));
    requireThat(reduction.runId === runId && reduction.attempt === attempt &&
      ['repository', 'control', 'pr', 'head', 'base', 'merge', 'tree'].every(key => reduction[key] === identity[key]) &&
      reduction.requestedScope === identity.scope, 'Reduction is incomplete or belongs to another candidate.');
    if (identity.scope === 'docs-only') requireThat(['not-applicable', 'not-established'].includes(reduction.cleanup),
      'Docs cleanup cannot claim runtime proof.', { code: 'scope-mismatch', gate: 'control', member: 'cleanup' });
    if (reduction.cleanup === (identity.scope === 'docs-only' ? 'not-applicable' : 'confirmed-by-required-gates')) cleanupState = reduction.cleanup;
    if (reduction.outcome !== 'verified' && reduction.primary) failure = { primary: reduction.primary, cleanup: reduction.cleanup };
    requireThat(reduction.outcome === 'verified', 'Reduction failed.', { code: 'reduction-incomplete', member: 'qualification-summary.json' });
    assertClassification(reduction);
    requireThat(reduction.scope === identity.scope && ['before', 'after', 'tree', 'route', 'changedEntries', 'documentation']
      .every(key => reduction.classification[key] === identity.classification[key]) &&
      reduction.runtimeExecuted === (identity.scope === 'full') &&
      reduction.cleanup === (identity.scope === 'full' ? 'confirmed-by-required-gates' : 'not-applicable'),
    'Reduction scope or execution attestation mismatch.', { code: 'scope-mismatch', gate: 'control', member: 'qualification-summary.json' });
    if (identity.scope === 'docs-only') requireThat(reduction.gates?.length === 1 && reduction.gates[0].gate === 'documentation' &&
      reduction.gates[0].status === 'complete' && reduction.gates[0].changedEntries === identity.classification.changedEntries &&
      JSON.stringify(reduction.gates[0].checks) === JSON.stringify(docsChecks) && !('cases' in reduction.gates[0]),
    'Docs attestation incomplete.', { code: 'scope-mismatch', gate: 'control', member: 'documentation-checks' });
    stage = 'publication';
    requireThat(publication?.reduction === 'success' && publication?.upload === 'success' && typeof publication?.artifact === 'string' && /^[1-9][0-9]*$/.test(publication.artifact) && Number.isSafeInteger(Number(publication.artifact)), 'Required control artifact publication failed or was canceled.',
      { code: 'control-upload-incomplete', gate: 'control', member: 'qualification-summary.json', outcome: outcome(publication?.reduction !== 'success' ? publication?.reduction : publication?.upload) });
    stage = 'control-artifact';
    const { response, diagnostic } = await readControlArtifact(github, owner, repo, Number(publication.artifact),
      `qualification-control-${runId}-${attempt}`, timing);
    const artifact = response?.data;
    requireThat(artifact && typeof artifact === 'object' && !Array.isArray(artifact) && artifact.id === Number(publication.artifact) && artifact.name === `qualification-control-${runId}-${attempt}` &&
      artifact.workflow_run?.id === runId && artifact.expired === false && artifact.size_in_bytes > 0, 'Required control artifact is missing or belongs to another invocation.',
    { code: 'control-artifact-incomplete', gate: 'control', member: `qualification-control-${runId}-${attempt}` });
    stage = 'freshness';
    await current(github, identity);
    conclusion = 'success'; summary = identity.scope === 'docs-only'
      ? 'Documentation-only qualification and required control publication passed on the current exact HEAD/base/tested merge tree. runtimeExecuted=false; cleanup=not-applicable; no runtime coverage credit. Pilot maintainer trust applies; this is not architect merge approval.'
      : 'Complete existing qualification and required control publication passed on the current exact HEAD/base/tested merge tree. Pilot maintainer trust applies; this is not architect merge approval.';
    summary += ' Control artifact read: ' + JSON.stringify(diagnostic);
  } catch (error) {
    // Completion never grants green credit for reduction/upload/freshness failure.
    failure ??= boundedFailure(error, stage);
    if (failure.cleanup === 'not-established') failure.cleanup = cleanupState;
    summary = 'Qualification did not pass: ' + JSON.stringify(failure);
  }
  const existing = (await github.rest.checks.get({ owner, repo, check_run_id: Number(checkId) })).data;
  requireThat(existing.head_sha === identity.head && existing.name === 'Final qualification' && existing.external_id === `${runId}:${attempt}`, 'Qualification check identity mismatch.');
  await github.rest.checks.update({ owner, repo, check_run_id: Number(checkId), status: 'completed', conclusion,
    completed_at: new Date().toISOString(), output: { title: 'Exact candidate qualification', summary },
    details_url: `https://github.com/${identity.repository}/actions/runs/${runId}/attempts/${attempt}` });
  requireThat(conclusion === 'success', 'Final qualification did not pass.');
}
