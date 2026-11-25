import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { git, parseDiff } from './ci-change-routing.mjs';

export const smoke = [
  ['ManifestCatalogPersistenceTests.Migration_and_fresh_provider_preserve_complete_typed_revision_and_original_history', '47a4427250a45570b86cc662504bee1709141be4c30be469aaafb3333f96a90f'],
  ['OperatorHostIntegrationTests.Explicit_execution_uses_production_singleton_and_pinned_inputs_and_survives_restart', '4d3138b78192e0daf927d9bfac70b8669e7960c53f66fea13e7544293b6b9b4d'],
].map(([method, testcase]) => ({ method: `Haby.PostgreSql.IntegrationTests.${method}`, case: testcase }));
export const nativeClasses = ['ApplicationConfigurationTests', 'ApplicationGenerationTests', 'PostgreSqlApplicationLoginTests',
  'ApplicationLoginLoggingTests', 'DurableDeploymentPlanningTests', 'OperatorHostIntegrationTests', 'OperatorProviderAccessTests', 'PostgreSqlPairImportTests'];
export const cliLeaves = ['src/habyctl/Commands/Environments/EnvironmentCommands.cs',
  'src/habyctl/Commands/Placements/PlacementCommands.cs', 'src/habyctl/Commands/Manifest/ValidateCommand.cs'];
