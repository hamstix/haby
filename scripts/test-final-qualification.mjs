import test from 'node:test';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { validateRequest, assertCandidate, assertCurrent, assertResults, assertJobs, artifactNames, validateEvidence, reduce, publish, prepare, classifyCandidate } from './final-qualification.mjs';

const sha = c => c.repeat(40);
const request = { pr: '17', head: sha('a'), base: sha('b'), merge: sha('c'), tree: sha('d') };
const environment = { repository: 'example/project', ref: 'refs/heads/main', workflowRef: 'example/project/.github/workflows/final-qualification.yml@refs/heads/main', control: sha('b'), event: 'workflow_dispatch' };
const pr = { state: 'open', number: 17, head: { sha: request.head }, base: { sha: request.base, ref: 'main', repo: { full_name: environment.repository } }, merge_commit_sha: request.merge };
const commit = { sha: request.merge, tree: { sha: request.tree }, parents: [{ sha: request.base }, { sha: request.head }] };

test('installed main control validates immutable candidate identities', () => {
  const valid = validateRequest(environment, request);
  assertCandidate(valid, pr, commit, 'write', request.base);
  assertCurrent(valid, pr, commit, request.base);
});
test('current main and tested parents determine base despite an older PR snapshot', () => {
  const valid = validateRequest(environment, request);
  const olderSnapshot = { ...pr, base: { ...pr.base, sha: sha('e') } };
  assertCurrent(valid, olderSnapshot, commit, request.base);
  for (const main of [sha('e'), '', undefined]) assert.throws(() => assertCurrent(valid, olderSnapshot, commit, main));
});
test('untrusted event/ref/control, shell input and malformed SHA fail closed', () => {
  for (const delta of [{ event: 'pull_request' }, { ref: 'refs/heads/topic' }, { control: sha('e') }, { workflowRef: 'example/project/.github/workflows/final-qualification.yml@refs/heads/topic' }]) {
    assert.throws(() => validateRequest({ ...environment, ...delta }, request));
  }
  for (const delta of [{ pr: '17;echo injected' }, { pr: '0' }, { head: 'main' }, { base: sha('B') }, { merge: '$()' }, { tree: '' }]) {
    assert.throws(() => validateRequest(environment, { ...request, ...delta }));
  }
  for (const permission of ['read', 'triage', '', undefined]) assert.throws(() => assertCandidate(validateRequest(environment, request), pr, commit, permission, request.base));
});
test('new HEAD/base/merge tree, closed PR and wrong repository are stale', () => {
  const valid = validateRequest(environment, request);
  for (const delta of [{ head: { sha: sha('e') } }, { base: { ...pr.base, ref: 'other' } }, { merge_commit_sha: sha('e') }, { state: 'closed' }, { base: { ...pr.base, repo: { full_name: 'other/repository' } } }]) {
    assert.throws(() => assertCurrent(valid, { ...pr, ...delta }, commit, request.base));
  }
  for (const delta of [{ tree: { sha: sha('e') } }, { parents: [{ sha: request.head }, { sha: request.base }] }, { parents: [] }]) {
    assert.throws(() => assertCurrent(valid, pr, { ...commit, ...delta }, request.base));
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
  const identity = classifiedIdentity(validateRequest(environment, request));
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
    put('postgresql', `${suite}/cases.json`, [{ ...row, outcome: 'Passed' }]);
    put('postgresql', `${suite}/resources.json`, owned);
    put('postgresql', `${suite}/failure-evidence.json`, { failedCases: 0 });
  }
  put('postgresql', 'cases.json', pgRows);
  put('postgresql', 'counts.json', { total: 2, passed: 2, failed: 0, skipped: 0 });
  put('postgresql', 'failure-evidence.json', { failedCases: 0 });
  put('postgresql', 'results.trx', '<sanitized/>');
  put('postgresql', 'coverage.json', { status: 'complete', selection: 'full', discovered: 2, executed: 2 });
  put('postgresql', 'resources.json', owned);
  put('postgresql', 'preflight.json', { prerequisites: { exitCode: 0, cleanup: 'ok' }, ownedServer: { exitCode: 0, cleanup: 'ok' } });
  put('composition', 'Haby.Operator.Composition.Sample.dll', 'example-executable-bytes');
  put('postgresql', 'measurement.json', { source: identity.merge, sourceChangedDuringRun: false, untrackedFileCountAtStart: 0,
    requiredAot: true, testExitCode: 0, reportingFailed: false, preflightCleanupExitCode: 0, selection: 'full',
    compositionSampleSha256: createHash('sha256').update('example-executable-bytes').digest('hex'),
    groups: ['functional', 'infrastructure-qualification'].map(suite => ({ suite, phase: 'complete', testExitCode: 0 })) });
  put('postgresql', 'anonymous-reflection-precheck/precheck.json', { source: identity.merge, status: 'passed', exitCode: 0,
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
test('PostgreSQL positive fixture uses the actual producer archive root', () => {
  const workflow = readFileSync(new URL('../.github/workflows/build.yml', import.meta.url), 'utf8');
  const block = workflow.split('name: Publish allowlisted PostgreSQL timing and case inventory')[1];
  const paths = [...block.matchAll(/^\s+(\.artifacts\/postgresql-measurement\/safe\/[^\r\n]+)$/gm)].map(match => match[1]);
  assert.ok(paths.length > 10);
  const directories = paths.map(path => path.split('/').slice(0, -1));
  const ancestor = directories[0].filter((part, index) => directories.every(parts => parts[index] === part)).join('/') + '/';
  assert.equal(ancestor, '.artifacts/postgresql-measurement/safe/');
  const actualMembers = paths.map(path => path.slice(ancestor.length));
  const f = fixture();
  for (const path of f.artifacts.get(f.names.postgresql).keys()) assert.ok(actualMembers.includes(path), path);
  assert.equal(validateEvidence(f.identity, f.plan, f.artifacts, 1).length, 8);
});
test('each artifact and required member is independently mandatory', () => {
  const good = fixture();
  for (const name of good.artifacts.keys()) {
    const bad = new Map(good.artifacts); bad.delete(name);
    assert.throws(() => validateEvidence(good.identity, good.plan, bad, 1), name);
  }
  for (const [key, path] of [['fast', 'results.trx'], ['linux', 'compiler.json'], ['postgresql', 'resources.json'],
    ['postgresql', 'infrastructure-qualification/cases.json'], ['postgresql', 'preflight.json'], ['provider', 'qualification-evidence/control/qualification-readiness-failure.txt'], ['configured', 'evidence.txt']]) {
    const f = fixture(); f.artifacts.get(f.names[key]).delete(path);
    assert.throws(() => validateEvidence(f.identity, f.plan, f.artifacts, 1), path);
  }
});
test('focused/stale/failed/skipped/incomplete coverage and cleanup evidence fail closed', () => {
  for (const [key, path, delta] of [
    ['fast', 'gate.json', { source: sha('e') }], ['fast', 'gate.json', { dirty: true }],
    ['windows', 'safe/gate.json', { requiredAot: false }],
    ['postgresql', 'coverage.json', { status: 'focused' }], ['postgresql', 'coverage.json', { executed: 1 }],
    ['postgresql', 'measurement.json', { testExitCode: 130 }], ['postgresql', 'measurement.json', { reportingFailed: true }],
    ['postgresql', 'measurement.json', { groups: [{ suite: 'functional', phase: 'complete', testExitCode: 0 }] }],
    ['postgresql', 'resources.json', { cleanupFailed: 1 }], ['postgresql', 'resources.json', { catalogDatabasesDropped: 0 }],
    ['postgresql', 'anonymous-reflection-precheck/precheck.json', { cleanup: 'not-confirmed' }],
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
test('native, composition and Provider known failures retain expected members', () => {
  for (const [change, code, gate, member] of [
    [f => { const value = JSON.parse(f.artifacts.get(f.names.linux).get('safe/gate.json')); f.put('linux', 'safe/gate.json', { ...value, requiredAot: false }); }, 'published-native-incomplete', 'Native AOT (linux-x64)', 'safe/gate.json'],
    [f => f.put('composition', 'Haby.Operator.Composition.Sample.dll', 'different-bytes'), 'composition-incomplete', 'postgresql-integration', 'Haby.Operator.Composition.Sample.dll'],
    [f => { const path = 'qualification-evidence/control/qualification-stock.txt'; f.put('provider', path, f.artifacts.get(f.names.provider).get(path).toString().replace('Failure:', 'Unperformed:')); }, 'rehearsal-outcome-incomplete', 'kubernetes-provider-access', 'qualification-stock.txt'],
    [f => { const path = 'qualification-evidence/control/qualification-success.txt'; f.put('provider', path, f.artifacts.get(f.names.provider).get(path).toString() + '\nContainer/storage cleanup: FAIL'); }, 'provider-evidence-incomplete', 'kubernetes-provider-access', 'qualification-success.txt'],
  ]) {
    const f = fixture(); change(f); let failure;
    try { validateEvidence(f.identity, f.plan, f.artifacts, 1); } catch (error) { failure = error; }
    assert.equal(failure?.details?.code, code);
    assert.equal(failure.details.gate, gate);
    assert.equal(failure.details.member, member);
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
test('required control artifact upload precedes candidate check completion', () => {
  const workflow = readFileSync(new URL('../.github/workflows/final-qualification.yml', import.meta.url), 'utf8');
  assert.ok(workflow.indexOf('uses: actions/upload-artifact@v4') < workflow.indexOf('await publish('), 'Check can turn green before the required control upload.');
});

test('publisher reads actual artifact files, rechecks live identity and never publishes stale success', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'haby-final-control-'));
  try {
    const f = fixture();
    for (const [name, files] of f.artifacts) for (const [path, bytes] of files) {
      const destination = join(directory, name, path); mkdirSync(join(destination, '..'), { recursive: true }); writeFileSync(destination, bytes);
    }
    const updates = [];
    let stale = false; let missing = false; let controlMissing = false; let apiError = false; let liveMain = request.base;
    const jobNames = ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
      'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel'];
    const listJobs = () => {}; const listArtifacts = () => {};
    const github = {
      rest: {
        actions: { listJobsForWorkflowRunAttempt: listJobs, listWorkflowRunArtifacts: listArtifacts,
          getArtifact: async () => ({ data: { id: 900, name: 'qualification-control-123-1', workflow_run: { id: 123 }, expired: controlMissing, size_in_bytes: 1 } }) },
        pulls: { get: async () => ({ data: stale ? { ...pr, head: { sha: sha('e') } } : { ...pr, base: { ...pr.base, sha: sha('e') } } }) },
        git: { getCommit: async () => ({ data: commit }) },
        repos: { getBranch: async () => ({ data: { commit: { sha: liveMain } } }) },
        checks: {
          get: async () => ({ data: { head_sha: request.head, name: 'Final qualification', external_id: '123:1' } }),
          update: async value => { updates.push(value); },
        },
      },
      paginate: async method => method === listJobs ? jobNames.map(name => ({ name, status: 'completed', conclusion: 'success' })) :
        (missing ? [] : Object.values(f.names).map(name => ({ name, expired: false, size_in_bytes: 1 }))),
      request: async () => { if (apiError) throw new Error('PROTECTED-api-or-parser-output'); return { data: f.plan }; },
    };
    const summary = join(directory, 'summary.json');
    const results = { build: 'success', api: 'success', workloads: 'success' };
    const uploaded = { reduction: 'success', upload: 'success', artifact: '900' };
    const execute = async (outcomes = results, publication = uploaded) => {
      await reduce(github, f.identity, outcomes, directory, 123, 1, summary);
      return publish(github, f.identity, 123, 1, '9', summary, publication);
    };
    await reduce(github, f.identity, results, directory, 123, 1, summary);
    assert.equal(updates.length, 0, 'Reduction published a check before required upload.');
    await publish(github, f.identity, 123, 1, '9', summary, uploaded);
    assert.equal(updates.at(-1).conclusion, 'success');
    stale = true;
    await assert.rejects(execute());
    assert.equal(updates.at(-1).conclusion, 'failure');
    const staleReport = JSON.parse(readFileSync(summary, 'utf8'));
    assert.equal(staleReport.primary?.code, 'stale-candidate');
    assert.equal(staleReport.primary?.stage, 'freshness');
    assert.equal(staleReport.cleanup, 'confirmed-by-required-gates');
    stale = false; missing = true;
    await assert.rejects(execute());
    assert.equal(updates.at(-1).conclusion, 'failure');
    const absentArtifact = JSON.parse(readFileSync(summary, 'utf8'));
    assert.equal(absentArtifact.primary.code, 'missing-artifact');
    assert.equal(absentArtifact.primary.stage, 'artifacts');
    assert.equal(absentArtifact.primary.member, f.names.fast);
    await assert.rejects(execute({ ...results, api: 'cancelled' }));
    assert.equal(updates.at(-1).conclusion, 'failure');
    const canceled = JSON.parse(readFileSync(summary, 'utf8'));
    assert.equal(canceled.primary.code, 'workflow-outcome');
    assert.equal(canceled.primary.gate, 'api');
    assert.equal(canceled.primary.outcome, 'cancelled');
    missing = false;
    const member = join(directory, f.names.postgresql, 'measurement.json');
    const saved = readFileSync(member); rmSync(member);
    const absentMember = await reduce(github, f.identity, results, directory, 123, 1, summary);
    assert.equal(absentMember.primary.code, 'missing-member');
    assert.equal(absentMember.primary.stage, 'evidence');
    assert.equal(absentMember.primary.gate, 'postgresql-integration');
    assert.equal(absentMember.primary.member, 'measurement.json');
    assert.equal(absentMember.cleanup, 'not-established');
    writeFileSync(member, saved);
    const resources = join(directory, f.names.postgresql, 'functional/resources.json');
    const resourceBytes = readFileSync(resources);
    writeFileSync(resources, JSON.stringify({ ...JSON.parse(resourceBytes), cleanupFailed: 1 }));
    const cleanup = await reduce(github, f.identity, results, directory, 123, 1, summary);
    assert.equal(cleanup.primary.code, 'cleanup-incomplete');
    assert.equal(cleanup.primary.member, 'functional/resources.json');
    assert.equal(cleanup.cleanup, 'incomplete');
    assert.equal(cleanup.primary.deadline, null);
    writeFileSync(resources, resourceBytes);
    for (const [key, path, change, code, cleanupState, expectedGate] of [
      ['postgresql', 'measurement.json', value => ({ ...value, preflightCleanupExitCode: 21 }), 'preflight-cleanup-incomplete', 'incomplete', 'postgresql-integration'],
      ['postgresql', 'preflight.json', value => ({ ...value, ownedServer: { ...value.ownedServer, cleanup: 'remove-failed' } }), 'preflight-incomplete', 'incomplete', 'postgresql-integration'],
      ['postgresql', 'coverage.json', value => ({ ...value, executed: 1 }), 'coverage-incomplete', 'not-established', 'postgresql-integration'],
      ['postgresql', 'anonymous-reflection-precheck/precheck.json', value => ({ ...value, cleanup: 'not-confirmed' }), 'precheck-incomplete', 'not-confirmed', 'postgresql-integration'],
      ['linux', 'compiler.json', value => ({ ...value, source: sha('e') }), 'native-compiler-incomplete', 'not-established', 'Native AOT (linux-x64)'],
    ]) {
      const file = join(directory, f.names[key], path); const bytes = readFileSync(file);
      writeFileSync(file, JSON.stringify(change(JSON.parse(bytes))));
      const diagnostic = await reduce(github, f.identity, results, directory, 123, 1, summary);
      assert.equal(diagnostic.primary.code, code, path);
      assert.equal(diagnostic.primary.stage, 'evidence', path);
      assert.equal(diagnostic.primary.gate, expectedGate, path);
      assert.equal(diagnostic.primary.member, path, path);
      assert.equal(diagnostic.cleanup, cleanupState, path);
      writeFileSync(file, bytes);
    }
    apiError = true;
    const unavailable = await reduce(github, f.identity, results, directory, 123, 1, summary);
    assert.equal(unavailable.primary.code, 'api-or-data-unavailable');
    assert.equal(unavailable.primary.stage, 'coverage-plan');
    assert.ok(!readFileSync(summary, 'utf8').includes('PROTECTED-api-or-parser-output'));
    apiError = false;
    for (const upload of ['failure', 'cancelled', 'skipped', undefined]) {
      await assert.rejects(execute(results, { ...uploaded, upload }));
      assert.equal(updates.at(-1).conclusion, 'failure');
    }
    await assert.rejects(execute(results, { ...uploaded, artifact: '' }));
    assert.equal(updates.at(-1).conclusion, 'failure');
    controlMissing = true;
    await assert.rejects(execute());
    assert.equal(updates.at(-1).conclusion, 'failure');
    assert.ok(updates.at(-1).output.summary.includes('control-artifact-incomplete'));
    controlMissing = false;
    await reduce(github, f.identity, results, directory, 123, 1, summary);
    stale = true; // HEAD advances after reduction/upload but before check completion.
    await assert.rejects(publish(github, f.identity, 123, 1, '9', summary, uploaded));
    assert.equal(updates.at(-1).conclusion, 'failure');
    assert.ok(updates.at(-1).output.summary.includes('stale-candidate'));
    stale = false;
    for (const value of [sha('e'), undefined]) {
      liveMain = value;
      await assert.rejects(publish(github, f.identity, 123, 1, '9', summary, uploaded));
      assert.equal(updates.at(-1).conclusion, 'failure');
      assert.ok(updates.at(-1).output.summary.includes('stale-main'));
    }
    assert.equal(updates.filter(value => value.conclusion === 'success').length, 1);
  } finally {
    assert.ok(resolve(directory).startsWith(resolve(tmpdir()) + (process.platform === 'win32' ? '\\' : '/')));
    rmSync(directory, { recursive: true });
  }
});

test('focused job and artifact identities never satisfy the installed final reducer', () => {
  const f = fixture();
  const focused = new Map(f.artifacts);
  for (const key of ['postgresql', 'configured', 'durable', 'kernel']) {
    const name = f.names[key]; focused.set(`focused-only-not-final-${name}`, focused.get(name)); focused.delete(name);
  }
  assert.throws(() => validateEvidence(f.identity, f.plan, focused, 1));
  const names = ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
    'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel'];
  assert.throws(() => assertJobs(names.map(name => ({ name: name.replace('workloads / ', 'workloads / Focused '), status: 'completed', conclusion: 'success' }))));
});

function classifiedIdentity(identity, scope = 'full') {
  return { ...identity, scope, classification: { before: identity.base, after: identity.merge, tree: identity.tree,
    route: scope, changedEntries: 1, documentation: 'passed' } };
}
function docsAdapter(identity) {
  const updates = []; const state = { main: identity.base, expired: false };
  const jobs = ['prepare', 'start', 'publish', 'build', 'api', 'workloads'].map(name => ({ name,
    status: name === 'publish' ? 'in_progress' : 'completed', conclusion: name === 'publish' ? null : ['build', 'api', 'workloads'].includes(name) ? 'skipped' : 'success' }));
  const github = {
    rest: {
      actions: { listJobsForWorkflowRunAttempt: 'jobs', listWorkflowRunArtifacts: 'artifacts',
        getArtifact: async () => ({ data: { id: 900, name: 'qualification-control-123-1', workflow_run: { id: 123 }, expired: state.expired, size_in_bytes: 1 } }) },
      pulls: { get: async () => ({ data: pr }) }, git: { getCommit: async () => ({ data: commit }) },
      repos: { getBranch: async () => ({ data: { commit: { sha: state.main } } }) },
      checks: { get: async () => ({ data: { head_sha: identity.head, name: 'Final qualification', external_id: '123:1' } }), update: async value => updates.push(value) },
    },
    paginate: async method => { assert.equal(method, 'jobs', 'Docs path requested runtime artifacts.'); return jobs; },
    request: async () => { throw new Error('PROTECTED-runtime-plan-must-not-be-requested'); },
  };
  return { github, jobs, updates, state };
}
test('trusted docs reduction verifies only lightweight checks with no runtime or cleanup credit', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'haby-docs-control-'));
  try {
    const identity = classifiedIdentity(validateRequest(environment, request), 'docs-only');
    const { github } = docsAdapter(identity);
    const result = await reduce(github, identity, { build: 'skipped', api: 'skipped', workloads: 'skipped' },
      join(directory, 'NO-RUNTIME-ARTIFACTS'), 123, 1, join(directory, 'summary.json'));
    assert.equal(result.outcome, 'verified'); assert.equal(result.scope, 'docs-only');
    assert.equal(result.runtimeExecuted, false); assert.equal(result.cleanup, 'not-applicable');
    assert.equal(result.gates.some(gate => 'cases' in gate), false);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

function gitCandidate(edit) {
  const root = mkdtempSync(join(tmpdir(), 'haby-classification-'));
  const env = { ...process.env, GIT_AUTHOR_NAME: 'Control fixture', GIT_AUTHOR_EMAIL: 'fixture@example.invalid',
    GIT_COMMITTER_NAME: 'Control fixture', GIT_COMMITTER_EMAIL: 'fixture@example.invalid' };
  const run = (...args) => execFileSync('git', ['-C', root, ...args], { env, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
  run('init', '--initial-branch=main'); run('config', 'commit.gpgsign', 'false'); run('config', 'core.hooksPath', join(root, 'no-hooks'));
  writeFileSync(join(root, 'README.md'), '# Base\n'); mkdirSync(join(root, 'docs'));
  run('add', '.'); run('commit', '-m', 'base'); const base = run('rev-parse', 'HEAD');
  edit(root, run); run('add', '-A'); run('commit', '-m', 'candidate');
  const head = run('rev-parse', 'HEAD'); const tree = run('rev-parse', 'HEAD^{tree}');
  const merge = run('commit-tree', tree, '-p', base, '-p', head, '-m', 'tested merge');
  run('update-ref', 'refs/heads/candidate', head); run('update-ref', 'refs/heads/tested-merge', merge);
  run('checkout', '--detach', base); run('update-ref', 'refs/heads/main', base); run('remote', 'add', 'origin', root);
  const identity = { repository: environment.repository, control: base, pr: 17, head, base, merge, tree };
  return { root, run, identity, cleanup: () => {
    assert.ok(resolve(root).startsWith(resolve(tmpdir()) + (process.platform === 'win32' ? '\\' : '/')));
    rmSync(root, { recursive: true });
  } };
}
test('trusted immutable Git classification supports docs and conservatively retains full routes', () => {
  for (const [name, edit, expected] of [
    ['docs', root => writeFileSync(join(root, 'docs', 'guide.md'), '[Base](../README.md)\n'), 'docs-only'],
    ['code', root => writeFileSync(join(root, 'source.cs'), 'code\n'), 'full'],
    ['mixed', root => { writeFileSync(join(root, 'README.md'), '# Changed\n'); writeFileSync(join(root, 'source.cs'), 'code\n'); }, 'full'],
    ['unknown', root => writeFileSync(join(root, 'notes.md'), '# Unknown path\n'), 'full'],
    ['renamed outside docs', (root, run) => run('mv', 'README.md', 'notes.md'), 'full'],
  ]) {
    const f = gitCandidate(edit);
    try {
      const result = classifyCandidate(f.root, { ...f.identity, scope: expected === 'full' ? 'docs-only' : 'full' });
      assert.equal(result.scope, expected, name); assert.equal(result.classification.documentation, 'passed');
      assert.equal(result.classification.before, f.identity.base); assert.equal(result.classification.after, f.identity.merge);
      assert.equal(f.run('rev-parse', 'HEAD'), f.identity.control, 'Candidate was checked out.');
    } finally { f.cleanup(); }
  }
});
test('failed documentation checks and unavailable or mismatched objects cannot authorize docs scope or leak Git output', () => {
  for (const edit of [root => writeFileSync(join(root, 'README.md'), '[protected-secret](missing.md)\n'),
    root => writeFileSync(join(root, 'README.md'), '# protected-secret trailing space \n')]) {
    const f = gitCandidate(edit);
    try { assert.throws(() => classifyCandidate(f.root, f.identity), error => error.message === 'Trusted classification unavailable or failed.'); }
    finally { f.cleanup(); }
  }
  const f = gitCandidate(root => writeFileSync(join(root, 'README.md'), '# Valid\n'));
  try {
    for (const delta of [{ merge: sha('e') }, { tree: sha('e') }, { base: f.identity.head }, { head: f.identity.base }])
      assert.throws(() => classifyCandidate(f.root, { ...f.identity, ...delta }));
    writeFileSync(join(f.root, '.git', 'shallow'), f.identity.base + '\n');
    assert.throws(() => classifyCandidate(f.root, f.identity));
  } finally { f.cleanup(); }
});
test('prepare derives scope after installed checkout and immutable admission, ignoring manual scope input', async () => {
  const f = gitCandidate(root => writeFileSync(join(root, 'README.md'), '# Docs change\n'));
  const snapshot = { ...pr, head: { sha: f.identity.head }, base: { ...pr.base, sha: f.identity.base }, merge_commit_sha: f.identity.merge };
  const github = { rest: { pulls: { get: async () => ({ data: snapshot }) },
    git: { getCommit: async () => ({ data: { sha: f.identity.merge, tree: { sha: f.identity.tree }, parents: [{ sha: f.identity.base }, { sha: f.identity.head }] } }) },
    repos: { getBranch: async () => ({ data: { commit: { sha: f.identity.base } } }), getCollaboratorPermissionLevel: async () => ({ data: { permission: 'write' } }) } } };
  try {
    const input = { ...f.identity, pr: '17', scope: 'full' };
    assert.equal(validateRequest({ ...environment, control: f.identity.base }, input).scope, undefined);
    const result = await prepare(github, { ...environment, control: f.identity.base }, input, 'maintainer', f.root);
    assert.equal(result.scope, 'docs-only'); assert.equal(f.run('rev-parse', 'HEAD'), f.identity.base);
    f.run('checkout', '--detach', f.identity.head);
    await assert.rejects(prepare(github, { ...environment, control: f.identity.base }, input, 'maintainer', f.root));
  } finally { f.cleanup(); }
});

test('docs reduction refuses missing classification, failed checks and unexpected runtime outcomes or jobs', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'haby-docs-negative-'));
  try {
    const identity = classifiedIdentity(validateRequest(environment, request), 'docs-only');
    for (const bad of [validateRequest(environment, request), { ...identity, classification: undefined },
      { ...identity, classification: { ...identity.classification, before: sha('e') } },
      { ...identity, classification: { ...identity.classification, documentation: 'failed' } },
      { ...identity, scope: 'protected-secret' }]) {
      const { github } = docsAdapter(identity);
      const result = await reduce(github, bad, { build: 'skipped', api: 'skipped', workloads: 'skipped' }, directory, 123, 1, join(directory, 'summary.json'));
      assert.equal(result.outcome, 'failure'); assert.equal(result.primary.code, 'classification-incomplete');
      assert.equal(result.scope, 'unknown'); assert.notEqual(result.cleanup, 'confirmed-by-required-gates');
    }
    for (const outcome of ['success', 'failure', 'cancelled', 'timed_out', undefined]) {
      const { github } = docsAdapter(identity);
      const result = await reduce(github, identity, { build: 'skipped', api: outcome, workloads: 'skipped' }, directory, 123, 1, join(directory, 'summary.json'));
      assert.equal(result.outcome, 'failure'); assert.equal(result.primary.code, 'unexpected-docs-runtime');
    }
    const { github, jobs } = docsAdapter(identity); jobs.push({ name: 'workloads / deployment-kernel', status: 'completed', conclusion: 'success' });
    assert.equal((await reduce(github, identity, { build: 'skipped', api: 'skipped', workloads: 'skipped' }, directory, 123, 1, join(directory, 'summary.json'))).outcome, 'failure');
  } finally { rmSync(directory, { recursive: true }); }
});
test('docs publication binds scope, classification and run without any runtime cleanup credit', async () => {
  const directory = mkdtempSync(join(tmpdir(), 'haby-docs-publish-'));
  try {
    const identity = classifiedIdentity(validateRequest(environment, request), 'docs-only');
    const summary = join(directory, 'summary.json'); const adapter = docsAdapter(identity);
    await reduce(adapter.github, identity, { build: 'skipped', api: 'skipped', workloads: 'skipped' }, directory, 123, 1, summary);
    const saved = readFileSync(summary, 'utf8');
    const uploaded = { reduction: 'success', upload: 'success', artifact: '900' };
    await publish(adapter.github, identity, 123, 1, '9', summary, uploaded);
    assert.equal(adapter.updates.at(-1).conclusion, 'success');
    assert.match(adapter.updates.at(-1).output.summary, /runtimeExecuted=false; cleanup=not-applicable; no runtime coverage credit/);
    for (const mutate of [value => { value.scope = 'full'; }, value => { value.requestedScope = 'full'; },
      value => { value.runtimeExecuted = true; }, value => { value.cleanup = 'confirmed-by-required-gates'; },
      value => { value.runId++; }, value => { value.attempt++; },
      ...['repository', 'control', 'pr', 'head', 'base', 'merge', 'tree'].map(key => value => { value[key] = 'wrong'; }),
      value => { value.classification.after = sha('e'); }, value => { value.classification.changedEntries++; },
      value => { value.gates[0].cases = 0; }, value => { value.gates[0].checks = []; }]) {
      const value = JSON.parse(saved); mutate(value); writeFileSync(summary, JSON.stringify(value));
      await assert.rejects(publish(adapter.github, identity, 123, 1, '9', summary, uploaded));
      assert.equal(adapter.updates.at(-1).conclusion, 'failure');
      assert.doesNotMatch(adapter.updates.at(-1).output.summary, /"cleanup":"confirmed-by-required-gates"/);
    }
    writeFileSync(summary, saved);
    for (const update of [{ ...uploaded, upload: 'failure' }, { ...uploaded, upload: 'cancelled' }, { ...uploaded, upload: 'skipped' }, { ...uploaded, artifact: '' }]) {
      await assert.rejects(publish(adapter.github, identity, 123, 1, '9', summary, update));
      assert.equal(adapter.updates.at(-1).conclusion, 'failure'); assert.match(adapter.updates.at(-1).output.summary, /not-applicable/);
    }
    adapter.state.expired = true;
    await assert.rejects(publish(adapter.github, identity, 123, 1, '9', summary, uploaded));
    assert.equal(adapter.updates.at(-1).conclusion, 'failure'); adapter.state.expired = false;
    adapter.state.main = sha('e');
    await assert.rejects(publish(adapter.github, identity, 123, 1, '9', summary, uploaded));
    assert.match(adapter.updates.at(-1).output.summary, /stale-main/);
    assert.match(adapter.updates.at(-1).output.summary, /not-applicable/);
    const stale = await reduce(adapter.github, identity, { build: 'skipped', api: 'skipped', workloads: 'skipped' }, directory, 123, 1, summary);
    assert.equal(stale.outcome, 'failure'); assert.equal(stale.primary.code, 'stale-main');
    assert.equal(stale.runtimeExecuted, false); assert.equal(stale.cleanup, 'not-applicable');
  } finally { rmSync(directory, { recursive: true }); }
});

async function publisherFixture(scope) {
  const directory = mkdtempSync(join(tmpdir(), 'haby-publisher-diagnostic-'));
  const f = fixture(); const identity = classifiedIdentity(f.identity, scope);
  const adapter = docsAdapter(identity);
  if (scope === 'full') {
    for (const [name, files] of f.artifacts) for (const [path, bytes] of files) {
      const destination = join(directory, name, path); mkdirSync(join(destination, '..'), { recursive: true }); writeFileSync(destination, bytes);
    }
    const jobs = ['build / validate', 'build / Native AOT (win-x64)', 'build / Native AOT (linux-x64)', 'build / postgresql-integration',
      'api / qualification', 'workloads / configured-deployment', 'workloads / durable-actions', 'workloads / deployment-kernel'];
    adapter.github.paginate = async method => method === 'jobs' ? jobs.map(name => ({ name, status: 'completed', conclusion: 'success' })) :
      Object.values(f.names).map(name => ({ name, expired: false, size_in_bytes: 1 }));
    adapter.github.request = async () => ({ data: f.plan });
  }
  const summary = join(directory, 'summary.json');
  const results = Object.fromEntries(['build', 'api', 'workloads'].map(key => [key, scope === 'full' ? 'success' : 'skipped']));
  const reduction = await reduce(adapter.github, identity, results, directory, 123, 1, summary);
  assert.equal(reduction.outcome, 'verified');
  let calls = 0; let freshness = 0;
  const getBranch = adapter.github.rest.repos.getBranch;
  adapter.github.rest.repos.getBranch = async args => { freshness++; return getBranch(args); };
  const artifact = { id: 900, name: 'qualification-control-123-1', workflow_run: { id: 123 }, expired: false, size_in_bytes: 1 };
  let response = { data: artifact }; let thrown; let shouldThrow = false;
  adapter.github.rest.actions.getArtifact = async args => {
    calls++; assert.deepEqual(args, { owner: 'example', repo: 'project', artifact_id: 900 });
    if (shouldThrow) throw thrown;
    return response;
  };
  return { ...adapter, artifact, directory, summary,
    setError: error => { thrown = error; shouldThrow = true; },
    setResponse: value => { response = value; shouldThrow = false; },
    counts: () => ({ calls, freshness }),
    publish: (publication = { reduction: 'success', upload: 'success', artifact: '900' }) =>
      publish(adapter.github, identity, 123, 1, '9', summary, publication),
    cleanup: () => rmSync(directory, { recursive: true }),
  };
}

test('publisher request diagnostics retain only safe structured facts and fail closed', async t => {
  for (const scope of ['docs-only', 'full']) for (const [status, code, category] of [
    [401, undefined, 'authentication'], [403, undefined, 'forbidden'], [404, undefined, 'not-found'],
    [429, undefined, 'rate-limited'], [503, undefined, 'server-error'], [422, undefined, 'other-http'],
    [undefined, 'ECONNRESET', 'transport'], [undefined, 'ETIMEDOUT', 'transport'],
    [undefined, 'PROTECTED-unknown-code', 'unknown'], ['503', undefined, 'unknown'],
    [99, undefined, 'unknown'], [600, undefined, 'unknown'], [503.5, undefined, 'unknown'],
    [NaN, undefined, 'unknown'], [undefined, undefined, 'unknown'],
  ]) await t.test(`${scope} ${String(status)} ${String(code)}`, async () => {
    const f = await publisherFixture(scope);
    try {
      const error = Object.assign(new Error('PROTECTED-message'), { status, code, stack: 'PROTECTED-stack',
        request: { headers: { authorization: 'PROTECTED-token' }, url: 'PROTECTED-url' },
        response: { data: 'PROTECTED-body', headers: { secret: 'PROTECTED-header' } } });
      f.setError(error); await assert.rejects(f.publish());
      const update = f.updates.at(-1); assert.equal(update.conclusion, 'failure');
      const failure = JSON.parse(update.output.summary.slice('Qualification did not pass: '.length));
      assert.equal(failure.primary.code, 'api-or-data-unavailable'); assert.equal(failure.primary.stage, 'control-artifact');
      assert.equal(failure.primary.gate, 'control'); assert.equal(failure.primary.member, 'qualification-control-123-1');
      assert.equal(failure.primary.deadline, null);
      assert.deepEqual(failure.primary.api, { operation: 'actions.getArtifact', httpStatus: Number.isInteger(status) && status >= 100 && status <= 599 ? status : null, category });
      assert.equal(failure.cleanup, scope === 'full' ? 'confirmed-by-required-gates' : 'not-applicable');
      assert.deepEqual(f.counts(), { calls: 1, freshness: 0 });
      assert.doesNotMatch(JSON.stringify(f.updates) + readFileSync(f.summary, 'utf8'), /PROTECTED/);
    } finally { f.cleanup(); }
  });
  for (const error of [null, 'PROTECTED-string', undefined]) await t.test(`nonobject ${String(error)}`, async () => {
    const f = await publisherFixture('docs-only');
    try {
      f.setError(error); await assert.rejects(f.publish());
      const output = f.updates.at(-1).output.summary;
      const failure = JSON.parse(output.slice('Qualification did not pass: '.length));
      assert.deepEqual(failure.primary.api, { operation: 'actions.getArtifact', httpStatus: null, category: 'unknown' });
      assert.equal(f.updates.at(-1).conclusion, 'failure'); assert.deepEqual(f.counts(), { calls: 1, freshness: 0 });
      assert.doesNotMatch(output, /PROTECTED/);
    } finally { f.cleanup(); }
  });
});

test('publisher metadata contract failures remain distinct from request errors', async t => {
  for (const scope of ['docs-only', 'full']) for (const variant of ['response-null', 'data-missing', 'data-null', 'data-string', 'data-array', 'id', 'name', 'run', 'expired', 'size'])
    await t.test(`${scope} ${variant}`, async () => {
      const f = await publisherFixture(scope);
      try {
        const responses = { 'response-null': null, 'data-missing': {}, 'data-null': { data: null }, 'data-string': { data: 'PROTECTED-metadata' }, 'data-array': { data: [] } };
        const changes = { id: { id: 901 }, name: { name: 'PROTECTED-other' }, run: { workflow_run: { id: 124 } }, expired: { expired: true }, size: { size_in_bytes: 0 } };
        f.setResponse(variant in responses ? responses[variant] : { data: { ...f.artifact, ...changes[variant] } });
        await assert.rejects(f.publish()); const update = f.updates.at(-1);
        const failure = JSON.parse(update.output.summary.slice('Qualification did not pass: '.length));
        assert.equal(update.conclusion, 'failure'); assert.equal(failure.primary.code, 'control-artifact-incomplete');
        assert.equal(failure.primary.stage, 'control-artifact'); assert.equal(failure.primary.gate, 'control');
        assert.equal(failure.primary.api, undefined); assert.deepEqual(f.counts(), { calls: 1, freshness: 0 });
        assert.doesNotMatch(JSON.stringify(f.updates), /PROTECTED/);
      } finally { f.cleanup(); }
    });
});

test('publisher diagnostics preserve upload gate and final freshness in both scopes', async () => {
  for (const scope of ['docs-only', 'full']) {
    const f = await publisherFixture(scope);
    try {
      for (const publication of [{ reduction: 'failure', upload: 'success', artifact: '900' }, { reduction: 'success', upload: 'failure', artifact: '900' }]) {
        await assert.rejects(f.publish(publication)); assert.equal(f.updates.at(-1).conclusion, 'failure');
      }
      assert.deepEqual(f.counts(), { calls: 0, freshness: 0 });
      await f.publish(); assert.equal(f.updates.at(-1).conclusion, 'success');
      assert.deepEqual(f.counts(), { calls: 1, freshness: 1 });
      f.state.main = sha('e'); await assert.rejects(f.publish());
      assert.equal(f.updates.at(-1).conclusion, 'failure'); assert.match(f.updates.at(-1).output.summary, /stale-main/);
      assert.deepEqual(f.counts(), { calls: 2, freshness: 2 });
    } finally { f.cleanup(); }
  }
});
