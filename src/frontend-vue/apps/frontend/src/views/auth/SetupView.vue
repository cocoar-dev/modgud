<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarTextInput, CoarPasswordInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const authStore = useAuthStore()

const userName = ref('')
const password = ref('')
const email = ref('')
const firstName = ref('')
const lastName = ref('')
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)

async function submit() {
    if (loading.value) return
    if (!userName.value || !password.value) {
        errorMessage.value = 'Username and password are required.'
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
        })
        router.push('/')
    } catch (err: unknown) {
        errorMessage.value =
            err instanceof Error ? err.message : 'Setup failed. Please try again.'
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
                <h1 class="auth-title">First-time setup</h1>
                <p class="auth-subtitle">Create the initial admin account</p>
            </div>

            <form class="auth-form" @submit.prevent="submit">
                <CoarTextInput
                    v-model="userName"
                    label="Username *"
                    autocomplete="username"
                    autofocus
                    :disabled="loading"
                />
                <CoarPasswordInput
                    v-model="password"
                    label="Password *"
                    autocomplete="new-password"
                    :disabled="loading"
                />
                <CoarTextInput
                    v-model="email"
                    label="Email"
                    autocomplete="email"
                    :disabled="loading"
                />
                <div class="form-row">
                    <CoarTextInput
                        v-model="firstName"
                        label="First name"
                        autocomplete="given-name"
                        :disabled="loading"
                    />
                    <CoarTextInput
                        v-model="lastName"
                        label="Last name"
                        autocomplete="family-name"
                        :disabled="loading"
                    />
                </div>

                <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>

                <CoarButton
                    type="submit"
                    variant="primary"
                    :disabled="loading"
                    :loading="loading"
                >
                    Create admin account
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
</style>
