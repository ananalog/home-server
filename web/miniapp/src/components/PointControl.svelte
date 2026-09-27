<script lang="ts">
  import type { Device, Point } from '../lib/types';
  import { fmtValue, label, level } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import { api } from '../lib/api';
  import { haptic, confirm } from '../lib/tg';

  let { device, point, onchart }: { device: Device; point: Point; onchart?: (p: Point) => void } = $props();

  const value = $derived(device.values[point.key]?.value);
  const editable = $derived(store.canControl && device.online && (point.kind === 'actuator' || point.kind === 'setting') && !point.readOnly);
  let draft = $state<number | null>(null);
  let arg = $state<number | null>(null);

  async function set(v: boolean | number | string) {
    haptic.tap();
    const prev = device.values[point.key];
    device.values[point.key] = { value: v, ts: Date.now() };
    const r = await store.run(() => api.set(device.id, point.key, v));
    if (r) device.values[point.key] = r;
    else if (prev) device.values[point.key] = prev;
  }

  async function invoke() {
    const a = point.hasArg ? (arg ?? point.argDefault ?? 0) : undefined;
    if (!(await confirm(`${point.title}${a !== undefined ? ` (${a}${point.unit ? ' ' + point.unit : ''})` : ''}?`))) return;
    haptic.tap();
    const r = await store.run(() => api.invoke(device.id, point.key, a));
    if (r) store.notify(r.text ?? 'Готово');
  }
</script>

{#if point.kind === 'action'}
  <div class="row">
    <div class="grow">{point.title}</div>
    {#if point.hasArg}
      <input type="number" class="arg" min={point.min} max={point.max} step={point.step ?? 1} placeholder={String(point.argDefault ?? '')} bind:value={arg} />
    {/if}
    <button class="btn secondary" disabled={!store.canControl || !device.online} onclick={invoke}>Выполнить</button>
  </div>
{:else if editable && point.type === 'bool'}
  <div class="row">
    <div class="grow">{point.title}</div>
    <label class="switch"><input type="checkbox" checked={value === true} onchange={(e) => set((e.target as HTMLInputElement).checked)} /><span></span></label>
  </div>
{:else if editable && point.type === 'enum'}
  <div class="row">
    <div class="grow">{point.title}</div>
    <select class="sel" value={typeof value === 'number' ? point.options[value] : ''} onchange={(e) => set((e.target as HTMLSelectElement).value)}>
      {#each point.options as o}<option value={o}>{label(o)}</option>{/each}
    </select>
  </div>
{:else if editable && (point.type === 'i32' || point.type === 'f32') && point.min !== undefined && point.max !== undefined}
  <div class="row col">
    <div class="line">
      <div class="grow">{point.title}</div>
      <div class="val">{draft ?? fmtValue(point, value)} {point.unit ?? ''}</div>
    </div>
    <input type="range" min={point.min} max={point.max} step={point.step ?? 1} value={typeof value === 'number' ? value : point.min}
      oninput={(e) => (draft = Number((e.target as HTMLInputElement).value))}
      onchange={(e) => { const v = Number((e.target as HTMLInputElement).value); draft = null; set(v); }} />
  </div>
{:else}
  <!-- svelte-ignore a11y_no_static_element_interactions, a11y_click_events_have_key_events -->
  <div class="row" class:link={point.history && !!onchart} onclick={() => point.history && onchart?.(point)}>
    <div class="grow">{point.title}</div>
    <div class="val lvl-{level(point, value)}">{fmtValue(point, value)} {point.unit ?? ''}</div>
  </div>
{/if}

<style>
  .col {
    flex-direction: column;
    align-items: stretch;
  }
  .line {
    display: flex;
    gap: 12px;
  }
  .sel {
    width: auto;
    max-width: 55%;
  }
  .arg {
    width: 90px;
  }
</style>
