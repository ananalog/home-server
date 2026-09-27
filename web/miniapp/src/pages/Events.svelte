<script lang="ts">
  import { onMount } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { api } from '../lib/api';
  import type { EventItem } from '../lib/types';

  let list = $state<EventItem[]>([]);
  onMount(async () => (list = (await store.run(() => api.events(undefined, 200))) ?? []));

  const icon = (k: string) =>
    ({ threshold: '⚠️', 'sensor-error': '❗', 'new-device': '🆕', adopted: '✅', 'ota-done': '⬆️', 'ota-rollback': '↩️', set: '🎛', invoke: '▶️', user: '👤', firmware: '📦' })[k] ?? '•';
</script>

<h1>Журнал</h1>
{#if list.length === 0}
  <div class="empty">Событий пока нет</div>
{:else}
  <div class="section">
    {#each list as e (e.id)}
      <div class="row">
        <span>{icon(e.kind)}</span>
        <div class="grow">{e.text}<div class="sub">{new Date(e.ts).toLocaleString('ru-RU')}{e.actor ? ` · ${e.actor}` : ''}</div></div>
      </div>
    {/each}
  </div>
{/if}
