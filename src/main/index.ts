import { app, dialog, shell } from 'electron';
import { resolveLocale, translate } from '@shared/i18n';
import icon from '../../resources/icon.png?asset';
import trayIcon from '../../resources/tray.png?asset';
import trayIcon2x from '../../resources/tray@2x.png?asset';
import fs from 'node:fs';
import path from 'node:path';
import { ReframeApp } from './app';
import { gpu, savedGpuAcceleration } from './gpu';
import { createLogger } from './log';
import { startedBySystem } from './autostart';
import { portableDataDir } from './portable';
import { applyLaunchConfig, refusedProviders } from './launch-config';
import { appUserModelId, managedShortcuts, shortcutWrite, staleElectronShortcut, toastActivatorClsid } from './toast';

// Started directly (not by the managed launcher): the fixed profile and CLI routes next to reframe.exe.
const launchConfig = applyLaunchConfig(process.execPath, process.env, { packaged: app.isPackaged });
if (launchConfig) Object.assign(refusedProviders, launchConfig.refused);

// Tests and side-by-side dev runs use their own profile; the portable copy keeps it next to itself.
const portable = app.isPackaged && !launchConfig ? portableDataDir(process.execPath) : null;
const profile = process.env.REFRAME_USER_DATA || portable;
if (profile) app.setPath('userData', profile);

const build = app.isPackaged ? 'installed' : 'development';
const quitCommand = process.argv.includes('--quit');

if (!app.requestSingleInstanceLock({ build, quit: quitCommand })) {
  // One reframe per profile: the running one shows its windows (second-instance) and this one leaves.
  createLogger(path.join(app.getPath('userData'), 'logs'))(
    'info',
    quitCommand ? 'quit requested for running profile' : 'reframe already runs, showing its windows',
    {
      build,
    },
  );
  app.quit();
} else if (quitCommand) {
  // Nothing owns this exact profile. A stop command never starts a GUI or changes saved state.
  app.quit();
} else {
  const log = createLogger(path.join(app.getPath('userData'), 'logs'));
  // Before the app is ready, or Chromium has already chosen: the GPU is used only when Settings asks for it.
  gpu.accelerated = savedGpuAcceleration(path.join(app.getPath('userData'), 'state.json'));
  if (!gpu.accelerated) app.disableHardwareAcceleration();
  log('info', 'hardware acceleration', { enabled: gpu.accelerated });
  // Windows shows toasts, and routes their clicks, only for an app id with a Start menu shortcut. The installer
  // creates one for dev.reframe.app; the managed launcher sets a fixed id (REFRAME_APP_ID) whose shortcut is kept
  // here; otherwise (npm run dev, portable) the executable's path is the id that works.
  if (process.platform === 'win32') {
    const appId = appUserModelId(process.env, process.execPath, app.isPackaged && !portable);
    app.setAppUserModelId(appId);
    const wanted = managedShortcuts(process.env, appId, process.execPath);
    if (wanted.length > 0) {
      const clsid = toastActivatorClsid(appId);
      const read = (file: string) => {
        try {
          return shell.readShortcutLink(file);
        } catch {
          return null;
        }
      };
      try {
        app.setToastActivatorCLSID(clsid);
        const ownLink = path.join(path.dirname(wanted[0]!.file), `${app.getName()}.lnk`);
        if (staleElectronShortcut(read(ownLink), clsid)) {
          fs.rmSync(ownLink, { force: true });
          log('info', 'stale toast shortcut removed', { file: ownLink });
        }
        for (const spec of wanted) {
          const write = shortcutWrite(read(spec.file), spec);
          if (!write) continue;
          const written = shell.writeShortcutLink(
            spec.file,
            write.operation,
            write.options as Electron.ShortcutDetails,
          );
          log(written ? 'info' : 'warn', 'shortcut app id', { file: spec.file, appId, ...write, written });
        }
      } catch (error) {
        log('warn', 'could not set up toast activation', { error: String(error) });
      }
    }
  }

  if (launchConfig) log('info', 'launch config applied', { ...launchConfig });
  // Logged and survived: ending the process would end every session, and Electron's own error box is a modal
  // nobody sees while reframe sits in the tray.
  process.on('uncaughtException', (error) =>
    log('error', 'uncaught exception', { error: error.stack ?? String(error) }),
  );
  process.on('unhandledRejection', (reason) =>
    log('error', 'unhandled promise rejection', { error: (reason as Error)?.stack ?? String(reason) }),
  );

  let reframe: ReframeApp | null = null;
  let quitting = false;
  let startupComplete = false;
  let quitRequested = false;

  app.on('second-instance', (_event, _argv, _cwd, data) => {
    if ((data as { quit?: unknown } | null)?.quit === true) {
      quitRequested = true;
      // Startup initializes the services shutdown uses; do not shut down half-initialized state.
      if (startupComplete) app.quit();
      return;
    }
    reframe?.revealWindows();
    // The installed app started while a development build runs, or the other way round: say so, or one ends up
    // trying out the build that is not running.
    const other = (data as { build?: unknown } | null)?.build;
    if ((other === 'installed' || other === 'development') && other !== build) reframe?.otherBuildStarted(other);
  });

  // Closing windows only hides them; reframe keeps running in the tray until "Quit".
  app.on('window-all-closed', () => {});

  app.on('before-quit', (event) => {
    if (quitting || !reframe) return;
    event.preventDefault();
    quitting = true;
    // Second quit() passes through (quitting is set) and lets Chromium close its helper processes.
    void reframe.shutdown().finally(() => app.quit());
  });

  app.on('activate', () => reframe?.revealWindows());

  void app.whenReady().then(async () => {
    const locale = resolveLocale(process.env.REFRAME_LOCALE || app.getLocale());
    try {
      reframe = new ReframeApp(locale, { icon, trayIcon, trayIcon2x }, !!portable);
      await reframe.start({ hidden: startedBySystem() });
      startupComplete = true;
      if (quitRequested) app.quit();
    } catch (error) {
      // Without this, a failed start left a process with no window and no tray that still held the single-instance
      // lock: every later launch only "showed the windows" of it. Sessions started meanwhile are ended first.
      const text = (error as Error)?.stack ?? String(error);
      log('error', 'reframe could not start', { error: text });
      const ending = reframe?.shutdown().catch(() => {});
      await Promise.race([ending, new Promise((resolve) => setTimeout(resolve, 8000))]);
      if (!process.env.REFRAME_E2E) {
        dialog.showErrorBox(
          translate(locale, 'startFailed.title'),
          translate(locale, 'startFailed.message', { error: String(error), log: log.file }),
        );
      }
      app.exit(1);
      return;
    }
    // End-to-end tests drive things a real drag would do (e.g. tearing a tile out at a point).
    if (process.env.REFRAME_E2E) Object.assign(globalThis, { reframe });
  });
}
