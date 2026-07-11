import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient } from '@/composables/useHttpClient'

// Paths outside the SPA (served directly by the backend) — Vue Router can't navigate
// there, so we use a full-page load after successful login.
// `/connect/*` are the OpenIddict OAuth/OIDC endpoints; an inbound third-party
// client kicks the flow off there, so completing login has to send the browser
// back to /connect/authorize verbatim, not push the path through Vue Router
// (which would silently drop it as an unknown route).
const NON_SPA_PREFIXES = ['/docs/', '/docs', '/connect/', '/connect']

// Open-redirect guard: a redirect target has to be a same-origin path,
// i.e. a string starting with a single '/'. Anything starting with '//',
// 'http:', 'https:', a scheme, or a backslash could send the user to an
// attacker-controlled host after a successful login.
export function isSameOriginPath(value: unknown): value is string {
  if (typeof value !== 'string') return false    // repeated ?redirect= → string[]
  if (!value.startsWith('/')) return false       // must be absolute path
  if (value.startsWith('//')) return false       // protocol-relative URL
  if (value.startsWith('/\\')) return false      // backslash-smuggling
  // Control chars (TAB/CR/LF) are stripped by the browser's URL parser, so
  // '/\t/evil.com' would collapse to '//evil.com' — reject them.
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(value)) return false
  return true
}

/**
 * Shared post-login continuation. Every flow that establishes the session
 * cookie (password, TOTP, email-OTP, passkey, magic-link, external IdP, …)
 * must finish through here so a pending `?redirect=` — typically the
 * /connect/authorize continuation of a client app's OIDC flow — is honored
 * instead of stranding the user on the dashboard.
 */
export function useLoginRedirect() {
  const route = useRoute()
  const router = useRouter()
  const gdprHttp = useHttpClient('/api/auth')

  // Redirect target after login. Set as ?redirect= by the router guard, the
  // cookie handler's login challenge (ReturnUrlParameter = "redirect"), or a
  // magic-link email URL. vue-router already URL-decodes query values, so the
  // raw value is used as-is; failing the same-origin guard falls back to the
  // dashboard.
  const redirectTarget = computed(() => {
    // route.query.redirect is string | string[] | undefined (a repeated param
    // yields an array); isSameOriginPath rejects any non-string, so a crafted
    // ?redirect=/a&redirect=/b falls back to '/' instead of throwing.
    const r = route.query.redirect
    return isSameOriginPath(r) ? r : '/'
  })

  async function finishLogin() {
    const target = redirectTarget.value

    // Self-service grace interstitial: a user who scheduled their own deletion
    // stays able to log in precisely so they can cancel. Divert them to the
    // interstitial (which continues to `target` on cancel/continue) before the
    // normal redirect. Admin recycle-bin users can't log in, so never land here.
    try {
      const status = await gdprHttp.addPath('deletion-status')
        .get<{ IsPending: boolean; Initiator?: string | null }>()
      if (status?.IsPending && status.Initiator === 'SelfService') {
        router.push({ path: '/deletion-pending', query: { redirect: target } })
        return
      }
    } catch { /* status unavailable — never block the login on it */ }

    if (NON_SPA_PREFIXES.some((p) => target === p || target.startsWith(p + '/') || target.startsWith(p + '?'))) {
      window.location.assign(target)
    } else {
      router.push(target)
    }
  }

  return { redirectTarget, finishLogin }
}
