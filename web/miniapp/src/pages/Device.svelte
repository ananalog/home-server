<script lang="ts">
  import { store } from '../lib/store.svelte';
  import { router } from '../lib/router.svelte';
  import { api } from '../lib/api';
  import { ago, otaName } from '../lib/format';
  import { confirm, haptic } from '../lib/tg';
  import PointControl from '../components/PointControl.svelte';
  import Chart from '../components/Chart.svelte';
  import type { NetStatus, NetworkCfg, Point } from '../lib/types';

  let { id }: { id: string } = $props();

  const device = $derived(store.devices[id]);
  const visible = $derived(device ? device.points.filter((p) => p.ui !== 'hidden') : []);
  const main = $derived(visible.filter((p) => (p.kind === 'sensor' || p.kind === 'actuator') && !p.advanced));
  const settings = $derived(visible.filter((p) => p.kind === 'setting' && !p.advanced));
  const actions = $derived(visible.filter((p) => p.kind === 'action' && !p.advanced));
  const advanced = $derived(visible.filter((p) => p.advanced));
  let chart = $state<Point | null>(null);
  let showAdvanced = $state(false);
  let net = $state<NetStatus | null>(null);
  let netForm = $state<NetworkCfg | null>(null);
  let name = $state('');
  let room = $state('');

  $effect(() => {
    if (device && !chart) chart = device.points.find((p) => p.history && p.ui === 'gauge') ?? null;
  });

  $effect(() => {
    if (device) {
      name = device.name;
      room = device.room ?? '';
    }
  });

  async function loadNet() {
    net = (await store.run(() => api.network(id))) ?? null;
    if (net?.current) netForm = { ...net.current, mode: net.current.mode as 'dhcp' | 'static' };
  }

  async function saveNet() {
    if (!netForm) return;
    if (!(await confirm('Применить сетевые настройки? Если сервер станет недоступен, устройство само вернёт прежние.'))) return;
    await store.run(() => api.setNetwork(id, netForm!), 'Настройки сети отправлены');
  }

  async function saveInfo() {
    await store.run(() => api.updateDevice(id, name.trim(), room), 'Сохранено');
  }

  async function reset(mode: 'settings' | 'firmware' | 'all') {
    const text = { settings: 'Сбросить настройки (Wi-Fi, IP, сервер)? Устройство придётся настроить заново через Android.', firmware: 'Откатить прошивку к заводской?', all: 'Полный сброс: настройки и прошивка к заводским?' }[mode];
    if (!(await confirm(text))) return;
    await store.run(() => api.factoryReset(id, mode), 'Команда отправлена');
  }

  async function remove() {
    if (!(await confirm(`Удалить «${device?.name}» и всю историю?`))) return;
    if (await store.run(() => api.remove(id))) router.go('/');
  }
</script>

