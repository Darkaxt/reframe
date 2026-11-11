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
  quit: string;
  endSessionsAndQuit: string;
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
  /** Quits; the sessions keep running and the next reframe takes them over. */
  quit: () => void;
  /** Quits the way reframe did before its sessions outlived it: they end (and resume next time). */
  endSessionsAndQuit: () => void;
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
        { label: labels.quit, click: actions.quit },
        { label: labels.endSessionsAndQuit, click: actions.endSessionsAndQuit },
      ]),
    );
  };
  refresh();
  tray.on('click', actions.showPrimary);
  // The tooltip changes more often than the menu (sessions start and stop needing the user).
  return { tray, refresh, refreshTooltip: () => tray.setToolTip(actions.labels().tooltip) };
}
