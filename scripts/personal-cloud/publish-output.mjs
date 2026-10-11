import { mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';

export async function reservePublishOutput(output) {
  await mkdir(dirname(output), { recursive: true });
  try {
    // Exclusive reservation: another publisher must never share this output directory.
    await mkdir(output);
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
    throw new Error('Publish destination already exists; choose a new version or preserve and move the previous output.', { cause: error });
  }
}
