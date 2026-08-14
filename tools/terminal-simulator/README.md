# Terminal simulator (dev tooling — never shipped)

A reference client for the function-terminal flows
(`docs/integrate/function-terminals.md`): a single static page plus a tiny
dev proxy that simulates a kiosk terminal end to end — device key
(non-extractable WebCrypto key in IndexedDB, the browser-grade equivalent
of a secure element), DPoP proofs on every request, device-flow enrollment,
a real passkey tap (`navigator.credentials.get`), refresh, and lock, with a
live log of every request (headers + form fields). It doubles as the living
wire-format reference for terminal implementers; the C# equivalent is the
E2E suite (`TerminalDeviceEnrollmentTests`, `FunctionStaffingTests`).

This directory lives outside every Docker build context (`src/`, `docs/`)
and is **never part of the shipped Modgud container**.

## Usage

1. Run Modgud locally (e.g. `http://localhost:8080`) with
   `AppSettings__Features__FunctionTerminals=true`.
2. Start the proxy — it serves the page and pipes everything else to
   Modgud, so the browser sees ONE origin (the terminal OAuth client
   allows no CORS origins by design, and the passkey RP-ID must match the
   host):

   ```bash
   node proxy.mjs --target http://localhost:8080 --port 5173
   ```

3. In the Modgud admin: create a function (terminal use on), authorize a
   user, create a terminal slot with **WebAuthn RP-ID `localhost`**.
4. The authorized user needs a passkey under that RP-ID — their normal
   Modgud account passkey works when the local realm domain is
   `localhost`.
5. Open `http://localhost:5173`, paste `client_id` + `terminal_id` from
   the slot view, and walk the flows:
   - **Enrollment** — user_code + device-key fingerprint appear; approve
     the consent as an admin in a second tab (match the fingerprint!);
     the poll collects the enrollment tokens.
   - **Passkey tap** — opens the shift.
   - **Refresh / lock** — steady state; a `staffing_required` answer
     locks the page exactly like the real terminal app must.

"Factory reset" destroys the device key — after that a **fresh slot** is
required by design (no silent re-enrollment; that is the product
behaviour, not a simulator limitation).
