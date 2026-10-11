import { cp, readdir } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { artifactLock, repositoryRoot, stageArtifact, verifyArtifact } from './stage-artifact.mjs';
import { reservePublishOutput } from './publish-output.mjs';

export async function stageSnapArtifact(source, destination, lock = artifactLock) {
  await verifyArtifact(source, lock);
  await reservePublishOutput(destination);
  for (const entry of await readdir(source)) {
    await cp(join(source, entry), join(destination, entry), { recursive: true, force: false, errorOnExist: true });
  }
  await verifyArtifact(destination, lock);
  return destination;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.length !== 2) throw new Error('Usage: node scripts/personal-cloud/stage-snap-artifact.mjs');
  const source = await stageArtifact();
  const destination = join(repositoryRoot, 'packaging/personal-cloud/artifact');
  console.log(`Verified Snap artifact: ${await stageSnapArtifact(source, destination)}`);
}
