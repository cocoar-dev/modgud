import { ref, watchEffect } from 'vue';

const isDark = ref(localStorage.getItem('coar-theme') === 'dark');

watchEffect(() => {
  if (isDark.value) {
    document.documentElement.classList.add('dark-mode');
    localStorage.setItem('coar-theme', 'dark');
  } else {
    document.documentElement.classList.remove('dark-mode');
    localStorage.setItem('coar-theme', 'light');
  }
});

export function useDarkMode() {
  function toggle() {
    isDark.value = !isDark.value;
  }
  return { isDark, toggle };
}
