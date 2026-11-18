import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, lstatSync, realpathSync, writeFileSync } from 'node:fs';
import { resolve, relative, isAbsolute, join } from 'node:path';

const sha = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value) && !/^0+$/.test(value);
const requireThat = (value, message) => { if (!value) throw new Error(message); };

export function validateRequest(environment, input) {
  requireThat(environment.event === 'workflow_dispatch' && environment.ref === 'refs/heads/main', 'Installed main dispatch required.');
  requireThat(environment.workflowRef === `${environment.repository}/.github/workflows/final-qualification.yml@refs/heads/main`, 'Unexpected control workflow.');
  requireThat(/^[1-9][0-9]{0,8}$/.test(input.pr ?? ''), 'Numeric PR required.');
  for (const key of ['head', 'base', 'merge', 'tree']) requireThat(sha(input[key]), 'Immutable expected identities required.');
  requireThat(sha(environment.control) && input.base === environment.control, 'Control must be installed at the expected main base.');
  return { repository: environment.repository, control: environment.control, pr: Number(input.pr), head: input.head, base: input.base, merge: input.merge, tree: input.tree };
}

export function assertCurrent(identity, pr, commit) {
  requireThat(pr.state === 'open' && pr.number === identity.pr && pr.head?.sha === identity.head &&
    pr.base?.sha === identity.base && pr.base?.ref === 'main' && pr.base?.repo?.full_name === identity.repository &&
    pr.merge_commit_sha === identity.merge, 'Candidate is stale or outside the expected PR/base.');
  requireThat(commit.sha === identity.merge && commit.tree?.sha === identity.tree && commit.parents?.length === 2 &&
    commit.parents[0].sha === identity.base && commit.parents[1].sha === identity.head, 'Tested merge/tree identity mismatch.');
}

export function assertCandidate(identity, pr, commit, permission) {
  requireThat(['write', 'maintain', 'admin'].includes(permission), 'Maintainer dispatch permission required.');
  assertCurrent(identity, pr, commit);
}

export function assertResults(results) {
  requireThat(['build', 'api', 'workloads'].every(name => results[name] === 'success'), 'Full qualification jobs failed, were canceled, skipped or are missing.');
}

export function assertJobs(jobs) {
  for (const name of ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
    'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel']) {
    requireThat(jobs.filter(job => job.name === name && job.status === 'completed' && job.conclusion === 'success').length === 1, 'Required job is missing, canceled, skipped or failed.');
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
  requireThat(files.has(path), 'Required evidence member is missing.');
  const text = files.get(path);
  requireThat(Buffer.byteLength(text) <= 8 * 1024 * 1024, 'Evidence member exceeds bound.');
  return JSON.parse(text.toString());
}
const array = value => Array.isArray(value) ? value : [value];
function inventory(expected, actual) {
  requireThat(expected.length > 0 && actual.length === expected.length, 'Incomplete gate coverage.');
  const keys = rows => rows.map(row => `${row.method}#${row.case}`).sort();
  requireThat(JSON.stringify(keys(expected)) === JSON.stringify(keys(actual)) && actual.every(row => row.outcome === 'Passed'), 'Failed, skipped or different gate cases.');
}
function counts(files, prefix, expected) {
  const value = json(files, `${prefix}counts.json`);
  requireThat(value.total === expected && value.passed === expected && value.failed === 0 && value.skipped === 0, 'Gate counts are incomplete.');
  requireThat(json(files, `${prefix}failure-evidence.json`).failedCases === 0, 'Primary test failure present.');
  requireThat(files.has(`${prefix}results.trx`), 'Sanitized test results missing.');
}
function gate(files, prefix, identity, name, expected) {
  const value = json(files, `${prefix}gate.json`);
  requireThat(value.source === identity.merge && value.status === 'complete' && value.gate === name &&
    value.cases === expected.length && value.dirty !== true, 'Gate source/completeness mismatch.');
  inventory(expected, array(json(files, `${prefix}cases.json`)));
  return value;
}
function resources(value) {
  requireThat(value && Number.isInteger(value.limit) && value.limit >= 2 && value.limit <= 8 &&
    value.active === 0 && value.cleanupFailed === 0 && value.readyActive === 0 && value.hostGroupsActive === 0 &&
    value.startupAttempts === value.cleaned && value.catalogDatabasesCreated === value.catalogDatabasesDropped &&
    value.peakReservedServers <= value.limit && value.peakReadyServers <= value.limit &&
    value.hostGroupLimit >= 1 && value.hostGroupLimit < value.limit && value.peakHostGroups <= value.hostGroupLimit, 'PostgreSQL cleanup/resource accounting incomplete.');
}
function cleanup(text, identity, marker) {
  const lines = text.toString().split(/\r?\n/);
  requireThat(lines.includes(`Source: ${identity.merge}`) && lines.includes('Dirty: False') &&
    lines.includes('Harness: PASS') && lines.includes('Run-owned private directory removed.') &&
    lines.filter(line => /^Run-owned container and [0-9]+ anonymous volumes absent\.$/.test(line)).length === 1 &&
    !lines.some(line => /^FAIL |^Failure:|cleanup: FAIL|stop: FAIL|Evidence write: FAIL/.test(line)), 'Kubernetes outcome/source/cleanup incomplete.');
  for (const item of marker) requireThat(lines.includes(item), 'Required Kubernetes lifecycle member missing.');
}

