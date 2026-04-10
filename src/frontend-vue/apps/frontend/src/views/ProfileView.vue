<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import QRCode from 'qrcode';
import { CoarCard, CoarButton, CoarTextInput, CoarNote, CoarSpinner, CoarPasswordInput, CoarTag, useToast } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import type { Profile, TwoFactorStatus, TwoFactorSetup, ExternalProvider, LinkedExternalLogin } from '@/core/models/auth.models';

const ui = useUI();

const toast = useToast();
const profile = ref<Profile | null>(null);
const isLoading = ref(true);
const isSaving = ref(false);
const isChangingPassword = ref(false);

const firstName = ref('');
const lastName = ref('');
const phoneNumber = ref('');

const currentPassword = ref('');
const newPassword = ref('');
const confirmPassword = ref('');

const profileError = ref('');
const passwordError = ref('');

// 2FA state
const twoFactorStatus = ref<TwoFactorStatus | null>(null);
const setupData = ref<TwoFactorSetup | null>(null);
const qrCodeDataUrl = ref('');
const showSetupForm = ref(false);
const showDisableForm = ref(false);
const twoFactorCode = ref('');
const recoveryCodes = ref<string[] | null>(null);
const isLoadingTwoFactor = ref(false);
const twoFactorError = ref('');
const twoFactorSuccess = ref('');
const copySuccess = ref(false);

// External logins state
const externalProviders = ref<ExternalProvider[]>([]);
const linkedLogins = ref<LinkedExternalLogin[]>([]);
const isLoadingExternalLogins = ref(false);
const externalLoginError = ref('');

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'My Profile';
  ctx.content.scrollable = true;
});

onMounted(async () => {
  try {
    const [profileData, tfaStatus, providers, linked] = await Promise.all([
      authApi.getProfile(),
      authApi.getTwoFactorStatus(),
      authApi.getExternalProviders().catch(() => ({ providers: [] })),
      authApi.getLinkedExternalLogins().catch(() => ({ logins: [] })),
    ]);
    profile.value = profileData;
    firstName.value = profileData.firstName || '';
    lastName.value = profileData.lastName || '';
    phoneNumber.value = profileData.phoneNumber || '';
    twoFactorStatus.value = tfaStatus;
    externalProviders.value = providers.providers;
    linkedLogins.value = linked.logins;
  } catch {
    profileError.value = 'Failed to load profile.';
  } finally {
    isLoading.value = false;
  }
});

async function saveProfile() {
  isSaving.value = true;
  profileError.value = '';
  try {
    await authApi.updateProfile({
      firstName: firstName.value || undefined,
      lastName: lastName.value || undefined,
      phoneNumber: phoneNumber.value || undefined,
    });
    toast.success('Profile updated successfully.');
  } catch (err) {
    profileError.value = err instanceof ApiError ? err.message : 'Failed to update profile.';
  } finally {
    isSaving.value = false;
  }
}

async function changePassword() {
  if (!currentPassword.value || !newPassword.value) return;
  if (newPassword.value !== confirmPassword.value) {
    passwordError.value = 'New passwords do not match.';
    return;
  }
  isChangingPassword.value = true;
  passwordError.value = '';
  try {
    await authApi.changePassword({ currentPassword: currentPassword.value, newPassword: newPassword.value });
    toast.success('Password changed successfully.');
    currentPassword.value = '';
    newPassword.value = '';
    confirmPassword.value = '';
  } catch (err) {
    passwordError.value = err instanceof ApiError ? err.message : 'Failed to change password.';
  } finally {
    isChangingPassword.value = false;
  }
}

async function startSetup2FA() {
  isLoadingTwoFactor.value = true;
  twoFactorError.value = '';
  twoFactorSuccess.value = '';
  try {
    setupData.value = await authApi.setupTwoFactor();
    if (setupData.value.authenticatorUri) {
      qrCodeDataUrl.value = await QRCode.toDataURL(setupData.value.authenticatorUri, { width: 200 });
    }
    showSetupForm.value = true;
    twoFactorCode.value = '';
  } catch (err) {
    twoFactorError.value = err instanceof ApiError ? err.message : 'Failed to start 2FA setup.';
  } finally {
    isLoadingTwoFactor.value = false;
  }
}

async function confirmEnable2FA() {
  if (!twoFactorCode.value) return;
  isLoadingTwoFactor.value = true;
  twoFactorError.value = '';
  try {
    const result = await authApi.enableTwoFactor({ code: twoFactorCode.value });
    recoveryCodes.value = result.codes;
    twoFactorStatus.value = await authApi.getTwoFactorStatus();
    showSetupForm.value = false;
    setupData.value = null;
    qrCodeDataUrl.value = '';
    twoFactorCode.value = '';
    toast.success('Two-factor authentication enabled.');
  } catch (err) {
    twoFactorError.value = err instanceof ApiError ? err.message : 'Invalid code. Please try again.';
  } finally {
    isLoadingTwoFactor.value = false;
  }
}

