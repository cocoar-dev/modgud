<script setup lang="ts">
import { computed } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { CoarMenuItem } from '@cocoar/vue-ui';

const props = defineProps<{
  to: string;
  icon?: string;
  exact?: boolean;
}>();

const router = useRouter();
const route = useRoute();

const isActive = computed(() =>
  props.exact ? route.path === props.to : route.path.startsWith(props.to),
);

function navigate() {
  router.push(props.to);
}
</script>

<template>
  <CoarMenuItem :icon="icon" :class="{ active: isActive }" @clicked="navigate">
    <slot />
  </CoarMenuItem>
</template>