// Read only the compact, existing per-gate reports and the candidate's existing
// coverage plan. Never import, execute or print candidate artifacts.
export function validateEvidence(identity, plan, artifacts, attempt) {
  requireThat(Array.isArray(plan.entries) && plan.entries.length > 0, 'Coverage plan is missing.');
  const names = artifactNames(identity, attempt);
  const files = key => { requireThat(artifacts.has(names[key]), 'Required qualification artifact is missing.'); return artifacts.get(names[key]); };
  const expected = (name, suite) => plan.entries.filter(row => row.gate === name && (!suite || row.suite === suite));
  const summary = [];
  for (const [key, name, prefix] of [['fast', 'validate', ''], ['windows', 'Native AOT (win-x64)', 'safe/'], ['linux', 'Native AOT (linux-x64)', 'safe/']]) {
    const rows = expected(name); const content = files(key);
    const value = gate(content, prefix, identity, name, rows);
    counts(content, prefix, rows.length);
    if (key !== 'fast') {
      requireThat(value.requiredAot === true && /^[0-9a-f]{64}$/.test(value.nativeSha256) && /^[0-9a-f]{64}$/.test(value.runtimeConsumerSha256), 'Published native evidence missing.');
      const compiler = json(content, 'compiler.json');
      requireThat(compiler.source === identity.merge && compiler.rid === (key === 'windows' ? 'win-x64' : 'linux-x64') && compiler.packs?.length === 2, 'Native compiler identity missing.');
    }
    summary.push({ gate: name, cases: rows.length, status: 'complete' });
  }
  const pg = files('postgresql'); const rows = expected('postgresql-integration');
  inventory(rows, array(json(pg, 'safe/cases.json'))); counts(pg, 'safe/', rows.length);
  const coverage = json(pg, 'safe/coverage.json'); const measurement = json(pg, 'safe/measurement.json');
  requireThat(coverage.status === 'complete' && coverage.selection === 'full' && coverage.discovered === rows.length && coverage.executed === rows.length, 'Focused or partial PostgreSQL evidence cannot qualify.');
  requireThat(measurement.source === identity.merge && measurement.sourceChangedDuringRun === false && measurement.untrackedFileCountAtStart === 0 &&
    measurement.requiredAot === true && measurement.testExitCode === 0 && measurement.reportingFailed === false && measurement.preflightCleanupExitCode === 0 && measurement.selection === 'full', 'PostgreSQL source, outcome or cleanup incomplete.');
  requireThat(measurement.groups?.length === 2 && ['functional', 'infrastructure-qualification'].every(suite => measurement.groups.filter(group => group.suite === suite && group.phase === 'complete' && group.testExitCode === 0).length === 1), 'Complete PostgreSQL suites missing.');
  resources(json(pg, 'safe/resources.json'));
  const preflight = json(pg, 'safe/preflight.json');
  requireThat(['prerequisites', 'ownedServer'].every(key => preflight[key]?.exitCode === 0 && preflight[key]?.cleanup === 'ok'), 'PostgreSQL preflight/cleanup evidence incomplete.');
  for (const suite of ['functional', 'infrastructure-qualification']) {
    inventory(expected('postgresql-integration', suite), array(json(pg, `safe/${suite}/cases.json`)));
    resources(json(pg, `safe/${suite}/resources.json`));
    requireThat(json(pg, `safe/${suite}/failure-evidence.json`).failedCases === 0, 'PostgreSQL suite failure present.');
  }
  const precheck = json(pg, 'safe/anonymous-reflection-precheck/precheck.json');
  requireThat(precheck.source === identity.merge && precheck.status === 'passed' && precheck.exitCode === 0 && precheck.testExitCode === 0 && precheck.reportingFailed === false && precheck.cleanup === 'confirmed-by-named-test-lifecycle', 'Named reflection precheck incomplete.');
  const composition = files('composition');
  requireThat(composition.has('Haby.Operator.Composition.Sample.dll') && createHash('sha256').update(composition.get('Haby.Operator.Composition.Sample.dll')).digest('hex') === measurement.compositionSampleSha256, 'Tested composition executable missing or different.');
  summary.push({ gate: 'postgresql-integration', cases: rows.length, status: 'complete', cleanup: 'confirmed' });
  for (const [key, name, markers] of [
    ['kernel', 'kubernetes-deployment-kernel', ['PASS kernel-cleanup-ownership', 'PASS kernel-namespace-absence-after-UID-delete']],
    ['durable', 'kubernetes-durable-actions', ['PASS durable-owned-catalog-cleanup']],
    ['configured', 'kubernetes-configured-deployment', ['Diagnostic workload: configured retirement DrainComplete', 'PASS configured-owned-namespace-absent', 'PASS configured-owned-catalog-cleanup']],
  ]) {
    const content = files(key); const members = expected(name);
    gate(content, '', identity, name, members);
    cleanup(content.get('evidence.txt') ?? '', identity, markers);
    summary.push({ gate: name, cases: members.length, status: 'complete', cleanup: 'confirmed' });
  }
  const provider = files('provider'); const members = expected('kubernetes-provider-access');
  gate(provider, 'provider-access/', identity, 'kubernetes-provider-access', members);
  const scenarios = ['success', 'stock', 'port-collision', 'readiness-failure'];
  for (const scenario of scenarios) {
    const matching = [...provider.keys()].filter(path => path.endsWith(`/qualification-${scenario}.txt`));
    requireThat(matching.length === 1, 'API rehearsal member missing or ambiguous.');
    const lines = provider.get(matching[0]).toString().split(/\r?\n/);
    requireThat(lines.includes(`Source: ${identity.merge}`) && lines.includes('Dirty: False') && lines.includes('Run-owned private directory removed.') &&
      lines.filter(line => /^Run-owned container and [0-9]+ anonymous volumes absent\.$/.test(line) || (scenario === 'port-collision' && line === 'Run-owned container absent after attempted launch.')).length === 1 &&
      !lines.some(line => /cleanup: FAIL|stop: FAIL|Evidence write: FAIL/.test(line)), 'API rehearsal cleanup/source incomplete.');
    if (scenario === 'success') cleanup(provider.get(matching[0]), identity, []);
    else {
      const failure = scenario === 'stock' ? 'stage=harness; reason=nonzero' : scenario === 'port-collision' ? 'stage=launch; reason=nonzero' : 'stage=readiness; reason=deadline';
      requireThat(lines.filter(line => line.startsWith(`Failure: ${failure}; category=`)).length === 1, 'Expected failure rehearsal outcome missing.');
    }
  }
  summary.push({ gate: 'kubernetes-provider-access', cases: members.length, status: 'complete', cleanup: 'confirmed' });
  return summary;
}

