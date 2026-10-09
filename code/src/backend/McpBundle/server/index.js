#!/usr/bin/env node
/*
 * Bento for Claude Desktop: a stdio MCP server that forwards every JSON-RPC message to Bento's MCP endpoint
 * (Streamable HTTP) with an API key. No dependencies: Node 18+ (fetch), as shipped with Claude Desktop.
 *
 * Environment (set by manifest.json from the extension settings):
 *   BENTO_MCP_URL  the endpoint, for example https://bento.example.com/mcp
 *   BENTO_MCP_KEY  a Bento API key, read or read-write (never logged)
 */
"use strict";

const readline = require("node:readline");

const url = (process.env.BENTO_MCP_URL || "").trim();
const key = (process.env.BENTO_MCP_KEY || "").trim();
let sessionId = null;

function write(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function log(text) {
  // stderr goes to the Claude Desktop MCP log; never include the key.
  process.stderr.write(`[bento] ${text}\n`);
}

function errorFor(message, code, text) {
  if (message && message.id !== undefined && message.id !== null) {
    write({ jsonrpc: "2.0", id: message.id, error: { code, message: text } });
  }
}

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

async function forward(message) {
  if (!url) {
    errorFor(message, -32001, "Bento: no address set. Add the MCP address in the extension settings.");
    return;
  }
  if (!key) {
    errorFor(message, -32001, "Bento: no API key set. Add a key (Bento → Connections → API keys) in the extension settings.");
    return;
  }
  const headers = {
    Authorization: `Bearer ${key}`,
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
  };
  if (sessionId) headers["Mcp-Session-Id"] = sessionId;
  let response;
  try {
    response = await fetch(url, { method: "POST", headers, body: JSON.stringify(message) });
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
    if (response.status === 401) text = "Bento rejected the API key (401): it is unknown, revoked or expired. Check the key in the extension settings.";
    if (response.status === 403) text = "Bento refused the request (403).";
    // Auth errors are answered before the message is read, so their JSON-RPC error has no id; Claude needs the id
    // of its request to match the error.
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
