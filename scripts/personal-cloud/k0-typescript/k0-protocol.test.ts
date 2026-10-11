import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  K0ContractError,
  canonicalize,
  computeCanonicalHash,
  instanceId,
  parseTaskCreateMutation,
  requireExactObject,
  tasksProduct,
} from "./k0-protocol.ts";

const fixtureRoot = new URL("../../../src/Openza.Tasks.Tests/Fixtures/PersonalCloudK0/", import.meta.url);
const fixture = (name: string): string => readFileSync(new URL(name, fixtureRoot), "utf8");

test("golden mutation has the same canonical form and hash", () => {
  const request = fixture("valid-task-create.json");
  const mutation = parseTaskCreateMutation(request, instanceId);

  assert.equal(mutation.entityId, "local_00000000000000000000000000000001");
  assert.equal(canonicalize(request), fixture("valid-task-create.canonical.json").trim());
  assert.equal(computeCanonicalHash(request), fixture("valid-task-create.sha256").trim());
});

test("golden receipts and change expose only their K0 fields", () => {
  const accepted = requireExactObject(fixture("accepted-receipt.json"), [
    "protocolVersion", "product", "mutationId", "outcome", "entityId", "entityVersion", "changeSeq", "serverAcceptedAt",
  ]);
  const duplicate = requireExactObject(fixture("duplicate-receipt.json"), [
    "protocolVersion", "product", "mutationId", "outcome", "entityId", "entityVersion", "changeSeq", "serverAcceptedAt",
  ]);
  const change = requireExactObject(fixture("task-created-change.json"), [
    "protocolVersion", "product", "changeSeq", "mutationId", "operation", "entityKind", "entityId", "entityVersion", "payload", "serverAcceptedAt",
  ]);

  assert.equal(accepted.outcome, "accepted");
  assert.equal(accepted.product, tasksProduct);
  assert.equal(duplicate.outcome, "duplicate");
  assert.equal(accepted.changeSeq, duplicate.changeSeq);
  assert.equal(change.operation, "task.created");
});

test("unknown task fields fail closed", () => {
  const request = fixture("valid-task-create.json").replace(
    '"title": "Validate one task"',
    '"title": "Validate one task", "notes": "not allowed"',
  );
  assertContractError(() => parseTaskCreateMutation(request, instanceId), "invalid_request");
});

test("envelope and task IDs must match", () => {
  const request = fixture("valid-task-create.json").replace(
    '"entityId": "local_00000000000000000000000000000001"',
    '"entityId": "local_different"',
  );
  assertContractError(() => parseTaskCreateMutation(request, instanceId), "invalid_request");
});

test("unsupported protocol fails closed", () => {
  const request = fixture("valid-task-create.json").replace('"protocolVersion": 1', '"protocolVersion": 2');
  assertContractError(() => parseTaskCreateMutation(request, instanceId), "unsupported_protocol");
});

test("unsupported product fails closed", () => {
  const request = fixture("valid-task-create.json").replace('"product": "tasks"', '"product": "reader"');
  assertContractError(() => parseTaskCreateMutation(request, instanceId), "unsupported_product");
});

test("changed content changes the canonical hash", () => {
  const request = fixture("valid-task-create.json");
  assert.notEqual(computeCanonicalHash(request), computeCanonicalHash(request.replace("Validate one task", "Changed title")));
});

test("duplicate properties are rejected before hashing", () => {
  const request = fixture("valid-task-create.json").replace(
    '"protocolVersion": 1,',
    '"protocolVersion": 1, "protocolVersion": 1,',
  );
  assertContractError(() => computeCanonicalHash(request), "invalid_request");
});

test("title limit is measured in UTF-8 bytes", () => {
  const request = fixture("valid-task-create.json");
  parseTaskCreateMutation(request.replace("Validate one task", "a".repeat(512)), instanceId);
  assertContractError(() => parseTaskCreateMutation(request.replace("Validate one task", "é".repeat(257)), instanceId), "invalid_request");
});

test("missing and provider metadata fields fail closed", () => {
  const request = fixture("valid-task-create.json");
  const missing = request.replace('      "spaceId": "space_default",\n', "");
  const providerMetadata = request.replace(
    '      "title": "Validate one task",',
    '      "title": "Validate one task",\n      "providerMetadataJson": "{}",',
  );

  assertContractError(() => parseTaskCreateMutation(missing, instanceId), "invalid_request");
  assertContractError(() => parseTaskCreateMutation(providerMetadata, instanceId), "invalid_request");
});

test("instance, identifier, timestamp, and Unicode boundaries fail closed", () => {
  const request = fixture("valid-task-create.json");
  parseTaskCreateMutation(request.replace("space_default", "a".repeat(128)), instanceId);

  assertContractError(
    () => parseTaskCreateMutation(request.replace(instanceId, "instance_other"), instanceId),
    "instance_mismatch",
  );
  assertContractError(
    () => parseTaskCreateMutation(request.replace("space_default", "a".repeat(129)), instanceId),
    "invalid_request",
  );
  assertContractError(
    () => parseTaskCreateMutation(request.replace("2026-09-13T06:30:00.000Z", "2026-09-13T06:30:00Z"), instanceId),
    "invalid_request",
  );
  assertContractError(
    () => parseTaskCreateMutation(request.replace("Validate one task", "\\uD800"), instanceId),
    "invalid_request",
  );
});

function assertContractError(action: () => unknown, code: string): void {
  assert.throws(action, (error: unknown) => error instanceof K0ContractError && error.code === code);
}
