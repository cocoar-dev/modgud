<script setup lang="ts">
import { useSlots } from 'vue'
import { CoarMenu, CoarMenuItem } from '@cocoar/vue-ui'
import type { SubNavItem } from './sub-nav-types'

/**
 * Single-Menu Sub-Nav. Eine flache Liste von Items, linke Spalte füllt die
 * komplette Höhe. Wenn die Items mehr Platz brauchen, scrollt das **Menu**
 * intern — der Container drumherum scrollt NICHT.
 *
 * Use-Case: viele flache Items oder Filter-Listen. Wenn Items thematisch
 * gruppiert werden sollen, stattdessen <SubNavLayoutGrouped> verwenden.
 *
 * Zwei API-Modi:
 *   1) **items-Prop** (einfach): flache Liste von `SubNavItem`s. Active-State
 *      übernimmt `CoarMenuItem` selbst aus `RouterLink.isActive`. Für Fälle die
 *      sich nicht per Path-Match auflösen (z.B. Query-Filter wie `?filter=open`)
 *      setzt der Konsument `item.active` explizit.
 *   2) **`#menu` Slot** (volle Komposition): die Komponente stellt nur die linke
 *      Spalte (Breite, Hintergrund, Border) — der Konsument baut sein eigenes
 *      `<CoarMenu>` mit `#header`/`#footer`, `<CoarMenuHeading sticky>`,
 *      Badges, eigener Item-Logik.
 *
 *  Wenn `#menu` belegt ist, wird `items` ignoriert.
 */
const props = defineProps<{
  items?: SubNavItem[]
}>()

const slots = useSlots()

// Items OHNE `to` aber MIT `onClick` rendern als <button> via `@clicked`-Pfad.
function onClicked(item: SubNavItem) {
  if (item.disabled) return
  if (item.onClick) item.onClick()
}

const visibleItems = (): SubNavItem[] =>
  (props.items ?? []).filter((i) => i.visible !== false)
</script>

<template>
  <div class="sub-nav-layout">
    <!-- Sub-Nav: feste Spalte links, Menu scrollt intern -->
    <aside class="sub-nav">
      <slot v-if="slots.menu" name="menu" />
      <CoarMenu v-else class="sub-nav-menu">
        <CoarMenuItem
          v-for="item in visibleItems()"
          :key="item.label"
          :icon="item.icon"
          :label="item.label"
          :to="item.to"
          :active="item.active"
          :disabled="item.disabled"
          @clicked="onClicked(item)"
        />
      </CoarMenu>
    </aside>

    <!-- Content: zentriert auf 11/12 der verfügbaren Breite, damit Tabellen/
         Forms nicht direkt an der Sub-Nav-Kante bzw. am rechten Viewport-Rand
         kleben. -->
    <main class="sub-nav-content">
      <div class="sub-nav-content-inner">
        <slot />
      </div>
    </main>
  </div>
</template>

<style scoped>
.sub-nav-layout {
  display: flex;
  flex: 1;
  min-height: 0;
  min-width: 0;
}

/* Sub-Nav-Spalte selbst hat KEINEN eigenen Hintergrund — sie zeigt den Parent
   durch. Das CoarMenu erbt aber `--coar-background-neutral-primary` aus diesem
   Scope und sitzt so als grauer "Streifen" innerhalb der Spalte mit
   transparentem Padding drumherum. */
.sub-nav {
  flex-shrink: 0;
  width: 14rem;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  min-height: 0;
  --coar-background-neutral-primary: var(--coar-background-neutral-secondary, #f7f7f7);
  /* Defensive override gegen CoarMenu's 12rem-Default — Sub-Nav-Spalten dürfen
     auch unter 14rem schmal werden ohne dass das Menu rechts überläuft. */
  --coar-menu-min-width: 0;
}

/* Single-Variante: Menu ist so hoch wie sein Content. `max-height: 100%` +
   `overflow-y: auto` greifen nur wenn Items überlaufen — dann scrollt das
   Menu intern, der äußere Container scrollt NICHT. */
.sub-nav-menu {
  max-height: 100%;
  min-height: 0;
  overflow-y: auto;
}

.sub-nav-content {
  flex: 1;
  min-width: 0;
  min-height: 0;
  display: flex;
  justify-content: center;
  overflow: hidden;
}

.sub-nav-content-inner {
  width: 91.6667%; /* 11/12 */
  display: flex;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}
</style>
