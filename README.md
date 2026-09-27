# home-server

Сервер системы [Home](https://github.com/ananalog/home) на C# / .NET 10:
TCP-шлюз устройств, HTTP API, живые обновления (SSE), Telegram-бот и Mini App, OTA, история показаний.
Здесь же CLI `homectl` и эмулятор устройств `home-sim`.
Проект — [`home/doc/03-server-miniapp-cli.md`](https://github.com/ananalog/home/blob/main/doc/03-server-miniapp-cli.md).

## Структура

```
external/home-protocol/   сабмодуль протокола (Home.Protocol)
src/Home.Server/          ASP.NET Core: API, SSE, gateway :7700, бот, OTA, SQLite (EF Core)
src/Home.Client/          контракт API (DTO) и типизированный клиент — общий для CLI и тестов
src/Home.Cli/             homectl (System.CommandLine, Native AOT)
src/Home.Simulator/       home-sim — эмулятор устройств по настоящему протоколу
web/miniapp/              Telegram Mini App (Svelte 5 + Vite), собирается в src/Home.Server/wwwroot
tests/Home.Server.Tests/  интеграционные тесты: настоящий Kestrel + эмуляторы
scripts/publish.sh        релизный архив: сервер (self-contained), homectl, home-sim
```

## Разработка

```
git clone --recursive https://github.com/ananalog/home-server && cd home-server
(cd web/miniapp && npm ci && npm run build)
dotnet test

# сервер с данными в ./data, без systemd:
Home__DataDir=./data Home__UnixSocket=./api.sock Home__DevUserId=1 Home__BootstrapAdmin=1 \
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Home.Server
# эмуляторы:
dotnet run --project src/Home.Simulator -- --server 127.0.0.1:7700 --count 3
# CLI через локальный сокет:
dotnet run --project src/Home.Cli -- --server unix:./api.sock devices list
# Mini App с горячей перезагрузкой (прокси /api → :8080), в браузере вход через /auth/dev:
(cd web/miniapp && npm run dev)
```

## Конфигурация

`appsettings.json` → `/etc/home/home.json` (или `HOME_CONFIG`) → переменные окружения `Home__…`.

| Ключ | По умолчанию | |
|---|---|---|
| `Home:DataDir` | `/var/lib/home` | БД, прошивки, ключ сервера |
| `Home:HttpUrls` | `["http://127.0.0.1:8080"]` | HTTP (за Caddy) |
| `Home:UnixSocket` | `/run/home/api.sock` | локальный CLI без токена |
| `Home:DevicePort` / `DiscoveryPort` | 7700 / 7701 | устройства (TCP) / поиск (UDP) |
| `Home:PublicUrl` | — | `https://<имя>.duckdns.org` — для кнопки бота |
| `Home:Telegram:BotTokenFile` | `/etc/home/secrets/bot_token` | токен бота |
| `Home:BootstrapAdmin` | — | Telegram id первого админа |
| `Home:OfflineAlertMinutes` | 10 | уведомление «не в сети» |
| `Home:RawHistoryDays` | 7 | сырые показания; почасовые хранятся всегда |

## Миграции БД

```
dotnet tool install -g dotnet-ef
dotnet ef migrations add <Name> --project src/Home.Server -o Data/Migrations
```
Сервер применяет миграции при старте.
