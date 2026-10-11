import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { verifyArtifact } from './stage-artifact.mjs';
import { reservePublishOutput } from './publish-output.mjs';
import { stageSnapArtifact } from './stage-snap-artifact.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
async function fixture(t, file = 'worker.js') {
  const directory = await mkdtemp(join(tmpdir(), 'openza-artifact-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const bytes = Buffer.from('test Worker');
  const manifest = Buffer.from(JSON.stringify({ formatVersion: 1, artifactVersion: 'test',
    worker: { file, size: bytes.length, sha256: hash(bytes) }, assets: [], migrations: [] }));
  await writeFile(join(directory, 'artifact-manifest.json'), manifest);
  await writeFile(join(directory, 'worker.js'), bytes);
  return { directory, lock: { artifactVersion: 'test', manifestSha256: hash(manifest) } };
}
test('valid artifact passes; altered bytes and additional private files fail', async t => {
  const { directory, lock } = await fixture(t);
  await verifyArtifact(directory, lock);
  await writeFile(join(directory, 'private.env'), 'fixture');
  await assert.rejects(verifyArtifact(directory, lock), /Unexpected/);
  await rm(join(directory, 'private.env'));
  await writeFile(join(directory, 'worker.js'), 'changed');
  await assert.rejects(verifyArtifact(directory, lock), /checksum/);
});
test('modified manifest fails pinned digest', async t => {
  const { directory, lock } = await fixture(t);
  await writeFile(join(directory, 'artifact-manifest.json'), '{}');
  await assert.rejects(verifyArtifact(directory, lock), /source lock/);
});
test('traversal paths and symlink entries fail', async t => {
  const traversal = await fixture(t, '../worker.js');
  await assert.rejects(verifyArtifact(traversal.directory, traversal.lock), /path/);
  const { directory, lock } = await fixture(t);
  await mkdir(join(directory, 'external'));
  await writeFile(join(directory, 'external/worker.js'), 'test Worker');
  await rm(join(directory, 'worker.js'));
  await symlink('external/worker.js', join(directory, 'worker.js'));
  await assert.rejects(verifyArtifact(directory, lock), /regular files/);
});
test('overlapping publishers reserve exactly one destination and preserve its files', async t => {
  const { directory } = await fixture(t);
  const output = join(directory, 'publish/version');
  const results = await Promise.allSettled([reservePublishOutput(output), reservePublishOutput(output)]);
  assert.equal(results.filter(item => item.status === 'fulfilled').length, 1);
  assert.equal(results.filter(item => item.status === 'rejected').length, 1);
  await writeFile(join(output, 'retained.txt'), 'keep');
  await assert.rejects(reservePublishOutput(output), /already exists/);
  assert.equal(await readFile(join(output, 'retained.txt'), 'utf8'), 'keep');
});
test('Snap staging verifies before copying and preserves an existing destination', async t => {
  const { directory, lock } = await fixture(t);
  const output = join(directory, 'snap/artifact');
  await writeFile(join(directory, 'worker.js'), 'changed');
  await assert.rejects(stageSnapArtifact(directory, output, lock), /checksum/);
  await assert.rejects(readFile(join(output, 'artifact-manifest.json')), { code: 'ENOENT' });
  await writeFile(join(directory, 'worker.js'), 'test Worker');
  // Keep the destination outside the source: verified artifacts reject extra files.
  const parent = await mkdtemp(join(tmpdir(), 'openza-snap-stage-test-'));
  t.after(() => rm(parent, { recursive: true, force: true }));
  const destination = join(parent, 'artifact');
  await stageSnapArtifact(directory, destination, lock);
  await verifyArtifact(destination, lock);
  await writeFile(join(destination, 'retained.txt'), 'keep');
  await assert.rejects(stageSnapArtifact(directory, destination, lock), /already exists/);
  assert.equal(await readFile(join(destination, 'retained.txt'), 'utf8'), 'keep');
});
