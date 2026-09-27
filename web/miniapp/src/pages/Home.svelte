<script lang="ts">
  import { store } from '../lib/store.svelte';
  import { router } from '../lib/router.svelte';
  import Tile from '../components/Tile.svelte';

  let room = $state<string>('all');

  const rooms = $derived(store.rooms.filter((r) => store.adopted.some((d) => d.room === r.id)));
  const noRoom = $derived(store.adopted.some((d) => !d.room || !store.rooms.some((r) => r.id === d.room)));
  const shown = $derived(
    store.adopted.filter((d) =>
      room === 'all' ? true : room === '-' ? !d.room || !store.rooms.some((r) => r.id === d.room) : d.room === room,
    ),
  );
  const running = $derived(store.otaJobs.filter((j) => j.status === 'Running' || j.status === 'Pending'));
</script>

<h1>Дом</h1>

{#if store.isAdmin && store.newDevices.length > 0}
  <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
  <div class="banner" onclick={() => router.go('/new')}>
    <span style="font-size:22px">🆕</span>
    <div class="grow">Найдено новых устройств: <b>{store.newDevices.length}</b></div>
    <span class="muted">›</span>
  </div>
{/if}

{#if running.length > 0}
  <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
  <div class="banner" onclick={() => router.go('/firmware')}>
    <span style="font-size:22px">⬆️</span>
    <div class="grow">Идёт обновление прошивки</div>
  </div>
{/if}

{#if rooms.length > 0}
  <div class="chips">
    <button class="chip" class:active={room === 'all'} onclick={() => (room = 'all')}>Все</button>
    {#each rooms as r (r.id)}
      <button class="chip" class:active={room === r.id} onclick={() => (room = r.id)}>{r.name}</button>
    {/each}
    {#if noRoom}
      <button class="chip" class:active={room === '-'} onclick={() => (room = '-')}>Без комнаты</button>
    {/if}
  </div>
{/if}

{#if shown.length === 0}
  <div class="empty">
    {#if store.adopted.length === 0}
      Устройств пока нет.<br />Настройте устройство через Android-приложение — оно появится здесь.
    {:else}
      В этой комнате нет устройств
    {/if}
  </div>
{:else}
  <div class="grid">
    {#each shown as d (d.id)}
      <Tile device={d} />
    {/each}
  </div>
{/if}

<div class="nav">
  <button class="btn secondary" onclick={() => router.go('/events')}>Журнал</button>
  {#if store.isAdmin}
    <button class="btn secondary" onclick={() => router.go('/firmware')}>Прошивки</button>
  {/if}
  <button class="btn secondary" onclick={() => router.go('/settings')}>Настройки</button>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    gap: 10px;
  }
  .grow {
    flex: 1;
  }
</style>
