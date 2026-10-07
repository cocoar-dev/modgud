/**
 * Fluent HTTP client composable wrapping native fetch.
 *
 * Returns an immutable builder — every mutating method returns a new instance,
 * so a single base client can be safely forked for different requests.
 *
 * @example
 * ```ts
 * const http = useHttpClient('api/todo');
 * const todos = await http.get<TodoDto[]>();
 * const todo  = await http.addPath(id).get<TodoDto>();
 * const created = await http.post<TodoDto>(createDto);
 * ```
 */

/** Paths where a 401 is expected and should NOT trigger a redirect. */
const AUTH_PATHS = ['/api/account/me', '/api/account/login']

/**
 * Detects the 2FA-enforcement 403 body shape. Any other 403 (e.g. permission denied)
 * should stay a normal error so callers can handle it locally.
 */
function isSecureSetupBlock(body: unknown): boolean {
  return typeof body === 'object' && body !== null && (body as Record<string, unknown>).RequiresSecureSetup === true
}

/** ADR 0025 — the session is below the area's sign-in level and the user can raise it. */
function isStepUpBlock(body: unknown): boolean {
  return typeof body === 'object' && body !== null && (body as Record<string, unknown>).RequiresStepUp === true
}

/**
 * ADR 0025 amendment C — an account change (a factor, the password, the e-mail address,
 * the deletion) needs a recent proof of the user's account factor, or of what they have.
 */
function isReauthenticationBlock(body: unknown): boolean {
  return typeof body === 'object' && body !== null && (body as Record<string, unknown>).RequiresReauthentication === true
}

/** A blocked request to the realm's administration (`/api/admin/*`). */
function isAdministrationUrl(url: string): boolean {
  return /^\/?api\/admin(\/|$|\?)/i.test(url)
}

const STEP_UP_GUARD_KEY = 'modgud.stepup.last'

/**
 * All three blocks continue on the login page, which asks for the missing factor (or the
 * setup, or the fresh proof), then returns to the page the user was on.
 *
 * The login page evaluates the step-up against a target. Which target is decided by the
 * blocked request, not by the page: an admin widget on the dashboard is blocked by the
 * administration's level, while the dashboard itself is the portal — evaluating the
 * dashboard's URL found nothing missing and sent the user straight back into the same
 * block, a reload loop. `for=admin` names the administration.
 *
 * A guard stops a second redirect for the same page and kind within a few seconds: the
 * login page has just raised the session or found nothing to raise, so another round
 * would only loop. The request then fails like any other 403.
 */
function redirectToStepUp(reauthenticate: boolean, administration: boolean) {
  const here = window.location.pathname + window.location.search
  const key = `${here}|${reauthenticate}|${administration}`
  try {
    const last = JSON.parse(sessionStorage.getItem(STEP_UP_GUARD_KEY) ?? 'null') as { key: string; at: number } | null
    if (last && last.key === key && Date.now() - last.at < 10_000) return
    sessionStorage.setItem(STEP_UP_GUARD_KEY, JSON.stringify({ key, at: Date.now() }))
  } catch { /* storage unavailable: no guard, the target fix alone prevents the loop */ }
  const flags = `${reauthenticate ? '&reauth=1' : ''}${administration ? '&for=admin' : ''}`
  window.location.href = `/login?stepup=1${flags}&redirect=${encodeURIComponent(here)}`
}

class HttpClient {
  private readonly basePath: string;
  private readonly pathSegments: readonly string[];
  private readonly params: ReadonlyMap<string, string>;

  constructor(
    basePath: string,
    pathSegments: readonly string[] = [],
    params: ReadonlyMap<string, string> = new Map(),
  ) {
    this.basePath = basePath;
    this.pathSegments = pathSegments;
    this.params = params;
  }

  // ---------------------------------------------------------------------------
  // Builder (immutable)
  // ---------------------------------------------------------------------------

  /**
   * Append one or more path segments to the URL.
   * Returns a new HttpClient instance.
   */
  addPath(...segments: string[]): HttpClient {
    return new HttpClient(
      this.basePath,
      [...this.pathSegments, ...segments],
      this.params,
    );
  }