{#if !device}
  <div class="empty">Устройство не найдено</div>
{:else}
  <h1>{device.name}</h1>
  <p class="muted small head">
    <span class="dot" class:on={device.online}></span>
    {device.online ? 'в сети' : `не в сети, ${ago(device.lastSeen)}`} · {device.model} · {device.fwVersion ?? '?'}
  </p>

  {#if device.state === 'New'}
    <div class="banner">Устройство ещё не принято. <a href="#/new">Принять</a></div>
  {/if}

  {#if device.ota && device.ota.status !== 'Done'}
    <div class="section stack pad">
      <div>Обновление до {device.ota.version}: {otaName(device.ota.status)} {device.ota.error ?? ''}</div>
      <div class="progress"><div style="width:{device.ota.progress}%"></div></div>
    </div>
  {/if}

  {#if chart}
    <h2>{chart.title}</h2>
    <div class="section">
      {#key chart.key}<Chart deviceId={device.id} point={chart} />{/key}
    </div>
  {/if}

  {#if main.length}
    <h2>Показания</h2>
    <div class="section">
      {#each main as p (p.key)}<PointControl {device} point={p} onchart={(x) => (chart = x)} />{/each}
    </div>
  {/if}

  {#if settings.length}
    <h2>Настройки</h2>
    <div class="section">
      {#each settings as p (p.key)}<PointControl {device} point={p} />{/each}
    </div>
  {/if}

  {#if actions.length}
    <h2>Действия</h2>
    <div class="section">
      {#each actions as p (p.key)}<PointControl {device} point={p} />{/each}
    </div>
  {/if}

  {#if advanced.length}
    <h2><button class="linkbtn" onclick={() => (showAdvanced = !showAdvanced)}>Дополнительно {showAdvanced ? '▴' : '▾'}</button></h2>
    {#if showAdvanced}
      <div class="section">
        {#each advanced as p (p.key)}<PointControl {device} point={p} onchart={(x) => (chart = x)} />{/each}
      </div>
    {/if}
  {/if}

  <h2>Об устройстве</h2>
  <div class="section">
    <div class="row"><div class="grow">ID</div><div class="val">{device.id}</div></div>
    <div class="row"><div class="grow">Прошивка</div><div class="val">{device.fwVersion} ({device.bootPartition})</div></div>
    <div class="row"><div class="grow">IP</div><div class="val">{device.ip ?? '—'}</div></div>
    <div class="row"><div class="grow">Впервые замечено</div><div class="val">{new Date(device.firstSeen).toLocaleDateString('ru-RU')}</div></div>
  </div>

  {#if store.canControl}
    <div class="nav">
      <button class="btn secondary" disabled={!device.online} onclick={() => { haptic.tap(); store.run(() => api.identify(id), 'Устройство мигает'); }}>Мигнуть</button>
      <a class="btn secondary" href="#/events">Журнал</a>
    </div>
  {/if}

  {#if store.isAdmin}
    <h2>Управление</h2>
    <div class="section pad stack">
      <label class="small muted" for="nm">Имя</label>
      <input id="nm" type="text" maxlength="24" bind:value={name} />
      <label class="small muted" for="rm">Комната</label>
      <select id="rm" bind:value={room}>
        <option value="">— без комнаты —</option>
        {#each store.rooms as r (r.id)}<option value={r.id}>{r.name}</option>{/each}
      </select>
      <button class="btn block" onclick={saveInfo}>Сохранить</button>
    </div>

    <h2>Сеть</h2>
    <div class="section pad stack">
      {#if !net}
        <button class="btn secondary block" disabled={!device.online} onclick={loadNet}>Показать сетевые настройки</button>
      {:else}
        <div class="small muted">{net.ssid} · {net.rssi} dBm · MAC {net.mac}</div>
        {#if netForm}
          <select bind:value={netForm.mode}>
            <option value="dhcp">DHCP (автоматически)</option>
            <option value="static">Статический IP</option>
          </select>
          {#if netForm.mode === 'static'}
            <input type="text" placeholder="IP, например 192.168.1.50" bind:value={netForm.ip} />
            <input type="number" placeholder="Префикс (24)" bind:value={netForm.prefix} />
            <input type="text" placeholder="Шлюз" bind:value={netForm.gateway} />
            <input type="text" placeholder="DNS" bind:value={netForm.dns1} />
          {/if}
          <button class="btn block" onclick={saveNet}>Применить</button>
        {/if}
      {/if}
    </div>

    <h2>Обслуживание</h2>
    <div class="section pad stack">
      <button class="btn secondary block" onclick={() => router.go(`/logs/${id}`)}>Логи устройства</button>
      <button class="btn secondary block" disabled={!device.online} onclick={async () => (await confirm('Перезагрузить устройство?')) && store.run(() => api.reboot(id), 'Перезагрузка')}>Перезагрузить</button>
      <button class="btn danger block" disabled={!device.online} onclick={() => reset('settings')}>Сбросить настройки</button>
      <button class="btn danger block" disabled={!device.online} onclick={() => reset('firmware')}>Откатить к заводской прошивке</button>
      <button class="btn danger block" disabled={!device.online} onclick={() => reset('all')}>Полный сброс</button>
      <button class="btn danger block" onclick={remove}>Удалить устройство</button>
    </div>
  {/if}
{/if}

<style>
  .head {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: -6px 4px 8px;
  }
  .pad {
    padding: 12px 16px;
  }
  .linkbtn {
    color: var(--link);
    text-transform: uppercase;
    font-size: 13px;
    letter-spacing: 0.04em;
    padding: 0;
  }
</style>
