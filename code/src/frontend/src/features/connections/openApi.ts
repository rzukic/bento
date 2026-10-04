import type { OpenApiDocument, OpenApiOperation, OpenApiParameter, OpenApiSchema } from "../../api/connections";

export const METHODS = ["get", "post", "put", "patch", "delete"] as const;

export type OperationEntry = {
  id: string;
  method: (typeof METHODS)[number];
  path: string;
  operation: OpenApiOperation;
};

export type TagGroup = { name: string; description?: string; operations: OperationEntry[] };

/** Operations grouped by their first tag, in the document's tag order. */
export function groupOperations(doc: OpenApiDocument): TagGroup[] {
  const groups = new Map<string, TagGroup>();
  for (const tag of doc.tags ?? []) groups.set(tag.name, { name: tag.name, description: tag.description, operations: [] });
  for (const [path, item] of Object.entries(doc.paths)) {
    for (const method of METHODS) {
      const operation = item[method];
      if (!operation) continue;
      const tag = operation.tags?.[0] ?? "Other";
      if (!groups.has(tag)) groups.set(tag, { name: tag, operations: [] });
      groups.get(tag)!.operations.push({ id: `${method} ${path}`, method, path, operation });
    }
  }
  return [...groups.values()].filter((g) => g.operations.length > 0);
}

export function resolveSchema(doc: OpenApiDocument, schema: OpenApiSchema | undefined): OpenApiSchema | undefined {
  let current = schema;
  for (let depth = 0; current?.$ref && depth < 10; depth++) {
    const name = current.$ref.split("/").pop()!;
    current = doc.components?.schemas?.[name];
  }
  return current;
}

/** A sample value for a schema: its example, else something of the right type. */
export function exampleFor(doc: OpenApiDocument, schema: OpenApiSchema | undefined, depth = 0): unknown {
  const resolved = resolveSchema(doc, schema);
  if (!resolved || depth > 6) return null;
  if (resolved.example !== undefined) return resolved.example;
  if (resolved.enum?.length) return resolved.enum[0];
  if (resolved.properties) {
    return Object.fromEntries(
      Object.entries(resolved.properties)
        .filter(([, property]) => !resolveSchema(doc, property)?.readOnly)
        .map(([name, property]) => [name, exampleFor(doc, property, depth + 1)]),
    );
  }
  switch (resolved.type) {
    case "array":
      return [exampleFor(doc, resolved.items, depth + 1)];
    case "integer":
      return 1;
    case "number":
      return 1.5;
    case "boolean":
      return true;
    case "string":
      return resolved.format === "date" ? "2026-10-04" : resolved.format === "date-time" ? "2026-10-04T12:00:00" : "text";
    case "object":
      return {};
    default:
      return null;
  }
}

export function requestBodySchema(operation: OpenApiOperation): OpenApiSchema | undefined {
  return operation.requestBody?.content?.["application/json"]?.schema;
}

export type FieldRow = { name: string; type: string; required: boolean; description?: string };

/** The fields of a request body, for the "fields" table. */
export function bodyFields(doc: OpenApiDocument, operation: OpenApiOperation): FieldRow[] {
  const schema = resolveSchema(doc, requestBodySchema(operation));
  if (!schema?.properties) return [];
  const required = new Set(schema.required ?? []);
  return Object.entries(schema.properties).map(([name, property]) => {
    const resolved = resolveSchema(doc, property) ?? {};
    const type = resolved.enum?.length
      ? resolved.enum.map(String).join(" | ")
      : [resolved.type ?? (property.$ref ? property.$ref.split("/").pop() : "any"), resolved.format].filter(Boolean).join(" · ");
    return { name, type, required: required.has(name), description: resolved.description };
  });
}

/**
 * The request URL: path parameters filled in, query parameters appended.
 * The `filter` parameter takes "column=value&column[op]=value" and becomes
 * filter[column]=value&filter[column][op]=value.
 */
export function buildUrl(base: string, path: string, parameters: OpenApiParameter[], values: Record<string, string>): string {
  let url = path;
  const query: string[] = [];
  for (const parameter of parameters) {
    const value = values[parameter.name]?.trim() ?? "";
    if (parameter.in === "path") {
      url = url.replace(`{${parameter.name}}`, encodeURIComponent(value));
    } else if (parameter.in === "query" && value) {
      if (parameter.style === "deepObject") {
        for (const part of value.split("&").map((p) => p.trim()).filter(Boolean)) {
          const [key, ...rest] = part.split("=");
          const match = key.trim().match(/^([^[\]]+)((?:\[[^\]]*\])*)$/);
          if (!match) continue;
          query.push(`${parameter.name}[${encodeURIComponent(match[1])}]${match[2]}=${encodeURIComponent(rest.join("="))}`);
        }
      } else {
        query.push(`${encodeURIComponent(parameter.name)}=${encodeURIComponent(value)}`);
      }
    }
  }
  return `${base}${url}${query.length ? `?${query.join("&")}` : ""}`;
}

/** curl with the key as an environment variable, never the key itself. */
export function curlFor(method: string, url: string, body: string | null): string {
  // -g: filter[...] brackets are curl globbing characters otherwise.
  const glob = /[[\]{}]/.test(url) ? "-g " : "";
  const lines = [`curl ${glob}-X ${method.toUpperCase()} "${url}"`, `  -H "X-API-Key: $BENTO_API_KEY"`];
  if (body !== null) {
    lines.push(`  -H "Content-Type: application/json"`);
    lines.push(`  -d '${body.replace(/'/g, "'\\''")}'`);
  }
  return lines.join(" \\\n");
}
