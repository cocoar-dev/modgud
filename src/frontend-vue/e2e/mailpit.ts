/**
 * Mailpit client — the rig's outbound-mail capture is a real Mailpit container
 * receiving SMTP from the auth API on port 1025 and exposing its REST API on
 * the host at `process.env.E2E_MAILPIT_URL`. Specs poll for the email they
 * expect (Mailpit indexes by recipient) and read the rendered HTML/text body
 * to grab tokens or links.
 *
 * We deliberately avoid scraping `docker logs` — Mailpit's API is the stable
 * contract.
 */

interface MailpitMessageSummary {
  ID: string
  Subject: string
  To: { Address: string; Name: string }[]
  Created: string
}

interface MailpitMessage extends MailpitMessageSummary {
  HTML: string
  Text: string
}

function mailpitBase(): string {
  const url = process.env.E2E_MAILPIT_URL
  if (!url) throw new Error('E2E_MAILPIT_URL is not set — globalSetup did not run, or is using an external instance.')
  return url
}

/**
 * Poll Mailpit for a message addressed to <to> that arrived after <since>.
 * Returns the latest match (Mailpit's default sort is newest-first). The
 * `since` cutoff guards against picking up a stale mail from a previous spec.
 */
export async function waitForMail(
  to: string,
  since: Date = new Date(Date.now() - 1000),
  timeoutMs = 10_000,
): Promise<MailpitMessage> {
  const deadline = Date.now() + timeoutMs
  const base = mailpitBase()
  const query = `to:${to}`
  while (Date.now() < deadline) {
    const res = await fetch(`${base}/api/v1/search?query=${encodeURIComponent(query)}`)
    if (res.ok) {
      const body = await res.json() as { messages: MailpitMessageSummary[] }
      // Newest-first; first match after `since` wins.
      const match = body.messages.find((m) => new Date(m.Created) >= since)
      if (match) {
        const detailRes = await fetch(`${base}/api/v1/message/${match.ID}`)
        if (detailRes.ok) {
          return await detailRes.json() as MailpitMessage
        }
      }
    }
    await new Promise((r) => setTimeout(r, 250))
  }
  throw new Error(`No mail to ${to} arrived within ${timeoutMs}ms`)
}

/** Wipe Mailpit's store — call between independent specs. */
export async function clearMailpit(): Promise<void> {
  const base = mailpitBase()
  await fetch(`${base}/api/v1/messages`, { method: 'DELETE' })
}

/**
 * Pull the magic-link / verify / reset token out of an HTML body.
 * The auth backend's email templates always carry the token as `?token=...`
 * (URL-encoded) on the action link. Specs that need it just call this on the
 * `HTML` field of a Mailpit message.
 */
export function extractTokenFromHtml(html: string): string {
  return extractQueryParam(html, 'token')
}

/**
 * Generic query-param extractor — pulls `?<name>=...` from the first match
 * in an HTML body. Used to lift userId, token, and any future link-encoded
 * ids straight out of the email the user would have clicked.
 */
export function extractQueryParam(html: string, name: string): string {
  const re = new RegExp(`[?&]${name}=([^"&]+)`)
  const match = html.match(re)
  if (!match) throw new Error(`No ${name}=... in HTML body. Body excerpt: ${html.slice(0, 200)}`)
  return decodeURIComponent(match[1])
}

/**
 * Same shape but pulls a numeric OTP code (typically a 6-digit Email-OTP)
 * out of an HTML body. The templates render the code in a styled `<div>`
 * with no surrounding text other than instructions; we look for the longest
 * digit run.
 */
export function extractOtpCodeFromHtml(html: string): string {
  // Strip tags first so a digit inside an attribute (e.g. style="line-height:18")
  // can't mask the real code.
  const text = html.replace(/<[^>]+>/g, ' ')
  const matches = [...text.matchAll(/\b(\d{4,8})\b/g)]
  if (matches.length === 0) throw new Error(`No OTP code in HTML body. Body excerpt: ${html.slice(0, 200)}`)
  // Pick the longest match — guards against picking up a year-like number.
  return matches.map((m) => m[1]).sort((a, b) => b.length - a.length)[0]
}
