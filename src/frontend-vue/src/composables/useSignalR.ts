/**
 * SignalR composable wrapping @cocoar/signalarrr.
 *
 * Holds a module-level singleton HARRRConnection. `useSignalR()` itself has
 * NO side-effects — callers must invoke `connect()` explicitly. The auth
 * store calls `connect()` after a successful `fetchMe()`, so the connection
 * only comes up once the session cookie actually exists. Before that,
 * starting SignalR would fail the negotiate request (401) and `/signalr/ui`
 * has no retry for the very first connect.
 *
 * Provides:
 * - `stream<T>()` — subscribe to a SignalR streaming hub method
 * - `invoke<T>()` — call a hub method and await its result
 * - `state`       — reactive `Ref<string>` reflecting connection state
 * - `runOnReconnect()` — register a callback that fires after every reconnect
 * - `ensureConnected()` — returns a Promise that resolves once connected
 * - `connect()`   — idempotent: starts the connection if not already connected
 */

import { ref, type Ref } from 'vue';
import { HARRRConnection } from '@cocoar/signalarrr';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type ConnectionState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting';

interface StreamSubscription<T> {
  methodName: string;
  args: unknown[];
  callbacks: {
    next: (value: T) => void;
    error?: (err: unknown) => void;
    complete?: () => void;
  };
  dispose: (() => void) | null;
  hasSubscribed: boolean;
}

interface SubscribeOptions<T> {
  next: (value: T) => void;
  error?: (err: unknown) => void;
  complete?: () => void;
}

// ---------------------------------------------------------------------------
// Retry policy — exponential back-off capped at 10 s
// ---------------------------------------------------------------------------

const retryPolicy = {
  nextRetryDelayInMilliseconds(retryContext: { previousRetryCount: number }): number | null {
    return Math.min(100 * Math.pow(2, retryContext.previousRetryCount), 10_000);
  },
};

// ---------------------------------------------------------------------------
// Module-level singleton state
// ---------------------------------------------------------------------------

let connection: HARRRConnection | null = null;
const state = ref<ConnectionState>('Disconnected') as Ref<string>;
const reconnectCallbacks: Array<{ callback: () => void; identifier?: string }> = [];
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const activeSubscriptions: StreamSubscription<any>[] = [];
let connectionStartedResolve: (() => void) | null = null;
let connectionStartedPromise: Promise<void> | null = null;
let hasConnectedOnce = false;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function resetConnectionPromise(): void {
  // Keep existing waiters attached when onReconnecting is followed by onClose.
  if (connectionStartedResolve) return;

  connectionStartedPromise = new Promise<void>((resolve) => {
    connectionStartedResolve = resolve;
  });
}

function resolveConnectionPromise(): void {
  if (connectionStartedResolve) {
    connectionStartedResolve();
    connectionStartedResolve = null;
  }
}

function unsubscribeAllStreams(): void {
  for (const sub of activeSubscriptions) {
    if (sub.dispose) {
      try {
        sub.dispose();
      } catch {
        // Ignore disposal errors during reconnect
      }
      sub.dispose = null;
    }
  }
}

function subscribeStream(sub: StreamSubscription<unknown>): void {
  if (!connection || sub.dispose || !activeSubscriptions.includes(sub)) return;

  try {
    const stream = connection.stream(sub.methodName, ...sub.args);
    const handle = stream.subscribe({
      next: (item: unknown) => sub.callbacks.next(item),
      error: (err: unknown) => sub.callbacks.error?.(err),
      complete: () => sub.callbacks.complete?.(),
    });
    sub.dispose = () => handle.dispose();
    sub.hasSubscribed = true;
  } catch (err) {
    console.error(`[SignalR] Failed to subscribe to ${sub.methodName}:`, err);
    sub.callbacks.error?.(err);
  }
}

function resubscribeAllStreams(): void {
  for (const sub of activeSubscriptions) {
    // A subscription added while disconnected is still waiting on
    // ensureConnected(); restoring it here as well would create it twice.
    if (sub.hasSubscribed) subscribeStream(sub);
  }
}

function invokeReconnectCallbacks(): void {
  for (const entry of reconnectCallbacks) {
    try {
      entry.callback();
    } catch (err) {
      console.error('[SignalR] Reconnect callback error:', err);
    }
  }
}

