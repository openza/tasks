import { spawnSync } from 'node:child_process';
import { access, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { reservePublishOutput } from './publish-output.mjs';

// Personal Cloud ships as signed cloud releases that Desktop downloads and
// installs (personal-cloud/docs/cloud-releases.md); a release build never bundles it.
const repositoryRoot = fileURLToPath(new URL('../..', import.meta.url));
const version = process.argv[2];
if (process.argv.length !== 3 || !/^1\.\d+\.\d+$/.test(version ?? ''))
  throw new Error('Usage: node scripts/personal-cloud/publish-desktop.mjs <1.minor.patch>');
const output = join(repositoryRoot, 'artifacts/desktop-publish', version);
await reservePublishOutput(output);
const result = spawnSync('dotnet', ['publish', 'src/Openza.Tasks.Desktop/Openza.Tasks.Desktop.Package.csproj',
  '-c', 'Release', '-p:OpenzaPackagingProfile=Production', `-p:Version=${version}`,
  `-p:InformationalVersion=${version}`, '-o', output],
{ cwd: repositoryRoot, stdio: 'inherit' });
if (result.error) throw result.error;
if (result.status !== 0) throw new Error('Desktop publish failed; incomplete output is retained for diagnosis.');
if (await access(join(output, 'PersonalCloudArtifact')).then(() => true, () => false))
  throw new Error('A release build must not bundle a Personal Cloud artifact.');
if ((await readFile(join(output, '.openza-channel'), 'utf8')).trim() !== 'Production')
  throw new Error('Production channel marker is missing or incorrect.');
console.log(`Verified maintainer publish output: ${output}. Do not launch or install it as part of validation.`);
