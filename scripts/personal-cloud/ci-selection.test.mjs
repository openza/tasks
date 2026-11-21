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
const evaluate = (gate, event_name, code = 'true') => runInNewContext(gate, {
  github: { event_name },
  needs: { changes: { outputs: { code } } },
}, { timeout: 1000 });

test('merge groups trigger CI when checks are requested', () => {
  assert.match(workflow, /^  merge_group:\n    types: \[checks_requested\]$/m);
});

test('docs-only pull requests skip Snap and Windows but keep the packaging contract', () => {
  assert.deepEqual([linuxGate, snapGate, windowsGate].map(gate => evaluate(gate, 'pull_request', 'false')),
    [true, false, false]);
  const [, contract] = workflow.match(/^      - name: Validate Snap packaging contract\n((?:^        .+\n)+)/m) ?? [];
  assert.ok(contract, 'the packaging contract step must exist');
  assert.doesNotMatch(contract, /^        if:/m, 'the packaging contract reads docs and must run for them');
});

test('code changes run Linux, Snap and Windows smoke; scheduled and manual runs use full Windows tests', () => {
  const smokeGate = stepGate('Run Windows smoke tests');
  const fullGate = stepGate('Run full Windows test suite');
  const checks = [linuxGate, snapGate, windowsGate, smokeGate, fullGate];
  for (const event of ['pull_request', 'push', 'merge_group']) {
    assert.deepEqual(checks.map(gate => evaluate(gate, event)), [true, true, true, true, false], event);
  }
  for (const event of ['schedule', 'workflow_dispatch']) {
    assert.deepEqual(checks.map(gate => evaluate(gate, event)), [false, false, true, false, true], event);
  }
});