async function confirmDisable2FA() {
  if (!twoFactorCode.value) return;
  isLoadingTwoFactor.value = true;
  twoFactorError.value = '';
  try {
    await authApi.disableTwoFactor({ code: twoFactorCode.value });
    twoFactorStatus.value = await authApi.getTwoFactorStatus();
    showDisableForm.value = false;
    twoFactorCode.value = '';
    recoveryCodes.value = null;
    toast.success('Two-factor authentication disabled.');
  } catch (err) {
    twoFactorError.value = err instanceof ApiError ? err.message : 'Invalid code. Please try again.';
  } finally {
    isLoadingTwoFactor.value = false;
  }
}

async function generateRecoveryCodes() {
  isLoadingTwoFactor.value = true;
  twoFactorError.value = '';
  twoFactorSuccess.value = '';
  try {
    const result = await authApi.generateRecoveryCodes();
    recoveryCodes.value = result.codes;
    twoFactorStatus.value = await authApi.getTwoFactorStatus();
  } catch (err) {
    twoFactorError.value = err instanceof ApiError ? err.message : 'Failed to generate recovery codes.';
  } finally {
    isLoadingTwoFactor.value = false;
  }
}

function cancelTwoFactor() {
  showSetupForm.value = false;
  showDisableForm.value = false;
  twoFactorCode.value = '';
  twoFactorError.value = '';
  setupData.value = null;
  qrCodeDataUrl.value = '';
}

function formatManualKey(key: string) {
  return key.replace(/(.{4})/g, '$1 ').trim();
}

async function copyRecoveryCodes() {
  if (!recoveryCodes.value) return;
  await navigator.clipboard.writeText(recoveryCodes.value.join('\n'));
  copySuccess.value = true;
  setTimeout(() => { copySuccess.value = false; }, 3000);
}

