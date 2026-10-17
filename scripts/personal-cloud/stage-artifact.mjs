import { createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, rm, lstat, readdir } from 'node:fs/promises';
import { dirname, join, resolve, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = resolve(scriptDirectory, '../..');
export const artifactLock = JSON.parse(await readFile(join(scriptDirectory, 'artifact-lock.json'), 'utf8'));
export const stagedDirectory = join(repositoryRoot, 'artifacts/personal-cloud', artifactLock.revision, 'artifact');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

export async function verifyArtifact(directory, lock = artifactLock) {
  const manifestBytes = await readFile(join(directory, 'artifact-manifest.json'));
  if (sha256(manifestBytes) !== lock.manifestSha256) throw new Error('Artifact manifest does not match the source lock.');
  const manifest = JSON.parse(manifestBytes);
  if (manifest.formatVersion !== 1 || manifest.artifactVersion !== lock.artifactVersion)
    throw new Error('Artifact version does not match the source lock.');
  const entries = [manifest.worker, ...manifest.assets, ...manifest.migrations];
  const expected = new Set(['artifact-manifest.json']);
  for (const entry of entries) {
    const path = resolve(directory, entry.file);
    const inside = relative(resolve(directory), path).split(sep).join('/');
    if (!inside || inside.startsWith('..') || inside !== entry.file || expected.has(entry.file))
      throw new Error('Invalid or duplicate artifact path.');
    expected.add(entry.file);
    const stat = await lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Artifact entries must be regular files.');
    const bytes = await readFile(path);
    if (bytes.length !== entry.size || sha256(bytes) !== entry.sha256) throw new Error(`Artifact checksum mismatch: ${entry.file}`);
  }
  async function inspect(path, prefix = '') {
    for (const item of await readdir(path, { withFileTypes: true })) {
      const name = prefix + item.name;
      if (item.isDirectory()) await inspect(join(path, item.name), name + '/');
      else if (!item.isFile() || !expected.delete(name)) throw new Error(`Unexpected artifact entry: ${name}`);
    }
  }
  await inspect(directory);
  if (expected.size) throw new Error('Artifact files are missing.');
  return manifest;
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', env: { ...process.env, CI: 'true', WRANGLER_SEND_METRICS: 'false' } });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed (${result.status}).`);
}

export async function stageArtifact() {
  if (!/^[a-f0-9]{40}$/.test(artifactLock.revision)) throw new Error('A full source revision is required.');
  const parent = dirname(stagedDirectory);
  await mkdir(parent, { recursive: true });
  // Reuse only a fully verified artifact. Never trust a sibling checkout or dist directory.
  try {
    await lstat(stagedDirectory);
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    return await buildArtifact(parent);
  }
  await verifyArtifact(stagedDirectory);
  return stagedDirectory;
}

async function buildArtifact(parent) {
  const temporary = await mkdtemp(join(parent, 'build-'));
  try {
    run('git', ['init', '--quiet'], temporary);
    run('git', ['remote', 'add', 'origin', artifactLock.repository], temporary);
    run('git', ['fetch', '--depth=1', 'origin', artifactLock.revision], temporary);
    run('git', ['checkout', '--detach', '--quiet', artifactLock.revision], temporary);
    run('npm', ['ci', '--no-audit', '--no-fund'], temporary);
    run('npm', ['run', 'build:artifact', '--', artifactLock.artifactVersion], temporary);
    const built = join(temporary, 'dist', `openza-personal-cloud-${artifactLock.artifactVersion}`);
    await verifyArtifact(built);
    await cp(built, stagedDirectory, { recursive: true, force: false, errorOnExist: true });
    await verifyArtifact(stagedDirectory);
    return stagedDirectory;
  } finally {
    await rm(temporary, { recursive: true, force: true });
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('Usage: node scripts/personal-cloud/stage-artifact.mjs');
  console.log(`Verified artifact: ${await stageArtifact()}`);
}