// ---------------------------------------------------------------------------
// Initialisation (runs once)
// ---------------------------------------------------------------------------

function initConnection(): void {
  if (connection) return;

  resetConnectionPromise();

  connection = HARRRConnection.create((builder) =>
    builder.withUrl('/signalr/ui').withAutomaticReconnect(retryPolicy),
  );

  connection.onReconnecting(() => {
    (state as Ref<ConnectionState>).value = 'Reconnecting';
    resetConnectionPromise();
    unsubscribeAllStreams();
  });

  connection.onClose(() => {
    (state as Ref<ConnectionState>).value = 'Disconnected';
    resetConnectionPromise();
    unsubscribeAllStreams();
  });

  connection.onReconnected(() => {
    (state as Ref<ConnectionState>).value = 'Connected';
    resubscribeAllStreams();
    resolveConnectionPromise();
    invokeReconnectCallbacks();
  });
}

async function startConnection(): Promise<void> {
  if (!connection) return;
  try {
    const isRestart = hasConnectedOnce;
    (state as Ref<ConnectionState>).value = 'Connecting';
    await connection.start();
    (state as Ref<ConnectionState>).value = 'Connected';

    // start() is also used after automatic reconnect has given up. Existing
    // streams then need the same restoration as onReconnected; subscriptions
    // created during the outage are started by their ensureConnected waiter.
    if (isRestart) {
      resubscribeAllStreams();
    }
    hasConnectedOnce = true;
    resolveConnectionPromise();
    if (isRestart) {
      invokeReconnectCallbacks();
    }
  } catch (err) {
    console.error('[SignalR] Error starting connection:', err);
    // Reset to Disconnected so the next connect() call can retry. `withAutomaticReconnect`
    // only kicks in after a successful initial start — the very first connect has no
    // retry of its own.
    (state as Ref<ConnectionState>).value = 'Disconnected';
  }
}

/**
 * Idempotent — starts the SignalR connection if not already up. Call after
 * authentication succeeds. Safe to call multiple times.
 */
async function connect(): Promise<void> {
  initConnection();
  if (!connection) return;
  if (state.value === 'Connected' || state.value === 'Connecting' || state.value === 'Reconnecting') {
    return;
  }
  await startConnection();
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

function stream<T>(methodName: string, ...args: unknown[]) {
  return {
    subscribe(callbacks: SubscribeOptions<T>): () => void {
      const sub: StreamSubscription<T> = {
        methodName,
        args,
        callbacks,
        dispose: null,
        hasSubscribed: false,
      };

      activeSubscriptions.push(sub);

      // Start the actual SignalR stream once connected
      ensureConnected().then(() => {
        subscribeStream(sub as StreamSubscription<unknown>);
      });

      // Return unsubscribe function
      return () => {
        if (sub.dispose) {
          try {
            sub.dispose();
          } catch {
            // Ignore
          }
        }
        const idx = activeSubscriptions.indexOf(sub);
        if (idx !== -1) {
          activeSubscriptions.splice(idx, 1);
        }
      };
    },
  };
}

async function invoke<T>(methodName: string, ...args: unknown[]): Promise<T> {
  await ensureConnected();
  if (!connection) {
    throw new Error('[SignalR] Connection not available');
  }
  return connection.invoke<T>(methodName, ...args);
}

function runOnReconnect(callback: () => void, identifier?: string): void {
  // De-duplicate by identifier
  if (identifier) {
    const exists = reconnectCallbacks.some((entry) => entry.identifier === identifier);
    if (exists) return;
  }

  // De-duplicate by callback reference
  const existsByRef = reconnectCallbacks.some((entry) => entry.callback === callback);
  if (existsByRef) return;

  reconnectCallbacks.push({ callback, identifier });
}

/**
 * Returns a promise that resolves once the connection is up. If `connect()`
 * has not been called yet, the promise stays pending — callers are expected
 * to only subscribe / invoke after the auth flow has triggered `connect()`.
 */
function ensureConnected(): Promise<void> {
  if (!connectionStartedPromise) resetConnectionPromise();
  return connectionStartedPromise!;
}

// ---------------------------------------------------------------------------
// Composable
// ---------------------------------------------------------------------------

export function useSignalR() {
  return {
    stream,
    invoke,
    state: state as Ref<string>,
    runOnReconnect,
    ensureConnected,
    connect,
  };
}
