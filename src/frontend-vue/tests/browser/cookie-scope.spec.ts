import { expect, test } from '@playwright/test'

// A real Chromium cookie jar with public dummy markers only. Every request is
// fulfilled locally by Playwright: no backend, DNS, auth values or FIDO mock.
for (const first of ['parent', 'host'] as const) {
  test(`same-name cookie order follows creation, ${first} first`, async ({ context, page, browser }) => {
    const order: string[] = []
    const last = first === 'parent' ? 'host' : 'parent'
    await context.route('**/*', async (route) => {
      const url = new URL(route.request().url())
      if (url.hostname !== 'portal.auth.example.test') return route.abort()
      const headers: Record<string, string> = { 'Content-Type': 'text/html' }
      if (url.pathname === '/parent' || url.pathname === '/host') {
        const scope = url.pathname.slice(1)
        headers['Set-Cookie'] = `scope-probe=${scope}; Path=/; HttpOnly; Secure; SameSite=Lax${
          scope === 'parent' ? '; Domain=auth.example.test' : ''
        }`
      }
      if (url.pathname === '/probe') {
        const cookie = (await route.request().allHeaders()).cookie ?? ''
        order.push(...cookie.split(';').map(x => x.trim())
          .filter(x => x.startsWith('scope-probe=')).map(x => x.slice('scope-probe='.length)))
      }
      await route.fulfill({ status: 200, headers, body: '<!doctype html><title>Cookie scope probe</title>' })
    })
    for (const path of [first, last, 'probe'])
      await page.goto(`https://portal.auth.example.test/${path}`)
    const cookies = await context.cookies('https://portal.auth.example.test/')
    expect(cookies).toHaveLength(2)
    expect(cookies.map(c => c.domain).sort()).toEqual(['.auth.example.test', 'portal.auth.example.test'])
    expect(order).toEqual([first, last])
    test.info().annotations.push({
      type: 'transport',
      description: `Chromium ${browser.version()}; dummy cookies; route.fulfill only`,
    })
  })
}
