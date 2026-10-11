import { createHash } from "node:crypto";

export const protocolVersion = 1;
export const tasksProduct = "tasks";
export const instanceId = "instance_00000000000000000000000000000001";
const maximumIdentifierLength = 128;
const maximumTitleUtf8Bytes = 512;
const identifierPattern = /^[A-Za-z0-9_.:-]+$/;
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/;

type JsonPrimitive = null | boolean | number | string;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };

export type K0TaskCreateMutation = {
  protocolVersion: number;
  product: "tasks";
  instanceId: string;
  deviceId: string;
  mutationId: string;
  deviceSequence: number;
  operation: "task.create";
  entityId: string;
  baseEntityVersion: null;
  payload: {
    task: {
      id: string;
      spaceId: string;
      title: string;
      completionState: "open";
      workflowStatus: "inbox";
      createdAt: string;
    };
  };
  clientCreatedAt: string;
};

export class K0ContractError extends Error {
  public readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

export function parseTaskCreateMutation(json: string, expectedInstanceId: string): K0TaskCreateMutation {
  const value = new StrictJsonParser(json).parse();
  const mutation = requireObject(value, "mutation");
  requireExactKeys(mutation, [
    "protocolVersion", "product", "instanceId", "deviceId", "mutationId", "deviceSequence",
    "operation", "entityId", "baseEntityVersion", "payload", "clientCreatedAt",
  ]);

  if (mutation.protocolVersion !== protocolVersion) {
    fail("unsupported_protocol", "Only Personal Cloud protocol version 1 is supported by K0.");
  }
  if (mutation.product !== tasksProduct) {
    fail("unsupported_product", "This operation belongs to the Openza Tasks product.");
  }

  requireIdentifier(mutation.instanceId, "instanceId");
  requireIdentifier(mutation.deviceId, "deviceId");
  requireIdentifier(mutation.mutationId, "mutationId");
  requireIdentifier(mutation.entityId, "entityId");
  if (mutation.instanceId !== expectedInstanceId) {
    fail("instance_mismatch", "The mutation targets another Personal Cloud instance.");
  }
  if (!Number.isSafeInteger(mutation.deviceSequence) || (mutation.deviceSequence as number) <= 0) {
    fail("invalid_request", "Device sequence must be a positive integer.");
  }
  if (mutation.operation !== "task.create" || mutation.baseEntityVersion !== null) {
    fail("invalid_request", "K0 accepts task.create without a base entity version only.");
  }

  const payload = requireObject(mutation.payload, "payload");
  requireExactKeys(payload, ["task"]);
  const task = requireObject(payload.task, "payload.task");
  requireExactKeys(task, ["id", "spaceId", "title", "completionState", "workflowStatus", "createdAt"]);
  requireIdentifier(task.id, "payload.task.id");
  requireIdentifier(task.spaceId, "payload.task.spaceId");
  if (mutation.entityId !== task.id) {
    fail("invalid_request", "The mutation entity ID must match the task ID.");
  }
  if (typeof task.title !== "string" || task.title.trim().length === 0) {
    fail("invalid_request", "Task title must not be empty.");
  }
  requireValidUnicode(task.title, "payload.task.title");
  if (new TextEncoder().encode(task.title).byteLength > maximumTitleUtf8Bytes) {
    fail("invalid_request", "Task title exceeds the K0 size limit.");
  }
  if (task.completionState !== "open" || task.workflowStatus !== "inbox") {
    fail("invalid_request", "K0 creates open Inbox tasks only.");
  }
  requireTimestamp(task.createdAt, "payload.task.createdAt");
  requireTimestamp(mutation.clientCreatedAt, "clientCreatedAt");

  return value as K0TaskCreateMutation;
}

export function canonicalize(json: string): string {
  return canonicalizeValue(new StrictJsonParser(json).parse());
}

export function computeCanonicalHash(json: string): string {
  return createHash("sha256").update(canonicalize(json), "utf8").digest("hex");
}

export function requireExactObject(json: string, expectedKeys: readonly string[]): Record<string, JsonValue> {
  const value = requireObject(new StrictJsonParser(json).parse(), "document");
  requireExactKeys(value, expectedKeys);
  return value;
}

function canonicalizeValue(value: JsonValue): string {
  if (value === null || typeof value === "boolean") return JSON.stringify(value);
  if (typeof value === "string") {
    requireValidUnicode(value, "JSON string");
    return JSON.stringify(value);
  }
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) fail("invalid_request", "K0 accepts safe integer JSON numbers only.");
    return value.toString();
  }
  if (Array.isArray(value)) return `[${value.map(canonicalizeValue).join(",")}]`;
  return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalizeValue(value[key])}`).join(",")}}`;
}

function requireObject(value: JsonValue | undefined, field: string): Record<string, JsonValue> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    fail("invalid_request", `${field} must be an object.`);
  }
  return value as Record<string, JsonValue>;
}

