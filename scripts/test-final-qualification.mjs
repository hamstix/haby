import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { validateRequest, assertCandidate, assertCurrent, assertResults, assertJobs, artifactNames, validateEvidence, publish } from './final-qualification.mjs';

const sha = c => c.repeat(40);
const request = { pr: '17', head: sha('a'), base: sha('b'), merge: sha('c'), tree: sha('d') };
const environment = { repository: 'example/project', ref: 'refs/heads/main', workflowRef: 'example/project/.github/workflows/final-qualification.yml@refs/heads/main', control: sha('b'), event: 'workflow_dispatch' };
const pr = { state: 'open', number: 17, head: { sha: request.head }, base: { sha: request.base, ref: 'main', repo: { full_name: environment.repository } }, merge_commit_sha: request.merge };
const commit = { sha: request.merge, tree: { sha: request.tree }, parents: [{ sha: request.base }, { sha: request.head }] };

test('installed main control validates immutable candidate identities', () => {
  const valid = validateRequest(environment, request);
  assertCandidate(valid, pr, commit, 'write');
  assertCurrent(valid, pr, commit);
});
test('untrusted event/ref/control, shell input and malformed SHA fail closed', () => {
  for (const delta of [{ event: 'pull_request' }, { ref: 'refs/heads/topic' }, { control: sha('e') }, { workflowRef: 'example/project/.github/workflows/final-qualification.yml@refs/heads/topic' }]) {
    assert.throws(() => validateRequest({ ...environment, ...delta }, request));
  }
  for (const delta of [{ pr: '17;echo injected' }, { pr: '0' }, { head: 'main' }, { base: sha('B') }, { merge: '$()' }, { tree: '' }]) {
    assert.throws(() => validateRequest(environment, { ...request, ...delta }));
  }
  for (const permission of ['read', 'triage', '', undefined]) assert.throws(() => assertCandidate(validateRequest(environment, request), pr, commit, permission));
});
test('new HEAD/base/merge tree, closed PR and wrong repository are stale', () => {
  const valid = validateRequest(environment, request);
  for (const delta of [{ head: { sha: sha('e') } }, { base: { ...pr.base, sha: sha('e') } }, { merge_commit_sha: sha('e') }, { state: 'closed' }, { base: { ...pr.base, repo: { full_name: 'other/repository' } } }]) {
    assert.throws(() => assertCurrent(valid, { ...pr, ...delta }, commit));
  }
  for (const delta of [{ tree: { sha: sha('e') } }, { parents: [{ sha: request.head }, { sha: request.base }] }, { parents: [] }]) {
    assert.throws(() => assertCurrent(valid, pr, { ...commit, ...delta }));
  }
});
test('failed, canceled, skipped, missing and unknown gate results cannot pass', () => {
  assertResults({ build: 'success', api: 'success', workloads: 'success' });
  for (const result of ['failure', 'cancelled', 'skipped', undefined, 'neutral']) {
    assert.throws(() => assertResults({ build: 'success', api: result, workloads: 'success' }));
  }
  assert.throws(() => assertResults({ build: 'success', api: 'success' }));
});
test('missing artifacts and empty coverage cannot qualify', () => {
  assert.throws(() => validateEvidence(validateRequest(environment, request), { entries: [] }, new Map(), 1));
});