  /**
   * Set a required query parameter.
   * Returns a new HttpClient instance.
   */
  setQueryParameter(key: string, value: string): HttpClient {
    const next = new Map(this.params);
    next.set(key, value);
    return new HttpClient(this.basePath, this.pathSegments, next);
  }

  /**
   * Set an optional query parameter. Skipped when the value is `undefined` or `null`.
   * Returns a new HttpClient instance.
   */
  setOptionalQueryParameter(key: string, value: string | undefined | null): HttpClient {
    if (value === undefined || value === null) {
      return new HttpClient(this.basePath, this.pathSegments, this.params);
    }
    return this.setQueryParameter(key, value);
  }

  // ---------------------------------------------------------------------------
  // HTTP methods
  // ---------------------------------------------------------------------------

  get<T>(): Promise<T> {
    return this.request<T>('GET');
  }

  post<T>(body?: unknown): Promise<T> {
    return this.request<T>('POST', body);
  }

  put<T>(body?: unknown): Promise<T> {
    return this.request<T>('PUT', body);
  }

  patch<T>(body?: unknown): Promise<T> {
    return this.request<T>('PATCH', body);
  }

  delete<T>(body?: unknown): Promise<T> {
    return this.request<T>('DELETE', body);
  }

  // ---------------------------------------------------------------------------
  // Internals
  // ---------------------------------------------------------------------------

  private buildUrl(): string {
    const base = this.basePath.replace(/\/+$/, '');
    const path =
      this.pathSegments.length > 0
        ? [base, ...this.pathSegments].filter(Boolean).join('/')
        : base;

    if (this.params.size === 0) {
      return path;
    }

    const qs = new URLSearchParams();
    this.params.forEach((v, k) => qs.set(k, v));
    return `${path}?${qs.toString()}`;
  }

  private async request<T>(method: string, body?: unknown): Promise<T> {
    const url = this.buildUrl();

    const headers: Record<string, string> = {};
    const init: RequestInit = { method, headers };

    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    const response = await fetch(url, init);

    if (!response.ok) {
      let errorBody: unknown;
      try {
        errorBody = await response.json();
      } catch {
        try {
          errorBody = await response.text();
        } catch {
          errorBody = null;
        }
      }

      // On 401, redirect to login unless this is an auth-related request
      if (response.status === 401 && !AUTH_PATHS.includes(url)) {
        window.location.href = '/login'
      }

      // On 403 with RequiresSecureSetup, the user's 2FA grace period has expired and
      // the server is blocking non-setup endpoints. Send them to /login where the
      // SecureSetupModal will appear (in its blocking form, since grace is over).
      if (response.status === 403 && (isSecureSetupBlock(errorBody) || isStepUpBlock(errorBody))) {
        redirectToStepUp(false, isAdministrationUrl(url))
      } else if (response.status === 403 && isReauthenticationBlock(errorBody)) {
        redirectToStepUp(true, false)
      }

      throw new HttpClientError(response.status, response.statusText, errorBody);
    }

    // Handle 204 No Content or empty bodies
    const text = await response.text();
    if (!text) {
      return undefined as T;
    }
    return JSON.parse(text) as T;
  }
}

/**
 * Error thrown when the server responds with a non-2xx status code.
 */
export class HttpClientError extends Error {
  public readonly status: number;
  public readonly statusText: string;
  public readonly body: unknown;

  constructor(status: number, statusText: string, body: unknown) {
    super(`HTTP ${status} ${statusText}`);
    this.name = 'HttpClientError';
    this.status = status;
    this.statusText = statusText;
    this.body = body;
  }
}

/**
 * Create an immutable HTTP client builder rooted at `basePath`.
 *
 * @param basePath  Root path for all requests (e.g. `'api/todo'`)
 */
export function useHttpClient(basePath: string): HttpClient {
  return new HttpClient(basePath);
}

export type { HttpClient };
