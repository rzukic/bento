import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { userEvent } from "@testing-library/user-event";
import { RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ApiKey, OpenApiDocument } from "../api/connections";
import { seedSetupInitializedForTest } from "../features/setup";
import { bodyFields, buildUrl, curlFor, exampleFor, groupOperations } from "../features/connections/openApi";
import { createTestRouter } from "../router";
import { resetAppStoreForTest } from "../stores/appStore";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import { resetSchemaStoreForTest } from "../stores/schemaStore";

const api = vi.hoisted(() => ({
  getOpenApiDocument: vi.fn(),
  listApiKeys: vi.fn(),
  createApiKey: vi.fn(),
  revokeApiKey: vi.fn(),
  sendRaw: vi.fn(),
}));
vi.mock("../api/connections", async (importOriginal) => ({ ...(await importOriginal<typeof import("../api/connections")>()), ...api }));

const doc: OpenApiDocument = {
  openapi: "3.0.3",
  info: { title: "Bento API", version: "1.0.0" },
  servers: [{ url: "/api/v1" }],
  tags: [
    { name: "Data · Customers", description: "Records of `customers`." },
    { name: "Query", description: "Aggregate a table." },
  ],
  paths: {
    "/data/customers": {
      get: {
        tags: ["Data · Customers"],
        summary: "List Customers",
        "x-bento-scope": "read",
        parameters: [
          { name: "page", in: "query", schema: { type: "integer", example: 1 } },
          { name: "filter", in: "query", style: "deepObject", schema: { type: "object" } },
        ],
      },
      post: {
        tags: ["Data · Customers"],
        summary: "Create a Customers record",
        "x-bento-scope": "write",
        requestBody: { content: { "application/json": { schema: { $ref: "#/components/schemas/CustomersInput" } } } },
      },
    },
    "/data/customers/{id}": {
      delete: {
        tags: ["Data · Customers"],
        summary: "Delete a Customers record",
        "x-bento-scope": "write",
        parameters: [{ name: "id", in: "path", required: true, schema: { type: "string", example: "1" } }],
      },
    },
    "/query": { post: { tags: ["Query"], summary: "Run an aggregation query", "x-bento-scope": "read" } },
  },
  components: {
    schemas: {
      CustomersInput: {
        type: "object",
        required: ["name"],
        properties: {
          name: { type: "string", example: "Acme" },
          tier: { type: "string", enum: ["bronze", "gold"] },
          joined_on: { type: "string", format: "date", nullable: true },
        },
      },
    },
  },
};

const activeKey: ApiKey = {
  id: 1,
  name: "Nightly sync",
  prefix: "bento_AbCdEf",
  scope: "read",
  createdBy: "admin",
  createdAt: "2026-10-01T10:00:00Z",
  expiresAt: null,
  lastUsedAt: null,
  revokedAt: null,
  status: "active",
};

function signIn(role: string) {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: role, role },
    accessToken: "token",
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
  });
}

beforeEach(() => {
  resetAppStoreForTest();
  resetAuthStoreForTest();
  seedSetupInitializedForTest();
  resetSchemaStoreForTest();
  signIn("admin");
  api.getOpenApiDocument.mockResolvedValue(doc);
  api.listApiKeys.mockResolvedValue([activeKey]);
  vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("fetch must be mocked in tests"))));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("OpenAPI helpers", () => {
  it("groups operations by tag and builds examples from schemas", () => {
    const groups = groupOperations(doc);
    expect(groups.map((g) => [g.name, g.operations.map((o) => o.id)])).toEqual([
      ["Data · Customers", ["get /data/customers", "post /data/customers", "delete /data/customers/{id}"]],
      ["Query", ["post /query"]],
    ]);
    expect(exampleFor(doc, { $ref: "#/components/schemas/CustomersInput" })).toEqual({ name: "Acme", tier: "bronze", joined_on: "2026-10-04" });
    expect(bodyFields(doc, doc.paths["/data/customers"].post)).toEqual([
      { name: "name", type: "string", required: true, description: undefined },
      { name: "tier", type: "bronze | gold", required: false, description: undefined },
      { name: "joined_on", type: "string · date", required: false, description: undefined },
    ]);
  });

  it("fills path parameters and turns filters into filter[...] parameters", () => {
    const parameters = [...doc.paths["/data/customers"].get.parameters!];
    expect(buildUrl("http://x/api/v1", "/data/customers", parameters, { page: "2", filter: "tier=gold&joined_on[gte]=2026-01-01" })).toBe(
      "http://x/api/v1/data/customers?page=2&filter[tier]=gold&filter[joined_on][gte]=2026-01-01",
    );
    expect(buildUrl("http://x/api/v1", "/data/customers/{id}", doc.paths["/data/customers/{id}"].delete.parameters!, { id: "7" })).toBe(
      "http://x/api/v1/data/customers/7",
    );
  });

  it("writes curl with the key as a variable and globbing off for brackets", () => {
    expect(curlFor("get", "http://x/api/v1/data/c?filter[a]=1", null)).toBe('curl -g -X GET "http://x/api/v1/data/c?filter[a]=1" \\\n  -H "X-API-Key: $BENTO_API_KEY"');
    expect(curlFor("post", "http://x/api/v1/data/c", '{"name":"O\'Brien"}')).toContain(`-d '{"name":"O'\\''Brien"}'`);
  });
});

