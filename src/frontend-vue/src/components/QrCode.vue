<script setup lang="ts">
// A QR code drawn in the browser. What it encodes never leaves the page: the authenticator
// setup puts the TOTP secret in here, so the code must not be fetched as an image from a
// QR service — and the CSP (`img-src 'self' data:`) would refuse that image anyway.
import { computed } from 'vue'
import { encode } from 'uqr'

const props = withDefaults(defineProps<{
  value: string
  /** Edge length in CSS pixels. */
  size?: number
  label?: string
}>(), {
  size: 200,
  label: 'QR Code',
})

// Four modules of quiet zone, as the QR specification asks for — scanners need it to find
// the code when the page behind it is dark.
const qr = computed(() => encode(props.value, { ecc: 'M', border: 4 }))

const path = computed(() => {
  const { data, size } = qr.value
  let d = ''
  for (let row = 0; row < size; row++) {
    for (let col = 0; col < size; col++) {
      if (data[row]?.[col]) d += `M${col},${row}h1v1h-1z`
    }
  }
  return d
})
</script>

<template>
  <!-- Always dark on white, in dark mode too: an inverted code is not readable by every scanner. -->
  <svg
    xmlns="http://www.w3.org/2000/svg"
    :viewBox="`0 0 ${qr.size} ${qr.size}`"
    :width="size"
    :height="size"
    shape-rendering="crispEdges"
    role="img"
    :aria-label="label">
    <rect :width="qr.size" :height="qr.size" fill="#fff" />
    <path :d="path" fill="#000" />
  </svg>
</template>
