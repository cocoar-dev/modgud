// Modgud Terminal Simulator — internal dev proxy.
//
// WHY: the simulator page must be SAME-ORIGIN with the Modgud container:
// the terminal OAuth client allows no CORS origins (fixed profile), and the
// passkey tap needs an origin whose host matches the slot's RP-ID. This tiny
// proxy serves terminal-simulator.html at "/" and pipes every other request
// through to the Modgud container — the browser sees one origin.
//
// USAGE:  node proxy.mjs [--target http://localhost:8080] [--port 5173]
// then open http://localhost:5173 and create the slot with RP-ID "localhost".
//
// Internal tooling only — never ship this.

import http from "node:http";
import https from "node:https";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const args = Object.fromEntries(process.argv.slice(2)
  .map((a, i, all) => a.startsWith("--") ? [a.slice(2), all[i + 1]] : null)
  .filter(Boolean));
const target = new URL(args.target ?? "http://localhost:8080");
const port = Number(args.port ?? 5173);
const htmlPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "terminal-simulator.html");

const server = http.createServer((req, res) => {
  if (req.method === "GET" && (req.url === "/" || req.url === "/terminal-simulator")) {
    res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    res.end(readFileSync(htmlPath));
    return;
  }

  // Pipe everything else to Modgud, headers intact (DPoP! Authorization!),
  // Host rewritten to the target so realm resolution works. Redirects are
  // passed through untouched — the admin consent happens in a separate tab
  // directly against Modgud anyway.
  const client = target.protocol === "https:" ? https : http;
  const upstream = client.request({
    host: target.hostname,
    port: target.port || (target.protocol === "https:" ? 443 : 80),
    path: req.url,
    method: req.method,
    headers: { ...req.headers, host: target.host },
    rejectUnauthorized: false, // local self-signed certs are fine here
  }, (up) => {
    res.writeHead(up.statusCode ?? 502, up.headers);
    up.pipe(res);
  });
  upstream.on("error", (e) => {
    res.writeHead(502, { "content-type": "text/plain" });
    res.end(`proxy error: ${e.message}`);
  });
  req.pipe(upstream);
});

server.listen(port, () => {
  console.log(`Terminal simulator:  http://localhost:${port}`);
  console.log(`Proxying to Modgud:  ${target.origin}`);
  console.log(`Slot RP-ID to use:   localhost`);
});