describe("Connections page", () => {
  it("sends a request as the signed-in user and shows the answer and curl", async () => {
    api.sendRaw.mockResolvedValue({ status: 201, statusText: "Created", durationMs: 12, body: '{"data":{"id":5,"name":"Acme"}}', contentType: "application/json" });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections"])} />);

    expect(await screen.findByRole("heading", { name: "Connections" })).toBeInTheDocument();
    const group = await screen.findByRole("region", { name: "Data · Customers" });
    expect(within(group).getByText("customers", { selector: "code" })).toBeInTheDocument();

    await user.click(within(group).getByRole("button", { name: /POST \/data\/customers/ }));
    const body = screen.getByLabelText("Request body");
    expect(JSON.parse((body as HTMLTextAreaElement).value)).toEqual({ name: "Acme", tier: "bronze", joined_on: "2026-10-04" });
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(api.sendRaw).toHaveBeenCalledTimes(1));
    expect(api.sendRaw.mock.calls[0]).toEqual(["POST", `${window.location.origin}/api/v1/data/customers`, '{"name":"Acme","tier":"bronze","joined_on":"2026-10-04"}', null]);
    expect(await screen.findByText("201 Created")).toBeInTheDocument();
    expect(screen.getByText(/"name": "Acme"/, { selector: "pre" })).toBeInTheDocument();
    expect(screen.getByText(/X-API-Key: \$BENTO_API_KEY/)).toBeInTheDocument();
  });

  it("sends with an API key instead of the session when chosen", async () => {
    api.sendRaw.mockResolvedValue({ status: 403, statusText: "Forbidden", durationMs: 3, body: '{"error":{"message":"This API key can only read."}}', contentType: "application/json" });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections"])} />);

    await user.click(await screen.findByRole("radio", { name: "API key" }));
    await user.type(screen.getByLabelText("API key to send"), "bento_secret");
    await user.click(await screen.findByRole("button", { name: /POST \/data\/customers/ }));
    await user.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(api.sendRaw).toHaveBeenCalled());
    expect(api.sendRaw.mock.calls[0][3]).toBe("bento_secret");
    expect(await screen.findByText("403 Forbidden")).toBeInTheDocument();
  });

  it("creates a key with chosen permissions, e.g. for a schema script", async () => {
    api.createApiKey.mockResolvedValue({ key: "bento_permissionkey", apiKey: { ...activeKey, id: 3, name: "Schema script", scope: "permissions", permissions: ["data.read", "schema.write"] } });
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections?tab=keys"])} />);

    expect(await screen.findByText("Nightly sync")).toBeInTheDocument();
    await user.type(screen.getByPlaceholderText("e.g. Nightly bank sync"), "Schema script");
    await user.selectOptions(screen.getByLabelText("Access"), "permissions");
    await user.click(screen.getByRole("checkbox", { name: /Tables and columns/ }));
    await user.click(screen.getByRole("button", { name: "Create key" }));

    expect(api.createApiKey).toHaveBeenCalledWith("Schema script", "read-write", undefined, ["data.read", "schema.write"]);
    expect(await screen.findByLabelText("New API key")).toHaveTextContent("bento_permissionkey");
  });

  it("creates a key, shows it once and revokes keys after confirmation", async () => {
    api.createApiKey.mockResolvedValue({ key: "bento_newsecretkeyvalue", apiKey: { ...activeKey, id: 2, name: "Bank sync", scope: "read-write" } });
    api.revokeApiKey.mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<RouterProvider router={createTestRouter(["/connections?tab=keys"])} />);

    expect(await screen.findByText("Nightly sync")).toBeInTheDocument();
    await user.type(screen.getByPlaceholderText("e.g. Nightly bank sync"), "Bank sync");
    await user.selectOptions(screen.getByLabelText("Access"), "read-write");
    await user.selectOptions(screen.getByLabelText("Expires"), "90");
    await user.click(screen.getByRole("button", { name: "Create key" }));

    expect(api.createApiKey).toHaveBeenCalledWith("Bank sync", "read-write", 90);
    expect(await screen.findByLabelText("New API key")).toHaveTextContent("bento_newsecretkeyvalue");
    expect(screen.getByText(/shown only once/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByLabelText("New API key")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Revoke" }));
    await user.click(await screen.findByRole("button", { name: "Revoke key" }));
    await waitFor(() => expect(api.revokeApiKey).toHaveBeenCalledWith(1));
  });

  it("tells non-admins who creates keys", async () => {
    signIn("editor");
    render(<RouterProvider router={createTestRouter(["/connections?tab=keys"])} />);

    expect(await screen.findByText(/created by an administrator/)).toBeInTheDocument();
    expect(api.listApiKeys).not.toHaveBeenCalled();
  });
});