function fixture() {
  const identity = validateRequest(environment, request);
  const names = artifactNames(identity, 1);
  const artifacts = new Map(Object.values(names).map(name => [name, new Map()]));
  const plan = { entries: [] };
  const put = (key, path, value) => artifacts.get(names[key]).set(path, Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)));
  const member = (name, suite) => {
    const row = { gate: name, suite, method: `Example.${name}`, case: name };
    plan.entries.push(row); return row;
  };
  const report = (key, name, prefix, suite) => {
    const row = member(name, suite);
    put(key, prefix + 'gate.json', { source: identity.merge, status: 'complete', gate: name, cases: 1, dirty: false,
      requiredAot: key !== 'fast', nativeSha256: 'a'.repeat(64), runtimeConsumerSha256: 'b'.repeat(64) });
    put(key, prefix + 'cases.json', [{ ...row, outcome: 'Passed' }]);
    put(key, prefix + 'counts.json', { total: 1, passed: 1, failed: 0, skipped: 0 });
    put(key, prefix + 'failure-evidence.json', { failedCases: 0 });
    put(key, prefix + 'results.trx', '<sanitized/>');
  };
  report('fast', 'validate', '', 'fast');
  report('windows', 'Native AOT (win-x64)', 'safe/', 'published-native');
  report('linux', 'Native AOT (linux-x64)', 'safe/', 'published-native');
  for (const [key, rid] of [['windows', 'win-x64'], ['linux', 'linux-x64']]) put(key, 'compiler.json', { source: identity.merge, rid, packs: [{}, {}] });
  const owned = { limit: 4, active: 0, cleanupFailed: 0, readyActive: 0, hostGroupsActive: 0, startupAttempts: 1, cleaned: 1,
    catalogDatabasesCreated: 1, catalogDatabasesDropped: 1, peakReservedServers: 1, peakReadyServers: 1, hostGroupLimit: 2, peakHostGroups: 1 };
  const pgRows = [];
  for (const suite of ['functional', 'infrastructure-qualification']) {
    const row = member('postgresql-integration', suite); row.method += suite; row.case += suite;
    pgRows.push({ ...row, outcome: 'Passed' });
    put('postgresql', `safe/${suite}/cases.json`, [{ ...row, outcome: 'Passed' }]);
    put('postgresql', `safe/${suite}/resources.json`, owned);
    put('postgresql', `safe/${suite}/failure-evidence.json`, { failedCases: 0 });
  }
  put('postgresql', 'safe/cases.json', pgRows);
  put('postgresql', 'safe/counts.json', { total: 2, passed: 2, failed: 0, skipped: 0 });
  put('postgresql', 'safe/failure-evidence.json', { failedCases: 0 });
  put('postgresql', 'safe/results.trx', '<sanitized/>');
  put('postgresql', 'safe/coverage.json', { status: 'complete', selection: 'full', discovered: 2, executed: 2 });
  put('postgresql', 'safe/resources.json', owned);
  put('postgresql', 'safe/preflight.json', { prerequisites: { exitCode: 0, cleanup: 'ok' }, ownedServer: { exitCode: 0, cleanup: 'ok' } });
  put('composition', 'Haby.Operator.Composition.Sample.dll', 'example-executable-bytes');
  put('postgresql', 'safe/measurement.json', { source: identity.merge, sourceChangedDuringRun: false, untrackedFileCountAtStart: 0,
    requiredAot: true, testExitCode: 0, reportingFailed: false, preflightCleanupExitCode: 0, selection: 'full',
    compositionSampleSha256: createHash('sha256').update('example-executable-bytes').digest('hex'),
    groups: ['functional', 'infrastructure-qualification'].map(suite => ({ suite, phase: 'complete', testExitCode: 0 })) });
  put('postgresql', 'safe/anonymous-reflection-precheck/precheck.json', { source: identity.merge, status: 'passed', exitCode: 0,
    testExitCode: 0, reportingFailed: false, cleanup: 'confirmed-by-named-test-lifecycle' });
  const common = `Source: ${identity.merge}\nDirty: False\nRun-owned private directory removed.\nRun-owned container and 4 anonymous volumes absent.\n`;
  for (const [key, name, markers] of [
    ['kernel', 'kubernetes-deployment-kernel', 'PASS kernel-cleanup-ownership\nPASS kernel-namespace-absence-after-UID-delete'],
    ['durable', 'kubernetes-durable-actions', 'PASS durable-owned-catalog-cleanup'],
    ['configured', 'kubernetes-configured-deployment', 'Diagnostic workload: configured retirement DrainComplete\nPASS configured-owned-namespace-absent\nPASS configured-owned-catalog-cleanup'],
  ]) {
    report(key, name, '', 'example'); put(key, 'evidence.txt', common + 'Harness: PASS\n' + markers);
  }
  report('provider', 'kubernetes-provider-access', 'provider-access/', 'api-authentication');
  for (const [scenario, outcome] of [['success', 'Harness: PASS'], ['stock', 'Failure: stage=harness; reason=nonzero; category=RuntimeException'],
    ['port-collision', 'Failure: stage=launch; reason=nonzero; category=RuntimeException'], ['readiness-failure', 'Failure: stage=readiness; reason=deadline; category=TimeoutException']]) {
    put('provider', `qualification-evidence/control/qualification-${scenario}.txt`, common + outcome);
  }
  return { identity, plan, artifacts, names, put };
}

