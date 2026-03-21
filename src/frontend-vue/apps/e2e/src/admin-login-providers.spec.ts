import { test, expect } from '@playwright/test';

/** Generate a unique name to avoid conflicts across test runs */
function uniqueName(prefix: string) {
  return `${prefix}-${Date.now()}`;
}

test.describe('Admin Login Providers', () => {
  test('list page loads', async ({ page }) => {
    await page.goto('/system/admin/login-providers');

    await expect(page.getByRole('heading', { name: 'Login Providers' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New Provider' })).toBeVisible();
  });

  test('create OIDC provider with specific fields', async ({ page }) => {
    const providerName = uniqueName('oidc');
    await page.goto('/system/admin/login-providers/create');

    // Fill basic info
    await page.getByRole('textbox', { name: 'Name *' }).fill(providerName);
    await page.getByLabel('Display Name').fill('Google Test');

    // Change type to OpenID Connect
    await page.getByRole('combobox').click();
    await page.getByRole('option', { name: 'OpenID Connect' }).click();

    // Configuration tab should now appear
    await page.getByText('Configuration').click();

    // Fill OIDC-specific fields
    await expect(page.getByLabel('Authority')).toBeVisible();
    await expect(page.getByLabel('Client ID')).toBeVisible();
    await expect(page.getByLabel('Client Secret')).toBeVisible();
    await expect(page.getByLabel('Scopes')).toBeVisible();

    await page.getByLabel('Authority').fill('https://accounts.google.com');
    await page.getByLabel('Client ID').fill('e2e-test-client-id');
    await page.getByLabel('Client Secret').fill('e2e-test-secret');

    // Submit
    await page.getByRole('button', { name: 'Create' }).click();

    // Should redirect to list
    await expect(page).toHaveURL(/\/admin\/login-providers$/, { timeout: 10_000 });
  });

  test('created provider appears in list', async ({ page }) => {
    const providerName = uniqueName('list');

    // Create a provider
    await page.goto('/system/admin/login-providers/create');
    await page.getByRole('textbox', { name: 'Name *' }).fill(providerName);
    await page.getByLabel('Display Name').fill('List Test');
    await page.getByRole('combobox').click();
    await page.getByRole('option', { name: 'OpenID Connect' }).click();
    await page.getByText('Configuration').click();
    await page.getByLabel('Authority').fill('https://example.com');
    await page.getByLabel('Client ID').fill('list-test-id');
    await page.getByRole('button', { name: 'Create' }).click();
    await expect(page).toHaveURL(/\/admin\/login-providers$/, { timeout: 10_000 });

    // Verify it appears in the list
    await expect(page.getByText(providerName)).toBeVisible();
    await expect(page.getByRole('gridcell', { name: 'List Test' }).first()).toBeVisible();
  });

  test('edit provider loads configuration fields', async ({ page }) => {
    const providerName = uniqueName('edit');

    // Create a provider first
    await page.goto('/system/admin/login-providers/create');
    await page.getByRole('textbox', { name: 'Name *' }).fill(providerName);
    await page.getByLabel('Display Name').fill('Edit Test');
    await page.getByRole('combobox').click();
    await page.getByRole('option', { name: 'OpenID Connect' }).click();
    await page.getByText('Configuration').click();
    await page.getByLabel('Authority').fill('https://original.example.com');
    await page.getByLabel('Client ID').fill('original-client-id');
    await page.getByLabel('Client Secret').fill('original-secret');
    await page.getByRole('button', { name: 'Create' }).click();
    await expect(page).toHaveURL(/\/admin\/login-providers$/, { timeout: 10_000 });

    // Click on the provider to edit
    await page.getByText(providerName).click();
    await expect(page.getByRole('heading', { name: 'Edit Login Provider' })).toBeVisible();

    // Wait for data to load, then switch to Configuration tab
    await expect(page.getByRole('tab', { name: 'Configuration' })).toBeVisible({ timeout: 10_000 });
    await page.getByRole('tab', { name: 'Configuration' }).click();

    // Fields should be populated with saved values
    await expect(page.getByLabel('Authority')).toHaveValue('https://original.example.com', { timeout: 5_000 });
    await expect(page.getByLabel('Client ID')).toHaveValue('original-client-id');
    await expect(page.getByLabel('Client Secret')).toHaveValue('original-secret');
  });

  test('OIDC validation requires Authority and Client ID', async ({ page }) => {
    await page.goto('/system/admin/login-providers/create');

    await page.getByRole('textbox', { name: 'Name *' }).fill('validation-test');
    await page.getByRole('combobox').click();
    await page.getByRole('option', { name: 'OpenID Connect' }).click();

    // Try to create without filling OIDC fields
    await page.getByRole('button', { name: 'Create' }).click();

    // Should show validation error
    await expect(page.getByText('Authority is required')).toBeVisible();
  });
});
