const BASE_URL = '/api';

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: unknown,
  ) {
    const message =
      (body as { message?: string })?.message ??
      `HTTP ${status}`;
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    ...init,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...init.headers,
    },
  });

  // Error handling — must come BEFORE the empty-body shortcut
  if (!response.ok) {
    if (response.status === 401) {
      // Only redirect on session expiry (mid-use), not during auth initialization.
      // The router guard handles the unauthenticated → login redirect on its own.
      const { useAuthStore } = await import('@/stores/auth.store');
      const auth = useAuthStore();
      if (auth.status === 'authenticated') {
        auth.currentUser = null;
        auth.status = 'unauthenticated';
        const { router } = await import('@/router');
        if (router.currentRoute.value.path !== '/login') {
          router.push('/login?sessionExpired=true');
        }
      }
    }
    const contentType = response.headers.get('content-type') ?? '';
    const errData = contentType.includes('application/json')
      ? await response.json().catch(() => null)
      : await response.text().catch(() => null);
    throw new ApiError(response.status, errData);
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  const contentType = response.headers.get('content-type') ?? '';
  const data = contentType.includes('application/json') ? await response.json() : await response.text();

  return data as T;
}

export const http = {
  get: <T>(path: string) => request<T>(path, { method: 'GET' }),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body !== undefined ? JSON.stringify(body) : undefined }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PUT', body: body !== undefined ? JSON.stringify(body) : undefined }),
  patch: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PATCH', body: body !== undefined ? JSON.stringify(body) : undefined }),
  delete: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: 'DELETE',
      body: body !== undefined ? JSON.stringify(body) : undefined,
    }),
};
