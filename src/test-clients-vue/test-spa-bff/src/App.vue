<script setup lang="ts">
import { onMounted, ref } from 'vue'

// The SPA never sees a token. It calls /bff/user to discover whether
// the cookie session is authenticated, then exercises the resource API
// through the BFF proxy at /api/*.
type User = {
  name: string | null
  sub: string | null
  email: string | null
  roles: string[]
  groups: string[]
}

const user = ref<User | null>(null)
const userError = ref<string | null>(null)
const apiResult = ref<string>('')

async function bffFetch(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers)
  headers.set('X-Requested-With', 'XMLHttpRequest')
  return fetch(path, { ...init, headers, credentials: 'same-origin' })
}

async function loadUser() {
  userError.value = null
  const res = await bffFetch('/bff/user')
  if (res.status === 401) {
    user.value = null
    return
  }
  if (!res.ok) {
    userError.value = `bff/user → ${res.status}`
    return
  }
  user.value = await res.json()
}

async function callApi(endpoint: string) {
  apiResult.value = `→ GET /api${endpoint} …`
  const res = await bffFetch(`/api${endpoint}`)
  const text = await res.text()
  apiResult.value = `← ${res.status}  ${text}`
}

function login() {
  // Top-level redirect, NOT fetch — OIDC needs the browser to follow.
  window.location.href = `/bff/login?returnUrl=${encodeURIComponent(window.location.pathname)}`
}

function logout() {
  window.location.href = '/bff/logout'
}

onMounted(loadUser)
</script>

<template>
  <h1>Cocoar Auth — BFF Test SPA</h1>
  <p>
    Status:
    <span v-if="user" class="pill ok">authenticated</span>
    <span v-else class="pill no">anonymous</span>
  </p>

  <div class="row">
    <button v-if="!user" @click="login">Login via Cocoar.Auth</button>
    <button v-if="user" @click="logout">Logout</button>
    <button @click="loadUser">Refresh /bff/user</button>
  </div>

  <h3>/bff/user</h3>
  <pre>{{ userError ?? (user ? JSON.stringify(user, null, 2) : '— anonymous —') }}</pre>

  <h3>Resource API (proxied through /api/*)</h3>
  <div class="row">
    <button @click="callApi('/me')">GET /me</button>
    <button @click="callApi('/scoped')">GET /scoped (demo.read)</button>
    <button @click="callApi('/admin')">GET /admin (demo.admin)</button>
  </div>
  <pre>{{ apiResult || '— click a button —' }}</pre>
</template>
