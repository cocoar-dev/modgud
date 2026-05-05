import { ref } from 'vue'
import { useLocalization } from '@cocoar/vue-localization'

const darkMode = ref(localStorage.getItem('dark-mode') === 'true')

function applyDarkMode() {
  document.documentElement.classList.toggle('dark-mode', darkMode.value)
}

/** Available locale options: language + region combinations */
export const localeOptions = [
  { value: 'de', label: 'Deutsch' },
  { value: 'de-AT', label: 'Deutsch (Österreich)' },
  { value: 'de-DE', label: 'Deutsch (Deutschland)' },
  { value: 'de-CH', label: 'Deutsch (Schweiz)' },
  { value: 'en', label: 'English' },
  { value: 'en-US', label: 'English (US)' },
  { value: 'en-GB', label: 'English (UK)' },
]

/** Extract base language from locale code (de-AT → de) */
export function getBaseLanguage(locale: string): string {
  return locale.split('-')[0] ?? locale
}

export function usePreferences() {
  const localization = useLocalization()!

  async function setLocale(locale: string) {
    await localization.setLanguage(locale)
    localStorage.setItem('language', locale)
  }

  function toggleDarkMode() {
    darkMode.value = !darkMode.value
    localStorage.setItem('dark-mode', String(darkMode.value))
    applyDarkMode()
  }

  function setDarkMode(value: boolean) {
    darkMode.value = value
    localStorage.setItem('dark-mode', String(value))
    applyDarkMode()
  }

  return {
    darkMode,
    toggleDarkMode,
    setDarkMode,
    setLocale,
  }
}