function requireExactKeys(value: Record<string, JsonValue>, expected: readonly string[]): void {
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index])) {
    fail("invalid_request", "The JSON object contains missing or unknown K0 properties.");
  }
}

function requireIdentifier(value: JsonValue, field: string): asserts value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > maximumIdentifierLength || !identifierPattern.test(value)) {
    fail("invalid_request", `${field} is not a valid K0 identifier.`);
  }
}

function requireTimestamp(value: JsonValue, field: string): asserts value is string {
  if (typeof value !== "string" || !timestampPattern.test(value)) {
    fail("invalid_request", `${field} must be a millisecond-precision UTC timestamp.`);
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.valueOf()) || parsed.toISOString() !== value) {
    fail("invalid_request", `${field} must be a millisecond-precision UTC timestamp.`);
  }
}

function requireValidUnicode(value: string, field: string): void {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) fail("invalid_request", `${field} contains invalid Unicode.`);
      index += 1;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      fail("invalid_request", `${field} contains invalid Unicode.`);
    }
  }
}

function fail(code: string, message: string): never {
  throw new K0ContractError(code, message);
}

class StrictJsonParser {
  private offset = 0;
  private readonly source: string;

  constructor(source: string) {
    this.source = source;
  }

  parse(): JsonValue {
    const value = this.parseValue();
    this.skipWhitespace();
    if (this.offset !== this.source.length) fail("invalid_request", "Unexpected content follows the JSON document.");
    return value;
  }

  private parseValue(): JsonValue {
    this.skipWhitespace();
    const character = this.source[this.offset];
    if (character === "{") return this.parseObject();
    if (character === "[") return this.parseArray();
    if (character === "\"") return this.parseString();
    if (character === "t") return this.parseLiteral("true", true);
    if (character === "f") return this.parseLiteral("false", false);
    if (character === "n") return this.parseLiteral("null", null);
    return this.parseNumber();
  }

  private parseObject(): Record<string, JsonValue> {
    this.offset += 1;
    const result: Record<string, JsonValue> = Object.create(null) as Record<string, JsonValue>;
    const keys = new Set<string>();
    this.skipWhitespace();
    if (this.source[this.offset] === "}") { this.offset += 1; return result; }
    while (true) {
      this.skipWhitespace();
      if (this.source[this.offset] !== "\"") fail("invalid_request", "Object property name must be a string.");
      const key = this.parseString();
      if (keys.has(key)) fail("invalid_request", `Duplicate JSON property '${key}' is not allowed.`);
      keys.add(key);
      this.skipWhitespace();
      if (this.source[this.offset++] !== ":") fail("invalid_request", "Object property is missing ':'.");
      result[key] = this.parseValue();
      this.skipWhitespace();
      const separator = this.source[this.offset++];
      if (separator === "}") return result;
      if (separator !== ",") fail("invalid_request", "Object property is missing ','.");
    }
  }

  private parseArray(): JsonValue[] {
    this.offset += 1;
    const result: JsonValue[] = [];
    this.skipWhitespace();
    if (this.source[this.offset] === "]") { this.offset += 1; return result; }
    while (true) {
      result.push(this.parseValue());
      this.skipWhitespace();
      const separator = this.source[this.offset++];
      if (separator === "]") return result;
      if (separator !== ",") fail("invalid_request", "Array item is missing ','.");
    }
  }

  private parseString(): string {
    const start = this.offset++;
    let escaped = false;
    while (this.offset < this.source.length) {
      const character = this.source[this.offset++];
      if (!escaped && character === "\"") {
        try {
          const value = JSON.parse(this.source.slice(start, this.offset)) as string;
          requireValidUnicode(value, "JSON string");
          return value;
        } catch (error) {
          if (error instanceof K0ContractError) throw error;
          fail("invalid_request", "Invalid JSON string.");
        }
      }
      if (!escaped && character.charCodeAt(0) < 0x20) fail("invalid_request", "Unescaped control character in JSON string.");
      if (!escaped && character === "\\") escaped = true;
      else escaped = false;
    }
    fail("invalid_request", "Unterminated JSON string.");
  }

  private parseNumber(): number {
    const match = this.source.slice(this.offset).match(/^-?(?:0|[1-9]\d*)/);
    if (!match) fail("invalid_request", "Invalid JSON value.");
    this.offset += match[0].length;
    if ([".", "e", "E"].includes(this.source[this.offset])) fail("invalid_request", "K0 accepts integer JSON numbers only.");
    const value = Number(match[0]);
    if (!Number.isSafeInteger(value)) fail("invalid_request", "K0 accepts safe integer JSON numbers only.");
    return value;
  }

  private parseLiteral<T extends JsonPrimitive>(text: string, value: T): T {
    if (this.source.slice(this.offset, this.offset + text.length) !== text) fail("invalid_request", "Invalid JSON literal.");
    this.offset += text.length;
    return value;
  }

  private skipWhitespace(): void {
    while ([" ", "\t", "\n", "\r"].includes(this.source[this.offset])) this.offset += 1;
  }
}
