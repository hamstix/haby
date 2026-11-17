import { execFileSync } from 'node:child_process';
import { appendFileSync, readFileSync } from 'node:fs';
import { posix } from 'node:path';
import { pathToFileURL } from 'node:url';

// No shell, API pagination, line-delimited filenames or partial stdout recovery.
export function git(root, ...args) {
  const bytes = execFileSync('git', ['-C', root, ...args], { maxBuffer: 64 * 1024 * 1024, stdio: ['pipe', 'pipe', 'pipe'] });
  return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
}

export function ordinaryDocumentation(path) {
  if (path.includes('\\') || path.split('/').some(part => !part || part === '.' || part === '..')) return false;
  // Example/schema/fixture trees remain functional even if a Markdown input is added.
  if (/^docs\/(?:.*\/)?(?:examples|schemas|fixtures)(?:\/|$)/.test(path)) return false;
  return /^(?:README|ROADMAP|CONTRIBUTING|AGENTS)\.md$/.test(path) || /^docs\/.+\.md$/.test(path);
}

export function parseDiff(raw) {
  if (!raw) return [];
  if (!raw.endsWith('\0')) throw new Error('Incomplete NUL-delimited diff.');
  const fields = raw.slice(0, -1).split('\0');
  const changes = [];
  while (fields.length) {
    const match = /^:(\d{6}) (\d{6}) [0-9a-f]{40} [0-9a-f]{40} ([AMDTRC])(?:\d+)?$/.exec(fields.shift());
    if (!match) throw new Error('Unsupported or incomplete raw diff record.');
    const paths = [fields.shift()];
    if (match[3] === 'R' || match[3] === 'C') paths.push(fields.shift());
    if (paths.some(path => !path)) throw new Error('Missing diff path.');
    changes.push({ oldMode: match[1], newMode: match[2], status: match[3], paths });
  }
  return changes;
}

export function classify(changes) {
  // Empty/no-op changes take full CI; absence of records is never skip authority.
  return changes.length > 0 && changes.every(change =>
    [change.oldMode, change.newMode].every(mode => mode === '000000' || mode === '100644') &&
    change.paths.every(ordinaryDocumentation)) ? 'docs-only' : 'full';
}

function commit(root, sha) {
  if (!/^[0-9a-f]{40}$/.test(sha ?? '') || /^0+$/.test(sha)) throw new Error('Missing or invalid event commit.');
  if (git(root, 'rev-parse', '--verify', `${sha}^{commit}`).trim() !== sha) throw new Error('Event commit unavailable.');
  return sha;
}

export function eventRange(root, eventName, event) {
  const after = commit(root, eventName === 'pull_request' ? event.pull_request?.head?.sha : event.after);
  const before = commit(root, eventName === 'pull_request' ? event.pull_request?.base?.sha : event.before);
  if (eventName === 'push') return { before, after };
  if (eventName !== 'pull_request') throw new Error('Unsupported routing event.');
  const bases = git(root, 'merge-base', '--all', before, after).trim().split('\n');
  if (bases.length !== 1) throw new Error('Ambiguous PR merge base.');
  return { before: commit(root, bases[0]), after };
}

function tree(root, revision) {
  const raw = git(root, 'ls-tree', '-r', '-z', '--full-tree', revision);
  if (raw && !raw.endsWith('\0')) throw new Error('Incomplete tree.');
  return new Map(raw.split('\0').filter(Boolean).map(record => {
    const match = /^(\d{6}) (blob|commit) ([0-9a-f]{40})\t([\s\S]+)$/.exec(record);
    if (!match) throw new Error('Invalid tree record.');
    return [match[4], { mode: match[1], sha: match[3] }];
  }));
}

// Practical repository links: inline/image links and reference definitions, path
// existence only. Fragments, external URLs, fenced code and inline code are ignored.
export function localLinks(markdown) {
  const prose = markdown.replace(/^\s*(`{3,}|~{3,})[^\n]*\n[\s\S]*?^\s*\1\s*$/gm, '').replace(/`[^`\n]*`/g, '');
  const links = [];
  const pattern = /!?\[[^\]\n]*\]\(\s*(<[^>\n]+>|[^\s)]*(?:\([^)]*\)[^\s)]*)*)[^\n)]*\)|^\s*\[[^\]\n]+\]:\s*(<[^>\n]+>|\S+)/gm;
  for (const match of prose.matchAll(pattern)) {
    let target = (match[1] ?? match[2]).replace(/^<|>$/g, '');
    if (!target || /^(?:[a-z][a-z0-9+.-]*:|\/|#)/i.test(target)) continue;
    target = target.split(/[?#]/)[0];
    if (target) links.push(decodeURIComponent(target));
  }
  return links;
}

function brokenLinks(root, revision, blobs) {
  const entries = tree(root, revision);
  const broken = new Map();
  for (const [file, entry] of entries) {
    if (!file.endsWith('.md') || entry.mode !== '100644') continue;
    if (!blobs.has(entry.sha)) blobs.set(entry.sha, git(root, 'cat-file', 'blob', entry.sha));
    for (const target of localLinks(blobs.get(entry.sha))) {
      const resolved = posix.normalize(posix.join(posix.dirname(file), target)).replace(/\/$/, '');
      const exists = entries.has(resolved) || [...entries.keys()].some(path => path.startsWith(`${resolved}/`)) || resolved === '.';
      if (!exists) {
        const key = JSON.stringify([file, target]);
        broken.set(key, (broken.get(key) ?? 0) + 1);
      }
    }
  }
  return broken;
}

export function validateDocumentation(root, before, after) {
  // Added whitespace defects fail on every route. Existing defects are not waived
  // by edits, and paths never become command text.
  git(root, '-c', 'core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol', 'diff', '--check', before, after, '--');
  const blobs = new Map();
  const baseline = brokenLinks(root, before, blobs);
  const current = brokenLinks(root, after, blobs);
  if ([...current].some(([key, count]) => count > (baseline.get(key) ?? 0))) throw new Error('New broken local Markdown links (including incoming links to deleted or renamed targets).');
}

export function route(root, eventName, event) {
  if (git(root, 'rev-parse', '--is-shallow-repository').trim() !== 'false') throw new Error('Complete history required.');
  const range = eventRange(root, eventName, event);
  const changes = parseDiff(git(root, 'diff', '--raw', '-z', '--no-abbrev', '--no-ext-diff', '--no-textconv', '--find-renames', range.before, range.after, '--'));
  validateDocumentation(root, range.before, range.after);
  return { ...range, route: classify(changes), changedEntries: changes.length };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = route(process.cwd(), process.env.GITHUB_EVENT_NAME, JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')));
    console.log(JSON.stringify(result));
    if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `full=${result.route === 'full'}\n`);
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY,
      `## CI routing\n${result.before} → ${result.after}: **${result.route}** (${result.changedEntries} entries).\n` +
      (result.route === 'docs-only' ? 'Ordinary documentation only: solution/fast tests, PostgreSQL and both Native AOT jobs are intentionally skipped. No test-case, coverage or artifact credit. Dependency review is unchanged.\n' : 'Code, mixed or unknown paths: full existing applicable validation required.\n'));
  } catch {
    // Avoid echoing untrusted filenames or arbitrary Git diagnostics as workflow commands.
    console.error('CI routing/documentation validation failed; no skip authorization produced.');
    process.exitCode = 1;
  }
}
