import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { repositoryRoot, stageArtifact, verifyArtifact } from './stage-artifact.mjs';
import { reservePublishOutput } from './publish-output.mjs';

const version = process.argv[2];
if (process.argv.length !== 3 || !/^1\.\d+\.\d+$/.test(version ?? ''))
  throw new Error('Usage: node scripts/personal-cloud/publish-desktop.mjs <1.minor.patch>');
const output = join(repositoryRoot, 'artifacts/desktop-publish', version);
const artifact = await stageArtifact();
await reservePublishOutput(output);
const result = spawnSync('dotnet', ['publish', 'src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.Package.csproj',
  '-c', 'Release', '-p:OpenzaPackagingProfile=Production', `-p:Version=${version}`,
  `-p:InformationalVersion=${version}`, `-p:PersonalCloudArtifactDirectory=${artifact}`, '-o', output],
{ cwd: repositoryRoot, stdio: 'inherit' });
if (result.error) throw result.error;
if (result.status !== 0) throw new Error('Desktop publish failed; incomplete output is retained for diagnosis.');
await verifyArtifact(join(output, 'PersonalCloudArtifact'));
if ((await readFile(join(output, '.openza-channel'), 'utf8')).trim() !== 'Production')
  throw new Error('Production channel marker is missing or incorrect.');
console.log(`Verified maintainer publish output: ${output}. Do not launch or install it as part of validation.`);
