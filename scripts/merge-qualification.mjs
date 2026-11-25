import { readFileSync, writeFileSync } from 'node:fs';
import { prepare as prepareFull, current, assertClassification, assertDocsResults, docsChecks, assertResults, assertJobs,
  artifactNames, readArtifacts, validateEvidence, boundedFailure, readControlArtifact, artifactTiming } from './final-qualification.mjs';
import { select } from './postgresql-relevance.mjs';
const requireThat = (condition, message) => { if (!condition) throw new Error(message); };
export async function prepare(github, environment, input, actor, root = process.env.GITHUB_WORKSPACE) {
  requireThat(environment.workflowRef === `${environment.repository}/.github/workflows/merge-qualification.yml@refs/heads/main`, 'Installed merge workflow required.');
  const identity = await prepareFull(github, {...environment,workflowRef:`${environment.repository}/.github/workflows/final-qualification.yml@refs/heads/main`}, input, actor, root);
  return {...identity, purpose:'merge', selection:identity.scope === 'full' ? select(root,identity.base,identity.merge) : null};
}
export async function reduce(github, identity, results, directory, runId, attempt, summaryPath = 'merge-summary.json') {
  const [owner, repo] = identity.repository.split('/');
  let verified = false; let report = []; let failure = null; let stage = 'classification'; let cleanupState = 'not-established';
  let scope = 'unknown'; let runtimeExecuted = null;
  try {
    requireThat(identity.purpose === 'merge' && (identity.scope === 'docs-only' || (identity.selection?.source === identity.merge && identity.selection?.before === identity.base && identity.selection?.tree === identity.tree)), 'Merge selection identity required.');
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
      report = validateEvidence(identity, plan, readArtifacts(directory, artifactNames(identity, attempt)), attempt, identity.selection);
      cleanupState = 'confirmed-by-required-gates';
      scope = identity.selection.scope === 'targeted' ? 'merge-targeted' : 'merge-full'; runtimeExecuted = true;
    }
    stage = 'freshness';
    await current(github, identity);
    verified = true;
  } catch (error) {
    // Never echo API/parser messages or artifact data, which may contain secrets.
    failure = boundedFailure(error, stage);
    if (failure.cleanup === 'not-established') failure.cleanup = cleanupState;
  }
  const reduction = { ...identity, qualification: 'merge-only-not-release', postgresqlScope: identity.selection?.scope ?? null, requestedScope: identity.scope ?? null, scope, runtimeExecuted, runId, attempt, outcome: verified ? 'verified' : 'failure', gates: report,
    primary: failure?.primary ?? null, cleanup: failure?.cleanup ?? cleanupState };
  writeFileSync(summaryPath, JSON.stringify(reduction, null, 2) + '\n');
  return reduction;
}

export async function publish(github, identity, runId, attempt, checkId, summaryPath, publication) {
  const [owner, repo] = identity.repository.split('/');
  let conclusion = 'failure';
  try {
    const report = JSON.parse(readFileSync(summaryPath, 'utf8'));
    requireThat(identity.purpose === 'merge' && report.qualification === 'merge-only-not-release' &&
      report.outcome === 'verified' && report.runId === runId && report.attempt === attempt &&
      ['head','base','merge','tree','control'].every(key => report[key] === identity[key]), 'Merge reduction incomplete.');
    requireThat(publication.reduction === 'success' && publication.upload === 'success' && /^[1-9][0-9]*$/.test(publication.artifact), 'Merge artifact publication failed.');
    const { response } = await readControlArtifact(github, owner, repo, Number(publication.artifact), `merge-control-${runId}-${attempt}`, artifactTiming);
    const artifact = response.data;
    requireThat(artifact.id === Number(publication.artifact) && artifact.name === `merge-control-${runId}-${attempt}` &&
      artifact.workflow_run?.id === runId && artifact.expired === false && artifact.size_in_bytes > 0, 'Wrong merge control artifact.');
    await current(github, identity);
    conclusion = 'success';
  } catch { /* Bounded public failure only: never echo candidate artifact content. */ }
  const existing = (await github.rest.checks.get({owner,repo,check_run_id:Number(checkId)})).data;
  requireThat(existing.head_sha === identity.head && existing.name === 'Merge qualification' && existing.external_id === `${runId}:${attempt}`, 'Wrong merge check identity.');
  await github.rest.checks.update({owner,repo,check_run_id:Number(checkId),status:'completed',conclusion,
    completed_at:new Date().toISOString(),output:{title:'Exact merge qualification',summary:conclusion === 'success'
      ? 'Exact merge candidate and required publication verified. Targeted merge evidence is not full release or installation readiness; architect approval remains separate.'
      : 'Merge qualification failed, stale, missing or incomplete. No release credit.'}});
  requireThat(conclusion === 'success', 'Merge qualification did not pass.');
}
