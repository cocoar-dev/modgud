import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'

// @cocoar/vue-ui setup
import {
    CoarOverlayPlugin,
    CoarIconPlugin,
    CoarHttpIconSource,
    CORE_ICONS,
} from '@cocoar/vue-ui'

// Localization
import { createCoarLocalization } from '@cocoar/vue-localization'

// Styles — library styles first, then app overrides.
import '@cocoar/vue-ui/styles'
import './styles.css'

// Apply dark mode before mount to prevent flash
if (localStorage.getItem('coar-theme') === 'dark') {
    document.documentElement.classList.add('dark-mode')
}

const app = createApp(App)

app.use(createPinia())
app.use(router)

const localization = createCoarLocalization({
    defaultLanguage: 'en',
    i18nUrl: (lang: string) => `/i18n/${lang}.json`,
})
app.use(localization)

app.use(CoarOverlayPlugin)
app.use(CoarIconPlugin, {
    sources: [
        CORE_ICONS,
        {
            key: 'lucide',
            source: new CoarHttpIconSource(
                (name: string) => `/icons/lucide/${name}.svg`,
            ),
        },
    ],
    defaultSource: 'lucide',
})

// Load translations before mounting to prevent flash of untranslated keys
const savedLanguage = localStorage.getItem('language') ?? 'en'
await localization.service.setLanguage(savedLanguage)

app.mount('#app')
