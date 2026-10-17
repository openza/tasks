import { cp, lstat, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { artifactLock, repositoryRoot, stageArtifact, verifyArtifact } from './stage-artifact.mjs';

export async function copySnapArtifact(source, destination, lock = artifactLock) {
  await verifyArtifact(source, lock);
  const existing = await lstat(destination).catch(error => {
    if (error.code !== 'ENOENT') throw error;
    return null;
  });
  if (existing) {
    if (!existing.isDirectory() || existing.isSymbolicLink()) throw new Error('Snap artifact destination must be a regular directory.');
    await verifyArtifact(destination, lock);
    return destination;
  }
  await mkdir(dirname(destination), { recursive: true });
  await cp(source, destination, { recursive: true, force: false, errorOnExist: true });
  await verifyArtifact(destination, lock);
  return destination;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('Usage: node scripts/personal-cloud/stage-snap-artifact.mjs');
  const destination = join(repositoryRoot, 'packaging/personal-cloud/artifact');
  console.log(`Verified Snap artifact: ${await copySnapArtifact(await stageArtifact(), destination)}`);
}
