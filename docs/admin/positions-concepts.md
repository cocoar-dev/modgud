# Positions & terminals — the concepts

> The [Positions & shared terminals](/admin/positions) page is the admin
> workflow (click here, enable that). This page explains the **model behind
> it** — what the building blocks are, how they connect, and how it feels in
> daily use. Protocol details live under
> [Integrate → Position terminals](/integrate/position-terminals).

<style>
.pt-card { border: 1px solid var(--vp-c-divider); border-radius: 12px;
  padding: 1rem 1.2rem; margin: 1.2rem 0; text-align: center; overflow-x: auto; }
.pt-card svg { max-width: 100%; height: auto; }
.pt-note { font-size: .85em; color: var(--vp-c-text-2); text-align: left; margin-top: .5rem; }
</style>

## The four building blocks

Two of them live in Modgud, two in the real world:

<div class="pt-card">
<svg viewBox="0 0 640 300" xmlns="http://www.w3.org/2000/svg" font-family="sans-serif">
  <rect x="20" y="30" width="180" height="90" rx="12" fill="#fdf3e3" stroke="#d97706" stroke-width="1.5"/>
  <text x="110" y="62" text-anchor="middle" font-size="16" font-weight="700" fill="#b45309">People</text>
  <text x="110" y="84" text-anchor="middle" font-size="12" fill="#8a6320">Anna, Ben, Carla …</text>
  <text x="110" y="102" text-anchor="middle" font-size="12" fill="#8a6320">ordinary user accounts</text>
  <rect x="440" y="30" width="180" height="90" rx="12" fill="#eef0fe" stroke="#6366f1" stroke-width="1.5"/>
  <text x="530" y="62" text-anchor="middle" font-size="16" font-weight="700" fill="#4f46e5">Position</text>
  <text x="530" y="84" text-anchor="middle" font-size="12" fill="#5b5fc0">the post: "gate",</text>
  <text x="530" y="102" text-anchor="middle" font-size="12" fill="#5b5fc0">"control room", "reception"</text>
  <rect x="440" y="180" width="180" height="90" rx="12" fill="#e6f7f0" stroke="#0e9f6e" stroke-width="1.5"/>
  <text x="530" y="212" text-anchor="middle" font-size="16" font-weight="700" fill="#0b7a55">Terminal</text>
  <text x="530" y="234" text-anchor="middle" font-size="12" fill="#177255">the device slot at the</text>
  <text x="530" y="252" text-anchor="middle" font-size="12" fill="#177255">position: "left terminal"</text>
  <rect x="20" y="180" width="180" height="90" rx="12" fill="#eef2f6" stroke="#64748b" stroke-width="1.5"/>
  <text x="110" y="212" text-anchor="middle" font-size="16" font-weight="700" fill="#475569">Device</text>
  <text x="110" y="234" text-anchor="middle" font-size="12" fill="#5c6b7e">the physical hardware</text>
  <text x="110" y="252" text-anchor="middle" font-size="12" fill="#5c6b7e">standing at the post</text>
  <line x1="320" y1="10" x2="320" y2="290" stroke="#c3c8d2" stroke-dasharray="5 5"/>
  <text x="255" y="20" text-anchor="middle" font-size="11" fill="#9aa1ad">real world</text>
  <text x="385" y="20" text-anchor="middle" font-size="11" fill="#9aa1ad">in Modgud</text>
</svg>
</div>

The **position** is the star of the model: a business role staffed by
*changing* people. It receives rights through the ordinary groups & roles
machinery, but it never signs in — it gets **activated** (more on that below).
For downstream systems, *the gate* acts — never Anna or Ben.

## Everything is a link

The whole system is three links between those blocks. Each has its own
moment, its own flow — and answers a different question.

