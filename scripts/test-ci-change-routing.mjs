import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { classify, eventRange, git, localLinks, ordinaryDocumentation, parseDiff, route } from './ci-change-routing.mjs';

const sha = '1'.repeat(40);
const record = (status, paths, oldMode = '100644', newMode = '100644') =>
  `:${oldMode} ${newMode} ${sha} ${sha} ${status}\0${paths.join('\0')}\0`;

for (const path of ['README.md', 'ROADMAP.md', 'CONTRIBUTING.md', 'AGENTS.md', 'docs/guide.md', 'docs/adr/decision.md']) {
  test(`ordinary documentation: ${path}`, () => assert.equal(ordinaryDocumentation(path), true));
}
for (const path of ['src/code.cs', '.github/workflows/build.yml', 'scripts/control.mjs', 'unknown.md', 'docs/input.json',
  'docs/examples/input.md', 'docs/development/examples/input.md', 'docs/development/schemas/input.md', 'docs/fixtures/input.md',
  'examples/README.md', 'tests/fixture.md', 'docs/../code.md', 'docs\\guide.md', 'docs/README.MD']) {
  test(`full path: ${path}`, () => assert.equal(classify(parseDiff(record('M', [path]))), 'full'));
}
test('mixed changes cannot skip', () => assert.equal(classify(parseDiff(record('M', ['README.md']) + record('M', ['src/code.cs']))), 'full'));
test('deletion uses old path', () => {
  assert.equal(classify(parseDiff(record('D', ['docs/guide.md'], '100644', '000000'))), 'docs-only');
  assert.equal(classify(parseDiff(record('D', ['src/code.cs'], '100644', '000000'))), 'full');
});
test('both rename paths and modes are authoritative', () => {
  assert.equal(classify(parseDiff(record('R100', ['docs/old.md', 'docs/new.md']))), 'docs-only');
  for (const paths of [['src/input.md', 'docs/input.md'], ['docs/input.md', 'src/input.md']])
    assert.equal(classify(parseDiff(record('R100', paths))), 'full');
  assert.equal(classify(parseDiff(record('T', ['docs/link.md'], '100644', '120000'))), 'full');
});
test('filenames retain tabs, newlines, quotes and shell metacharacters', () => {
  const path = 'docs/a\t\n"$(echo unsafe).md';
  assert.deepEqual(parseDiff(record('M', [path]))[0].paths, [path]);
});
test('incomplete and unsupported diff records fail; empty diff takes full', () => {
  for (const raw of [record('M', ['README.md']).slice(0, -1), record('R100', ['docs/old.md']), ':invalid\0', record('U', ['README.md'])])
    assert.throws(() => parseDiff(raw));
  assert.equal(classify(parseDiff('')), 'full');
});
test('practical local links ignore code, URLs and fragments', () => {
  assert.deepEqual(localLinks('[a](path.md#part) ![b](<image space.png>)\n[r]: ../file.md\n`[fake](bad.md)`\n```md\n[x](bad.md)\n```\n[x](https://example.com) [x](#part)'), ['path.md', 'image space.png', '../file.md']);
});

function repository(run) {
  const root = mkdtempSync(join(tmpdir(), 'haby-routing-'));
  const put = (path, value) => { mkdirSync(join(root, path, '..'), { recursive: true }); writeFileSync(join(root, path), value); };
  const save = () => { git(root, 'add', '--all'); git(root, '-c', 'user.name=Routing Test', '-c', 'user.email=routing@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-m', 'test'); return git(root, 'rev-parse', 'HEAD').trim(); };
  try { git(root, 'init', '--initial-branch=main'); git(root, 'config', 'core.autocrlf', 'false'); run({ root, put, save }); }
  finally { rmSync(root, { recursive: true, force: true }); }
}

