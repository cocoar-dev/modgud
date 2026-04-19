<script setup lang="ts">
import { RouterView } from 'vue-router'
import { CoarOverlayHost, CoarToastContainer } from '@cocoar/vue-ui'
</script>

<template>
    <RouterView />
    <CoarOverlayHost />
    <CoarToastContainer />
</template>
