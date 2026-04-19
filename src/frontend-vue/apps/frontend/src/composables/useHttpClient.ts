/**
 * Fluent HTTP client composable wrapping native fetch.
 *
 * Returns an immutable builder — every mutating method returns a new instance,
 * so a single base client can be safely forked for different requests.
 *
 * @example
 * ```ts
 * const http = useHttpClient('/api/admin/users');
 * const users = await http.get<UserDto[]>();
 * const user  = await http.addPath(id).get<UserDto>();
 * const created = await http.post<UserDto>(createDto);
 * ```
 */

/** Paths where a 401 is expected and should NOT trigger a redirect. */
const AUTH_PATHS = ['/api/auth/me', '/api/auth/login', '/api/setup/status']

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
    const init: RequestInit = {
      method,
      headers,
      credentials: 'include',
    };

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
        const currentPath = window.location.pathname + window.location.search;
        const redirectPath = currentPath !== '/' ? currentPath : undefined;
        window.location.href = redirectPath
          ? `/login?redirect=${encodeURIComponent(redirectPath)}`
          : '/login';
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
 * @param basePath  Root path for all requests (e.g. `'/api/admin/users'`)
 */
export function useHttpClient(basePath: string): HttpClient {
  return new HttpClient(basePath);
}

export type { HttpClient };