test('complete multi-commit push sees earlier code despite final docs commit and docs branch name', () => repository(({ root, put, save }) => {
  put('README.md', '# Test\n'); const before = save();
  git(root, 'switch', '-c', 'codex/docs-not-authority');
  put('src/code.cs', 'code\n'); save(); put('docs/guide.md', '# Guide\n'); const after = save();
  assert.equal(route(root, 'push', { before, after }).route, 'full');
}));
test('entire multi-commit PR uses merge base despite base branch advancing', () => repository(({ root, put, save }) => {
  put('README.md', '# Test\n'); const original = save();
  git(root, 'switch', '-c', 'topic'); put('src/code.cs', 'code\n'); save(); put('docs/guide.md', '# Guide\n'); const head = save();
  git(root, 'switch', 'main'); put('base.txt', 'advance\n'); const base = save();
  const event = { pull_request: { base: { sha: base }, head: { sha: head } } };
  assert.deepEqual(eventRange(root, 'pull_request', event), { before: original, after: head });
  assert.equal(route(root, 'pull_request', event).route, 'full');
}));
test('complete local diff has no API page limit and retains the final code path', () => repository(({ root, put, save }) => {
  put('README.md', '# Test\n'); const before = save();
  for (let i = 0; i < 501; i++) put(`docs/guide-${i}.md`, '# Guide\n');
  put('z-code.cs', 'code\n'); const after = save();
  const result = route(root, 'push', { before, after });
  assert.equal(result.changedEntries, 502);
  assert.equal(result.route, 'full');
}));
test('ordinary docs route validates links; deleted or renamed targets fail incoming references', () => repository(({ root, put, save }) => {
  put('README.md', '[Guide](docs/old.md)\n'); put('docs/old.md', '# Guide\n'); const before = save();
  put('docs/old.md', '# Updated\n'); const good = save();
  assert.equal(route(root, 'push', { before, after: good }).route, 'docs-only');
  git(root, 'mv', 'docs/old.md', 'docs/new.md'); const renamed = save();
  assert.throws(() => route(root, 'push', { before, after: renamed }), /broken local/);
  git(root, 'rm', 'docs/new.md'); const deleted = save();
  assert.throws(() => route(root, 'push', { before, after: deleted }), /broken local/);
  put('README.md', '# Guide removed\n'); const repaired = save();
  assert.equal(route(root, 'push', { before, after: repaired }).route, 'docs-only');
}));
test('new broken inline/reference links and whitespace fail; historical broken links do not', () => repository(({ root, put, save }) => {
  put('README.md', '[old](missing.md)\n'); const before = save();
  put('docs/guide.md', '# Guide\n'); const good = save();
  assert.equal(route(root, 'push', { before, after: good }).route, 'docs-only');
  put('docs/guide.md', '[new](missing.md)\n[r]: missing-reference.md\n'); const broken = save();
  assert.throws(() => route(root, 'push', { before, after: broken }), /broken local/);
  put('docs/guide.md', '# Guide  \n'); const whitespace = save();
  assert.throws(() => route(root, 'push', { before, after: whitespace }));
}));
test('missing/zero/invalid/unavailable commits, unsupported events, failed Git and shallow history fail', () => repository(({ root, put, save }) => {
  put('README.md', '# Test\n'); const before = save();
  for (const after of [undefined, '0'.repeat(40), '--help', sha]) assert.throws(() => route(root, 'push', { before, after }));
  assert.throws(() => route(root, 'workflow_dispatch', { before, after: before }));
  assert.throws(() => route(join(root, 'absent'), 'push', { before, after: before }));
  writeFileSync(join(root, '.git', 'shallow'), `${before}\n`);
  assert.throws(() => route(root, 'push', { before, after: before }), /Complete history/);
}));
test('CLI failure cannot emit skip output or success', () => repository(({ root, put, save }) => {
  put('README.md', '# Test\n'); save();
  put('event.json', '{}'); put('output.txt', '');
  assert.throws(() => execFileSync(process.execPath, [fileURLToPath(new URL('./ci-change-routing.mjs', import.meta.url))], {
    cwd: root, stdio: 'pipe', env: { ...process.env, GITHUB_EVENT_NAME: 'push', GITHUB_EVENT_PATH: join(root, 'event.json'), GITHUB_OUTPUT: join(root, 'output.txt') }
  }));
  assert.equal(readFileSync(join(root, 'output.txt'), 'utf8'), '');
}));

