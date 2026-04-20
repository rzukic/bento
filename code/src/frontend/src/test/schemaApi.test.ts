import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiClientError } from "../api/client";
import {
  applySchemaDraft,
  getSchemaConfirmationRequiredDetails,
  getSchemaSnapshot,
  getSchemaValidationFailedDetails,
  previewSchemaDraft
} from "../api/schema";
import { resetAuthStoreForTest, useAuthStore } from "../stores/authStore";
import type { SchemaModelDocument } from "../types/api";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}

function authenticated(accessToken: string): void {
  useAuthStore.setState({
    status: "authenticated",
    user: { id: 1, username: "admin", role: "admin" },
    accessToken,
    expiresAt: Date.now() + 60_000,
    loginInFlight: false,
    error: null,
    refreshInFlight: null
  });
}

const simpleDraft: SchemaModelDocument = {
  version: 0,
  tables: [
    {
      name: "widgets",
      columns: [
        { name: "id", type: "integer", primaryKey: true, nullable: false, unique: true },
        { name: "name", type: "text", nullable: false }
      ],
      indexes: []
    }
  ],
  relations: []
};

beforeEach(() => {
  resetAuthStoreForTest();
  authenticated("token-1");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("schema api", () => {
  it("loads the current schema snapshot through the shared admin schema route", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(simpleDraft));
    vi.stubGlobal("fetch", fetchMock);

    const result = await getSchemaSnapshot();

    expect(result).toEqual(simpleDraft);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/admin/schema");
    expect(init.method).toBe("GET");
  });

  it("previews a schema draft with typed preview metadata", async () => {
    const previewResponse = {
      isValid: true,
      hasChanges: true,
      isDestructive: false,
      requiresConfirmation: false,
      currentVersion: 1,
      errors: [],
      warnings: [],
      changes: [
        {
          changeType: "AddTable",
          table: "widgets",
          isDestructive: false
        }
      ],
      normalizedModel: simpleDraft,
      plan: {
        sqlUp: ['CREATE TABLE "widgets" ("id" INTEGER NOT NULL PRIMARY KEY, "name" TEXT NOT NULL);'],
        sqlDown: ['DROP TABLE "widgets";'],
        isDestructive: false,
        requiresConfirmation: false
      }
    };

    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(previewResponse));
    vi.stubGlobal("fetch", fetchMock);

    const result = await previewSchemaDraft(simpleDraft);

    expect(result).toEqual(previewResponse);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/admin/schema/preview");
    expect(init.method).toBe("POST");
    expect(init.body).toBe(JSON.stringify(simpleDraft));
  });

  it("applies a schema draft and appends destructive confirmation when requested", async () => {
    const applyResponse = {
      applied: true,
      version: 2,
      changes: [
        {
          changeType: "AddTable",
          table: "widgets",
          isDestructive: false
        }
      ],
      warnings: []
    };

    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(applyResponse));
    vi.stubGlobal("fetch", fetchMock);

    const result = await applySchemaDraft(simpleDraft, { confirmDestructive: true });

    expect(result).toEqual(applyResponse);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/v1/admin/schema?confirm_destructive=true");
    expect(init.method).toBe("PUT");
    expect(init.body).toBe(JSON.stringify(simpleDraft));
  });

  it("surfaces typed validation details when apply fails validation", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse(
        {
          error: {
            code: "VALIDATION_FAILED",
            message: "Schema validation failed.",
            details: {
              errors: [
                { code: "missing_table_name", message: "Table name is required.", path: "tables[0].name" }
              ]
            }
          }
        },
        400
      )
    );
    vi.stubGlobal("fetch", fetchMock);

    let caught: unknown;
    try {
      await applySchemaDraft(simpleDraft);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(ApiClientError);
    expect(caught).toMatchObject({
      code: "VALIDATION_FAILED",
      message: "Schema validation failed."
    });
    expect(getSchemaValidationFailedDetails(caught)).toEqual({
      errors: [
        { code: "missing_table_name", message: "Table name is required.", path: "tables[0].name" }
      ]
    });
  });

  it("surfaces typed confirmation-required change details when apply is blocked", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse(
        {
          error: {
            code: "CONFIRMATION_REQUIRED",
            message: "Schema change includes destructive operations.",
            details: {
              changes: [
                {
                  changeType: "DropTable",
                  table: "widgets",
                  isDestructive: true
                }
              ]
            }
          }
        },
        409
      )
    );
    vi.stubGlobal("fetch", fetchMock);

    let caught: unknown;
    try {
      await applySchemaDraft(simpleDraft);
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(ApiClientError);
    expect(caught).toMatchObject({
      code: "CONFIRMATION_REQUIRED",
      status: 409
    });
    expect(getSchemaConfirmationRequiredDetails(caught)).toEqual({
      changes: [
        {
          changeType: "DropTable",
          table: "widgets",
          isDestructive: true
        }
      ]
    });
  });
});
