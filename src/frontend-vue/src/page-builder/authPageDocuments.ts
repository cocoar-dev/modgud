import type { PageNode } from '@cocoar/vue-page-builder'
import type { AuthPageSlot } from './authPageSlots'
import consentDocument from './documents/consent.json'
import loginDocument from './documents/login.json'
import logoutDocument from './documents/logout.json'
import passwordForgotDocument from './documents/password-forgot.json'

/*
 * The starting document a realm gets when it creates a page.
 *
 * Page Builder 3.0 ships nothing auth-specific, so these belong to Modgud —
 * see the ownership table in the package's IDP_INTEGRATION.md, which puts
 * "structure, styles, translations, Page State and Element Code" on the
 * document side. They were captured from the pre-3.0 preset plus Modgud's
 * alignment patches by `scripts/freeze-auth-documents.ts`, so what a realm
 * gets today is what it got before, then migrated to schemaVersion 6:
 * view-state conditions read `runtime.viewState` from the runtime context, and
 * repeaters name their array through `contextPath`.
 *
 * Edit the JSON directly from here on. The script was a one-shot capture and
 * cannot be re-run against 3.0 — the preset it derived from is gone.
 */
const documents: Record<AuthPageSlot, unknown> = {
  'login': loginDocument,
  'password-forgot': passwordForgotDocument,
  'logout': logoutDocument,
  'consent': consentDocument,
}

/**
 * Callers mutate the schema they receive (the editor binds it with v-model,
 * the runtime normalizes it in place), so hand out a fresh copy every time
 * rather than a shared reference to the imported module object.
 */
export function createDefaultAuthPageSchema(slot: AuthPageSlot): PageNode {
  return JSON.parse(JSON.stringify(documents[slot])) as PageNode
}
