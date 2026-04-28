import { test, expect } from '@playwright/test'

test.describe('Magic Link', () => {
  test('Request magic link and login via token', async ({ page }) => {
    // Clear emails
    await page.request.delete('/api/dev/emails')

    // Step 1: Go to login page and request magic link
    await page.goto('/login')
    await page.getByRole('button', { name: 'Anmelde-Link per E-Mail' }).click()

    await expect(page.getByPlaceholder('email@beispiel.de')).toBeVisible()
    await page.getByPlaceholder('email@beispiel.de').fill('ka@test.com')
    await page.getByRole('button', { name: 'Link senden' }).click()

    // Should show success message
    await expect(page.getByText('Anmelde-Link gesendet')).toBeVisible({ timeout: 5_000 })

    // Step 2: Extract magic link from dev email endpoint
    const magicUrl = await extractMagicLinkUrl(page, 'ka@test.com')
    expect(magicUrl).toBeTruthy()

    // Step 3: Navigate to magic link URL
    await page.goto(magicUrl!)

    // Should be logged in and redirected to todos
    await expect(page.locator('.title').getByText('Aufgaben')).toBeVisible({ timeout: 10_000 })
  })

  test('Magic link with invalid token redirects to login', async ({ page }) => {
    await page.goto('/magic-login?userId=00000000-0000-0000-0000-000000000000&token=invalid')

    // Invalid token → should end up on login page
    await expect(page).toHaveURL(/\/login/, { timeout: 10_000 })
  })
})

/**
 * Extract magic link URL from the dev email endpoint. Polls briefly — the
 * magic-link request is synchronous from the API's perspective, but the
 * success-message hooks before our network read settled in CI, and a dev-only
 * mailbox retry costs nothing.
 */
async function extractMagicLinkUrl(page: import('@playwright/test').Page, email: string): Promise<string | null> {
  const endpoint = `/api/dev/emails/${encodeURIComponent(email)}`
  let lastBody: string | undefined
  for (let i = 0; i < 10; i++) {
    const response = await page.request.get(endpoint)
    if (response.ok()) {
      const data = await response.json()
      lastBody = data.HtmlBody
      const match = data.HtmlBody?.match(/href="([^"]*magic-login[^"]*)"/)
      if (match) {
        const url = new URL(match[1].replace(/&amp;/g, '&'))
        return `${url.pathname}${url.search}`
      }
    }
    await page.waitForTimeout(300)
  }
  if (lastBody) console.error('[magic-link] body had no magic-login href:', lastBody.slice(0, 500))
  return null
}
