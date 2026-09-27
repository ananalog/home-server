<script lang="ts">
  import { onMount } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { api } from '../lib/api';
  import { bytes, otaName } from '../lib/format';
  import { confirm } from '../lib/tg';
  import type { Firmware } from '../lib/types';

  let list = $state<Firmware[]>([]);
  let uploading = $state(false);
  let selected = $state<Firmware | null>(null);
  let targets = $state<Record<string, boolean>>({});
  let canary = $state(true);

  onMount(async () => {
    list = (await store.run(() => api.firmware())) ?? [];
    store.otaJobs = (await store.run(() => api.otaJobs())) ?? store.otaJobs;
  });

  async function upload(e: Event) {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    uploading = true;
    const fw = await store.run(() => api.uploadFirmware(file), 'Прошивка загружена');
    uploading = false;
    input.value = '';
    if (fw) list = [fw, ...list.filter((f) => f.id !== fw.id)];
  }

  function pick(fw: Firmware) {
    selected = fw;
    targets = Object.fromEntries(store.adopted.filter((d) => d.modelId === fw.modelId && d.fwVersion !== fw.version).map((d) => [d.id, true]));
  }

  const candidates = $derived(selected ? store.adopted.filter((d) => d.modelId === selected!.modelId) : []);

  async function flash() {
    const ids = Object.entries(targets).filter(([, v]) => v).map(([k]) => k);
    if (!selected || ids.length === 0) return;
    if (!(await confirm(`Обновить ${ids.length} устр. до ${selected.version}?`))) return;
    const job = await store.run(() => api.startOta(selected!.id, ids, false, canary && ids.length > 1), 'Обновление запущено');
    if (job) {
      store.otaJobs = [job, ...store.otaJobs];
      selected = null;
    }
  }

  async function removeFw(fw: Firmware) {
    if (!(await confirm(`Удалить прошивку ${fw.model} ${fw.version}?`))) return;
    if (await store.run(() => api.removeFirmware(fw.id))) list = list.filter((f) => f.id !== fw.id);
  }
</script>

<h1>Прошивки</h1>

<label class="btn block upload">
  {uploading ? 'Загрузка…' : 'Загрузить .bin'}
  <input type="file" accept=".bin,application/octet-stream" onchange={upload} disabled={uploading} hidden />
</label>

{#if store.otaJobs.length}
  <h2>Обновления</h2>
  <div class="section">
    {#each store.otaJobs.slice(0, 5) as j (j.id)}
      <div class="row col">
        <div class="line"><b class="grow">{j.model} {j.version}</b><span class="muted small">{otaName(j.status)}</span></div>
        {#each j.items as i (i.deviceId)}
          <div class="small line"><span class="grow">{i.deviceName}</span><span class="muted">{otaName(i.status)} {i.progress}%</span></div>
          {#if i.status === 'Running' || i.status === 'Rebooting'}<div class="progress"><div style="width:{i.progress}%"></div></div>{/if}
          {#if i.error}<div class="small" style="color:var(--danger)">{i.error}</div>{/if}
        {/each}
        {#if j.status === 'Running' || j.status === 'Pending'}
          <button class="btn danger" onclick={() => store.run(() => api.cancelOta(j.id), 'Отменено')}>Отменить</button>
        {/if}
      </div>
    {/each}
  </div>
{/if}

<h2>Загруженные</h2>
{#if list.length === 0}
  <div class="empty small">Прошивок пока нет. Соберите: <code>scripts/fw-build.sh co2-egg</code></div>
{:else}
  <div class="section">
    {#each list as fw (fw.id)}
      <div class="row">
        <div class="grow">
          <div>{fw.model} <b>{fw.version}</b>{fw.channel !== 'stable' ? ` (${fw.channel})` : ''}</div>
          <div class="sub">{bytes(fw.size)} · {new Date(fw.uploadedAt).toLocaleDateString('ru-RU')} · {fw.sha256.slice(0, 8)}</div>
        </div>
        <button class="btn secondary" onclick={() => pick(fw)}>Прошить</button>
        <button class="btn danger" onclick={() => removeFw(fw)}>✕</button>
      </div>
    {/each}
  </div>
{/if}

{#if selected}
  <h2>Обновить до {selected.version}</h2>
  <div class="section">
    {#each candidates as d (d.id)}
      <label class="row">
        <input type="checkbox" bind:checked={targets[d.id]} />
        <div class="grow">{d.name}<div class="sub">сейчас {d.fwVersion}{d.online ? '' : ' · не в сети'}</div></div>
      </label>
    {:else}
      <div class="row muted">Нет устройств этой модели</div>
    {/each}
    <label class="row"><input type="checkbox" bind:checked={canary} /><div class="grow">Сначала одно устройство (канарейка)</div></label>
  </div>
  <div class="nav">
    <button class="btn" onclick={flash}>Обновить</button>
    <button class="btn secondary" onclick={() => (selected = null)}>Отмена</button>
  </div>
{/if}

<style>
  .upload {
    margin-bottom: 4px;
  }
  .col {
    flex-direction: column;
    align-items: stretch;
    gap: 6px;
  }
  .line {
    display: flex;
    gap: 8px;
    align-items: center;
  }
  .grow {
    flex: 1;
  }
</style>
