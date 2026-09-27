# home-server

Сервер системы [Home](https://github.com/ananalog/home) на C# / .NET 10: HTTP API, SignalR, TCP-gateway устройств,
Telegram-бот, CLI `homectl`, эмулятор устройств и Telegram Mini App.
Проект — [`home/doc/03-server-miniapp-cli.md`](https://github.com/ananalog/home/blob/main/doc/03-server-miniapp-cli.md).

```
external/home-protocol/   сабмодуль протокола
src/Home.Server/          ASP.NET Core хост (API, SignalR, gateway :7700, бот)
src/Home.Client/          клиент API
src/Home.Cli/             homectl
src/Home.Simulator/       эмулятор устройств
web/miniapp/              Telegram Mini App (Svelte + Vite)
tests/
```
