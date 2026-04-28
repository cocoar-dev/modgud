<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { CoarButton, CoarTextInput, CoarPasswordInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()
const { t } = useI18n()

const userName = ref('')
const password = ref('')
const rememberMe = ref(false)
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)

const redirectPath = computed(() => {
    const raw = route.query.redirect
    if (typeof raw === 'string' && raw.length > 0) return raw
    return '/'
})

async function submit() {
    if (loading.value) return
    if (!userName.value || !password.value) {
        errorMessage.value = t('auth.login.missingCredentials', {}, 'Please enter your username and password.')
        return
    }

    loading.value = true
    errorMessage.value = undefined
    try {
        const result = await authStore.login(userName.value, password.value, rememberMe.value)
        if (result.Succeeded) {
            router.push(redirectPath.value)
            return
        }
        if (result.RequiresTwoFactor) {
            errorMessage.value = t('auth.login.twoFactorUnsupported', {}, 'Two-factor authentication is not yet supported in this build.')
            return
        }
        if (result.IsLockedOut) {
            errorMessage.value = t('auth.login.lockedOut', {}, 'Account locked. Please try again later.')
            return
        }
        if (result.IsNotAllowed) {
            errorMessage.value = t('auth.login.notAllowed', {}, 'Account is not allowed to sign in. Confirm your email and try again.')
            return
        }
        errorMessage.value = result.ErrorMessage ?? t('auth.login.invalidCredentials', {}, 'Invalid username or password.')
    } catch (err: unknown) {
        errorMessage.value = err instanceof Error ? err.message : t('auth.login.failed', {}, 'Login failed.')
    } finally {
        loading.value = false
    }
}
</script>

<template>
    <div class="auth-shell">
        <CoarCard class="auth-card">
            <div class="auth-brand">
                <div class="auth-brand-logo">CA</div>
                <h1 class="auth-title">{{ t('auth.login.title', {}, 'Cocoar Auth') }}</h1>
                <p class="auth-subtitle">{{ t('auth.login.subtitle', {}, 'Sign in to continue') }}</p>
            </div>

            <form class="auth-form" @submit.prevent="submit">
                <CoarTextInput
                    v-model="userName"
                    :label="t('common.username', {}, 'Username')"
                    autocomplete="username"
                    autofocus
                    :disabled="loading"
                />
                <CoarPasswordInput
                    v-model="password"
                    :label="t('common.password', {}, 'Password')"
                    autocomplete="current-password"
                    :disabled="loading"
                />

                <label class="remember-me">
                    <input type="checkbox" v-model="rememberMe" :disabled="loading" />
                    <span>{{ t('auth.login.rememberMe', {}, 'Remember me') }}</span>
                </label>

                <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>

                <CoarButton
                    type="submit"
                    variant="primary"
                    :disabled="loading"
                    :loading="loading"
                >
                    {{ t('auth.login.submit', {}, 'Sign in') }}
                </CoarButton>

                <div class="auth-links">
                    <a href="#" @click.prevent="router.push('/forgot-password')">{{ t('auth.login.forgotPassword', {}, 'Forgot password?') }}</a>
                    <span class="auth-links-sep">·</span>
                    <a href="#" @click.prevent="router.push('/register')">{{ t('auth.login.createAccount', {}, 'Create account') }}</a>
                </div>
            </form>
        </CoarCard>
    </div>
</template>

<style scoped>
.auth-shell {
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 2rem 1rem;
    background: var(--coar-background-neutral-secondary);
}

.auth-card {
    width: 100%;
    max-width: 420px;
    padding: 2rem 2rem 2.25rem;
}

.auth-brand {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
    margin-bottom: 1.5rem;
}

.auth-brand-logo {
    width: 48px;
    height: 48px;
    border-radius: 12px;
    background: var(--coar-background-accent-primary, #1f2937);
    color: white;
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 700;
    font-size: 1.125rem;
    margin-bottom: 0.5rem;
}

.auth-title {
    margin: 0;
    font-size: 1.375rem;
    font-weight: 700;
    color: var(--coar-text-neutral-primary);
    letter-spacing: -0.02em;
}

.auth-subtitle {
    margin: 0.125rem 0 0 0;
    color: var(--coar-text-neutral-secondary);
    font-size: 0.875rem;
}

.auth-form {
    display: flex;
    flex-direction: column;
    gap: 0.875rem;
}

.remember-me {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.875rem;
    color: var(--coar-text-neutral-secondary);
    user-select: none;
}

.remember-me input {
    margin: 0;
}

.auth-links {
    margin-top: 0.5rem;
    text-align: center;
    font-size: 0.875rem;
    color: var(--coar-text-neutral-secondary);
}

.auth-links a {
    color: var(--coar-text-accent-primary, #2563eb);
    text-decoration: none;
}

.auth-links a:hover {
    text-decoration: underline;
}

.auth-links-sep {
    margin: 0 0.5rem;
    color: var(--coar-text-neutral-tertiary);
}
</style>