test('workflow keeps mandatory gate, names, conditions and permissions', () => {
  const workflow = readFileSync(new URL('../.github/workflows/build.yml', import.meta.url), 'utf8');
  assert.doesNotMatch(workflow, /paths-ignore:|pull_request_target:/);
  assert.match(workflow, /permissions:\s+contents: read/);
  assert.match(workflow, /fetch-depth: 0/);
  assert.match(workflow, /node --test \.\/scripts\/test-ci-change-routing.mjs/);
  assert.match(workflow, /id: routing\s+env:\s+QUALIFICATION_CANDIDATE: \$\{\{ inputs.candidate \}\}/);
  assert.match(workflow, /else\s+node \.\/scripts\/ci-change-routing.mjs\s+fi/);
  assert.match(workflow, /echo 'full=true' >> "\$GITHUB_OUTPUT"/);
  for (const id of ['native-aot-windows', 'native-aot-linux', 'postgresql-integration']) {
    assert.match(workflow, new RegExp(`  ${id}:\\r?\\n    needs: validate\\r?\\n    if: needs.validate.outputs.runtime == 'true'`));
  }
  assert.match(workflow, /name: Native AOT \(win-x64\)/);
  assert.match(workflow, /name: Native AOT \(linux-x64\)/);
  assert.match(workflow, /HABY_NATIVE_RID: win-x64/); assert.match(workflow, /HABY_NATIVE_RID: linux-x64/);
  assert.match(workflow, /steps: &native-steps/); assert.match(workflow, /steps: \*native-steps/);
  assert.doesNotMatch(workflow, /matrix\./);
  assert.match(workflow, /dependency-review:\s+if: github.event_name == 'pull_request'/);
  const validate = workflow.split('  native-aot-windows:')[0];
  for (const step of validate.split('      - ').filter(step => /setup-dotnet|dotnet |export-test-gate|upload-artifact/.test(step)))
    assert.match(step, /if: (?:always\(\) && )?steps.routing.outputs.full == 'true'/);
});

test('manual focused contours preserve automatic and reusable full defaults', () => {
  const workloads = readFileSync(new URL('../.github/workflows/kubernetes-deployment-kernel.yml', import.meta.url), 'utf8');
  assert.ok(workloads.includes('options: [all, configured-deployment, durable-actions, deployment-kernel]'));
  assert.ok(workloads.includes('default: all'));
  assert.equal((workloads.match(/focused-only-not-final-/g) ?? []).length, 3);
  const pg = readFileSync(new URL('../.github/workflows/infrastructure-qualification.yml', import.meta.url), 'utf8');
  assert.ok(pg.includes('focused-only-not-final-postgresql-'));
  assert.ok(pg.includes('Focused PostgreSQL (not final)'));
  assert.ok(pg.includes('-Suite infrastructure-qualification'));
  assert.ok(pg.includes('Assert-HabyFocusedPostgreSqlPlan'));
  assert.ok(pg.indexOf('Admit exact current focused selection') < pg.indexOf('Validate native prerequisites'));
});

