import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Execute the actual workflow adapter with only synthetic fs/GitHub operations.
const workflow = readFileSync(new URL('../.github/workflows/runtime-consumer-image.yml', import.meta.url), 'utf8');
const match = workflow.match(/          script: \|\r?\n([\s\S]*?)      - name: Authenticate/);
assert.ok(match, 'Publisher adapter block missing');
const adapter = match[1].split(/\r?\n/).map(line => line.startsWith('            ') ? line.slice(12) : line).join('\n');
let cases = 0;
for (const operation of ['package-lookup', 'package-versions']) {
  const statuses = operation === 'package-lookup' ? [404, 401, 403, undefined, '403', 403.5, 999] : [404, 401, 403, undefined];
  for (const status of statuses) {
    let lookups = 0, versions = 0, writes = 0, outputs = 0;
    const failure = Object.assign(new Error('SENTINEL_PRIVATE_MESSAGE'), {
      status, response: { data: 'SENTINEL_PRIVATE_BODY', headers: { authorization: 'SENTINEL_PRIVATE_TOKEN' } }
    });
    const fs = {
      readFileSync(path) {
        if (path.endsWith('inputs.json')) return JSON.stringify({ key: 'a'.repeat(64), acceptedImage: null });
        if (path.endsWith('runtime-consumer-first-package.json')) return JSON.stringify({ package: 'ghcr.io/hamstix/haby-runtime-consumer', confirmed: false });
        throw new Error('Unexpected adapter file read');
      },
      writeFileSync() { writes++; }
    };
    const github = {
      async request(route) {
        lookups++;
        assert.equal(route, 'GET /orgs/{org}/packages/{package_type}/{package_name}');
        if (operation === 'package-lookup') throw failure;
        return { data: {} };
      },
      async paginate(route) {
        versions++;
        assert.equal(route, 'GET /orgs/{org}/packages/{package_type}/{package_name}/versions');
        throw failure;
      }
    };
    let message;
    try {
      await vm.runInNewContext(`(async () => {${adapter}\n})()`, {
        github, core: { setOutput() { outputs++; } },
        require(name) { assert.equal(name, 'fs'); return fs; }
      }, { timeout: 1000 });
    } catch (error) { message = error.message; }
    const expected = Number.isInteger(status) && status >= 100 && status <= 599 ? status : 'transport-unknown';
    assert.equal(message, `Package observation stopped: operation=${operation}; status=${expected}; no build/push authorized`);
    assert.equal(lookups, 1, 'No package probe retry');
    assert.equal(versions, operation === 'package-versions' ? 1 : 0, 'No adjacent query after lookup failure');
    assert.equal(writes, 0, 'Failure cannot authorize registry state');
    assert.equal(outputs, 0, 'Failure cannot authorize build');
    assert.ok(!message.includes('SENTINEL'), 'Private error content escaped');
    cases++;
  }
}
console.log(`PASS actual registry adapter: ${cases} synthetic failures; bounded status/operation only; no private output, retries or build authorization.`);
