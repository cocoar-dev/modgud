import type { PageCompositionDefinition, VisualMarkupNode } from '@cocoar/vue-page-builder'

/*
 * The AcmeList brand panel — the left half of a fictional product's login page.
 *
 * It is our reference case for "can a realm build its real login page in the
 * PageBuilder, without us shipping anything tenant-specific?" The product is
 * invented, but the panel is deliberately as demanding as a real one: a
 * two-tone wordmark, a rotated card, a staggered tick-off animation and a
 * reduced-motion escape hatch. Everything below is ordinary authored content:
 * a `visual-markup` node with HTML and CSS, the same thing a realm admin can
 * write in the editor. There is no product-specific branch anywhere in the
 * renderer, and the CSS refers only to the custom properties the host exposes
 * through `createAuthVisualMarkupConfig`.
 *
 * It lives in a composition rather than in the login document because the panel
 * is shared: login and logout pin the same immutable version, and updating it
 * is one publish rather than an edit per page.
 */

const HTML = `<div class="brand-panel">
  <h1 class="wordmark">
    <span class="wordmark-lead">Acme</span><span class="wordmark-accent">List</span>
  </h1>
  <p class="tagline">Shop together. Tick it off.</p>

  <div class="list-card">
    <div class="list-head">
      <span class="list-tile">W</span>
      <span class="list-title">Weekly shop</span>
      <svg width="30" height="30" viewBox="0 0 36 36" class="progress" aria-hidden="true">
        <circle cx="18" cy="18" r="15.5" fill="none" class="progress-track" stroke-width="4.5"></circle>
        <circle cx="18" cy="18" r="15.5" fill="none" class="progress-value" stroke-width="4.5"
                stroke-linecap="round" transform="rotate(-90 18 18)"></circle>
      </svg>
    </div>
    <ul class="list-items">
      <li style="--delay:1.2s;--category:#0284c7"><span class="dot"></span><span class="item">Milk</span><span class="qty">2 L</span></li>
      <li style="--delay:2.25s;--category:#d97706"><span class="dot"></span><span class="item">Bread</span><span class="qty">1 pc</span></li>
      <li style="--delay:3.3s;--category:#16a34a"><span class="dot"></span><span class="item">Apples</span><span class="qty">6 pcs</span></li>
      <li style="--delay:4.35s;--category:#2563eb"><span class="dot"></span><span class="item">Coffee</span><span class="qty">500 g</span></li>
      <li style="--delay:5.4s;--category:#0284c7"><span class="dot"></span><span class="item">Butter</span><span class="qty">250 g</span></li>
    </ul>
  </div>
</div>`

const CSS = `
body {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 48px;
  color: var(--ink);
  font-family: var(--font-ui);
  border-right: 1px solid var(--line);
  background:
    radial-gradient(700px 480px at 85% 12%, rgba(139, 92, 246, 0.10), transparent 60%),
    radial-gradient(620px 460px at 0% 100%, rgba(16, 185, 129, 0.13), transparent 55%),
    var(--surface);
}

.brand-panel { width: 100%; max-width: 360px; }

/* The wordmark carries the brand on its own — this panel replaces the generic
   logo/product-name zone rather than sitting next to it. */
.wordmark {
  margin: 0;
  /* Inside the sealed iframe, vw resolves against the iframe — this pane, not
     the viewport. The panel is 44% wide, so the viewport-relative 4.5vw a
     full-page design would use is ~10vw here. Without this the wordmark
     collapses onto its clamp minimum and reads far too small next to the form. */
  font-size: clamp(2.6rem, 10vw, 3.4rem);
  display: inline-flex;
  align-items: baseline;
  letter-spacing: -.02em;
  line-height: 1;
  white-space: nowrap;
  animation: rise-in .5s var(--ease-out) both;
}
.wordmark-lead { color: var(--ink); font-family: var(--font-ui); font-weight: 700; }
.wordmark-accent {
  color: var(--brand-deep);
  font-family: var(--font-display);
  font-style: italic;
  font-variation-settings: "opsz" 40, "wght" 520;
}

.tagline {
  margin: 6px 0 40px;
  color: var(--ink-soft);
  font-size: 1.06rem;
  animation: rise-in .5s var(--ease-out) .08s both;
}

.list-card {
  padding: 20px 22px;
  border: 1px solid var(--line);
  border-radius: var(--radius-l);
  background: var(--surface);
  box-shadow: var(--shadow-pop);
  transform: rotate(-1.2deg);
  animation: rise-in .55s var(--ease-out) .18s both;
}
.list-head {
  display: flex;
  align-items: center;
  gap: 11px;
  padding-bottom: 14px;
  margin-bottom: 6px;
  border-bottom: 1px solid var(--line);
}
.list-tile {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  border-radius: var(--radius-s);
  background: var(--brand);
  color: #fff;
  font-weight: 700;
}
.list-title { flex: 1; font-size: 1.04rem; font-weight: 700; }
.progress-track { stroke: var(--hover); }
.progress-value {
  stroke: var(--brand);
  stroke-dasharray: 0 97.4;
  animation: ring-fill 5.5s var(--ease-out) 1.2s both;
}

.list-items { margin: 0; padding: 0; list-style: none; }
.list-items li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 2px;
  border-bottom: 1px solid var(--line);
  font-size: .98rem;
  font-weight: 500;
}
.list-items li:last-child { border-bottom: 0; }

/* Each row ticks itself off on a stagger, which is the product's whole idea:
   a shared list being worked through. */
.dot {
  width: 19px;
  height: 19px;
  flex: 0 0 auto;
  border: 2px solid color-mix(in srgb, var(--category) 55%, var(--line-strong));
  border-radius: 50%;
  animation: dot-fill .35s ease var(--delay) both;
}
.item { flex: 1; animation: word-strike .35s ease var(--delay) both; }
.qty {
  padding: 2px 9px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--category) 13%, var(--surface));
  color: var(--ink-soft);
  font-size: .74rem;
  font-weight: 700;
}

@keyframes rise-in { from { opacity: 0; transform: translateY(14px); } }
@keyframes ring-fill { to { stroke-dasharray: 97.4 97.4; } }
@keyframes dot-fill { to { border-color: var(--category); background: var(--category); } }
@keyframes word-strike {
  to {
    color: var(--ink-faint);
    text-decoration: line-through;
    text-decoration-color: var(--category);
    text-decoration-thickness: 2px;
  }
}

@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: .01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: .01ms !important;
  }
}
`

export const ACME_BRAND_PANEL: PageCompositionDefinition = {
  id: 'acme-brand-panel',
  name: 'AcmeList brand panel',
  version: '1',
  root: {
    id: 'acme-brand-panel-root',
    type: 'visual-markup',
    name: 'brandPanel',
    props: { html: HTML, css: CSS },
    // No height: the shell row stretches its children, which fills this pane
    // top to bottom. An explicit height would opt out of that stretching, and a
    // percentage cannot resolve against a content-sized row anyway.
    //
    // Hidden below desktop: on a phone the form is the whole screen, and a
    // decorative half would push it under the fold.
    style: { size: 'fixed', width: '44%', minWidth: '380px', hidden: true },
    responsive: { desktop: { hidden: false } },
  } satisfies VisualMarkupNode,
}