<div class="pt-card">
<svg viewBox="0 0 640 330" xmlns="http://www.w3.org/2000/svg" font-family="sans-serif">
  <defs>
    <marker id="carr" markerWidth="9" markerHeight="9" refX="7" refY="4.5" orient="auto">
      <path d="M0,0 L8,4.5 L0,9 z" fill="#7a8194"/>
    </marker>
  </defs>
  <rect x="20" y="20" width="150" height="64" rx="10" fill="#fdf3e3" stroke="#d97706" stroke-width="1.5"/>
  <text x="95" y="57" text-anchor="middle" font-size="15" font-weight="700" fill="#b45309">Person</text>
  <rect x="470" y="20" width="150" height="64" rx="10" fill="#eef0fe" stroke="#6366f1" stroke-width="1.5"/>
  <text x="545" y="57" text-anchor="middle" font-size="15" font-weight="700" fill="#4f46e5">Position</text>
  <rect x="470" y="240" width="150" height="64" rx="10" fill="#e6f7f0" stroke="#0e9f6e" stroke-width="1.5"/>
  <text x="545" y="277" text-anchor="middle" font-size="15" font-weight="700" fill="#0b7a55">Terminal</text>
  <rect x="20" y="240" width="150" height="64" rx="10" fill="#eef2f6" stroke="#64748b" stroke-width="1.5"/>
  <text x="95" y="277" text-anchor="middle" font-size="15" font-weight="700" fill="#475569">Device</text>
  <line x1="170" y1="52" x2="468" y2="52" stroke="#7a8194" stroke-width="1.5" marker-end="url(#carr)"/>
  <rect x="230" y="30" width="180" height="22" rx="11" fill="#ffffff" stroke="#c3c8d2"/>
  <text x="320" y="46" text-anchor="middle" font-size="12.5" font-weight="600" fill="#374151">① "may staff"</text>
  <text x="320" y="72" text-anchor="middle" font-size="11" fill="#9aa1ad">changeable any time, per person</text>
  <line x1="545" y1="238" x2="545" y2="86" stroke="#7a8194" stroke-width="1.5" marker-end="url(#carr)"/>
  <rect x="282" y="148" width="250" height="22" rx="11" fill="#ffffff" stroke="#c3c8d2"/>
  <text x="407" y="164" text-anchor="middle" font-size="12.5" font-weight="600" fill="#374151">② "the position may run here"</text>
  <text x="407" y="188" text-anchor="middle" font-size="11" fill="#9aa1ad">created when you add the terminal</text>
  <line x1="170" y1="272" x2="468" y2="272" stroke="#7a8194" stroke-width="1.5" marker-end="url(#carr)"/>
  <rect x="235" y="250" width="170" height="22" rx="11" fill="#ffffff" stroke="#c3c8d2"/>
  <text x="320" y="266" text-anchor="middle" font-size="12.5" font-weight="600" fill="#374151">③ "this device is it"</text>
  <text x="320" y="292" text-anchor="middle" font-size="11" fill="#9aa1ad">at installation, exactly once</text>
</svg>
</div>

| Link | Question it answers | When & how |
|---|---|---|
| ① Person ↔ Position | **Who** may staff this post? | A simple list on the position ("authorized users"). Grant, suspend, revoke — takes effect immediately. |
| ② Terminal ↔ Position | **Where** may this post be staffed? | An authorization assignment. One terminal may carry several positions, selected for each shift. |
| ③ Device ↔ Terminal | **Which hardware** actually stands there? | At installation, exactly once. DPoP pins a device key, client-secret identifies its holder, while `none` deliberately leaves this link unproven. |

::: tip Mnemonic
Link ① says *who*, ② says *where*, ③ says *with what*. The daily unlock is
not a fourth link — it is the moment all three are checked at once.
:::

The realm security floor decides how strong links ① and ③ must be. A weaker
position policy cannot silently undercut that floor.

> For engineers: ① is the *grant*, ② is the *terminal slot* with its
> auto-created OAuth client, ③ is the *enrollment* (device key binding). The
> client appears in the OAuth grid as inventory only — everything is managed
> in the position.

## A position is not a group

The most tempting confusion — and the most important distinction in the model:

::: info The one-liner
**A group distributes rights. A position acts.**
:::

- **Group "porters" with Anna as member:** rights flow *to the person*.
  **Anna** acts, under her own name, with the group's rights. The group itself
  never appears at runtime — no tokens, no sessions. It is a distribution
  mechanism.
- **Position "gate" with Anna authorized:** rights never flow to Anna! The
  grant gives her **no right of the gate** — only the ability to **switch the
  gate on**. Then *the gate* acts, with *its* rights. Anna's own permissions
  are irrelevant during the shift.

The authorized-users list looks like membership but is a **key cabinet**:
"these people may start the engine", not "these people are the engine".
And the two concepts stack instead of competing — the position receives its
own rights *through groups*, like any other principal.

### Same person, two devices, two actors

<div class="pt-card">
<svg viewBox="0 0 640 250" xmlns="http://www.w3.org/2000/svg" font-family="sans-serif">
  <rect x="265" y="95" width="110" height="52" rx="10" fill="#fdf3e3" stroke="#d97706" stroke-width="1.5"/>
  <text x="320" y="118" text-anchor="middle" font-size="14" font-weight="700" fill="#b45309">Anna</text>
  <text x="320" y="136" text-anchor="middle" font-size="10.5" fill="#8a6320">one human</text>
  <rect x="20" y="30" width="200" height="180" rx="12" fill="#e6f7f0" stroke="#0e9f6e" stroke-width="1.5"/>
  <text x="120" y="58" text-anchor="middle" font-size="13.5" font-weight="700" fill="#0b7a55">Secured terminal</text>
  <text x="120" y="78" text-anchor="middle" font-size="11.5" fill="#177255">Anna unlocks → the actor is</text>
  <text x="120" y="96" text-anchor="middle" font-size="12.5" font-weight="700" fill="#4f46e5">"the gate"</text>
  <text x="120" y="126" text-anchor="middle" font-size="11" fill="#177255">✓ acknowledge alarms</text>
  <text x="120" y="144" text-anchor="middle" font-size="11" fill="#177255">✓ operate barriers</text>
  <text x="120" y="162" text-anchor="middle" font-size="11" fill="#177255">✓ keep the watch log</text>
  <text x="120" y="192" text-anchor="middle" font-size="10.5" fill="#9aa1ad">rights of the POSITION</text>
  <rect x="420" y="30" width="200" height="180" rx="12" fill="#eef2f6" stroke="#64748b" stroke-width="1.5"/>
  <text x="520" y="58" text-anchor="middle" font-size="13.5" font-weight="700" fill="#475569">Her PC, 1 m away</text>
  <text x="520" y="78" text-anchor="middle" font-size="11.5" fill="#5c6b7e">Anna signs in → the actor is</text>
  <text x="520" y="96" text-anchor="middle" font-size="12.5" font-weight="700" fill="#b45309">"Anna"</text>
  <text x="520" y="126" text-anchor="middle" font-size="11" fill="#5c6b7e">✓ read e-mail</text>
  <text x="520" y="144" text-anchor="middle" font-size="11" fill="#5c6b7e">✓ time tracking</text>
  <text x="520" y="162" text-anchor="middle" font-size="11" fill="#5c6b7e">✗ no barriers, no alarms</text>
  <text x="520" y="192" text-anchor="middle" font-size="10.5" fill="#9aa1ad">rights of the PERSON</text>
