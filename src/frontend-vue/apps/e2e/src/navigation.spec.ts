import { test, expect } from '@playwright/test';

test.describe('Authenticated Navigation', () => {
  // These tests use the saved admin auth state

  test('home page shows welcome message', async ({ page }) => {
    await page.goto('/system/');

    await expect(page.getByText('Welcome back')).toBeVisible();
  });

  test('sidebar shows account and admin sections', async ({ page }) => {
    await page.goto('/system/');

    // Account section
    await expect(page.getByRole('menuitem', { name: 'Home' })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Profile' })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Sessions' })).toBeVisible();

    // Admin section (admin user)
    await expect(page.getByRole('menuitem', { name: 'Users' })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Roles' })).toBeVisible();
  });

  test('profile page loads', async ({ page }) => {
    await page.goto('/system/profile');

    await expect(page.getByRole('heading', { name: 'My Profile' })).toBeVisible();
    await expect(page.getByText('Personal Information')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Change Password' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Two-Factor Authentication' })).toBeVisible();
  });

  test('sessions page loads', async ({ page }) => {
    await page.goto('/system/sessions');

    await expect(page.getByRole('heading', { name: 'Sessions' })).toBeVisible();
  });

  test('admin users page loads', async ({ page }) => {
    await page.goto('/system/admin/users');

    await expect(page.getByRole('heading', { name: 'Users' })).toBeVisible();
  });

  test('admin roles page loads', async ({ page }) => {
    await page.goto('/system/admin/roles');

    await expect(page.getByRole('heading', { name: 'Roles' })).toBeVisible();
  });

  test('admin login providers page loads', async ({ page }) => {
    await page.goto('/system/admin/login-providers');

    await expect(page.getByRole('heading', { name: 'Login Providers' })).toBeVisible();
  });
});