const prefix = 'Haby.PostgreSql.IntegrationTests.';
const key = row => `${row.method}#${row.case}`;
const requireThat = (condition, message) => { if (!condition) throw new Error(message); };
export function selectedRows(plan, methods, classes) {
  requireThat(Array.isArray(plan.entries), 'Missing complete coverage plan.');
  const rows = plan.entries.filter(row => row.gate === 'postgresql-integration');
  requireThat(rows.length > 0 && new Set(rows.map(key)).size === rows.length, 'Invalid PG inventory.');
  for (const fixed of smoke) requireThat(rows.filter(row => key(row) === key(fixed)).length === 1, 'Fixed smoke missing or changed.');
  for (const name of classes) requireThat(rows.some(row => row.method.startsWith(prefix + name + '.')), 'Mapped class missing.');
  for (const name of methods) requireThat(rows.some(row => row.method === name), 'Mapped method missing.');
  const chosen = rows.filter(row => smoke.some(fixed => key(fixed) === key(row)) || methods.includes(row.method) || classes.some(name => row.method.startsWith(prefix + name + '.')));
  requireThat(chosen.length > 0, 'Empty PostgreSQL selection.');
  return chosen;
}
// Leaf narrowing permits literal changes only. Structural/new-call edits are
// visibly full until their dependency boundary is independently renewed.
export function literalSkeleton(source) {
  return source.replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g, '').replace(/"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g, 'LITERAL').replace(/\b\d+\b/g, 'NUMBER').replace(/\s+/g, '');
}
export function select(root, before, after) {
  for (const sha of [before, after]) requireThat(/^[0-9a-f]{40}$/.test(sha), 'Immutable selection range required.');
  const tree = git(root, 'rev-parse', `${after}^{tree}`).trim();
  const plan = JSON.parse(git(root, 'show', `${after}:tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json`));
  // Smoke integrity is required even when falling back to full.
  selectedRows(plan, [], []);
  const changes = parseDiff(git(root, 'diff', '--raw', '-z', '--no-abbrev', '--find-renames', before, after));
  const classes = new Set(); const reasons = [];
  // Current source, not a frozen row-count cap: a new native consumer forces full
  // until the reviewed native closure is renewed.
  const pgFiles = git(root, 'ls-tree', '-r', '--name-only', after, 'tests/Haby.PostgreSql.IntegrationTests').trim().split('\n').filter(file => file.endsWith('.cs'));
  let unknownNativeConsumer = false;
  for (const file of pgFiles) {
    const text = git(root, 'show', `${after}:${file}`);
    if (/HABY_AOT_CLI_PATH|RunNativeCommitCli\(|ProcessStartInfo|Process\.Start/.test(text) && file !== 'tests/Haby.PostgreSql.IntegrationTests/PostgreSqlIntegrationTests.cs') {
      const owners = [...text.matchAll(/public (?:sealed |abstract )?(?:partial )?class\s+(\w+)/g)].map(match => match[1]);
      if (owners.length === 0 || owners.some(name => ![...nativeClasses, 'ApplicationLoginScenario'].includes(name))) unknownNativeConsumer = true;
    }
    if (/:\s*ApplicationLoginScenario\(/.test(text)) {
      const known = /public sealed (?:partial )?class\s+(\w+)/.exec(text)?.[1];
      if (!known || !nativeClasses.includes(known)) unknownNativeConsumer = true;
    }
  }
  let full = changes.length === 0 || unknownNativeConsumer;
  if (unknownNativeConsumer) reasons.push('unclassified-native-consumer');
  for (const change of changes) {
    const file = change.paths[0];
    if (change.status !== 'M' || change.paths.length !== 1 || change.oldMode !== '100644' || change.newMode !== '100644') { full = true; reasons.push('non-ordinary-change'); continue; }
    if (/^(?:docs\/.+\.md|(?:README|ROADMAP|CONTRIBUTING|AGENTS)\.md)$/.test(file)) continue;
    if (cliLeaves.includes(file)) {
      if (literalSkeleton(git(root, 'show', `${before}:${file}`)) !== literalSkeleton(git(root, 'show', `${after}:${file}`))) { full = true; reasons.push('cli-leaf-structural-change'); }
      nativeClasses.forEach(name => classes.add(name));
      continue;
    }
    if (/^tests\/Haby\.PostgreSql\.IntegrationTests\/[^/]+\.cs$/.test(file)) {
      const text = git(root, 'show', `${after}:${file}`);
      const prior = git(root, 'show', `${before}:${file}`);
      const names = [...text.matchAll(/public sealed (?:partial )?class\s+(\w+)/g)].map(match => match[1]);
      // Abstract/helper/multiple-class edits cannot use filename guesses.
      if (names.length !== 1 || /(?:abstract |internal |private |protected )(?:sealed |partial )*class\s|public\s+static\s/.test(text) ||
          /(?:abstract |internal |private |protected )(?:sealed |partial )*class\s/.test(prior)) { full = true; reasons.push('test-helper-or-class-ambiguity'); continue; }
      const declaration = new RegExp(`class\\s+${names[0]}\\b`);
      const reference = new RegExp(`\\b${names[0]}\\b`);
      const externalConsumer = pgFiles.some(path => {
        const source = git(root, 'show', `${after}:${path}`);
        return reference.test(source) && !declaration.test(source);
      });
      if (externalConsumer) { full = true; reasons.push('test-class-external-consumer'); }
      classes.add(names[0]); continue;
    }
    full = true; reasons.push('shared-sensitive-or-unknown-path');
  }
  if (classes.size === 0) { full = true; reasons.push('no-proven-relevant-class'); }
  const all = plan.entries.filter(row => row.gate === 'postgresql-integration');
  let rows = full ? all : selectedRows(plan, [], [...classes]);
  // One targeted invocation intentionally has one suite. Infrastructure plus
  // fixed functional smoke uses the existing BOTH-suite full entrypoint.
  if (!full && new Set(rows.map(row => row.suite)).size !== 1) {
    full = true; reasons.push('mixed-suite-targeted-fallback'); rows = all;
  }
  return { version: 1, scope: full ? 'full' : 'targeted', before, source: after, tree,
    reasons: [...new Set(reasons)], smoke, classes: [...classes].sort(), cases: full ? [] : rows.map(({method,case: testcase,suite}) => ({method,case:testcase,suite})) };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [root, before, after, output] = process.argv.slice(2);
  requireThat(root && output, 'Root/range/output required.');
  writeFileSync(output, JSON.stringify(select(root, before, after), null, 2));
}
