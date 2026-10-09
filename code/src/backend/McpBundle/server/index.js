#!/usr/bin/env node
/*
 * Bento for Claude Desktop: a stdio MCP server that forwards every JSON-RPC message to Bento's MCP endpoint
 * (Streamable HTTP). It signs in like any MCP client: on first use it opens Bento's sign-in in the browser
 * (OAuth with PKCE, the code comes back to 127.0.0.1), then keeps the tokens in ~/.bento and renews them.
 * No dependencies: Node 18+ (fetch), as shipped with Claude Desktop.
 *
 * Environment (set by manifest.json from the extension settings):
 *   BENTO_MCP_URL  the endpoint, for example https://bento.example.com/mcp
 */
"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const readline = require("node:readline");
const { spawn } = require("node:child_process");

const url = (process.env.BENTO_MCP_URL || "").trim().replace(/\/+$/, "");
const LOGIN_TIMEOUT_MS = 5 * 60 * 1000;
/** How long `initialize` waits for a first sign-in before answering without the server's instructions. */
const INITIALIZE_WAIT_MS = 45 * 1000;

const storeDir = path.join(os.homedir(), ".bento");
const storeFile = path.join(storeDir, `mcp-${crypto.createHash("sha256").update(url).digest("hex").slice(0, 16)}.json`);

let sessionId = null;
let login = null; // the sign-in in progress, shared by every waiting request

