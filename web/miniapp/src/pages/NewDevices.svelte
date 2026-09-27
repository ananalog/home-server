<script lang="ts">
  import { store } from '../lib/store.svelte';
  import { api } from '../lib/api';
  import { fmtValue, mainPoint } from '../lib/format';
  import { confirm } from '../lib/tg';

  let names = $state<Record<string, string>>({});
  let rooms = $state<Record<string, string>>({});

  async function adopt(id: string) {
    const d = store.devices[id];
    if (await store.run(() => api.adopt(id))) {
      const n = (names[id] ?? '').trim();
      if (n || rooms[id]) await store.run(() => api.updateDevice(id, n || d.name, rooms[id] ?? ''));
      store.notify('Устройство принято');
    }
  }

  async function reject(id: string) {
    if (await confirm('Отклонить устройство? Оно не сможет работать с сервером.')) await store.run(() => api.reject(id), 'Отклонено');
  }
</script>

<h1>Новые устройства</h1>
{#if store.newDevices.length === 0}
  <div class="empty">Новых устройств нет</div>
{/if}
{#each store.newDevices as d (d.id)}
  {@const m = mainPoint(d)}
  <div class="section card stack">
    <div class="line">
      <span class="dot" class:on={d.online}></span>
      <b class="grow">{d.name}</b>
      <span class="muted small">{d.model} · {d.fwVersion}</span>
    </div>
    <div class="muted small">id {d.id} · {d.ip}{m ? ` · ${m.title}: ${fmtValue(m, d.values[m.key]?.value)} ${m.unit ?? ''}` : ''}</div>
    <input type="text" placeholder="Имя (например, «Спальня CO2»)" maxlength="24" bind:value={names[d.id]} />
    <select bind:value={rooms[d.id]}>
      <option value="">— комната —</option>
      {#each store.rooms as r (r.id)}<option value={r.id}>{r.name}</option>{/each}
    </select>
    <div class="line">
      <button class="btn secondary" disabled={!d.online} onclick={() => store.run(() => api.identify(d.id), 'Смотрите, какое устройство мигает')}>Мигнуть</button>
      <button class="btn grow" onclick={() => adopt(d.id)}>Принять</button>
      <button class="btn danger" onclick={() => reject(d.id)}>✕</button>
    </div>
  </div>
{/each}

<style>
  .card {
    padding: 14px 16px;
    margin-bottom: 12px;
  }
  .line {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .grow {
    flex: 1;
  }
</style>