export async function current(github, identity) {
  const [owner, repo] = identity.repository.split('/');
  const pr = (await github.rest.pulls.get({ owner, repo, pull_number: identity.pr })).data;
  const commit = (await github.rest.git.getCommit({ owner, repo, commit_sha: identity.merge })).data;
  assertCurrent(identity, pr, commit);
  return { pr, commit };
}

export async function prepare(github, environment, input, actor) {
  const identity = validateRequest(environment, input);
  const [owner, repo] = identity.repository.split('/');
  const { pr, commit } = await current(github, identity);
  const permission = (await github.rest.repos.getCollaboratorPermissionLevel({ owner, repo, username: actor })).data.permission;
  assertCandidate(identity, pr, commit, permission);
  requireThat((await github.rest.repos.getBranch({ owner, repo, branch: 'main' })).data.commit.sha === identity.control, 'Installed main control moved.');
  return identity;
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

export async function publish(github, identity, results, directory, runId, attempt, checkId, summaryPath = 'qualification-summary.json') {
  const [owner, repo] = identity.repository.split('/');
  let conclusion = 'failure'; let summary = 'Qualification failed, is stale or has incomplete evidence.'; let report = [];
  try {
    assertResults(results);
    const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRunAttempt, { owner, repo, run_id: runId, attempt_number: attempt, per_page: 100 });
    assertJobs(jobs);
    const actualArtifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, { owner, repo, run_id: runId, per_page: 100 });
    for (const name of Object.values(artifactNames(identity, attempt))) requireThat(actualArtifacts.filter(item => item.name === name && !item.expired && item.size_in_bytes > 0).length === 1, 'Run artifact missing, expired or ambiguous.');
    const planResponse = await github.request('GET /repos/{owner}/{repo}/contents/{path}', { owner, repo, path: 'tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json', ref: identity.merge, headers: { accept: 'application/vnd.github.raw+json' } });
    const plan = typeof planResponse.data === 'string' ? JSON.parse(planResponse.data) : planResponse.data;
    requireThat(plan && Buffer.byteLength(JSON.stringify(plan)) <= 4 * 1024 * 1024, 'Coverage plan unavailable or exceeds bound.');
    report = validateEvidence(identity, plan, readArtifacts(directory, artifactNames(identity, attempt)), attempt);
    await current(github, identity);
    conclusion = 'success'; summary = 'Complete existing qualification passed on the current exact HEAD/base/tested merge tree. Pilot maintainer trust applies; this is not architect merge approval.';
  } catch {
    // Never echo API/parser messages or artifact data, which may contain secrets.
  }
  writeFileSync(summaryPath, JSON.stringify({ ...identity, runId, attempt, conclusion, gates: report }, null, 2) + '\n');
  const existing = (await github.rest.checks.get({ owner, repo, check_run_id: Number(checkId) })).data;
  requireThat(existing.head_sha === identity.head && existing.name === 'Final qualification' && existing.external_id === `${runId}:${attempt}`, 'Qualification check identity mismatch.');
  await github.rest.checks.update({ owner, repo, check_run_id: Number(checkId), status: 'completed', conclusion,
    completed_at: new Date().toISOString(), output: { title: 'Exact candidate qualification', summary },
    details_url: `https://github.com/${identity.repository}/actions/runs/${runId}/attempts/${attempt}` });
  requireThat(conclusion === 'success', 'Final qualification did not pass.');
}
