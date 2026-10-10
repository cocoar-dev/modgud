<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarIcon } from '@cocoar/vue-ui'
import { useDashboardStats, type DashboardCounts } from '../dashboardStats'
import type { WidgetOptions } from '../layout'
import WidgetCard from './WidgetCard.vue'

/** A group of "how many are there" tiles, each a shortcut to its list. */
export type CountsGroup = 'directory' | 'applications'

const props = defineProps<{
  group: CountsGroup
  /** `show`: the count keys to display, as chosen in the widget's settings. */
  options: WidgetOptions
}>()

const { t, language } = useI18n()
const router = useRouter()
const { stats, loading, failed } = useDashboardStats()

interface TileSpec {
  key: keyof DashboardCounts
  icon: string
  label: string
  to: string
  /** Extra line under the label, e.g. "3 enabled". */
  note?: string
}

const groups = computed<Record<CountsGroup, { title: string; icon: string; tiles: TileSpec[] }>>(() => {
  const enabled = stats.value?.Counts.LoginProvidersEnabled
  return {
    directory: {
      title: t('dashboard.directory.title', {}, 'Directory'),
      icon: 'users',
      tiles: [
        { key: 'Users', icon: 'users', label: t('nav.users', {}, 'Users'), to: '/admin/users' },
        { key: 'ServiceAccounts', icon: 'cpu', label: t('admin.serviceAccounts.title', {}, 'Service Accounts'), to: '/admin/service-accounts' },
        { key: 'Positions', icon: 'briefcase', label: t('admin.positions.title', {}, 'Positions'), to: '/admin/positions' },
        { key: 'Groups', icon: 'users-round', label: t('nav.groups', {}, 'Groups'), to: '/admin/groups' },
        { key: 'Roles', icon: 'shield', label: t('nav.roles', {}, 'Roles'), to: '/admin/roles' },
      ],
    },
    applications: {
      title: t('dashboard.applications.title', {}, 'Applications & sign-in'),
      icon: 'layout-grid',
      tiles: [
        { key: 'Apps', icon: 'layout-grid', label: t('admin.apps.title', {}, 'Applications'), to: '/admin/apps' },
        { key: 'OAuthClients', icon: 'app-window', label: t('admin.oauthClients.title', {}, 'OAuth Clients'), to: '/admin/oauth/clients' },
        { key: 'OAuthApis', icon: 'server', label: t('admin.oauthApis.title', {}, 'OAuth APIs'), to: '/admin/oauth/apis' },
        { key: 'OAuthScopes', icon: 'tags', label: t('admin.oauthScopes.title', {}, 'OAuth Scopes'), to: '/admin/oauth/scopes' },
        {
          key: 'LoginProviders',
          icon: 'log-in',
          label: t('admin.loginProviders.title', {}, 'Login Providers'),
          to: '/admin/login-providers',
          note: enabled != null ? t('dashboard.applications.enabled', { n: enabled }, '{n} enabled') : undefined,
        },
      ],
    },
  }
})

const group = computed(() => groups.value[props.group])
const numberFormat = computed(() => new Intl.NumberFormat(language.value))

// Shown: what the settings select, minus counts the viewer has no permission
// for (those arrive as null).
const tiles = computed(() => group.value.tiles.flatMap(tile => {
  if (!props.options.show?.includes(tile.key)) return []
  const count = stats.value?.Counts[tile.key]
  return count == null ? [] : [{ ...tile, value: numberFormat.value.format(count) }]
}))
</script>

<template>
  <WidgetCard
    :title="group.title"
    :icon="group.icon"
    :loading="loading && !stats"
    :failed="failed"
    :empty="stats && tiles.length === 0 ? t('dashboard.counts.none', {}, 'Nothing selected to show.') : null"
  >
    <div class="counts">
      <button v-for="tile in tiles" :key="tile.key" type="button" class="counts__tile" @click="router.push(tile.to)">
        <span class="counts__icon"><CoarIcon :name="tile.icon" size="s" /></span>
        <span class="counts__value">{{ tile.value }}</span>
        <span class="counts__label">{{ tile.label }}</span>
        <span v-if="tile.note" class="counts__note">{{ tile.note }}</span>
        <CoarIcon name="arrow-right" size="s" class="counts__arrow" />
      </button>
    </div>
  </WidgetCard>
</template>

<style scoped>
.counts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(7.5rem, 1fr));
  gap: 0.75rem;
}
.counts__tile {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.125rem;
  padding: 0.75rem;
  border: 1px solid var(--coar-border-neutral-tertiary, rgba(0, 0, 0, 0.08));
  border-radius: 0.5rem;
  /* One step lighter than the card it sits on, so the tile reads as a surface
     of its own rather than an outline. */
  background: var(--coar-background-neutral-primary, #ffffff);
  color: inherit;
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}
.counts__tile:hover,
.counts__tile:focus-visible {
  border-color: var(--coar-accent, #1077be);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
}
.counts__icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 1.75rem;
  height: 1.75rem;
  margin-bottom: 0.375rem;
  border-radius: 0.5rem;
  background: var(--coar-background-neutral-secondary, rgba(0, 0, 0, 0.05));
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.counts__value {
  font-size: 1.5rem;
  font-weight: 700;
  line-height: 1.1;
}
.counts__label,
.counts__note {
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.counts__note {
  font-size: 0.75rem;
}
.counts__arrow {
  position: absolute;
  top: 0.625rem;
  right: 0.625rem;
  opacity: 0;
  color: var(--coar-text-neutral-secondary, #9ca3af);
  transition: opacity 0.15s ease;
}
.counts__tile:hover .counts__arrow,
.counts__tile:focus-visible .counts__arrow {
  opacity: 1;
}
</style>
