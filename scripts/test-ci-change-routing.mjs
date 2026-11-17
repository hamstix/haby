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
  assert.match(workflow, /id: routing\s+run: node \.\/scripts\/ci-change-routing.mjs/);
  for (const id of ['native-aot-windows', 'native-aot-linux', 'postgresql-integration']) {
    assert.match(workflow, new RegExp(`  ${id}:\\r?\\n    needs: validate\\r?\\n    if: needs.validate.outputs.full == 'true'`));
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
