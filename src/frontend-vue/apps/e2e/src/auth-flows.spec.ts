import { test, expect } from '@playwright/test';

test.describe('Auth Flow Pages', () => {
  // These tests run WITHOUT auth state (fresh browser)
  test.use({ storageState: { cookies: [], origins: [] } });

  test('forgot password page loads and submits', async ({ page }) => {
    await page.goto('/system/forgot-password');

    await expect(page.getByText('Reset Password')).toBeVisible();
    await expect(page.locator('input[type="email"], input[autocomplete="email"]').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send Reset Link' })).toBeVisible();

    // Submit with an email
    await page.locator('input[type="email"], input[autocomplete="email"]').first().fill('test@example.com');
    await page.getByRole('button', { name: 'Send Reset Link' }).click();

    // Should show success message (always, to prevent user enumeration)
    await expect(page.getByText('reset link has been sent')).toBeVisible({ timeout: 5_000 });
  });

  test('reset password page loads with token params', async ({ page }) => {
    await page.goto('/system/reset-password?email=test@example.com&token=fake-token');

    await expect(page.locator('input[autocomplete="new-password"]').first()).toBeVisible();
    await expect(page.getByRole('button', { name: /Reset Password/i })).toBeVisible();
  });

  test('confirm email page handles invalid token', async ({ page }) => {
    await page.goto('/system/confirm-email?userId=fake-id&token=fake-token');

    // Should show error heading after failed confirmation
    await expect(page.getByRole('heading', { name: /Failed/i })).toBeVisible({ timeout: 10_000 });
  });

  test('register page loads with all fields', async ({ page }) => {
    await page.goto('/system/register');

    await expect(page.getByRole('heading', { name: 'Create Account' })).toBeVisible();
    await expect(page.getByLabel('First Name')).toBeVisible();
    await expect(page.getByLabel('Last Name')).toBeVisible();
    await expect(page.getByLabel('Username')).toBeVisible();
    await expect(page.locator('input[type="email"], input[autocomplete="email"]').first()).toBeVisible();
    await expect(page.locator('input[autocomplete="new-password"]').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Create Account' })).toBeVisible();
    await expect(page.getByText('Already have an account?')).toBeVisible();
  });

  test('consent denied page shows message', async ({ page }) => {
    await page.goto('/system/consent/denied?error_description=User+denied+access');

    await expect(page.getByText('User denied access')).toBeVisible();
  });
});
