<script lang="ts">
  import { onMount } from 'svelte';
  import { store } from '../lib/store.svelte';
  import { api } from '../lib/api';
  import { bytes, roleName } from '../lib/format';
  import { confirm } from '../lib/tg';
  import type { AccessRequest, Role, SystemInfo, User } from '../lib/types';

  let users = $state<User[]>([]);
  let requests = $state<AccessRequest[]>([]);
  let sys = $state<SystemInfo | null>(null);
  let notify = $state(true);
  let newId = $state('');
  let newRole = $state<Role>('User');
  let roomId = $state('');
  let roomName = $state('');

  onMount(async () => {
    sys = (await store.run(() => api.system())) ?? null;
    if (store.isAdmin) {
      users = (await store.run(() => api.users())) ?? [];
      requests = (await store.run(() => api.accessRequests())) ?? [];
    }
    const me = users.find((u) => u.telegramId === store.me?.telegramId);
    if (me) notify = me.notify;
  });

  async function toggleNotify() {
    await store.run(() => api.setMyNotify(notify), notify ? 'Уведомления включены' : 'Уведомления выключены');
  }

  async function addUser() {
    const id = Number(newId.trim());
    if (!id) return store.notify('Введите Telegram id (число)', true);
    const u = await store.run(() => api.upsertUser(id, newRole), 'Доступ выдан');
    if (u) {
      users = [...users.filter((x) => x.telegramId !== u.telegramId), u];
      newId = '';
    }
  }

  async function setRole(u: User, role: Role) {
    const r = await store.run(() => api.upsertUser(u.telegramId, role), 'Роль изменена');
    if (r) users = users.map((x) => (x.telegramId === r.telegramId ? r : x));
  }

  async function removeUser(u: User) {
    if (!(await confirm(`Забрать доступ у ${u.name || u.telegramId}?`))) return;
    if (await store.run(() => api.removeUser(u.telegramId))) users = users.filter((x) => x.telegramId !== u.telegramId);
  }

  async function approve(r: AccessRequest, role: Role) {
    const u = await store.run(() => api.approve(r.telegramId, role), 'Доступ выдан');
    if (u) {
      users = [...users, u];
      requests = requests.filter((x) => x.telegramId !== r.telegramId);
    }
  }

  async function deny(r: AccessRequest) {
    if (await store.run(() => api.deny(r.telegramId))) requests = requests.filter((x) => x.telegramId !== r.telegramId);
  }

  async function addRoom() {
    const id = roomId.trim().toLowerCase();
    if (!id) return;
    if (await store.run(() => api.saveRoom({ id, name: roomName.trim() || id, sort: store.rooms.length }), 'Комната добавлена')) {
      roomId = roomName = '';
      await store.refreshRooms();
    }
  }

  async function removeRoom(id: string) {
    if (!(await confirm('Удалить комнату? Устройства останутся без комнаты.'))) return;
    if (await store.run(() => api.removeRoom(id))) await store.refreshRooms();
  }
</script>

<h1>Настройки</h1>

<h2>Я</h2>
<div class="section">
  <div class="row"><div class="grow">{store.me?.name}</div><div class="val">{roleName(store.me?.role ?? '')}</div></div>
  {#if store.me?.telegramId}
    <div class="row">
      <div class="grow">Уведомления в Telegram</div>
      <label class="switch"><input type="checkbox" bind:checked={notify} onchange={toggleNotify} /><span></span></label>
    </div>
  {/if}
</div>

{#if store.isAdmin}
  {#if requests.length}
    <h2>Запросы доступа</h2>
    <div class="section">
      {#each requests as r (r.telegramId)}
        <div class="row">
          <div class="grow">{r.name}<div class="sub">{r.username ? '@' + r.username + ' · ' : ''}id {r.telegramId}</div></div>
          <button class="btn secondary" onclick={() => approve(r, 'User')}>Пустить</button>
          <button class="btn danger" onclick={() => deny(r)}>✕</button>
        </div>
      {/each}
    </div>
  {/if}

  <h2>Пользователи</h2>
  <div class="section">
    {#each users as u (u.telegramId)}
      <div class="row">
        <div class="grow">{u.name || '—'}<div class="sub">id {u.telegramId}</div></div>
        <select class="role" value={u.role} onchange={(e) => setRole(u, (e.target as HTMLSelectElement).value as Role)}>
          <option value="Admin">админ</option>
          <option value="User">пользователь</option>
          <option value="Viewer">наблюдатель</option>
        </select>
        <button class="btn danger" onclick={() => removeUser(u)}>✕</button>
      </div>
    {/each}
    <div class="row">
      <input type="text" inputmode="numeric" placeholder="Telegram id" bind:value={newId} />
      <select class="role" bind:value={newRole}>
        <option value="User">пользователь</option>
        <option value="Viewer">наблюдатель</option>
        <option value="Admin">админ</option>
      </select>
      <button class="btn" onclick={addUser}>+</button>
    </div>
  </div>
  <p class="small muted hint">Telegram id человек может узнать, написав боту /id. То же из консоли: <code>homectl users add &lt;id&gt;</code></p>

  <h2>Комнаты</h2>
  <div class="section">
    {#each store.rooms as r (r.id)}
      <div class="row"><div class="grow">{r.name}<div class="sub">{r.id}</div></div><button class="btn danger" onclick={() => removeRoom(r.id)}>✕</button></div>
    {/each}
    <div class="row">
      <input type="text" placeholder="id (kitchen)" bind:value={roomId} />
      <input type="text" placeholder="Название" bind:value={roomName} />
      <button class="btn" onclick={addRoom}>+</button>
    </div>
  </div>
{/if}

{#if sys}
  <h2>Система</h2>
  <div class="section">
    <div class="row"><div class="grow">Версия сервера</div><div class="val">{sys.version}</div></div>
    <div class="row"><div class="grow">Устройства</div><div class="val">{sys.online} из {sys.devices} в сети</div></div>
    <div class="row"><div class="grow">Протокол</div><div class="val">v{sys.protocolMajor}.{sys.protocolMinor}</div></div>
    <div class="row"><div class="grow">База данных</div><div class="val">{bytes(sys.dbSize)}</div></div>
    <div class="row"><div class="grow">Свободно на диске</div><div class="val">{bytes(sys.diskFree)}</div></div>
    <div class="row"><div class="grow">Бот</div><div class="val">{sys.botUsername ? '@' + sys.botUsername : sys.botConfigured ? 'подключается' : 'не настроен'}</div></div>
  </div>
{/if}

<style>
  .role {
    width: auto;
  }
  .hint {
    margin: 6px 16px;
  }
</style>
