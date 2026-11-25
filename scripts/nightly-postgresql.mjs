import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { git } from './ci-change-routing.mjs';
import { artifactNames, readArtifacts, validatePostgreSqlEvidence } from './final-qualification.mjs';
export function nightly(root, directory, source, runId, attempt, result) {
  const report = { source, tree:null, runId, attempt, ref:process.env.GITHUB_REF ?? null,event:process.env.GITHUB_EVENT_NAME ?? null,integrationMain:process.env.GITHUB_REF === 'refs/heads/main',qualification:'nightly-full-postgresql-not-release', outcome:'failure', primary:{code:'incomplete-nightly'}, cleanup:'not-established', gates:[] };
  try {
    if (!/^[0-9a-f]{40}$/.test(source) || !Number.isSafeInteger(runId) || runId < 1 || !Number.isSafeInteger(attempt) || attempt < 1 || result !== 'success') throw new Error('Incomplete invocation.');
    report.tree = git(root, 'rev-parse', `${source}^{tree}`).trim();
    const plan = JSON.parse(git(root,'show',`${source}:tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json`));
    const identity = {merge:source};
    const names = artifactNames(identity,attempt);
    report.gates = validatePostgreSqlEvidence(identity,plan,readArtifacts(directory,{postgresql:names.postgresql,composition:names.composition}),attempt);
    report.cleanup = 'confirmed-by-required-gates'; report.primary=null; report.outcome='verified';
  } catch { /* preserve safe closed outcome; no candidate/raw parser text */ }
  return report;
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
 const [root,directory,source,runId,attempt,result,output]=process.argv.slice(2);
 const report=nightly(root,directory,source,Number(runId),Number(attempt),result);
 writeFileSync(output,JSON.stringify(report,null,2));
 if(report.outcome !== 'verified') process.exitCode=1;
}
