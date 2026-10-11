import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
const gates = [...workflow.matchAll(/^\s+if: \$\{\{ (.+) \}\}\s*$/gm)].map(match => match[1]);

test('only the authorized same-repository Personal Cloud PR skips Snap and Windows', () => {
  assert.equal(gates.length, 3, 'Snap metadata, Snap build, and Windows each require a gate');
  const cases = [
    ['pull_request', 'openza/tasks', 'fix/personal-cloud-localhost-recovery', false],
    ['pull_request', 'contributor/tasks', 'fix/personal-cloud-localhost-recovery', true],
    ['pull_request', 'openza/tasks', 'fix/unrelated', true],
    ['push', undefined, undefined, true],
  ];
  for (const [eventName, headRepository, headRef, expected] of cases) {
    const github = {
      event_name: eventName,
      repository: 'openza/tasks',
      head_ref: headRef,
      event: eventName === 'pull_request'
        ? { pull_request: { head: { repo: { full_name: headRepository } } } }
        : {},
    };
    for (const gate of gates) {
      assert.equal(runInNewContext(gate, { github }, { timeout: 1000 }), expected,
        `${eventName} ${headRepository ?? 'main'} ${headRef ?? ''}`);
    }
  }
  assert.match(workflow, /build-test-linux:\s+runs-on: ubuntu-latest/,
    'Linux validation must remain unconditional');
});
