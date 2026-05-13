# @cocoar/vue-page-builder — expose `./styles` in package exports

**Repo:** `@cocoar/vue-page-builder`
**Reported by:** Cocoar.Auth beta-test of 2.1.0 (2026-05-13) — the
editor mounted but rendered with zero styling. Tracked down to the
package shipping the CSS but not exposing it through the exports map.

## Problem

`dist/index.css` is built and present in the package, and
`package.json` lists `"sideEffects": ["*.css"]`, but the `exports`
map only has:

```json
"exports": {
  ".": {
    "import": "./dist/index.js",
    "types": "./dist/index.d.ts"
  }
}
```

— no `./styles` subpath. So a consumer doing

```css
@import "@cocoar/vue-page-builder/styles";   /* ← rejected by node ESM resolver */
```

or

```ts
import "@cocoar/vue-page-builder/styles";    /* ← same */
```

gets a module-not-found error. The CSS is also not auto-imported by
the JS entry (`dist/index.js` has no top-level `import './index.css'`),
so consumers have no way to load the stylesheet through the public
surface of the package.

## Effect on Cocoar.Auth

`<CoarPageBuilder>` renders the unstyled HTML — three vertical lists
with no panel borders, no palette tile styling, no canvas frame, no
properties panel layout. Effectively unusable without a workaround.

## Workaround in Cocoar.Auth

Deep-import via the dist path, which Vite resolves through filesystem
even when the exports map doesn't allow it:

```css
@import "@cocoar/vue-page-builder/dist/index.css";
```

This works under Vite + Rollup but is non-portable to strict ESM
resolvers and breaks if the package is later restructured.

## Suggested fix

Sister packages already do this — copy their shape:

```json
"exports": {
  ".": {
    "import": "./dist/index.js",
    "types": "./dist/index.d.ts"
  },
  "./styles": "./dist/index.css"
}
```

`@cocoar/vue-ui` and `@cocoar/vue-data-grid` both have `./styles`
entries pointing at their `dist/index.css`. Adopting the same shape
here makes the package consumable via the canonical
`@import "@cocoar/vue-page-builder/styles";` line.
