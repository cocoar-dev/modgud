import { ref } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';

export function useDirtyGuard(message = 'You have unsaved changes. Are you sure you want to leave?') {
  const isDirty = ref(false);

  onBeforeRouteLeave(() => {
    if (isDirty.value) {
      return confirm(message);
    }
  });

  return { isDirty };
}
