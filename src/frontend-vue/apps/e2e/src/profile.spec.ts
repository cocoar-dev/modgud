import { test, expect } from '@playwright/test';

test.describe('Profile Page', () => {
  test('shows personal information fields', async ({ page }) => {
    await page.goto('/system/profile');

    await expect(page.getByLabel('First Name')).toBeVisible();
    await expect(page.getByLabel('Last Name')).toBeVisible();
    await expect(page.getByText('Username')).toBeVisible();
    await expect(page.getByText('Email')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save Changes' })).toBeVisible();
  });

  test('shows two-factor authentication section', async ({ page }) => {
    await page.goto('/system/profile');

    await expect(page.getByRole('heading', { name: 'Two-Factor Authentication' })).toBeVisible();
    // Should show either "Enable" button or "Enabled" tag
    const enableBtn = page.getByRole('button', { name: /Enable Two-Factor/i });
    const disabledTag = page.getByText('DISABLED');
    const enabledTag = page.getByText('ENABLED');

    // One of these must be visible
    const isSetup = await enabledTag.isVisible().catch(() => false);
    if (isSetup) {
      await expect(enabledTag).toBeVisible();
    } else {
      await expect(disabledTag).toBeVisible();
      await expect(enableBtn).toBeVisible();
    }
  });

  test('shows connected accounts section when providers exist', async ({ page }) => {
    await page.goto('/system/profile');

    // Wait for the page to fully load
    await page.waitForResponse('**/api/auth/external-providers');

    // Connected Accounts section only shows if OIDC providers are configured
    const connectedAccounts = page.getByText('Connected Accounts');
    if (await connectedAccounts.isVisible()) {
      await expect(page.getByText('Link external accounts')).toBeVisible();
    }
  });
});