test('complete separate gate outcomes reduce to compact coverage/cleanup summary', () => {
  const f = fixture();
  assert.equal(validateEvidence(f.identity, f.plan, f.artifacts, 1).length, 8);
});
test('each artifact and required member is independently mandatory', () => {
  const good = fixture();
  for (const name of good.artifacts.keys()) {
    const bad = new Map(good.artifacts); bad.delete(name);
    assert.throws(() => validateEvidence(good.identity, good.plan, bad, 1), name);
  }
  for (const [key, path] of [['fast', 'results.trx'], ['linux', 'compiler.json'], ['postgresql', 'safe/resources.json'],
    ['postgresql', 'safe/infrastructure-qualification/cases.json'], ['postgresql', 'safe/preflight.json'], ['provider', 'qualification-evidence/control/qualification-readiness-failure.txt'], ['configured', 'evidence.txt']]) {
    const f = fixture(); f.artifacts.get(f.names[key]).delete(path);
    assert.throws(() => validateEvidence(f.identity, f.plan, f.artifacts, 1), path);
  }
});
test('focused/stale/failed/skipped/incomplete coverage and cleanup evidence fail closed', () => {
  for (const [key, path, delta] of [
    ['fast', 'gate.json', { source: sha('e') }], ['fast', 'gate.json', { dirty: true }],
    ['windows', 'safe/gate.json', { requiredAot: false }],
    ['postgresql', 'safe/coverage.json', { status: 'focused' }], ['postgresql', 'safe/coverage.json', { executed: 1 }],
    ['postgresql', 'safe/measurement.json', { testExitCode: 130 }], ['postgresql', 'safe/measurement.json', { reportingFailed: true }],
    ['postgresql', 'safe/measurement.json', { groups: [{ suite: 'functional', phase: 'complete', testExitCode: 0 }] }],
    ['postgresql', 'safe/resources.json', { cleanupFailed: 1 }], ['postgresql', 'safe/resources.json', { catalogDatabasesDropped: 0 }],
    ['postgresql', 'safe/anonymous-reflection-precheck/precheck.json', { cleanup: 'not-confirmed' }],
  ]) {
    const f = fixture(); const value = JSON.parse(f.artifacts.get(f.names[key]).get(path));
    f.put(key, path, { ...value, ...delta });
    assert.throws(() => validateEvidence(f.identity, f.plan, f.artifacts, 1), `${path}:${JSON.stringify(delta)}`);
  }
  for (const outcome of ['Failed', 'NotExecuted', 'Skipped']) {
    const f = fixture(); const path = 'safe/cases.json'; const value = JSON.parse(f.artifacts.get(f.names.linux).get(path)); value[0].outcome = outcome;
    f.put('linux', path, value); assert.throws(() => validateEvidence(f.identity, f.plan, f.artifacts, 1));
  }
  for (const change of ['Container/storage cleanup: FAIL', 'Failure: stage=harness; reason=nonzero; category=RuntimeException']) {
    const f = fixture(); const text = f.artifacts.get(f.names.configured).get('evidence.txt').toString();
    f.put('configured', 'evidence.txt', text + '\n' + change);
    assert.throws(() => validateEvidence(f.identity, f.plan, f.artifacts, 1));
  }
});
test('successful aggregate cannot hide missing or skipped actual jobs', () => {
  const names = ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
    'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel'];
  const jobs = names.map(name => ({ name, status: 'completed', conclusion: 'success' }));
  assertJobs(jobs);
  for (const name of names) {
    assert.throws(() => assertJobs(jobs.filter(job => job.name !== name)));
    assert.throws(() => assertJobs(jobs.map(job => job.name === name ? { ...job, conclusion: 'skipped' } : job)));
  }
});
test('workflow isolates write permission and never executes candidate in publisher', () => {
  const workflow = readFileSync(new URL('../.github/workflows/final-qualification.yml', import.meta.url), 'utf8');
  assert.doesNotMatch(workflow, /pull_request_target:|secrets: inherit|statuses: write|actions: write/);
  assert.equal((workflow.match(/checks: write/g) ?? []).length, 2);
  for (const job of ['build', 'api', 'workloads']) {
    const block = workflow.split(`  ${job}:`)[1].split(/\r?\n  [a-z]+:/)[0];
    assert.match(block, /permissions:\s+contents: read/);
    assert.doesNotMatch(block, /write|secrets:/);
    assert.match(block, /candidate: \$\{\{ needs.prepare.outputs.merge \}\}/);
  }
  const publisher = workflow.split('  publish:')[1];
  assert.match(publisher, /ref: \$\{\{ github.workflow_sha \}\}/);
  assert.doesNotMatch(publisher, /ref: \$\{\{ (?:inputs|needs)\./);
  assert.match(workflow, /cancel-in-progress: true/);
  for (const file of ['build.yml', 'kubernetes-qualification.yml', 'kubernetes-deployment-kernel.yml']) {
    const text = readFileSync(new URL(`../.github/workflows/${file}`, import.meta.url), 'utf8');
    assert.match(text, /pull_request:/); assert.match(text, /workflow_call:/);
    assert.match(text, /persist-credentials: false/);
    assert.doesNotMatch(text, /checks: write|secrets: inherit/);
  }
});

test('publisher reads actual artifact files, rechecks live identity and never publishes stale success', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'haby-final-control-'));
  try {
    const f = fixture();
    for (const [name, files] of f.artifacts) for (const [path, bytes] of files) {
      const destination = join(directory, name, path); mkdirSync(join(destination, '..'), { recursive: true }); writeFileSync(destination, bytes);
    }
    const updates = [];
    let stale = false; let missing = false;
    const jobNames = ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
      'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel'];
    const listJobs = () => {}; const listArtifacts = () => {};
    const github = {
      rest: {
        actions: { listJobsForWorkflowRunAttempt: listJobs, listWorkflowRunArtifacts: listArtifacts },
        pulls: { get: async () => ({ data: stale ? { ...pr, head: { sha: sha('e') } } : pr }) },
        git: { getCommit: async () => ({ data: commit }) },
        checks: {
          get: async () => ({ data: { head_sha: request.head, name: 'Final qualification', external_id: '123:1' } }),
          update: async value => { updates.push(value); },
        },
      },
      paginate: async method => method === listJobs ? jobNames.map(name => ({ name, status: 'completed', conclusion: 'success' })) :
        (missing ? [] : Object.values(f.names).map(name => ({ name, expired: false, size_in_bytes: 1 }))),
      request: async () => ({ data: f.plan }),
    };
    const summary = join(directory, 'summary.json');
    const results = { build: 'success', api: 'success', workloads: 'success' };
    await publish(github, f.identity, results, directory, 123, 1, '9', summary);
    assert.equal(updates.at(-1).conclusion, 'success');
    stale = true;
    await assert.rejects(publish(github, f.identity, results, directory, 123, 1, '9', summary));
    assert.equal(updates.at(-1).conclusion, 'failure');
    stale = false; missing = true;
    await assert.rejects(publish(github, f.identity, results, directory, 123, 1, '9', summary));
    assert.equal(updates.at(-1).conclusion, 'failure');
    await assert.rejects(publish(github, f.identity, { ...results, api: 'cancelled' }, directory, 123, 1, '9', summary));
    assert.equal(updates.at(-1).conclusion, 'failure');
    assert.equal(updates.filter(value => value.conclusion === 'success').length, 1);
  } finally {
    assert.ok(resolve(directory).startsWith(resolve(tmpdir()) + (process.platform === 'win32' ? '\\' : '/')));
    rmSync(directory, { recursive: true });
  }
});
