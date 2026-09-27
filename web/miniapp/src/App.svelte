<script lang="ts">
  import { onMount } from 'svelte';
  import { store } from './lib/store.svelte';
  import { router } from './lib/router.svelte';
  import { initTelegram, tg } from './lib/tg';
  import Home from './pages/Home.svelte';
  import DevicePage from './pages/Device.svelte';
  import NewDevices from './pages/NewDevices.svelte';
  import Firmware from './pages/Firmware.svelte';
  import Settings from './pages/Settings.svelte';
  import Events from './pages/Events.svelte';
  import Logs from './pages/Logs.svelte';

  onMount(() => {
    if (tg) document.documentElement.classList.add('tg');
    initTelegram();
    store.start();
  });

  const page = $derived(router.parts[0] ?? '');
</script>

{#if store.phase === 'loading'}
  <div class="empty">Загрузка…</div>
{:else if store.phase === 'no-access'}
  <div class="empty stack">
    <div style="font-size:48px">🔒</div>
    <h1>Нет доступа</h1>
    <p>Ваш Telegram id: <b>{store.noAccessId}</b></p>
    <p class="muted">Запрос отправлен администраторам дома. Они могут открыть доступ в приложении или командой<br /><code>homectl users approve {store.noAccessId}</code></p>
  </div>
{:else if store.phase === 'no-telegram'}
  <div class="empty stack">
    <div style="font-size:48px">🏠</div>
    <h1>Откройте через Telegram</h1>
    <p class="muted">Это приложение работает внутри Telegram: откройте бота и нажмите кнопку «Дом».</p>
  </div>
{:else if store.phase === 'error'}
  <div class="empty stack">
    <h1>Ошибка</h1>
    <p class="muted">{store.error}</p>
    <button class="btn" onclick={() => location.reload()}>Повторить</button>
  </div>
{:else if page === 'device' && router.parts[1]}
  <DevicePage id={router.parts[1]} />
{:else if page === 'new'}
  <NewDevices />
{:else if page === 'firmware'}
  <Firmware />
{:else if page === 'settings'}
  <Settings />
{:else if page === 'events'}
  <Events />
{:else if page === 'logs' && router.parts[1]}
  <Logs id={router.parts[1]} />
{:else}
  <Home />
{/if}

{#if store.toast}
  <div class="toast" class:bad={store.toast.bad}>{store.toast.text}</div>
{/if}