function write(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function log(text) {
  // stderr goes to the Claude Desktop MCP log; never include tokens.
  process.stderr.write(`[bento] ${text}\n`);
}

function errorFor(message, code, text) {
  if (message && message.id !== undefined && message.id !== null) {
    write({ jsonrpc: "2.0", id: message.id, error: { code, message: text } });
  }
}

// --- Token store ----------------------------------------------------------------------------

function readStore() {
  try {
    return JSON.parse(fs.readFileSync(storeFile, "utf8"));
  } catch {
    return {};
  }
}

function writeStore(data) {
  fs.mkdirSync(storeDir, { recursive: true, mode: 0o700 });
  fs.writeFileSync(storeFile, JSON.stringify(data, null, 2), { mode: 0o600 });
}

function saveTokens(store, tokens) {
  store.access_token = tokens.access_token;
  store.refresh_token = tokens.refresh_token || store.refresh_token;
  store.expires_at = Date.now() + (Number(tokens.expires_in) || 3600) * 1000;
  writeStore(store);
}

// --- OAuth ----------------------------------------------------------------------------------

const base64url = (buffer) => buffer.toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

async function getJson(target) {
  const response = await fetch(target, { headers: { Accept: "application/json" } });
  if (!response.ok) throw new Error(`${target} answered HTTP ${response.status}`);
  return response.json();
}

/** Where to sign in: the resource metadata names the authorization server, whose metadata names the endpoints. */
async function discover(store) {
  if (store.metadata) return store.metadata;
  const origin = new URL(url).origin;
  let issuer = origin;
  try {
    const resource = await getJson(`${origin}/.well-known/oauth-protected-resource`);
    if (Array.isArray(resource.authorization_servers) && resource.authorization_servers[0]) issuer = resource.authorization_servers[0];
  } catch (error) {
    log(`no resource metadata (${error.message}); trying ${origin}`);
  }
  const metadata = await getJson(`${issuer.replace(/\/+$/, "")}/.well-known/oauth-authorization-server`);
  store.metadata = {
    authorization_endpoint: metadata.authorization_endpoint,
    token_endpoint: metadata.token_endpoint,
    registration_endpoint: metadata.registration_endpoint,
  };
  return store.metadata;
}

async function tokenRequest(endpoint, params) {
  const response = await fetch(endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
    body: new URLSearchParams(params).toString(),
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error_description || body.error || `HTTP ${response.status}`);
  return body;
}

function openBrowser(target) {
  const [command, args] =
    process.platform === "darwin"
      ? ["open", [target]]
      : process.platform === "win32"
        ? ["rundll32", ["url.dll,FileProtocolHandler", target]]
        : ["xdg-open", [target]];
  try {
    spawn(command, args, { detached: true, stdio: "ignore" }).unref();
  } catch (error) {
    log(`could not open the browser (${error.message}); open this address yourself: ${target}`);
  }
}

const page = (title, text) =>
  `<!doctype html><meta charset="utf-8"><title>${title}</title>` +
  `<body style="font:16px system-ui,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#f6f6f4;color:#1c1c1c">` +
  `<div style="text-align:center"><h1 style="font-size:22px">${title}</h1><p>${text}</p></div></body>`;

/** Browser sign-in: a one-shot server on 127.0.0.1 receives the code. */
async function signIn(store) {
  const metadata = await discover(store);
  const verifier = base64url(crypto.randomBytes(32));
  const challenge = base64url(crypto.createHash("sha256").update(verifier).digest());
  const state = base64url(crypto.randomBytes(16));

  const server = http.createServer();
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const redirectUri = `http://127.0.0.1:${server.address().port}/callback`;

  try {
    // Bento lets a loopback client come back on another port, so one registration serves every sign-in.
    if (!store.client_id) {
      const response = await fetch(metadata.registration_endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          client_name: "Claude Desktop",
          redirect_uris: [redirectUri],
          grant_types: ["authorization_code", "refresh_token"],
          response_types: ["code"],
          token_endpoint_auth_method: "none",
        }),
      });
      const client = await response.json();
      if (!response.ok) throw new Error(client.error_description || `registration answered HTTP ${response.status}`);
      store.client_id = client.client_id;
      writeStore(store);
    }

    const code = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("the sign-in was not finished within 5 minutes")), LOGIN_TIMEOUT_MS);
      server.on("request", (request, response) => {
        const query = new URL(request.url, redirectUri).searchParams;
        if (!request.url.startsWith("/callback") || query.get("state") !== state) {
          response.writeHead(404).end();
          return;
        }
        clearTimeout(timer);
        response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
        if (query.get("code")) {
          response.end(page("Connected to Bento", "You can close this tab and go back to Claude."));
          resolve(query.get("code"));
        } else {
          response.end(page("Not connected", query.get("error_description") || "The sign-in was declined."));
          reject(new Error(query.get("error_description") || query.get("error") || "the sign-in was declined"));
        }
      });
      const authorize = new URL(metadata.authorization_endpoint);
      authorize.search = new URLSearchParams({
        response_type: "code",
        client_id: store.client_id,
        redirect_uri: redirectUri,
        code_challenge: challenge,
        code_challenge_method: "S256",
        state,
        scope: "read write",
        resource: url,
      }).toString();
      log("opening Bento's sign-in in the browser");
      openBrowser(authorize.toString());
    });

    const tokens = await tokenRequest(metadata.token_endpoint, {
      grant_type: "authorization_code",
      client_id: store.client_id,
      code,
      redirect_uri: redirectUri,
      code_verifier: verifier,
    });
    saveTokens(store, tokens);
    log("signed in");
  } finally {
    server.close();
  }
}

async function refresh(store) {
  const metadata = await discover(store);
  const tokens = await tokenRequest(metadata.token_endpoint, {
    grant_type: "refresh_token",
    client_id: store.client_id,
    refresh_token: store.refresh_token,
  });
  saveTokens(store, tokens);
}

/** A valid access token: the stored one, a refreshed one, or a new sign-in in the browser. */
async function accessToken({ forceRefresh = false } = {}) {
  const store = readStore();
  if (!forceRefresh && store.access_token && store.expires_at - Date.now() > 60 * 1000) return store.access_token;
  if (!login) {
    login = (async () => {
      const current = readStore();
      if (current.refresh_token && current.client_id) {
        try {
          await refresh(current);
          return;
        } catch (error) {
          log(`renewing the sign-in failed (${error.message}); signing in again`);
          delete current.access_token;
          delete current.refresh_token;
          writeStore(current);
        }
      }
      try {
        await signIn(current);
      } catch (error) {
        // A client Bento no longer knows (e.g. a reset installation) registers again next time.
        if (/client/i.test(error.message)) {
          delete current.client_id;
          writeStore(current);
        }
        throw error;
      }
    })().finally(() => {
      login = null;
    });
  }
  await login;
  return readStore().access_token;
}

// --- Forwarding -----------------------------------------------------------------------------

