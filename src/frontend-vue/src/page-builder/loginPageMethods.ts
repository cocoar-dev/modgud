import type { PageNode } from '@cocoar/vue-page-builder'

/** A sign-in method and the host actions a login page needs to offer it. */
export const LOGIN_METHOD_ACTIONS = {
  password: ['auth:login'],
  emailCode: ['auth:request-login-code', 'auth:verify-login-code'],
  passkey: ['auth:passkey'],
  magicLink: ['auth:magic-link'],
} as const

export type LoginMethod = keyof typeof LOGIN_METHOD_ACTIONS

function collectActions(node: unknown, into: Set<string>) {
  if (!node || typeof node !== 'object') return
  const n = node as { props?: { action?: unknown }, children?: unknown }
  if (typeof n.props?.action === 'string') into.add(n.props.action)
  if (Array.isArray(n.children)) for (const child of n.children) collectActions(child, into)
}

/**
 * B10-PA02 — the sign-in methods a custom login page cannot offer because it lacks their
 * actions. An app that allows only those methods leaves its users without a way in on this
 * page (only `?safemode=1` helps), so the editor warns before publishing.
 */
export function loginPageMissingMethods(schema: PageNode | null | undefined): LoginMethod[] {
  const actions = new Set<string>()
  collectActions(schema, actions)
  return (Object.keys(LOGIN_METHOD_ACTIONS) as LoginMethod[])
    .filter((method) => LOGIN_METHOD_ACTIONS[method].some((action) => !actions.has(action)))
}
