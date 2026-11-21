import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
const condition = pattern => {
  const match = workflow.match(pattern);
  assert.ok(match, `Missing workflow condition matching ${pattern}`);
  return match[1].replace(/^\$\{\{\s*|\s*\}\}$/g, '');
};
const jobGate = name => condition(new RegExp(`^  ${name}:\n(?:^    [^\n]*\n)*?^    if: (.+)$`, 'm'));
const stepGate = name => condition(new RegExp(`^      - name: ${name}\n        if: (.+)$`, 'm'));
const linuxGate = jobGate('build-test-linux');
const snapGate = jobGate('build-snap');
const windowsGate = jobGate('build-test');
const gates = [stepGate('Validate Snap packaging contract'), snapGate, windowsGate];

test('only the authorized same-repository Personal Cloud PR skips Snap and Windows', () => {
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
      assert.equal(runInNewContext(gate, { github, needs: changes('true') }, { timeout: 1000 }), expected,
        `${eventName} ${headRepository ?? 'main'} ${headRef ?? ''}`);
    }
    assert.equal(runInNewContext(linuxGate, { github, needs: changes('true') }, { timeout: 1000 }), true,
      'Linux validation must run on PRs and pushes');
  }
});

const changes = code => ({ changes: { outputs: { code } } });

test('docs-only pull requests skip Snap and Windows but keep the packaging contract', () => {
  const github = {
    event_name: 'pull_request',
    repository: 'openza/tasks',
    head_ref: 'docs/update',
    event: { pull_request: { head: { repo: { full_name: 'openza/tasks' } } } },
  };
  const jobGates = gates.filter(gate => gate.includes('needs.changes'));
  assert.equal(jobGates.length, 2, 'Snap build and Windows depend on code changes');
  for (const gate of jobGates) {
    assert.equal(runInNewContext(gate, { github, needs: changes('false') }, { timeout: 1000 }), false);
  }
  assert.equal(runInNewContext(linuxGate, { github, needs: changes('false') }, { timeout: 1000 }), true);
  const contract = workflow.match(/- name: Validate Snap packaging contract\n((?:\s+\S.*\n)+?)\s+run: /)[1];
  assert.doesNotMatch(contract, /needs\.changes/, 'the packaging contract reads docs and must run for them');
});

test('PRs and pushes run the Windows smoke suite; scheduled and manual runs use the full suite', () => {
  const smokeGate = stepGate('Run Windows smoke tests');
  const fullGate = stepGate('Run full Windows test suite');
  for (const event_name of ['pull_request', 'push', 'schedule', 'workflow_dispatch']) {
    const github = {
      event_name,
      repository: 'openza/tasks',
      head_ref: 'fix/unrelated',
      event: { pull_request: { head: { repo: { full_name: 'openza/tasks' } } } },
    };
    const changesRun = event_name === 'pull_request' || event_name === 'push';
    const evaluate = gate => runInNewContext(gate, { github, needs: changes('true') }, { timeout: 1000 });
    assert.deepEqual([linuxGate, snapGate, windowsGate, smokeGate, fullGate].map(evaluate),
      [changesRun, changesRun, true, changesRun, !changesRun], event_name);
  }
});
