import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
const gates = [...workflow.matchAll(/^\s+if: \$\{\{ (.+) \}\}\s*$/gm)].map(match => match[1]);

test('only explicitly scoped same-repository PRs narrow platform validation', () => {
  assert.equal(gates.length, 4, 'Linux app, Snap metadata, Snap build, and Windows each require a gate');
  const cases = [
    ['pull_request', 'openza/tasks', 'fix/personal-cloud-localhost-recovery', [true, false, false, false]],
    ['pull_request', 'openza/tasks', 'fix/hosted-snap-cloud-staging', [false, true, true, false]],
    ['pull_request', 'contributor/tasks', 'fix/personal-cloud-localhost-recovery', [true, true, true, true]],
    ['pull_request', 'contributor/tasks', 'fix/hosted-snap-cloud-staging', [true, true, true, true]],
    ['pull_request', 'openza/tasks', 'fix/unrelated', [true, true, true, true]],
    ['push', undefined, undefined, [true, true, true, true]],
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
    for (const [index, gate] of gates.entries()) {
      assert.equal(runInNewContext(gate, { github }, { timeout: 1000 }), expected[index],
        `${eventName} ${headRepository ?? 'main'} ${headRef ?? ''}`);
    }
  }
});

test('Release Validation narrows Windows and Core only for explicit Linux Snap dispatch', () => {
  const release = readFileSync(new URL('../../.github/workflows/release.yml', import.meta.url), 'utf8');
  const releaseGates = [...release.matchAll(/^\s+if: \$\{\{ (.+) \}\}\s*$/gm)].map(match => match[1]);
  assert.equal(releaseGates.length, 2);
  for (const [event_name, platforms, expected] of [
    ['workflow_dispatch', 'linux-snap', false],
    ['workflow_dispatch', 'all', true],
    ['workflow_dispatch', undefined, true],
    ['push', undefined, true],
  ]) {
    for (const gate of releaseGates)
      assert.equal(runInNewContext(gate, { github: { event_name }, inputs: { platforms } }), expected);
  }
});