function downloadRecoveryCodes() {
  if (!recoveryCodes.value) return;
  const text = recoveryCodes.value.join('\n');
  const blob = new Blob([text], { type: 'text/plain' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'recovery-codes.txt';
  a.click();
  URL.revokeObjectURL(url);
}

// External login methods
function linkExternalLogin(providerName: string) {
  const returnUrl = '/profile';
  const url = `/api/auth/external-login?provider=${encodeURIComponent(providerName)}&returnUrl=${encodeURIComponent(returnUrl)}`;
  window.location.href = url;
}

async function unlinkExternalLogin(providerName: string) {
  isLoadingExternalLogins.value = true;
  externalLoginError.value = '';
  try {
    await authApi.unlinkExternalLogin(providerName);
    linkedLogins.value = linkedLogins.value.filter(l => l.providerName !== providerName);
    toast.success(`${providerName} has been unlinked.`);
  } catch (err) {
    externalLoginError.value = err instanceof ApiError ? err.message : 'Failed to unlink provider.';
  } finally {
    isLoadingExternalLogins.value = false;
  }
}

function isLinked(providerName: string): boolean {
  return linkedLogins.value.some(l => l.providerName === providerName);
}

const hasPassword = computed(() => !!profile.value?.userName);
const canUnlink = computed(() => {
  return hasPassword.value || linkedLogins.value.length > 1;
});
</script>

<template>
  <div class="page">
    <div v-if="isLoading" class="centered">
      <CoarSpinner size="l" />
    </div>

    <template v-else>
      <CoarCard padding="l" class="section-card">
        <h2 class="section-title">Personal Information</h2>

        <CoarNote v-if="profileError" variant="error" padding="s" class="mb-3">{{ profileError }}</CoarNote>

        <div class="form-row-2">
          <CoarTextInput v-model="firstName" label="First Name" />
          <CoarTextInput v-model="lastName" label="Last Name" />
        </div>
        <div class="form-group">
          <div class="readonly-field">
            <span class="readonly-label">Username</span>
            <span class="readonly-value">{{ profile?.userName }}</span>
          </div>
        </div>
        <div class="form-group">
          <div class="readonly-field">
            <span class="readonly-label">Email</span>
            <span class="readonly-value">
              {{ profile?.email }}
              <CoarTag v-if="profile?.emailConfirmed" variant="success" size="s">Verified</CoarTag>
              <CoarTag v-else variant="warning" size="s">Unverified</CoarTag>
            </span>
          </div>
        </div>
        <div class="form-group">
          <CoarTextInput v-model="phoneNumber" label="Phone Number" />
        </div>

        <CoarButton variant="primary" :loading="isSaving" @click="saveProfile">
          Save Changes
        </CoarButton>
      </CoarCard>

      <CoarCard padding="l" class="section-card">
        <h2 class="section-title">Change Password</h2>

        <CoarNote v-if="passwordError" variant="error" padding="s" class="mb-3">{{ passwordError }}</CoarNote>

        <div class="form-group">
          <CoarPasswordInput v-model="currentPassword" label="Current Password" autocomplete="current-password" />
        </div>
        <div class="form-group">
          <CoarPasswordInput v-model="newPassword" label="New Password" autocomplete="new-password" />
        </div>
        <div class="form-group">
          <CoarPasswordInput v-model="confirmPassword" label="Confirm New Password" autocomplete="new-password" />
        </div>

        <CoarButton variant="primary" :loading="isChangingPassword" @click="changePassword">
          Change Password
        </CoarButton>
      </CoarCard>

      <CoarCard padding="l" class="section-card">
        <h2 class="section-title">Two-Factor Authentication</h2>

        <CoarNote v-if="twoFactorError" variant="error" padding="s" class="mb-3">{{ twoFactorError }}</CoarNote>

        <template v-if="twoFactorStatus">
          <div class="tfa-status-row mb-3">
            <CoarTag :variant="twoFactorStatus.isEnabled ? 'success' : 'neutral'" size="m">
              {{ twoFactorStatus.isEnabled ? 'Enabled' : 'Disabled' }}
            </CoarTag>
            <span v-if="twoFactorStatus.isEnabled" class="tfa-recovery-info">
              {{ twoFactorStatus.recoveryCodesRemaining }} recovery code{{ twoFactorStatus.recoveryCodesRemaining !== 1 ? 's' : '' }} remaining
            </span>
          </div>

          <!-- Setup flow -->
          <template v-if="showSetupForm && setupData">
            <CoarNote variant="info" padding="s" class="mb-3">
              Scan the QR code with your authenticator app, or enter the manual key. Then enter the 6-digit code to confirm.
            </CoarNote>
            <div v-if="qrCodeDataUrl" class="tfa-qr-wrapper mb-3">
              <img :src="qrCodeDataUrl" alt="Scan with your authenticator app" class="tfa-qr-code" />
            </div>
            <div class="tfa-key-block mb-3">
              <span class="tfa-key-label">Manual key</span>
              <code class="tfa-key-value">{{ formatManualKey(setupData.sharedKey) }}</code>
            </div>
            <div class="form-group">
              <CoarTextInput v-model="twoFactorCode" label="Verification Code" placeholder="6-digit code" autocomplete="one-time-code" />
            </div>
            <div class="button-row">
              <CoarButton variant="primary" :loading="isLoadingTwoFactor" @click="confirmEnable2FA">Confirm &amp; Enable</CoarButton>
              <CoarButton variant="ghost" @click="cancelTwoFactor">Cancel</CoarButton>
            </div>
          </template>

          <!-- Disable flow -->
          <template v-else-if="showDisableForm">
            <CoarNote variant="warning" padding="s" class="mb-3">
              Disabling two-factor authentication will make your account less secure. You will only need your password to sign in.
            </CoarNote>
            <div class="form-group">
              <CoarTextInput v-model="twoFactorCode" label="Current Authenticator Code" placeholder="6-digit code" autocomplete="one-time-code" />
            </div>
            <div class="button-row">
              <CoarButton variant="danger" :loading="isLoadingTwoFactor" @click="confirmDisable2FA">Confirm Disable</CoarButton>
              <CoarButton variant="ghost" @click="cancelTwoFactor">Cancel</CoarButton>
            </div>
          </template>

          <!-- Recovery codes display -->
          <template v-else-if="recoveryCodes">
            <CoarNote variant="warning" padding="s" class="mb-3">
              Save these recovery codes somewhere safe. They will not be shown again.
            </CoarNote>
            <div class="tfa-recovery-codes mb-3">
              <code v-for="code in recoveryCodes" :key="code" class="tfa-recovery-code">{{ code }}</code>
            </div>
            <div class="button-row mb-3">
              <CoarButton variant="secondary" @click="copyRecoveryCodes">
                {{ copySuccess ? 'Copied!' : 'Copy All' }}
              </CoarButton>
              <CoarButton variant="secondary" @click="downloadRecoveryCodes">Download</CoarButton>
            </div>
            <CoarButton variant="ghost" @click="recoveryCodes = null">Done</CoarButton>
          </template>

          <!-- Default actions -->
          <template v-else>
            <div class="button-row" v-if="!twoFactorStatus.isEnabled">
              <CoarButton variant="primary" :loading="isLoadingTwoFactor" icon-start="shield" @click="startSetup2FA">
                Enable Two-Factor Authentication
              </CoarButton>
            </div>
            <div class="button-row" v-else>
              <CoarButton variant="secondary" :loading="isLoadingTwoFactor" @click="generateRecoveryCodes">
                Generate New Recovery Codes
              </CoarButton>
              <CoarButton variant="danger" @click="showDisableForm = true; twoFactorCode = ''">
                Disable 2FA
              </CoarButton>
            </div>
          </template>
        </template>
      </CoarCard>

      <CoarCard v-if="externalProviders.length > 0" padding="l" class="section-card">
        <h2 class="section-title">Connected Accounts</h2>

        <CoarNote v-if="externalLoginError" variant="error" padding="s" class="mb-3">{{ externalLoginError }}</CoarNote>

        <p class="section-description mb-3">
          Link external accounts to sign in with them. You can also use these as an alternative to your password.
        </p>

        <div class="external-login-list">
          <div v-for="provider in externalProviders" :key="provider.name" class="external-login-item">
            <div class="external-login-info">
              <span class="external-login-name">{{ provider.displayName || provider.name }}</span>
              <CoarTag v-if="isLinked(provider.name)" variant="success" size="s">Connected</CoarTag>
              <CoarTag v-else variant="neutral" size="s">Not connected</CoarTag>
            </div>
            <CoarButton
              v-if="isLinked(provider.name)"
              variant="danger"
              size="s"
              :loading="isLoadingExternalLogins"
              :disabled="!canUnlink"
              @click="unlinkExternalLogin(provider.name)"
            >
              Unlink
            </CoarButton>
            <CoarButton
              v-else
              variant="secondary"
              size="s"
              @click="linkExternalLogin(provider.name)"
            >
              Link
            </CoarButton>
          </div>
        </div>

        <CoarNote v-if="linkedLogins.length === 1 && !hasPassword" variant="info" padding="s" class="mt-3">
          You cannot unlink your only login method. Set a password first or link another account.
        </CoarNote>
      </CoarCard>
    </template>
  </div>
</template>

<style scoped>
.page { }
.section-card { margin-bottom: 1.25rem; }
.section-title { margin: 0; }  /* global .section-title handles divider + spacing */
.form-group { margin-bottom: 1rem; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }

.readonly-field { display: flex; flex-direction: column; gap: 0.25rem; }
.readonly-label { font-size: 0.8125rem; font-weight: 500; color: var(--coar-text-neutral-secondary); }
.readonly-value { display: flex; align-items: center; gap: 0.5rem; font-size: 0.9375rem; color: var(--coar-text-neutral-primary); }

.tfa-status-row { display: flex; align-items: center; gap: 0.75rem; }
.tfa-recovery-info { font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }

.tfa-qr-wrapper { display: flex; }
.tfa-qr-code { border-radius: 6px; border: 1px solid var(--coar-border-neutral-tertiary); }

.tfa-key-block { background: var(--coar-color-slate-50); border: 1px solid var(--coar-color-slate-100); border-radius: 7px; padding: 0.75rem 1rem; }
.tfa-key-label { display: block; font-size: 0.75rem; font-weight: 600; color: var(--coar-text-neutral-secondary); margin-bottom: 0.375rem; text-transform: uppercase; letter-spacing: 0.06em; }
.tfa-key-value { font-family: monospace; font-size: 0.9375rem; letter-spacing: 0.08em; color: var(--coar-text-neutral-primary); word-break: break-all; }

.tfa-recovery-codes { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 0.5rem; }
.tfa-recovery-code { font-family: monospace; font-size: 0.875rem; background: var(--coar-color-slate-50); border: 1px solid var(--coar-color-slate-100); padding: 0.375rem 0.625rem; border-radius: 6px; }

.button-row { display: flex; gap: 0.75rem; flex-wrap: wrap; }

.section-description { font-size: 0.875rem; color: var(--coar-text-neutral-secondary); margin: 0; }
.mt-3 { margin-top: 0.75rem; }

.external-login-list { display: flex; flex-direction: column; gap: 0.5rem; }
.external-login-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 1rem;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 8px;
}
.external-login-info { display: flex; align-items: center; gap: 0.75rem; }
.external-login-name { font-size: 0.9375rem; font-weight: 500; color: var(--coar-text-neutral-primary); }
</style>
