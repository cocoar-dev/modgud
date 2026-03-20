import { test as setup } from '@playwright/test';

const ADMIN_USER = process.env['E2E_ADMIN_USER'] || 'admin';
const ADMIN_PASSWORD = process.env['E2E_ADMIN_PASSWORD'] || 'ABC12abc!';
const adminFile = 'playwright/.auth/admin.json';

/**
 * Auth setup — ensures an admin user exists and saves the authenticated cookie state.
 * Other tests reuse this state so they don't need to login via UI.
 *
 * Configure credentials via environment variables:
 *   E2E_ADMIN_USER (default: admin)
 *   E2E_ADMIN_PASSWORD (default: ABC12abc!)
 *
 * If setup is needed (fresh DB), the admin user is auto-created first.
 */
setup('authenticate as admin', async ({ page }) => {
  // Check if setup is needed (fresh DB → create admin)
  const setupStatus = await page.request.get('/system/api/setup/status');
  if (setupStatus.ok()) {
    const status = await setupStatus.json();
    if (status.needsSetup) {
      const createAdmin = await page.request.post('/system/api/setup/create-admin', {
        data: {
          userName: ADMIN_USER,
          password: ADMIN_PASSWORD,
          email: `${ADMIN_USER}@test.com`,
        },
      });
      if (!createAdmin.ok()) {
        const body = await createAdmin.text();
        throw new Error(`Failed to create admin: ${createAdmin.status()} ${body}`);
      }
      // Setup endpoint auto-logs in — verify by navigating to home
      await page.goto('/system/');
      await page.waitForURL(/\/system\/?$/, { timeout: 10_000 });
      await page.context().storageState({ path: adminFile });
      return;
    }
  }

  // DB already set up — login via UI
  await page.goto('/system/login');
  await page.getByLabel('Username').fill(ADMIN_USER);
  await page.locator('input[autocomplete="current-password"]').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: 'Sign In' }).click();

  // Wait for redirect to home page (successful login)
  await page.waitForURL(/\/system\/?$/, { timeout: 10_000 });

  // Save the authenticated state (cookies) for reuse
  await page.context().storageState({ path: adminFile });
});
