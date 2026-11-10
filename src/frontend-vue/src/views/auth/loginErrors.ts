/** A login error as a translation key with its English fallback. */
export interface LoginErrorText {
  key: string
  fallback: string
  params?: Record<string, string>
}

/**
 * The message for a failed password login. A 403 has several causes, told apart by the
 * response's Code: an unconfirmed address is not "password sign-in is off".
 */
export function loginErrorText(status: number, statusText: string, body: unknown): LoginErrorText {
  if (status === 401) return { key: 'auth.login.invalidCredentials', fallback: 'Invalid username or password.' }
  if (status === 403) {
    const code = typeof body === 'object' && body !== null ? (body as { Code?: unknown }).Code : undefined
    switch (code) {
      case 'Account.EmailNotVerified':
        return {
          key: 'auth.login.emailNotVerified',
          fallback: 'Please confirm your email address first: open the link in the confirmation email.',
        }
      case 'Account.InternalLoginDisabled':
        return {
          key: 'auth.login.internalLoginDisabled',
          fallback: 'This application does not allow sign-in with a username and password.',
        }
      case 'Account.PasswordLoginDisabled':
        return { key: 'auth.login.passwordDisabled', fallback: 'Password login is disabled.' }
    }
    const message = typeof body === 'object' && body !== null ? (body as { Message?: unknown }).Message : undefined
    if (typeof message === 'string' && message.length > 0)
      return { key: 'auth.login.error', fallback: 'Error: {detail}', params: { detail: message } }
  }
  return { key: 'auth.login.error', fallback: 'Error: {detail}', params: { detail: statusText } }
}
