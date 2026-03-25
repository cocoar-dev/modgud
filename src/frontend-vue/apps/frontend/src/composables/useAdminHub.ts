import { onUnmounted } from 'vue';
import { HARRRConnection } from '@cocoar/signalarrr';
import { realmContext } from './useRealmContext';

type EntityChangeCallback = () => void;

const listeners = new Map<string, Set<EntityChangeCallback>>();
let connection: HARRRConnection | null = null;
let connectPromise: Promise<void> | null = null;

function ensureConnection(): Promise<void> {
  if (!connectPromise) {
    connection = HARRRConnection.create(builder => {
      builder.withUrl(`/${realmContext.slug}/admin-hub`);
      builder.withAutomaticReconnect();
    });

    connection.on('OnEntityChanged', (entityType: string, _changeType: string, _entityId: string | null) => {
      const callbacks = listeners.get(entityType);
      if (callbacks) {
        callbacks.forEach(cb => cb());
      }
    });

    connectPromise = connection.start().catch(err => {
      console.warn('[AdminHub] Connection failed:', err);
      connectPromise = null;
    });
  }
  return connectPromise;
}

/**
 * Composable for real-time admin entity change notifications via SignalARRR.
 * Automatically connects to the admin hub on first use.
 */
export function useAdminHub() {
  function onEntityChanged(entityType: string, callback: EntityChangeCallback) {
    ensureConnection();

    if (!listeners.has(entityType)) {
      listeners.set(entityType, new Set());
    }
    listeners.get(entityType)!.add(callback);

    onUnmounted(() => {
      listeners.get(entityType)?.delete(callback);
    });
  }

  return { onEntityChanged };
}