// Evaluate only the boolean/string subset used by these actual workflow fields.
// Missing context properties are empty strings, as in GitHub expressions. This
// reads producer expressions, rather than copying routing into a test policy.
function workflowExpression(expression, context) {
  const tokens = expression.match(/\|\||&&|==|!=|[()]|'[^']*'|[A-Za-z_][A-Za-z0-9_.]*/g) ?? [];
  assert.equal(tokens.join(''), expression.replace(/\s+(?=(?:[^']*'[^']*')*[^']*$)/g, ''));
  let index = 0;
  const atom = () => {
    const token = tokens[index++];
    if (token === '(') { const value = or(); assert.equal(tokens[index++], ')'); return value; }
    if (token?.startsWith("'")) return token.slice(1, -1);
    assert.match(token ?? '', /^(github|inputs|needs|steps)\.[A-Za-z0-9_.]+$/);
    return token.split('.').reduce((value, key) => value?.[key], context) ?? '';
  };
  const equality = () => {
    const value = atom(); const operator = tokens[index];
    if (operator !== '==' && operator !== '!=') return value;
    index++; const right = atom();
    const same = typeof value === 'string' && typeof right === 'string' ? value.toLowerCase() === right.toLowerCase() : value === right;
    return operator === '==' ? same : !same;
  };
  const and = () => { let value = equality(); while (tokens[index] === '&&') { index++; const right = equality(); value = value && right; } return value; };
  const or = () => { let value = and(); while (tokens[index] === '||') { index++; const right = and(); value = value || right; } return value; };
  const value = or(); assert.equal(index, tokens.length); return value;
}
function workloadRoute(event, inputs = {}) {
  const source = readFileSync(new URL('../.github/workflows/kubernetes-deployment-kernel.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n').split('\njobs:\n')[1];
  const headings = [...source.matchAll(/^  ([a-z][a-z0-9-]*):\n/gm)];
  const context = { inputs, github: { event_name: event, sha: 'a'.repeat(40), run_attempt: '1', event: { pull_request: { head: { sha: '' } } } } };
  const render = value => value.replace(/\$\{\{(.*?)\}\}/g, (_, expression) => String(workflowExpression(expression.trim(), context)));
  return headings.map((heading, i) => {
    const block = source.slice(heading.index, headings[i + 1]?.index);
    const condition = block.match(/^    if: (.+)$/m)?.[1];
    const enabled = Boolean(workflowExpression(condition, context));
    let admitted = null;
    if (heading[1] === 'validate-focused-selection' && enabled) {
      const program = block.match(/^\s*node -e "(.+)"$/m)?.[1]; assert.ok(program);
      try {
        execFileSync(process.execPath, ['-e', program], { env: { ...process.env, CONTOUR: inputs.contour ?? '', EXPECTED_HEAD: inputs.expected_head ?? '', ACTUAL_HEAD: context.github.sha }, stdio: 'pipe' });
        admitted = true;
      } catch (error) { assert.equal(error.status, 1); admitted = false; }
    }
    return { id: heading[1], enabled, admitted,
      name: render(block.match(/^    name: (.+)$/m)?.[1] ?? heading[1]),
      artifact: render(block.match(/^          name: (.+)$/m)?.[1] ?? '') };
  });
}
const workloadIds = ['configured-deployment', 'durable-actions', 'deployment-kernel'];
function assertFullWorkloads(route, source) {
  assert.equal(route.find(job => job.id === 'validate-focused-selection').enabled, false);
  const jobs = route.filter(job => workloadIds.includes(job.id));
  assert.equal(jobs.length, 3);
  for (const job of jobs) {
    assert.equal(job.enabled, true, job.id); assert.equal(job.name, job.id);
    assert.equal(job.artifact, `kubernetes-${job.id}-${source}${job.id === 'durable-actions' ? '-1' : ''}`);
  }
}
test('dispatch caller with reusable candidate and absent focused inputs runs all original full workloads', () => {
  assertFullWorkloads(workloadRoute('workflow_dispatch', { candidate: 'b'.repeat(40) }), 'b'.repeat(40));
  // A caller's focused-looking inputs cannot override required reusable candidate.
  assertFullWorkloads(workloadRoute('workflow_dispatch', { candidate: 'b'.repeat(40), contour: 'unknown', expected_head: 'stale' }), 'b'.repeat(40));
});
test('direct all and automatic PR preserve full identities while each focused contour is isolated', () => {
  assertFullWorkloads(workloadRoute('workflow_dispatch', { contour: 'all' }), 'a'.repeat(40));
  assertFullWorkloads(workloadRoute('pull_request'), 'a'.repeat(40));
  for (const contour of workloadIds) {
    const route = workloadRoute('workflow_dispatch', { contour, expected_head: 'a'.repeat(40) });
    assert.equal(route.find(job => job.id === 'validate-focused-selection').enabled, true);
    assert.equal(route.find(job => job.id === 'validate-focused-selection').admitted, true);
    const jobs = route.filter(job => workloadIds.includes(job.id) && job.enabled);
    assert.equal(jobs.length, 1); assert.equal(jobs[0].id, contour); assert.equal(jobs[0].name, `Focused ${contour}`);
    assert.equal(jobs[0].artifact, `focused-only-not-final-kubernetes-${contour}-${'a'.repeat(40)}${contour === 'durable-actions' ? '-1' : ''}`);
  }
});
test('unknown stale or absent direct focused inputs launch no runtime workload', () => {
  for (const inputs of [{}, { contour: 'unknown', expected_head: 'a'.repeat(40) }, { contour: 'deployment-kernel', expected_head: 'b'.repeat(40) }, { contour: 'durable-actions' }]) {
    const route = workloadRoute('workflow_dispatch', inputs);
    assert.equal(route.find(job => job.id === 'validate-focused-selection').enabled, true);
    assert.equal(route.find(job => job.id === 'validate-focused-selection').admitted, false);
    assert.equal(route.filter(job => workloadIds.includes(job.id) && job.enabled).length, 0);
  }
});

test('focused admission agrees with case-insensitive workflow string identity comparisons', () => {
  assertFullWorkloads(workloadRoute('workflow_dispatch', { contour: 'ALL' }), 'a'.repeat(40));
  const route = workloadRoute('workflow_dispatch', { contour: 'DEPLOYMENT-KERNEL', expected_head: 'A'.repeat(40) });
  assert.equal(route.find(job => job.id === 'validate-focused-selection').admitted, true);
  assert.deepEqual(route.filter(job => workloadIds.includes(job.id) && job.enabled).map(job => job.id), ['deployment-kernel']);
});

test('runtime output is a legacy alias and does not activate fast-only ordinary routes', () => {
  const source = readFileSync(new URL('../.github/workflows/build.yml', import.meta.url), 'utf8');
  const alias = source.match(/^      runtime: \$\{\{ (.+) \}\}/m)?.[1]; assert.ok(alias);
  const gates = [...source.matchAll(/^    if: (needs\.validate\.outputs\.runtime == 'true')/gm)]; assert.equal(gates.length, 3);
  for (const full of ['true', 'false', '']) {
    const runtime = workflowExpression(alias, { steps: { routing: { outputs: { full } } } });
    assert.equal(runtime, full);
    for (const gate of gates) assert.equal(Boolean(workflowExpression(gate[1], { needs: { validate: { outputs: { runtime } } } })), full === 'true');
  }
});

test('installed workflow derives scope in trusted prepare and gates only runtime groups', () => {
  const source = readFileSync(new URL('../.github/workflows/final-qualification.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
  assert.ok(source.includes('scope: ${{ steps.candidate.outputs.scope }}'));
  assert.ok(source.includes("core.setOutput('scope', identity.scope)"));
  assert.ok(source.includes('fetch-depth: 0'));
  assert.equal((source.match(/^      scope:/gm) ?? []).length, 1);
  const groups = ['build', 'api', 'workloads'].map(id => source.split(`  ${id}:\n`)[1].match(/^    if: (.+)$/m)[1]);
  assert.equal(groups.length, 3);
  const download = source.split("name: Download only this invocation's evidence")[1].match(/^        if: (.+)$/m)[1];
  for (const scope of ['full', 'docs-only', '', 'unknown']) {
    const context = { needs: { prepare: { outputs: { scope } } } };
    for (const condition of [...groups, download]) assert.equal(Boolean(workflowExpression(condition, context)), scope === 'full');
  }
  assert.ok(source.includes("if (identity.scope === 'full' && process.env.DOWNLOAD !== 'success') results.build = 'failure'"));
  assert.ok(source.includes("if: always() && needs.prepare.result == 'success' && needs.start.result == 'success'"));
});