/** Writes every `data:` payload of a server-sent event stream as one JSON-RPC message per line. */
function writeEventStream(body) {
  let data = [];
  for (const line of body.split(/\r?\n/)) {
    if (line.startsWith("data:")) {
      data.push(line.slice(5).trimStart());
    } else if (line === "" && data.length) {
      write(JSON.parse(data.join("\n")));
      data = [];
    }
  }
  if (data.length) write(JSON.parse(data.join("\n")));
}

async function post(message, token) {
  const headers = {
    Authorization: `Bearer ${token}`,
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
  };
  if (sessionId) headers["Mcp-Session-Id"] = sessionId;
  return fetch(url, { method: "POST", headers, body: JSON.stringify(message) });
}

/** Without a sign-in yet, `initialize` gets a local answer so Claude Desktop does not give up while the browser is open. */
function answerInitializeLocally(message) {
  const requested = message.params && message.params.protocolVersion;
  write({
    jsonrpc: "2.0",
    id: message.id,
    result: {
      protocolVersion: requested || "2025-06-18",
      capabilities: { tools: { listChanged: false } },
      serverInfo: { name: "bento", title: "Bento", version: "1.0.0" },
      instructions: "Bento is the user's data workspace. Start with describe_schema; use aggregate for totals over time.",
    },
  });
}

async function forward(message) {
  if (!url) {
    errorFor(message, -32001, "Bento: no address set. Add the MCP address in the extension settings.");
    return;
  }

  let token;
  try {
    if (message.method === "initialize" && !readStore().access_token) {
      const signedIn = accessToken();
      const winner = await Promise.race([signedIn.then(() => "done"), new Promise((r) => setTimeout(() => r("wait"), INITIALIZE_WAIT_MS))]);
      signedIn.catch((error) => log(`sign-in failed: ${error.message}`));
      if (winner === "wait") {
        answerInitializeLocally(message);
        return;
      }
    }
    token = await accessToken();
  } catch (error) {
    log(`sign-in failed: ${error.message}`);
    errorFor(message, -32001, `Bento: sign-in failed (${error.message}). Try again; your browser opens Bento's sign-in.`);
    return;
  }

  let response;
  try {
    response = await post(message, token);
    if (response.status === 401) {
      // Revoked or expired on the server: renew (or sign in again) once.
      token = await accessToken({ forceRefresh: true });
      response = await post(message, token);
    }
  } catch (error) {
    log(`request failed: ${error && error.message}`);
    errorFor(message, -32000, `Bento is not reachable at ${url}.`);
    return;
  }

  const id = response.headers.get("mcp-session-id");
  if (id) sessionId = id;
  const body = await response.text();
  if (response.status === 202 || body.trim() === "") return; // notifications and responses
  if (!response.ok) {
    log(`HTTP ${response.status}`);
    let text = `Bento answered HTTP ${response.status}.`;
    if (response.status === 401) text = "Bento did not accept the sign-in (401). Try again; your browser opens Bento's sign-in.";
    if (response.status === 403) text = "Bento refused the request (403).";
    let code = -32000;
    try {
      const parsed = JSON.parse(body);
      if (parsed && parsed.error && typeof parsed.error.code === "number") code = parsed.error.code;
    } catch {
      // not JSON: keep the generic code
    }
    errorFor(message, code, text);
    return;
  }
  try {
    if ((response.headers.get("content-type") || "").includes("text/event-stream")) {
      writeEventStream(body);
    } else {
      const parsed = JSON.parse(body);
      for (const item of Array.isArray(parsed) ? parsed : [parsed]) write(item);
    }
  } catch (error) {
    log(`unreadable response: ${error && error.message}`);
    errorFor(message, -32700, "Bento sent a response that could not be read.");
  }
}

const input = readline.createInterface({ input: process.stdin });
const pending = new Set();
input.on("line", (line) => {
  if (!line.trim()) return;
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    write({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "Parse error" } });
    return;
  }
  const job = forward(message).finally(() => pending.delete(job));
  pending.add(job);
});
input.on("close", () => {
  Promise.allSettled([...pending]).then(() => process.exit(0));
});
log(`bridge to ${url || "(no address)"} started`);
