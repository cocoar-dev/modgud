<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarTextInput, CoarPasswordInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const authStore = useAuthStore()
const { t } = useI18n()

const userName = ref('')
const password = ref('')
const email = ref('')
const firstName = ref('')
const lastName = ref('')
const loadDemoData = ref(false)
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)

async function submit() {
    if (loading.value) return
    if (!userName.value || !password.value) {
        errorMessage.value = t('setup.requiredFields', {}, 'Username and password are required.')
        return
    }

    loading.value = true
    errorMessage.value = undefined
    try {
        await authStore.createAdmin({
            UserName: userName.value,
            Password: password.value,
            Email: email.value || undefined,
            FirstName: firstName.value || undefined,
            LastName: lastName.value || undefined,
            LoadDemoData: loadDemoData.value,
        })
        router.push('/')
    } catch (err: unknown) {
        errorMessage.value =
            err instanceof Error ? err.message : t('setup.failed', {}, 'Setup failed. Please try again.')
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
                <h1 class="auth-title">{{ t('setup.title', {}, 'First-time setup') }}</h1>
                <p class="auth-subtitle">{{ t('setup.subtitle', {}, 'Create the initial admin account') }}</p>
            </div>

            <form class="auth-form" @submit.prevent="submit">
                <CoarTextInput
                    v-model="userName"
                    :label="t('setup.usernameRequired', {}, 'Username *')"
                    autocomplete="username"
                    autofocus
                    :disabled="loading"
                />
                <CoarPasswordInput
                    v-model="password"
                    :label="t('setup.passwordRequired', {}, 'Password *')"
                    autocomplete="new-password"
                    :disabled="loading"
                />
                <CoarTextInput
                    v-model="email"
                    :label="t('common.email', {}, 'Email')"
                    autocomplete="email"
                    :disabled="loading"
                />
                <div class="form-row">
                    <CoarTextInput
                        v-model="firstName"
                        :label="t('admin.users.firstName', {}, 'First name')"
                        autocomplete="given-name"
                        :disabled="loading"
                    />
                    <CoarTextInput
                        v-model="lastName"
                        :label="t('admin.users.lastName', {}, 'Last name')"
                        autocomplete="family-name"
                        :disabled="loading"
                    />
                </div>

                <label class="demo-toggle">
                    <input type="checkbox" v-model="loadDemoData" :disabled="loading" />
                    <span class="demo-toggle-label">
                        <strong>{{ t('setup.loadDemoData', {}, 'Load demo data') }}</strong>
                        <small>
                            {{ t('setup.loadDemoDataDescription', {}, 'Seeds 7 demo users, 4 permission-roles and 5 authorization-groups — including an auto-membership group that matches on email, and a nested group-of-groups. Default password for every demo user is Demo1234!. Skip this on production installs.') }}
                        </small>
                    </span>
                </label>

                <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>

                <CoarButton
                    type="submit"
                    variant="primary"
                    :disabled="loading"
                    :loading="loading"
                >
                    {{ t('setup.createAdmin', {}, 'Create admin account') }}
                </CoarButton>
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
    max-width: 480px;
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

.form-row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
}

.demo-toggle {
    display: flex;
    gap: 0.625rem;
    align-items: flex-start;
    padding: 0.75rem 0.875rem;
    border: 1px solid var(--coar-border-neutral-secondary);
    border-radius: 8px;
    background: var(--coar-background-neutral-tertiary, rgba(0,0,0,0.02));
    cursor: pointer;
    user-select: none;
}

.demo-toggle input[type=checkbox] {
    margin-top: 0.2rem;
}

.demo-toggle-label {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    font-size: 0.875rem;
    color: var(--coar-text-neutral-primary);
}

.demo-toggle-label small {
    color: var(--coar-text-neutral-secondary);
    line-height: 1.4;
    font-size: 0.8125rem;
}

.demo-toggle-label code {
    background: var(--coar-background-neutral-secondary);
    padding: 0.05rem 0.25rem;
    border-radius: 4px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: 0.8rem;
}
</style>
