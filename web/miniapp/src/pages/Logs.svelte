<script lang="ts">
  import { onMount } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { api, openStream } from '../lib/api';
  import type { LogLine } from '../lib/types';

  let { id }: { id: string } = $props();
  let lines = $state<LogLine[]>([]);
  let box: HTMLDivElement;

  onMount(() => {
    store.run(() => api.logs(id)).then((l) => (lines = l ?? []));
    const close = openStream((type, data) => {
      if (type === 'log' && data.deviceId === id) {
        lines = [...lines.slice(-499), data.line];
        queueMicrotask(() => box?.scrollTo(0, box.scrollHeight));
      }
    }, id);
    return close;
  });
</script>

<h1>Логи: {store.devices[id]?.name ?? id}</h1>
<div class="section logs" bind:this={box}>
  {#each lines as l, i (i)}
    <div class="ln lv-{l.level}"><span class="muted">{new Date(l.ts).toLocaleTimeString('ru-RU')}</span> <b>{l.tag}</b> {l.text}</div>
  {:else}
    <div class="muted">Пока пусто. Логи приходят от устройства в реальном времени.</div>
  {/each}
</div>

<style>
  .logs {
    padding: 10px 12px;
    font: 12px/1.45 ui-monospace, Menlo, Consolas, monospace;
    max-height: 75vh;
    overflow: auto;
  }
  .ln {
    white-space: pre-wrap;
    word-break: break-word;
  }
  .lv-error {
    color: var(--alarm);
  }
  .lv-warn {
    color: var(--warn);
  }
</style>
