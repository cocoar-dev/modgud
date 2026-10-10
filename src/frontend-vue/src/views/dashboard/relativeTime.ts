import { useI18n } from '@cocoar/vue-localization'

/** "5 min ago"-style timestamps for the dashboard's list widgets. */
export function useRelativeTime() {
  const { t, language } = useI18n()

  return function relativeTime(iso: string): string {
    const diffSec = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000))
    if (diffSec < 60) return t('dashboard.time.justNow', {}, 'just now')
    const diffMin = Math.floor(diffSec / 60)
    if (diffMin < 60) return t('dashboard.time.minutesAgo', { n: diffMin }, '{n} min ago')
    const diffH = Math.floor(diffMin / 60)
    if (diffH < 24) return t('dashboard.time.hoursAgo', { n: diffH }, '{n} h ago')
    const diffD = Math.floor(diffH / 24)
    if (diffD < 30) return t('dashboard.time.daysAgo', { n: diffD }, '{n} days ago')
    return new Date(iso).toLocaleDateString(language.value)
  }
}
