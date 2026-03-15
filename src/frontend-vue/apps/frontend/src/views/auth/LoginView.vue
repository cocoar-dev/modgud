<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { CoarCard, CoarButton, CoarTextInput, CoarPasswordInput, CoarCheckbox, CoarNote } from '@cocoar/vue-ui';
import { useAuthStore } from '@/stores/auth.store';

const router = useRouter();
const route = useRoute();
const auth = useAuthStore();

onMounted(() => {
  auth.clearError();
});

const userName = ref('');
const password = ref('');
const rememberMe = ref(false);
const touched = ref({ userName: false, password: false });

const userNameError = computed(() => {
  if (!touched.value.userName) return '';
  if (!userName.value) return 'Username is required';
  return '';
});
const passwordError = computed(() => {
  if (!touched.value.password) return '';
  if (!password.value) return 'Password is required';
  return '';
});

const isValid = computed(() => !!userName.value && !!password.value);

async function onSubmit() {
  touched.value = { userName: true, password: true };
  if (!isValid.value) return;

  const returnUrl = (route.query.returnUrl as string) || '/';
  const result = await auth.login({ userName: userName.value, password: password.value, rememberMe: rememberMe.value }, { redirectTo: returnUrl });

  if (result.requiresTwoFactor) {
    const query: Record<string, string> = { returnUrl };
    if (result.availableTwoFactorMethods?.length) {
      query['methods'] = result.availableTwoFactorMethods.join(',');
    }
    router.push({ path: '/login/2fa', query });
  }
}
</script>

<template>
  <div class="auth-page">
    <div class="auth-container">
      <div class="auth-brand">
        <span class="auth-brand-icon">⚡</span>
        <span class="auth-brand-name">Cocoar Auth</span>
      </div>

      <CoarCard elevated padding="l" class="auth-card">
        <h1 class="auth-title">Sign In</h1>
        <p class="auth-subtitle">Welcome back! Please enter your credentials.</p>

        <CoarNote v-if="auth.error" variant="error" padding="s" class="mb-4">
          {{ auth.error }}
        </CoarNote>

        <form @submit.prevent="onSubmit">
          <div class="form-group">
            <CoarTextInput
              v-model="userName"
              label="Username"
              placeholder="Enter your username"
              autocomplete="username"
              :required="true"
              :error="userNameError"
              @blur="touched.userName = true"
            />
          </div>

          <div class="form-group">
            <CoarPasswordInput
              v-model="password"
              label="Password"
              placeholder="Enter your password"
              autocomplete="current-password"
              :required="true"
              :error="passwordError"
              @blur="touched.password = true"
            />
          </div>

          <div class="form-row">
            <CoarCheckbox v-model="rememberMe" label="Remember me" />
            <RouterLink to="/forgot-password" class="link">Forgot password?</RouterLink>
          </div>

          <CoarButton
            type="submit"
            variant="primary"
            :full-width="true"
            :loading="auth.isLoading"
            :disabled="auth.isLoading"
          >
            Sign In
          </CoarButton>
        </form>

        <p class="auth-footer">
          Don't have an account?
          <RouterLink to="/register" class="link">Create one</RouterLink>
        </p>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.auth-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
}

.auth-container {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 100%;
  max-width: 420px;
  gap: 1.25rem;
}

.auth-brand {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.auth-brand-icon {
  font-size: 1.5rem;
  line-height: 1;
}

.auth-brand-name {
  font-size: 1.125rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.01em;
}

.auth-card {
  width: 100%;
}

.auth-title {
  margin: 0 0 0.375rem;
  font-size: 1.375rem;
  font-weight: 700;
  color: var(--coar-text-neutral-primary);
  letter-spacing: -0.02em;
}

.auth-subtitle {
  margin: 0 0 1.5rem;
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary);
}

.form-group {
  margin-bottom: 1rem;
}

.form-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 1.25rem;
}

.link {
  font-size: 0.875rem;
  color: var(--coar-text-accent-primary);
  text-decoration: none;
}

.link:hover {
  text-decoration: underline;
}

.auth-footer {
  margin: 1.5rem 0 0;
  text-align: center;
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary);
}

.mb-4 {
  margin-bottom: 1rem;
}
</style>