</svg>
<p class="pt-note">This is why the terminal is hardened more than the PC next to it: it is
the vessel for the <em>post's</em> rights, which can exceed those of any single person in
front of it. Security scales with the position's rights, not the person's. The inverse
exists too — a kiosk position can deliberately hold <em>fewer</em> rights than the human
in front of it.</p>
</div>

## A position never authenticates — it gets activated

A position has no login credential of its own (that is the difference to a
[service account](/admin/service-accounts), which identifies *itself*, from
anywhere). Every position token starts with an allowed activation proof — a
person proving themselves or a position-owned hardware token — **at an enrolled
terminal**. The chain is strict:

```
Position → terminal assignment → enrolled device → allowed activation proof → session
```

No slot → no device → no unlock → never a token. A position without terminals
is valid, but dormant: configuration waiting for hardware.

## A shift at the gate

1. **06:02 — Anna taps.** Modgud checks all three links at once: is this the
   real device (③)? may the gate run here (②)? may Anna staff the gate (①)?
   → unlocked. From now on the terminal acts as *the gate*.
2. **Handover:** Ben taps → Anna's shift ends automatically, his begins.
   Exactly **one** shift runs per terminal at any time.
3. **Locking:** at the device, or remotely by an admin (**force-lock**,
   effective immediately — terminal tokens are revoked on the spot).
4. **Time limits:** every shift ends at the configured ceiling at the latest
   (default 16 h, absolute maximum 24 h), even if nobody locks.
5. **Cascades:** deactivating Anna, revoking her grant, disabling the slot or
   the position — each ends the affected running shift automatically.

## What the audit attests — and what it doesn't

The staffing audit attests **the unlock, not each action**:

```
06:02  gate / left terminal unlocked by Anna
07:15  alarm #4711 acknowledged by "the gate"
14:01  handover: Anna's shift ended, unlocked by Ben
17:40  force-lock by admin — terminal locked
```

Who *actually clicked* the alarm at 07:15 is not recorded — if Anna was on a
break and a colleague clicked, the log still shows Anna's shift. That is not a
gap; it is the nature of every shared device. What the model guarantees:
**only authorized people can unlock, and who unlocked is cleanly recorded.**
Accountability is **session-level, not action-level**. For a critical action,
the consumer can request a fresh step-up proof. Modgud then returns a separate
access token valid for at most 60 seconds; it may be bound to an action and
consumer nonce and is intended to be consumed once by `jti`.

## Which principal for which job?

| If … | … then |
|---|---|
| a **person** acts and must appear in the business data (receipt, ticket, signature) | ordinary **user login** — also on a shared device, with fast switching |
| a **post** acts that has to be activated (gate, control room, reception) | **position** + terminals — this model |
| a **machine** acts, with no human activation at all (sealed appliance, server job) | **service account** |

The test question in one sentence: *"Who owns what the system does — the
person, the post, or the machine?"* One concept per answer, and no fourth is
needed. (A **group** is none of the three — it distributes rights, it never
acts.)

## Policy choices and guard rails

How people and devices prove themselves is a per-position policy. Multiple
activation classes can be enabled together; DPoP + personal passkey remains
the recommended default.

- **Activation proof:** personal passkey, personal password, personal e-mail
  OTP, or a **position-owned activation token**. The token is a logical,
  individually revocable object with an RP-bound WebAuthn credential; the audit
  names the token rather than a person. `team-secret` is reserved for a future
  feature and is deliberately unavailable today.
- **Device binding:** DPoP key, client secret, or none. Client-secret and none
  still run the complete admin-approved Device Flow; `none` only removes a
  cryptographic device identity and is appropriate only where the physical and
  network controls justify it.
- **Realm guard rails:** the realm declares required proof and binding
  capabilities. Tightening a floor first previews affected positions and, when
  confirmed, immediately ends sessions that no longer comply.
- **Multi-position terminals:** one device may serve several positions
  ("reception" by day, "night gate" after hours). New assignments are fixed
  before enrollment; adding one later requires a replacement slot and fresh
  approval. Exactly one active shift still exists per terminal.
