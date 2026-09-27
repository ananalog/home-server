// Thin wrapper over window.Telegram.WebApp (https://core.telegram.org/bots/webapps).

interface TgButton {
  show(): void;
  hide(): void;
  onClick(cb: () => void): void;
  offClick(cb: () => void): void;
}

interface TgWebApp {
  initData: string;
  initDataUnsafe: { user?: { id: number; first_name: string } };
  colorScheme: 'light' | 'dark';
  platform: string;
  version: string;
  ready(): void;
  expand(): void;
  close(): void;
  BackButton: TgButton;
  HapticFeedback?: {
    impactOccurred(style: 'light' | 'medium' | 'heavy' | 'rigid' | 'soft'): void;
    notificationOccurred(type: 'error' | 'success' | 'warning'): void;
    selectionChanged(): void;
  };
  showConfirm?(message: string, cb: (ok: boolean) => void): void;
  showAlert?(message: string, cb?: () => void): void;
  showPopup?(p: { title?: string; message: string; buttons?: { id?: string; type?: string; text?: string }[] }, cb?: (id: string) => void): void;
  isVersionAtLeast?(v: string): boolean;
}

declare global {
  interface Window {
    Telegram?: { WebApp?: TgWebApp };
  }
}

export const tg: TgWebApp | undefined = window.Telegram?.WebApp?.initData ? window.Telegram.WebApp : undefined;

export function initTelegram() {
  if (!tg) return;
  tg.ready();
  tg.expand();
}

export const haptic = {
  tap: () => tg?.HapticFeedback?.impactOccurred('light'),
  ok: () => tg?.HapticFeedback?.notificationOccurred('success'),
  error: () => tg?.HapticFeedback?.notificationOccurred('error'),
  select: () => tg?.HapticFeedback?.selectionChanged(),
};

export function confirm(message: string): Promise<boolean> {
  if (tg?.showConfirm && tg.isVersionAtLeast?.('6.2')) return new Promise((r) => tg!.showConfirm!(message, r));
  return Promise.resolve(window.confirm(message));
}

export function alert(message: string): Promise<void> {
  if (tg?.showAlert && tg.isVersionAtLeast?.('6.2')) return new Promise((r) => tg!.showAlert!(message, () => r()));
  window.alert(message);
  return Promise.resolve();
}

let backHandler: (() => void) | null = null;

/** Shows Telegram's native back button (or hides it with null). */
export function setBack(handler: (() => void) | null) {
  if (!tg) return;
  if (backHandler) tg.BackButton.offClick(backHandler);
  backHandler = handler;
  if (handler) {
    tg.BackButton.onClick(handler);
    tg.BackButton.show();
  } else tg.BackButton.hide();
}
