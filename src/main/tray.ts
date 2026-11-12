// Tray icon: brings hidden windows back, starts sessions, installs updates, and is the only way to really quit.
import { Menu, Tray, nativeImage } from 'electron';

export interface TrayWindowItem {
  id: string;
  label: string;
  visible: boolean;
}

/** The menu's texts, read on every refresh (the language can change while reframe runs). */
export interface TrayLabels {
  tooltip: string;
  newSession: string;
  newWindow: string;
  about: string;
  terminateAll: string;
  quit: string;
}

export interface TrayActions {
  labels: () => TrayLabels;
  windows: () => TrayWindowItem[];
  toggleWindow: (id: string, visible: boolean) => void;
  showPrimary: () => void;
  newSession: () => void;
  newWindow: () => void;
  /** "Restart to update" once an update is downloaded; in the portable copy "Download reframe X" (the release page). */
  update: () => { label: string; install: () => void } | null;
  about: () => void;
  /** Whether a session is running (the menu is refreshed when that changes). */
  anyRunning: () => boolean;
  /** Ends every running session, as its tile's × does; reframe stays open. */
  terminateAll: () => void;
  /** Quits; the sessions keep running and the next reframe takes them over. */
  quit: () => void;
}

export function createTray(
  icon: string,
  icon2x: string,
  actions: TrayActions,
): { tray: Tray; refresh: () => void; refreshTooltip: () => void } {
  const image = nativeImage.createFromPath(icon);
  image.addRepresentation({ scaleFactor: 2, buffer: nativeImage.createFromPath(icon2x).toPNG() });
  const tray = new Tray(image);

  const refresh = () => {
    const labels = actions.labels();
    const update = actions.update();
    tray.setToolTip(labels.tooltip);
    tray.setContextMenu(
      Menu.buildFromTemplate([
        ...actions.windows().map((w) => ({
          label: w.label,
          type: 'checkbox' as const,
          checked: w.visible,
          click: () => actions.toggleWindow(w.id, !w.visible),
        })),
        { type: 'separator' },
        { label: labels.newSession, click: actions.newSession },
        { label: labels.newWindow, click: actions.newWindow },
        { type: 'separator' },
        ...(update ? [{ label: update.label, click: update.install }] : []),
        { label: labels.about, click: actions.about },
        { label: labels.terminateAll, enabled: actions.anyRunning(), click: actions.terminateAll },
        { label: labels.quit, click: actions.quit },
      ]),
    );
  };
  refresh();
  tray.on('click', actions.showPrimary);
  // The tooltip changes more often than the menu (sessions start and stop needing the user).
  return { tray, refresh, refreshTooltip: () => tray.setToolTip(actions.labels().tooltip) };
}
