import { test, expect } from '@playwright/test';

test.describe('Login Page', () => {
  // These tests run WITHOUT auth state (fresh browser)
  test.use({ storageState: { cookies: [], origins: [] } });

  test('shows login form', async ({ page }) => {
    await page.goto('/system/login');

    await expect(page.getByRole('heading', { name: 'Sign In' })).toBeVisible();
    await expect(page.getByLabel('Username')).toBeVisible();
    await expect(page.locator('input[autocomplete="current-password"]')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign In' })).toBeVisible();
  });

  test('shows external provider buttons when providers exist', async ({ page }) => {
    await page.goto('/system/login');

    // Wait for providers to load (may be empty if no OIDC providers configured)
    await page.waitForResponse('**/api/auth/external-providers');

    // The "or continue with" divider only shows if there are providers
    const divider = page.getByText('or continue with');
    // This is a soft check — depends on whether providers are configured
    if (await divider.isVisible()) {
      // At least one provider button should be visible
      const providerButtons = page.locator('.external-providers button');
      await expect(providerButtons.first()).toBeVisible();
    }
  });

  test('login with valid credentials redirects to home', async ({ page }) => {
    const user = process.env['E2E_ADMIN_USER'] || 'admin';
    const pass = process.env['E2E_ADMIN_PASSWORD'] || 'ABC12abc!';

    await page.goto('/system/login');

    await page.getByLabel('Username').fill(user);
    await page.locator('input[autocomplete="current-password"]').fill(pass);
    await page.getByRole('button', { name: 'Sign In' }).click();

    // Should redirect to home after successful login
    await expect(page).toHaveURL(/\/system\/?$/, { timeout: 10_000 });
    await expect(page.getByText('Welcome back')).toBeVisible();
  });

  test('login with invalid credentials shows error', async ({ page }) => {
    await page.goto('/system/login');

    await page.getByLabel('Username').fill('nonexistent');
    await page.locator('input[autocomplete="current-password"]').fill('wrongpassword');
    await page.getByRole('button', { name: 'Sign In' }).click();

    await expect(page.getByText('Invalid username or password')).toBeVisible();
  });

  test('unauthenticated user is redirected to login', async ({ page }) => {
    await page.goto('/system/profile');

    // Should redirect to login with returnUrl
    await expect(page).toHaveURL(/\/system\/login\?returnUrl=/);
  });
});
