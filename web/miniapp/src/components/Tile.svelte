<script lang="ts">
  import type { Device } from '../lib/types';
  import { fmtValue, level, mainPoint } from '../lib/format';
  import { store } from '../lib/store.svelte';
  import { router } from '../lib/router.svelte';
  import { api } from '../lib/api';
  import { haptic } from '../lib/tg';

  let { device }: { device: Device } = $props();

  const main = $derived(mainPoint(device));
  const value = $derived(main ? device.values[main.key]?.value : undefined);
  const lvl = $derived(main ? level(main, value) : 0);
  const isSwitch = $derived(main?.kind === 'actuator' && main.type === 'bool');

  async function toggle(e: Event) {
    e.stopPropagation();
    if (!main || !store.canControl) return;
    haptic.tap();
    const next = !(value === true);
    device.values[main.key] = { value: next, ts: Date.now() };
    const r = await store.run(() => api.set(device.id, main.key, next));
    if (!r) device.values[main.key] = { value: !next, ts: Date.now() };
  }
</script>

<div class="tile" role="button" tabindex="0" class:offline={!device.online} class:alarm={lvl === 2} class:warn={lvl === 1}
  onclick={() => router.go(`/device/${device.id}`)} onkeydown={(e) => e.key === 'Enter' && router.go(`/device/${device.id}`)}>
  <div class="top">
    <span class="name">{device.name}</span>
    <span class="dot" class:on={device.online}></span>
  </div>
  {#if isSwitch && main}
    <div class="sub">{main.title}: {fmtValue(main, value)}</div>
    <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions -->
    <label class="switch sw" onclick={(e) => e.stopPropagation()}>
      <input type="checkbox" checked={value === true} disabled={!device.online || !store.canControl} onchange={toggle} />
      <span></span>
    </label>
  {:else if main}
    <div class="big lvl-{lvl}">{fmtValue(main, value)}<small>{main.unit ?? ''}</small></div>
    <div class="sub">{main.title}</div>
  {:else}
    <div class="sub">{device.model}</div>
  {/if}
  {#if device.ota && device.ota.status !== 'Done'}
    <div class="sub">⬆ {device.ota.version}: {device.ota.progress}%</div>
  {/if}
</div>

<style>
  .tile {
    background: var(--section);
    border-radius: var(--radius);
    padding: 12px 14px;
    text-align: left;
    display: flex;
    flex-direction: column;
    gap: 4px;
    min-height: 112px;
    border: 2px solid transparent;
    cursor: pointer;
  }
  .tile.warn {
    border-color: color-mix(in srgb, var(--warn) 55%, transparent);
  }
  .tile.alarm {
    border-color: var(--alarm);
  }
  .tile.offline {
    opacity: 0.55;
  }
  .top {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .name {
    flex: 1;
    font-size: 15px;
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .big {
    font-size: 32px;
    font-weight: 700;
    line-height: 1.1;
    margin-top: auto;
  }
  .big small {
    font-size: 14px;
    font-weight: 500;
    color: var(--hint);
    margin-left: 4px;
  }
  .sw {
    margin-top: auto;
  }
  .sub {
    font-size: 13px;
    color: var(--hint);
  }
</style>
