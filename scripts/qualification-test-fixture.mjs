import {createHash} from 'node:crypto';
import {artifactNames} from './final-qualification.mjs';
export function qualificationFixture(identity) {
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

