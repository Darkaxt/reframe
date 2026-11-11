// Wires state, windows, sessions, hooks, tray and IPC together. Main owns the state: windows send
// intents, main applies them, saves, and pushes the new view to the affected window.
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  app,
  dialog,
  ipcMain,
  Notification,
  powerMonitor,
  screen,
  shell,
  type BrowserWindow,
  type IpcMainEvent,
  type IpcMainInvokeEvent,
  type OpenDialogOptions,
  type Menu,
  type MenuItem,
  type MenuItemConstructorOptions,
} from 'electron';
import {
  addTile,
  applyLayoutAction,
  settleGrid,
  viewModeOf,
  dropSession,
  isCollapsed,
  isDropTarget,
  isLayoutAction,
  isWindowEmpty,
  patchGroup,
  windowsToRestore,
  putNode,
  takeNode,
  windowGroups,
  windowHasSession,
  windowSessions,
} from '@shared/actions';
import { nextWaiting } from '@shared/attention';
import { isGroupColor, nextGroupColor, nextGroupNumber } from '@shared/groups';
import { translate, type Locale, type MessageKey } from '@shared/i18n';
import {
  addToGroup,
  appendNode,
  dissolveGroup as removeGroupFrame,
  findNode,
  groupOf,
  groups,
  groupTile,
  leaves,
  removeFromGroup,
  tile,
  tileKey,
  type DropTarget,
  type GroupNode,
  type LayoutNode,
} from '@shared/layout';
import {
  IPC,
  LIST_PAGE,
  type NewSessionRequest,
  type NewSessionResult,
  type AppListItem,
  type FoundListItem,
  type ForeignCandidate,
  type ForeignDropAnswer,
  type ForeignPointerReport,
  type ClipboardContents,
  type ListItemRef,
  type OpenMode,
  type OpenSessionsRequest,
  type PreferencesPatch,
  type OpenSessionRequest,
  type RunningListItem,
  type SessionList,
  type SessionView,
  type TileSize,
  type TransferRequest,
  type WindowView,
  type ChatBatchResult,
} from '@shared/ipc';
import { EFFORT_LEVELS, PERMISSION_MODES, type PermissionMode } from '@shared/launch';
import { RELEASES_URL } from '@shared/links';
import {
  CLOSE_WINDOW,
  emptyWindow,
  FONT_SIZE,
  SIDEBAR_WIDTH,
  clampSidebarWidth,
  LANGUAGES,
  type CloseWindow,
  type Language,
  type AppState,
  type SessionRecord,
  type WindowState,
} from '@shared/state';
import { listRunning, listRunningAsync, TranscriptIndex } from './claude/discovery';
import { ClaudeWatcher } from './claude/watcher';
import {
  claudeHome,
  isRunningEntry,
  readRegistryEntry,
  readRegistryEntryAsync,
  transcriptModifiedAsync,
  type RegistryEntry,
} from './claude/registry';
import { resolveClaude, type ClaudeBinary } from './claude/resolve';
import { consoleHelper } from './console';
import { ForeignWindowWatcher, type ForeignDrop, type OutsideClaude } from './foreign';
import { activeByTitle, isShell, onlyShells } from './foreign-match';
import { createLogger } from './log';
import { isAlive } from './process';
import { PtyHostClient, type QuitMode } from './ptyhost-client';
import { hostRuntime } from './ptyhost-runtime';
import { SessionManager, type IdentityPatch, type NotifyKind } from './sessions';
import type { LaunchMode } from './claude/args';
import { StateStore } from './state/store';
import { NotificationKeeper, sessionFromActivation, sessionToastXml } from './toast';
import { stopExternalClaude } from './takeover';
import { colorMenu, groupMenu, listMenu, tileMenu } from './menus';
import { parseWslPath, wslWindowsPath, type WslPath } from '@shared/wsl';
import { createTray } from './tray';
import { clipboardImage, readClipboard } from './clipboard';
import { homeRelative } from './paths';
import { Updates } from './updates';
import { autostartEnabled, setAutostart } from './autostart';
import { win32 } from './win32';
import { WindowManager } from './windows';
import { discoverable, SHARE_TIMEOUT_MS, WslDistros } from './wsl/distros';
import { settle, withTimeout } from './wait';
import { cleanUp, dropsDir } from './cleanup';
import { History, type PreparedHistory, type RetireJob } from './history';
import { isArchiveRoot } from './archive-root';
import { cleanAddDirs, cleanPrompt, rememberedDefaults } from '@shared/new-session';
import { refusedProviders } from './launch-config';
import { adoptedClient } from './client-adoption';
import { isProvider, providerName, modelForProviderChange, type Provider } from '@shared/providers';
import { expandedWorkspaceKey, folderWorkspaces, workspaceKey } from '@shared/folder-workspaces';
import { chatKey, parseChatKey } from '@shared/sidebar-chats';
import { showProject } from '@shared/project-view';
import { parseClaudeRateLimits, type ProviderQuota } from '@shared/quota';
import { coordinatorExecutable, readCodexQuota, userHasStatusLine } from './quota';
import { TranscriptTitles } from './claude/titles';
import { sessionLabel } from '@shared/labels';

export interface AppAssets {
  icon: string;
  trayIcon: string;
  trayIcon2x: string;
}

/** What runs outside reframe, as last looked at: per place where Claude Code keeps sessions (Windows' own first). */
interface OutsideScan {
  sources: { source: ClaudeSource; running: RegistryEntry[] }[];
  /** WSL distributions with Claude Code that do not run. */
  stopped: string[];
}

/** Where Claude Code keeps sessions and transcripts: Windows' own, or a WSL distribution's. */
interface ClaudeSource {
  distro?: string;
  /** Claude Code's folder, as Windows reaches it. */
  home: string;
  transcripts: TranscriptIndex;
  /** Whether the claude of a registry entry runs; in WSL asked over the share, so without blocking. */
  alive: (entry: RegistryEntry) => Promise<boolean>;
  /** A folder it names, as Windows reaches it (WSL names Linux paths). */
  windowsPath: (folder: string) => string;
}

const MAX_RECENT_FOLDERS = 8;
/**
 * How often the session list looks again what runs outside reframe without being told (a claude that was killed
 * says nothing), and how often that includes WSL, whose folders cannot be watched: with a list shown, and without.
 */
const LIST_LOOK_MS = 20_000;
const WSL_LOOK_MS = { shown: 20_000, hidden: 60_000 };
/** A list asked for by the user is looked at afresh, WSL included, if it is older than this. */
const LIST_ASKED_MS = 1_000;
/** Changes that come together (a session's status, a layout change) send the list once. */
const LIST_PUSH_MS = 300;
/** How long quitting waits for the sessions to end (each gets Ctrl+C, then its process tree is killed). */
const SHUTDOWN_MS = 15_000;
/** How long after the system said it is going down sessions that end still count as ended by it. */
const SYSTEM_ENDING_MS = 60_000;

export class ReframeApp {
  private history = new History(() => this.state.preferences.archiveRoot);
  private historyBusy = new Set<string>();
  private historyOperations = new Set<Promise<unknown>>();
  private chatBatchRunning = false;
  /** Cancels archive/delete confirmations still open when Reframe quits, so quit never waits on a dialog. */
  private confirmations = new AbortController();
  /** Project groups of the last session list, the ones the sidebar can expand. */
  private listedProjects = new Set<string>();
  private trackHistory<T>(run: () => Promise<T>): Promise<T> {
    if (this.shuttingDown)
      return Promise.reject(Error('Reframe is shutting down; no new history operation was started.'));
    const operation = run();
    this.historyOperations.add(operation);
    void operation.finally(() => this.historyOperations.delete(operation)).catch(() => {});
    return operation;
  }
  private store = new StateStore(path.join(app.getPath('userData'), 'state.json'));
  private state!: AppState;
  private host!: PtyHostClient;
  /**
   * How quitting leaves the sessions: running in the PTY host (the next reframe, e.g. an update, takes them over), or
   * ended ("End sessions and quit"). The end-to-end tests end them unless a test says otherwise.
   */
  private quitMode: QuitMode = process.env.REFRAME_QUIT_ENDS_SESSIONS === '1' ? 'end' : 'keep';
  /** Sessions a PTY host of another reframe still runs: shown as such, and taken over only when asked. */
  private olderHost = new Set<string>();
  private sessions!: SessionManager;
  private windows: WindowManager;
  private refreshTray: () => void = () => {};
  private refreshTrayTooltip: () => void = () => {};
  /** The system starts reframe when the user logs in: read at the start, changed in Settings. */
  private startsWithSystem = false;
  private shuttingDown = false;
  private log = createLogger(path.join(app.getPath('userData'), 'logs'));
  /** Shown session notifications, held so their 'click' listener lives until it can fire. */
  private readonly notifications = new NotificationKeeper<Notification>();
  private claudeHome = claudeHome();
  private transcripts = new TranscriptIndex(this.claudeHome);
  private updates: Updates;
  /** Sessions in WSL folders (Windows only). */
  private wsl = process.platform === 'win32' ? new WslDistros(this.log) : null;

  /** The UI language in use: the one chosen in About, else the system's. */
  private locale: Locale;

  constructor(
    private systemLocale: Locale,
    private assets: AppAssets,
    /** The portable copy (data next to it): it does not update itself. */
    private portable = false,
  ) {
    this.updates = new Updates(this.log, portable, () => this.state.preferences.checkForUpdates);
    this.locale = systemLocale;
    this.windows = new WindowManager(assets.icon, this.log);
  }

  private applyLanguage(): void {
    const chosen = this.state.preferences.language;
    this.locale = chosen === 'system' ? this.systemLocale : chosen;
  }

  /** `hidden`: started by the system, so the windows stay in the tray (sessions resume all the same). */
  async start({ hidden = false }: { hidden?: boolean } = {}): Promise<void> {
    const loaded = this.store.load();
    if (loaded.source === 'newer') return this.refuseNewerState();
    if (loaded.rejected) this.log('error', 'state file could not be used, set aside', loaded.rejected);
    // Project groups are derived from the chats on every list; 0.10.0-claudegpt.4 persisted them as well.
    this.state = { ...loaded.state, folderWorkspaces: undefined };
    if (process.env.REFRAME_HISTORY_PYTHON) {
      try {
        const recovery = await this.history.recover(this.claudeHome);
        const archives = [...(this.state.archives || [])].filter((a) => !recovery.restored.includes(a.id));
        for (const entry of recovery.entries) {
          if (!archives.some((a) => a.archive === entry.archive)) archives.push(entry);
          if (!entry.partial) {
            this.state = this.withoutHistory(entry.id);
          }
        }
        this.state = { ...this.state, archives };
        if (recovery.entries.length || recovery.restored.length) this.store.save(this.state);
        for (const warning of recovery.warnings) this.log('warn', 'archive recovery needs attention', warning);
      } catch (error) {
        this.log('warn', 'archive recovery unavailable; existing state preserved', { error: String(error) });
      }
    }
    if (this.state.windows.length === 0) this.state.windows.push(emptyWindow(crypto.randomUUID()));
    // Empty windows are not brought back (one stays when all are empty), and the first window with something in it
    // is the first window from now on. Up to 0.6.1, closed empty windows were kept and shown again on a second launch.
    const windows = windowsToRestore(this.state.windows);
    const dropped = this.state.windows.length - windows.length;
    if (dropped > 0) {
      this.state = { ...this.state, windows };
      this.store.save(this.state);
    }
    this.applyLanguage();
    this.log('info', 'reframe starting', {
      version: app.getVersion(),
      platform: process.platform,
      build: app.isPackaged ? 'installed' : 'development',
      ...(this.portable ? { portable: app.getPath('userData') } : {}),
      hidden,
      state: loaded.source,
      windows: this.state.windows.length,
      ...(dropped > 0 ? { emptyWindowsDropped: dropped } : {}),
      sessions: Object.keys(this.state.sessions).length,
      claude: this.findClaude()?.source ?? 'not found',
      claudeHome: this.claudeHome,
    });

    this.host = new PtyHostClient({
      dir: path.join(app.getPath('userData'), 'ptyhost'),
      version: app.getVersion(),
      runtime: () => hostRuntime(app.getPath('userData'), this.log),
      log: this.log,
    });
    await this.host.start();
    this.sessions = new SessionManager({
      host: this.host,
      claudeHome: this.claudeHome,
      settingsDir: path.join(app.getPath('userData'), 'hooks'),
      env: process.env,
      resolveClaude: (provider) => this.findClaude(provider),
      log: this.log,
      wsl: this.wsl ?? undefined,
      captureStatusLine: () => !process.env.REFRAME_E2E && !userHasStatusLine(this.claudeHome),
    });
    this.sessions.on('renamed', (id) => this.refreshTitle(id));
    this.sessions.on('quota', (_id, rateLimits) => {
      const windows = parseClaudeRateLimits(rateLimits);
      if (windows.length) this.setQuota({ source: 'claude', windows, at: Date.now() });
    });
    this.sessions.on('changed', (id) => {
      this.pushSession(id);
      this.listChanged();
    });
    this.sessions.on('identity', (id, patch) => {
      this.updateIdentity(id, patch);
      if (patch.claudeSessionId) this.refreshTitle(id);
    });
    this.sessions.on('notify', (id, kind) => {
      // Claude Code names a session (ai-title) after a turn: Stop is when a new one may be there.
      if (kind === 'done') this.refreshTitle(id);
      this.notify(id, kind);
    });
    this.sessions.on('used', (id) => this.sessionUsed(id));
    this.sessions.on('ended', (id) => this.sessionEnded(id));
    // Distributions with Claude Code are remembered, so the list can tell when one is not running.
    this.wsl?.on('found', (distro) => {
      const known = this.state.wslDistros ?? [];
      if (known.some((d) => d.toLowerCase() === distro.toLowerCase())) return;
      this.state = { ...this.state, wslDistros: [...known, distro] };
      this.save();
    });
    // Sessions the host still runs (reframe quit keeping them) go on; those of a host of another reframe are left to
    // it. Neither is an orphan.
    const taken = this.sessions.adopt(this.state.sessions, (id) => !!this.windowOf(id));
    const foreignPids = new Set(this.host.foreign.flatMap((h) => h.sessionPids));
    const hosted = new Set([...this.host.adopted.map((s) => s.pid), ...foreignPids]);
    await this.sessions.cleanupOrphans(Object.values(this.state.sessions), hosted);
    for (const s of Object.values(this.state.sessions)) {
      if (!taken.has(s.id) && s.pid && foreignPids.has(s.pid)) this.olderHost.add(s.id);
    }

    this.registerIpc();
    // Told first when the system goes down, before it closes the claudes, which then must not count as ended.
    win32()?.shutDownFirst();
    this.windows.on('systemEnding', () => this.systemEnding());
    if (process.platform !== 'win32') powerMonitor.on('shutdown', () => this.systemEnding());
    this.startsWithSystem = this.readStartWithSystem();
    this.openWindows(hidden);
    this.watchForeignWindows();
    this.resumeSessions();
    this.createTray();
    this.watchUpdates();
    this.watchToastActivation();
    // A GPU driver reset takes the GPU process (and the terminals' WebGL) down; Electron restarts it, but the
    // terminals draw again only when told.
    app.on('child-process-gone', (_event, details) => {
      if (details.type !== 'GPU') return;
      this.log('warn', 'gpu process gone', { reason: details.reason, exitCode: details.exitCode });
      for (const w of this.state.windows) this.windows.send(w.id, IPC.redrawTerminals, null);
    });
    this.watchList();
    this.save();
    void cleanUp(
      dropsDir(),
      path.join(app.getPath('userData'), 'hooks'),
      new Set(Object.keys(this.state.sessions)),
    ).then((removed) => {
      if (removed.drops || removed.hooks) this.log('info', 'old dropped files and hook settings removed', removed);
    });
  }

  /**
   * The saved state comes from a newer reframe (this one was installed over it, say). Starting from scratch would
   * lose it once anything is saved, so this one says so and quits; the newer version finds everything as it was.
   */
  private refuseNewerState(): void {
    this.log('error', 'state file is from a newer reframe, leaving it alone and quitting', { file: this.store.file });
    // The end-to-end tests cannot click a native dialog away; the log tells them.
    if (!process.env.REFRAME_E2E) {
      dialog.showErrorBox(this.t('state.newerTitle'), this.t('state.newerMessage', { file: this.store.file }));
    }
    app.exit(1);
  }

  /**
   * Shows the windows again, e.g. when reframe is launched a second time: the same ones a start shows, so windows
   * the user closed stay in the tray.
   */
  revealWindows(): void {
    for (const w of this.windowsToShow()) this.windows.show(w.id);
  }

  /** Another build of reframe (installed vs. development) was started and handed over to this one. */
  otherBuildStarted(other: 'installed' | 'development'): void {
    const own = app.isPackaged ? 'installed' : 'development';
    this.log('info', 'another build was started, this one keeps running', { started: other, running: own });
    const browserWindow = this.windows.get(this.state.windows[0]!.id);
    const options = {
      type: 'info' as const,
      title: this.t('otherBuild.title'),
      message: this.t('otherBuild.message', {
        started: this.t(`otherBuild.${other}`),
        running: this.t(`otherBuild.${own}`),
      }),
      detail: this.t('otherBuild.detail'),
    };
    void (browserWindow ? dialog.showMessageBox(browserWindow, options) : dialog.showMessageBox(options));
  }

  /** "End sessions and quit": quits the way reframe did before its sessions outlived it. */
  endSessionsAndQuit(): void {
    this.quitMode = 'end';
    app.quit();
  }

  /**
   * Saves and exits. By default the sessions keep running in the PTY host, and the next reframe (this one again, or
   * an update) takes them over; with "End sessions and quit" they end the way a user would, and resume next time.
   */
  async shutdown(): Promise<void> {
    if (this.shuttingDown) return;
    this.shuttingDown = true;
    this.windows.quitting = true;
    const mode = this.quitMode;
    this.log('info', mode === 'keep' ? 'reframe quitting, sessions keep running' : 'reframe quitting, ending sessions');
    // Open confirmations behave as Cancel; operations already confirmed own durable outcomes, so quit waits for them.
    this.confirmations.abort();
    await Promise.allSettled([...this.historyOperations]);
    // A state that cannot be saved (a failed start) must not keep the sessions and the PTY host from being let go.
    try {
      this.save();
    } catch (error) {
      this.log('error', 'could not save the state on quitting', { error: String(error) });
    }
    // Quitting must not hang on a session that does not go: what is left is ended as an orphan on the next start.
    if (this.sessions && mode === 'end' && !(await settle(this.sessions.stopAll(), SHUTDOWN_MS))) {
      this.log('warn', 'sessions did not end in time, quitting anyway', { sessions: this.sessions.active });
    }
    this.sessions?.dispose();
    await this.host?.stop(mode);
    this.wsl?.release();
    this.listWatcher?.stop();
    clearInterval(this.listLook);
    clearInterval(this.quotaLook);
    clearTimeout(this.listPush);
    this.save();
    this.log('info', 'reframe stopped');
  }

  /**
   * The system is shutting down, restarting or logging off. Sessions it ends now were not ended by the user: they
   * stay open and resume on the next start. Should the system not go down after all (another program objected),
   * the ones that ended meanwhile count as ended a minute later.
   */
  systemEnding(): void {
    if (!this.systemEndingTimer) this.log('info', 'the system is going down, sessions it ends stay open');
    clearTimeout(this.systemEndingTimer);
    this.systemEndingTimer = setTimeout(() => {
      this.systemEndingTimer = undefined;
      for (const id of this.endedWhileSystemEnding) {
        if (!this.sessions.isRunning(id)) this.updateSession(id, { lifecycle: 'ended' });
      }
      this.endedWhileSystemEnding.clear();
    }, SYSTEM_ENDING_MS);
    this.save();
  }
  private systemEndingTimer: NodeJS.Timeout | undefined;
  private endedWhileSystemEnding = new Set<string>();

  /** A session ended by itself (not by reframe): it stays in its tile as ended, unless the system ended it. */
  private sessionEnded(sessionId: string): void {
    if (this.systemEndingTimer) {
      this.endedWhileSystemEnding.add(sessionId);
      this.log('info', 'session ended while the system goes down, stays open', { session: sessionId });
      return;
    }
    this.updateSession(sessionId, { lifecycle: 'ended' });
  }

  // ---------- windows and views ----------

  /** The windows that were open (not closed into the tray); the first one if all were closed. */
  private windowsToShow(): WindowState[] {
    const open = this.state.windows.filter((w) => !w.hidden);
    return open.length > 0 ? open : this.state.windows.slice(0, 1);
  }

  private openWindows(hidden: boolean): void {
    const shown = new Set(this.windowsToShow());
    for (const w of this.state.windows) this.windows.open(w, !hidden && shown.has(w));
    this.windows.on('bounds', (id, bounds, maximized) => {
      this.mutateWindow(id, (w) => ({ ...w, bounds, maximized }), 'soon');
    });
    this.windows.on('fullscreen', (id, fullscreen) => {
      this.mutateWindow(id, (w) => ({ ...w, fullscreen }), 'soon');
      this.push(id); // the window shows how to leave full screen
    });
    this.windows.on('visibility', (id, hidden) => {
      this.mutateWindow(id, (w) => ({ ...w, hidden }), 'soon');
      // A secondary window closed while empty is gone for good, as when its last tile leaves it (after the hide
      // has run its course, not inside it).
      if (hidden && !this.shuttingDown) {
        setImmediate(() => {
          if (!this.shuttingDown && this.findWindow(id)?.hidden) this.closeIfEmpty(id);
        });
      }
      this.refreshTray();
    });
    this.windows.on('closeRequested', (id) => void this.closeWindow(id));
  }

  /**
   * The user closes a window. With sessions in it, reframe asks (unless an answer was remembered): end them, and the
   * window goes (the first one stays, empty, in the tray), or keep them running with the window hidden in the tray.
   * An empty window just closes; while the system goes down nothing is asked.
   */
  private async closeWindow(windowId: string): Promise<void> {
    const win = this.findWindow(windowId);
    if (!win || this.askingToClose.has(windowId)) return;
    const sessions = windowSessions(win);
    let choice: CloseWindow | null = this.state.preferences.closeWindow;
    if (sessions.length === 0 || this.systemEndingTimer) choice = 'keep';
    if (choice === 'ask') {
      this.askingToClose.add(windowId);
      try {
        choice = await this.askToClose(windowId, sessions.length);
      } finally {
        this.askingToClose.delete(windowId);
      }
    }
    // What is in the window now (the question may have been open for a while).
    const now = this.findWindow(windowId);
    if (!now || !choice) return;
    if (choice === 'end') {
      const ending = windowSessions(now);
      this.log('info', 'window closed, its sessions ended', { window: windowId, sessions: ending.length });
      // Ending the last one closes a secondary window for good; the first one stays, empty.
      for (const id of ending) this.endSession(windowId, id);
    }
    this.windows.hide(windowId);
  }
  /** Windows whose closing question is open, so a second click on X does not ask twice. */
  private askingToClose = new Set<string>();

  /** End the window's sessions, keep them in the tray, or (null) leave the window open; the answer may be kept. */
  private async askToClose(windowId: string, count: number): Promise<'end' | 'keep' | null> {
    const browserWindow = this.windows.get(windowId);
    const options = {
      type: 'question' as const,
      buttons: [this.t('closeWindow.end'), this.t('closeWindow.keep'), this.t('dialog.cancel')],
      defaultId: 1,
      cancelId: 2,
      title: this.t('closeWindow.title'),
      message: count === 1 ? this.t('closeWindow.messageOne') : this.t('closeWindow.message', { count }),
      detail: this.t('closeWindow.detail'),
      checkboxLabel: this.t('closeWindow.remember'),
    };
    const { response, checkboxChecked } = browserWindow
      ? await dialog.showMessageBox(browserWindow, options)
      : await dialog.showMessageBox(options);
    const choice = response === 0 ? 'end' : response === 1 ? 'keep' : null;
    if (choice && checkboxChecked) {
      this.state = { ...this.state, preferences: { ...this.state.preferences, closeWindow: choice } };
      this.save();
      this.log('info', 'closing windows with sessions from now on', { choice });
      for (const w of this.state.windows) this.push(w.id); // Settings shows it
    }
    return choice;
  }

  private view(windowId: string): WindowView | null {
    const win = this.state.windows.find((w) => w.id === windowId);
    if (!win) return null;
    const sessions: Record<string, SessionView> = {};
    for (const id of windowSessions(win)) {
      const record = this.state.sessions[id];
      if (record) sessions[id] = this.sessionView(record);
    }
    return {
      windowId,
      archiveRoot: this.state.preferences.archiveRoot || process.env.REFRAME_ARCHIVE_ROOT,
      locale: this.locale,
      fullscreen: !!win.fullscreen,
      language: this.state.preferences.language,
      fontSize: this.state.preferences.fontSize,
      sidebarWidth: clampSidebarWidth(this.state.preferences.sidebarWidth ?? SIDEBAR_WIDTH.normal),
      snapToGrid: this.state.preferences.snapToGrid,
      lookInWsl: this.wsl ? this.state.preferences.lookInWsl : null,
      checkForUpdates: this.updates.checks ? this.state.preferences.checkForUpdates : null,
      closeWindow: this.state.preferences.closeWindow,
      claudeBin: this.state.preferences.claudeBin ?? null,
      defaultModel: this.state.preferences.defaultModel ?? null,
      defaultEffort: this.state.preferences.defaultEffort ?? null,
      defaultPermissionMode: this.state.preferences.defaultPermissionMode ?? null,
      startWithSystem: this.startsWithSystem,
      systemLocale: this.systemLocale,
      version: app.getVersion(),
      sidebarOpen: !!win.sidebarOpen,
      layout: win.layout,
      dock: win.dock,
      activeProject: win.activeProject ?? null,
      viewMode: viewModeOf(win),
      quota: [...this.quota.values()],
      sessions,
    };
  }

  private titles = new TranscriptTitles();
  private titleAsked = new Set<string>();

  /** The user's name for it, else Claude Code's title from its transcript (read once, then on Stop), else the folder. */
  private labelOf(record: SessionRecord): string {
    const title = this.titles.cached(record.claudeSessionId);
    if (title === undefined && !this.titleAsked.has(record.claudeSessionId)) this.refreshTitle(record.id);
    return sessionLabel(record.name, title, record.cwd);
  }

  /** Reads Claude Code's title for the session again (after Stop, or a new Claude session id), and shows a change. */
  private refreshTitle(sessionId: string): void {
    const record = this.state.sessions[sessionId];
    if (!record || this.wslPath(record.cwd)) return;
    const claudeSessionId = record.claudeSessionId;
    this.titleAsked.add(claudeSessionId);
    const before = this.titles.cached(claudeSessionId);
    void this.titles.read(this.claudeHome, claudeSessionId).then((title) => {
      if (title === (before ?? null) || !this.state.sessions[sessionId]) return;
      this.pushSession(sessionId);
      this.listChanged();
    });
  }

  private sessionView(record: SessionRecord): SessionView {
    const runtime = this.sessions.status(record.id);
    const status = runtime?.status ?? (record.lifecycle === 'ended' ? 'ended' : 'starting');
    const where = this.wslPath(record.cwd);
    const info = where && this.wsl!.cached(where.distro);
    return {
      id: record.id,
      provider: record.launch.provider ?? 'chatgpt',
      label: this.labelOf(record),
      cwd: record.cwd,
      // A WSL folder as "Ubuntu ~/shop" (its home is known once the distribution was asked).
      cwdLabel: where
        ? `${where.distro} ${info ? homeRelative(where.path, info.home, false) : where.path}`
        : homeRelative(record.cwd, os.homedir(), process.platform === 'win32'),
      status,
      lifecycle: record.lifecycle,
      error: runtime?.error,
      ...(where ? { wsl: { distro: where.distro, mountRoot: info?.mountRoot ?? '/mnt/' } } : {}),
    };
  }

  /** Where a folder is in WSL, if it is in one. */
  private wslPath(folder: string): WslPath | null {
    return this.wsl ? parseWslPath(folder, this.wsl.share) : null;
  }

  private push(windowId: string): void {
    const view = this.view(windowId);
    if (view) this.windows.send(windowId, IPC.view, view);
    this.updateAttention();
  }

  /** How many sessions need the user, per window: shown as a badge on its taskbar button, and in all in the tray. */
  private updateAttention(): void {
    if (!this.sessions) return;
    let total = 0;
    for (const w of this.state.windows) {
      const count = windowSessions(w).filter((id) => this.sessions.status(id)?.status === 'waiting').length;
      total += count;
      if ((this.attention.get(w.id) ?? 0) === count) continue;
      this.attention.set(w.id, count);
      this.windows.badge(w.id, count, count > 0 ? this.waitingText(count) : '');
    }
    if (total === this.waitingTotal) return;
    this.waitingTotal = total;
    this.refreshTrayTooltip();
  }
  private attention = new Map<string, number>();
  private waitingTotal = 0;

  private waitingText(count: number): string {
    return count === 1 ? this.t('title.waitingOne') : this.t('title.waiting', { n: count });
  }

  /** Brings up the next session that needs the user: in this window first (after `after`), then in the others. */
  private focusNextWaiting(windowId: string, after: string | null): void {
    const here = this.findWindow(windowId);
    const windows = here ? [here, ...this.state.windows.filter((w) => w !== here)] : this.state.windows;
    const order = windows.flatMap(windowSessions);
    const waiting = new Set(order.filter((id) => this.sessions.status(id)?.status === 'waiting'));
    const next = nextWaiting(order, waiting, after);
    if (next) this.focusSession(next);
  }

  private pushSession(sessionId: string): void {
    const win = this.windowOf(sessionId);
    if (win) this.push(win.id);
  }

  private windowOf(sessionId: string): WindowState | undefined {
    return this.state.windows.find((w) => windowHasSession(w, sessionId));
  }

  private findWindow(windowId: string): WindowState | undefined {
    return this.state.windows.find((w) => w.id === windowId);
  }

  /** Moves a tile or group from another window into `toId`; without a target it goes to the end. */
  private transferNode(toId: string, { key, fromWindowId, target }: TransferRequest): void {
    const from = this.findWindow(fromWindowId);
    if (!from || !this.findWindow(toId) || from.id === toId) return;
    const { win: rest, node } = takeNode(from, key);
    if (!node) return;
    this.state = {
      ...this.state,
      windows: this.state.windows.map((w) => {
        if (w.id === from.id) return rest;
        if (w.id === toId) return putNode(w, node, target);
        return w;
      }),
    };
    this.save();
    this.log('info', 'moved to another window', { node: key, from: from.id, to: toId });
    this.push(from.id);
    this.push(toId);
    this.closeIfEmpty(from.id);
    this.windows.show(toId);
    this.windows.send(toId, IPC.focusTile, leaves(node)[0]);
    this.refreshTray();
  }

  /**
   * A new, empty window, a little offset from `nearId`'s window. It stays until something leaves it empty again
   * (like a window a tile was torn out into) and comes back after a restart like any other.
   */
  newWindow(nearId?: string): string {
    const near =
      this.windows.get(nearId ?? '')?.getBounds() ?? this.windows.get(this.state.windows[0]!.id)?.getBounds();
    const win: WindowState = {
      ...emptyWindow(crypto.randomUUID()),
      bounds: near ? { ...near, x: near.x + 40, y: near.y + 40 } : null,
    };
    this.state = { ...this.state, windows: [...this.state.windows, win] };
    this.save();
    this.log('info', 'new window', { window: win.id });
    this.windows.open(win);
    this.refreshTray();
    return win.id;
  }

  /** Gives a tile or group a window of its own at a screen point, unless the point is over one of our windows. */
  private tearOut(fromId: string, key: string, point: { x: number; y: number }, size?: TileSize): boolean {
    const from = this.findWindow(fromId);
    if (!from || this.windows.windowAt(point)) return false;
    const { win: rest, node } = takeNode(from, key);
    if (!node) return false;
    const clamp = (v: number, min: number, max: number) => Math.round(Math.min(max, Math.max(min, v)));
    const win: WindowState = {
      ...emptyWindow(crypto.randomUUID()),
      // Room for the window frame, toolbar and padding around the tile; the title bar lands under the pointer.
      bounds: {
        x: Math.round(point.x - 60),
        y: Math.round(point.y - 16),
        width: clamp((size?.width ?? 800) + 24, 480, 2400),
        height: clamp((size?.height ?? 500) + 90, 320, 1600),
      },
      layout: node,
    };
    this.state = {
      ...this.state,
      windows: [...this.state.windows.map((w) => (w.id === from.id ? rest : w)), win],
    };
    this.save();
    this.log('info', 'torn out into a new window', { node: key, from: from.id, window: win.id });
    this.push(from.id);
    this.windows.open(win);
    this.closeIfEmpty(from.id);
    this.refreshTray();
    return true;
  }

  /** Test hook (REFRAME_E2E): tears a tile or group out at a given point instead of the pointer position. */
  tearOutNode(key: string, point: { x: number; y: number }): boolean {
    const from = this.state.windows.find((w) => findNode(w.layout, key));
    return from ? this.tearOut(from.id, key, point) : false;
  }

  /** Test hook (REFRAME_E2E): tears a session's tile out at a given point. */
  tearOutSession(sessionId: string, point: { x: number; y: number }): boolean {
    return this.tearOutNode(tileKey(sessionId), point);
  }

  /** Secondary windows go away once nothing is left in them; the first window always stays. */
  private closeIfEmpty(windowId: string): void {
    const win = this.findWindow(windowId);
    if (!win || win.id === this.state.windows[0]?.id || !isWindowEmpty(win)) return;
    this.state = { ...this.state, windows: this.state.windows.filter((w) => w.id !== windowId) };
    this.save();
    this.log('info', 'empty window closed', { window: windowId });
    this.windows.destroy(windowId);
    this.refreshTray();
  }

  private mutateWindow(
    windowId: string,
    change: (w: WindowState) => WindowState,
    persist: 'now' | 'soon' = 'now',
  ): void {
    this.state = {
      ...this.state,
      windows: this.state.windows.map((w) => (w.id === windowId ? change(w) : w)),
    };
    if (persist === 'now') {
      this.save();
      this.push(windowId);
    } else {
      this.store.saveSoon(this.state);
    }
  }

  /** Each window's grid key as last saved (see settleGrid). */
  private gridKeys = new Map<string, string>();

  private save(): void {
    // Tiled windows are kept in the even grid as their tiles come and go.
    this.state = {
      ...this.state,
      windows: this.state.windows.map((w) => {
        const { win, key } = settleGrid(w, this.gridKeys.get(w.id));
        this.gridKeys.set(w.id, key);
        return win;
      }),
    };
    this.store.save(this.state);
    this.listChanged();
  }

  // ---------- sessions ----------

  private resumeSessions(): void {
    for (const record of Object.values(this.state.sessions)) {
      if (record.lifecycle !== 'open') continue;
      // On the start nobody can be asked: one whose conversation runs elsewhere now waits for "Take over".
      if (this.windowOf(record.id) && this.olderHost.has(record.id)) this.sessions.refuse(record.id, 'olderHost');
      else if (this.windowOf(record.id)) void this.startOrTakeOver(record.id, 'resume', null);
      else this.updateSession(record.id, { lifecycle: 'ended' }, false);
    }
  }

  private updateSession(sessionId: string, patch: Partial<SessionRecord>, persist = true): void {
    const record = this.state.sessions[sessionId];
    if (!record) return;
    this.state = { ...this.state, sessions: { ...this.state.sessions, [sessionId]: { ...record, ...patch } } };
    if (persist) this.save();
    this.pushSession(sessionId);
  }

  private updateIdentity(sessionId: string, patch: IdentityPatch): void {
    const record = this.state.sessions[sessionId];
    if (!record) return;
    const next: IdentityPatch = { ...patch };
    // A new claude session id (after /clear) starts unused unless the same update says otherwise.
    if (patch.claudeSessionId && patch.claudeSessionId !== record.claudeSessionId && patch.used === undefined) {
      next.used = false;
    }
    // A worktree session whose folder moved runs in the worktree Claude Code made for it.
    const intoWorktree =
      !!record.launch.worktree && !record.launch.worktreeCreated && !!patch.cwd && !samePath(patch.cwd, record.cwd);
    const changed = intoWorktree || (Object.keys(next) as (keyof IdentityPatch)[]).some((k) => next[k] !== record[k]);
    if (!changed) return;
    this.updateSession(sessionId, {
      ...next,
      ...(intoWorktree ? { launch: { ...record.launch, worktreeCreated: true } } : {}),
    });
  }

  /** A session was used (see countsAsUse): the list shows it as just used, and puts it first. */
  private sessionUsed(sessionId: string): void {
    const record = this.state.sessions[sessionId];
    if (!record) return;
    const next = { ...record, lastActiveAt: Date.now() };
    this.state = { ...this.state, sessions: { ...this.state.sessions, [sessionId]: next } };
    // Every prompt and answer: written with the next save, or in a moment.
    this.store.saveSoon(this.state);
    this.listChanged();
  }

  private async createSession(windowId: string, request: NewSessionRequest): Promise<NewSessionResult> {
    const cwd = request.cwd.trim();
    if (!(await isFolder(cwd)) && !(await this.offerToCreateFolder(windowId, cwd))) {
      return { ok: false, error: 'folderMissing' };
    }
    const where = this.wslPath(cwd);
    if (where ? !(await this.wsl!.info(where.distro))?.claude : !this.findClaude()) {
      return { ok: false, error: 'claudeNotFound', ...(where ? { distro: where.distro } : {}) };
    }
    const now = Date.now();
    const record: SessionRecord = {
      id: crypto.randomUUID(),
      claudeSessionId: crypto.randomUUID(),
      cwd,
      name: request.name?.trim() || undefined,
      launch: {
        provider: isProvider(request.provider) ? request.provider : 'chatgpt',
        model: request.model?.trim() || undefined,
        effort:
          request.effort === ''
            ? undefined
            : EFFORT_LEVELS.find((level) => level === (request.effort ?? this.state.preferences.defaultEffort)),
        permissionMode: PERMISSION_MODES.find((m) => m === request.permissionMode),
        worktree: request.worktree || undefined,
        worktreeName: (request.worktree && request.worktreeName?.trim()) || undefined,
        addDirs: cleanAddDirs(request.addDirs),
        initialPrompt: cleanPrompt(request.initialPrompt),
      },
      lifecycle: 'open',
      createdAt: now,
      lastActiveAt: now,
    };
    const key = (p: string) => (process.platform === 'win32' ? p.toLowerCase() : p);
    this.state = {
      ...this.state,
      sessions: { ...this.state.sessions, [record.id]: record },
      recentFolders: [cwd, ...this.state.recentFolders.filter((f) => key(f) !== key(cwd))].slice(0, MAX_RECENT_FOLDERS),
      preferences: {
        ...this.state.preferences,
        providerDefaults: {
          ...this.state.preferences.providerDefaults,
          [record.launch.provider ?? 'chatgpt']: rememberedDefaults(record.launch),
        },
      },
    };
    this.mutateWindow(windowId, (w) => addTile(w, record.id, request.target));
    this.showProjectOf(windowId, record.id);
    this.sessions.start(record, 'new');
    return { ok: true };
  }

  private restartSession(windowId: string, sessionId: string, fresh: boolean): void {
    const record = this.state.sessions[sessionId];
    if (record && this.historyBusy.has(record.claudeSessionId)) return;
    if (!record || this.sessions.isRunning(sessionId)) return;
    const next: SessionRecord = {
      ...record,
      // A fresh start has had no prompt yet, whatever the session before it had; it is a new conversation, as new
      // as a new session. Resumed, it was last used when it was last used.
      ...(fresh ? { claudeSessionId: crypto.randomUUID(), used: false, lastActiveAt: Date.now() } : {}),
      lifecycle: 'open',
    };
    this.state = { ...this.state, sessions: { ...this.state.sessions, [sessionId]: next } };
    this.save();
    void this.startOrTakeOver(sessionId, fresh ? 'new' : 'resume', windowId);
    this.pushSession(sessionId);
  }

  /**
   * Starts a session (resuming it), unless its conversation runs outside reframe already: someone resumed it in a
   * terminal of their own, or in WSL. Then it is taken over (ended there, continued here) after asking in
   * `windowId`'s window. Without a window to ask in (reframe starting), or when the answer is no, it is not started,
   * and its tile says why and offers to take it over.
   */
  private async startOrTakeOver(sessionId: string, mode: LaunchMode, windowId: string | null): Promise<void> {
    if (this.historyBusy.has(this.state.sessions[sessionId]?.claudeSessionId ?? '')) return;
    if (this.startsPending.has(sessionId) || this.sessions.keepsRunning(sessionId)) return;
    const record = this.state.sessions[sessionId];
    if (!record) return;
    this.startsPending.add(sessionId);
    try {
      const outside = mode === 'resume' ? await this.runningElsewhere(record) : null;
      if (outside) {
        const asked = windowId !== null && (await this.confirmTakeover(windowId, [this.sessionView(record).label]));
        this.log('info', 'session runs elsewhere already', { session: sessionId, pid: outside.entry.pid, asked });
        if (!asked) return this.sessions.refuse(sessionId, 'runningElsewhere');
        if (!(await this.stopOutside(outside.entry.pid, outside.source.distro))) {
          await this.showError(windowId!, this.t('takeover.failed'));
          return this.sessions.refuse(sessionId, 'runningElsewhere');
        }
        // Resumed in another folder, the conversation went on there; so it does here. It was used there, too.
        const usedThere = await withTimeout(
          transcriptModifiedAsync(outside.source.home, record.claudeSessionId),
          SHARE_TIMEOUT_MS,
          null,
        );
        const latest = this.state.sessions[sessionId];
        this.updateSession(sessionId, {
          ...(outside.entry.cwd ? { cwd: outside.source.windowsPath(outside.entry.cwd) } : {}),
          ...(latest && usedThere && usedThere > latest.lastActiveAt ? { lastActiveAt: usedThere } : {}),
        });
      }
    } catch (error) {
      this.log('error', 'could not tell whether a session runs elsewhere, starting it', { error: String(error) });
    } finally {
      this.startsPending.delete(sessionId);
    }
    const current = this.state.sessions[sessionId];
    // Removed, or its tile closed, while that was asked; or started meanwhile.
    if (!current || !this.windowOf(sessionId) || this.sessions.keepsRunning(sessionId)) return;
    this.sessions.start(current, mode);
  }
  /** Sessions on their way through startOrTakeOver, so a second click does not start one twice. */
  private startsPending = new Set<string>();

  /** The claude running a session's conversation outside reframe, if any; in WSL only if its distribution runs. */
  private async runningElsewhere(
    record: SessionRecord,
  ): Promise<{ entry: RegistryEntry; source: ClaudeSource } | null> {
    const where = this.wslPath(record.cwd);
    let source: ClaudeSource | null = this.ownSource();
    if (where) {
      // Asking a distribution that does not run would start it, and nothing runs there.
      const running = await this.wsl!.running();
      if (!running.some((d) => d.toLowerCase() === where.distro.toLowerCase())) return null;
      source = await this.wslSource(where.distro);
      if (!source) return null;
    }
    // Its own last process is no other terminal's: an orphan of reframe's, ended before it resumes.
    const last = where ? record.linuxPid : record.pid;
    const entry = (await this.externalRunning(source)).find(
      (e) => e.sessionId === record.claudeSessionId && e.pid !== last,
    );
    return entry ? { entry, source } : null;
  }

  /** Ends a claude running outside reframe the way its user would; whether it went. */
  private async stopOutside(pid: number, distro?: string): Promise<boolean> {
    // One in WSL is a Linux process: it gets the interrupt as a signal, as from Ctrl+C in its terminal.
    const result = distro ? await this.wsl!.stopClaude(distro, pid) : await stopExternalClaude(pid, this.log);
    this.log(result === 'failed' ? 'warn' : 'info', 'other terminal stopped', { pid, result });
    return result !== 'failed';
  }

  private endSession(windowId: string, sessionId: string): void {
    if (!this.state.sessions[sessionId]) return;
    this.log('info', 'session ended by the user', { session: sessionId });
    void this.sessions.stop(sessionId);
    this.state = {
      ...this.state,
      sessions: { ...this.state.sessions, [sessionId]: { ...this.state.sessions[sessionId]!, lifecycle: 'ended' } },
    };
    this.mutateWindow(windowId, (w) => dropSession(w, sessionId));
    this.closeIfEmpty(windowId);
  }

  // ---------- groups (actions of the native menus; public so end-to-end tests can drive them) ----------

  private windowOfGroup(groupId: string): WindowState | undefined {
    return this.state.windows.find((w) => windowGroups(w).some((g) => g.id === groupId));
  }

  private findGroup(groupId: string): GroupNode | undefined {
    return this.state.windows.flatMap(windowGroups).find((g) => g.id === groupId);
  }

  /** Name ("Group n") and color for a new group: the next free ones. */
  private newGroupLook(): { name: string; color: string } {
    const existing = this.state.windows.flatMap(windowGroups);
    const nameFor = (n: number) => this.t('group.defaultName', { n });
    const number = nextGroupNumber(
      existing.map((g) => g.name),
      nameFor,
    );
    return { name: nameFor(number), color: nextGroupColor(existing.map((g) => g.color)) };
  }

  /** Frames a tile as a new group, named "Group n" with the next free color. */
  createGroup(sessionId: string): void {
    const win = this.windowOf(sessionId);
    if (!win) return;
    const group = this.newGroupLook();
    this.mutateWindow(win.id, (w) => ({ ...w, layout: groupTile(w.layout, sessionId, group) }));
    this.log('info', 'group created', { session: sessionId, ...group });
  }

  addSessionToGroup(sessionId: string, groupId: string): void {
    const win = this.windowOf(sessionId);
    if (win && this.windowOfGroup(groupId)?.id === win.id) {
      this.mutateWindow(win.id, (w) => ({ ...w, layout: addToGroup(w.layout, sessionId, groupId) }));
    }
  }

  removeSessionFromGroup(sessionId: string): void {
    const win = this.windowOf(sessionId);
    if (win) this.mutateWindow(win.id, (w) => ({ ...w, layout: removeFromGroup(w.layout, sessionId) }));
  }

  dissolveGroup(groupId: string): void {
    const win = this.windowOfGroup(groupId);
    if (win) this.mutateWindow(win.id, (w) => ({ ...w, layout: removeGroupFrame(w.layout, groupId) }));
  }

  recolorGroup(groupId: string, color: string): void {
    const win = this.windowOfGroup(groupId);
    if (win && isGroupColor(color)) this.mutateWindow(win.id, (w) => patchGroup(w, groupId, { color }));
  }

  /** Ends every session of the group; like a tile's ×, they stay in the session list. */
  endGroup(groupId: string): void {
    const win = this.windowOfGroup(groupId);
    const group = this.findGroup(groupId);
    if (!win || !group) return;
    this.log('info', 'ending all sessions of a group', { group: groupId, sessions: leaves(group).length });
    for (const sessionId of leaves(group)) this.endSession(win.id, sessionId);
  }

  private showTileMenu(windowId: string, sessionId: string): void {
    const win = this.findWindow(windowId);
    const browserWindow = this.windows.get(windowId);
    if (!win || !browserWindow || !windowHasSession(win, sessionId)) return;
    const group = groupOf(win.layout, tileKey(sessionId));
    const menu = tileMenu(this.t, {
      chatActions: this.chatActions(windowId, { kind: 'app', sessionId }),
      rename: () => this.windows.send(windowId, IPC.startRenameSession, { sessionId, place: 'tile' }),
      group,
      otherGroups: groups(win.layout).filter((g) => g.id !== group?.id),
      newGroup: () => this.createGroup(sessionId),
      addToGroup: (groupId) => this.addSessionToGroup(sessionId, groupId),
      removeFromGroup: () => this.removeSessionFromGroup(sessionId),
    });
    this.popup(menu, browserWindow);
  }

  /** The list's menu for the selected entries. */
  private showListMenu(windowId: string, items: ListItemRef[]): void {
    const browserWindow = this.windows.get(windowId);
    if (!browserWindow || items.length === 0) return;
    const open = (mode: OpenMode) => () =>
      void this.openSessions(windowId, items, mode, null).catch((error: unknown) =>
        this.log('error', 'opening sessions from the list failed', { error: String(error) }),
      );
    const single = items.length === 1 && items[0]!.kind === 'app' ? items[0]!.sessionId : null;
    const menu = listMenu(this.t, {
      chatActions: items.length === 1 ? this.chatActions(windowId, items[0]!) : this.batchChatActions(windowId, items),
      rename: single
        ? () => this.windows.send(windowId, IPC.startRenameSession, { sessionId: single, place: 'list' })
        : undefined,
      count: items.length,
      open: open('open'),
      group: open('group'),
      groupInNewWindow: open('groupInNewWindow'),
    });
    this.popup(menu, browserWindow);
  }

  /** Shows a context menu; end-to-end tests cannot click native menus, so there it is only kept for clickMenuItem. */
  private popup(menu: Menu, browserWindow: BrowserWindow): void {
    this.lastMenu = menu;
    if (!process.env.REFRAME_E2E) menu.popup({ window: browserWindow });
  }
  private lastMenu: Menu | null = null;

  private editChatModels(windowId: string, items: ListItemRef[]): void {
    const records = items.flatMap((item) => {
      const record =
        item.kind === 'app'
          ? this.state.sessions[item.sessionId]
          : Object.values(this.state.sessions).find((entry) => entry.claudeSessionId === item.claudeSessionId);
      return record ? [record] : [];
    });
    const provider = records[0]?.launch.provider ?? 'chatgpt';
    const same = records.every((record) => (record.launch.provider ?? 'chatgpt') === provider);
    this.windows.send(windowId, IPC.editChatModels, {
      items,
      provider: same ? provider : 'chatgpt',
      model:
        same && records.every((record) => record.launch.model === records[0]?.launch.model)
          ? records[0]?.launch.model
          : undefined,
    });
  }

  private batchChatActions(windowId: string, items: ListItemRef[]): MenuItemConstructorOptions[] {
    const run = (operation: ChatBatchResult['operation'], provider?: Provider) => {
      void this.trackHistory(() => this.runChatBatch(windowId, items, operation, provider)).catch((error) =>
        this.showError(windowId, String(error)),
      );
    };
    return [
      { type: 'separator' },
      {
        label: 'Model provider',
        enabled: !this.chatBatchRunning,
        submenu: (['chatgpt', 'anthropic'] as Provider[]).map((provider) => ({
          label: providerName(provider),
          click: () => run('provider', provider),
        })),
      },
      { label: 'Change model…', enabled: !this.chatBatchRunning, click: () => this.editChatModels(windowId, items) },
      { label: 'Archive selected…', enabled: !this.chatBatchRunning, click: () => run('archive') },
      { label: 'Delete selected…', enabled: !this.chatBatchRunning, click: () => run('delete') },
    ];
  }

  /** One confirmation for the selection; archive/delete run as one prepare and one retire process. */
  private async runChatBatch(
    windowId: string,
    items: ListItemRef[],
    operation: ChatBatchResult['operation'],
    provider?: Provider,
    model?: string,
  ): Promise<void> {
    if (this.chatBatchRunning || this.shuttingDown) throw Error('Another batch or shutdown is already running.');
    this.chatBatchRunning = true;
    const result: ChatBatchResult = { operation, running: true, results: [] };
    const publish = () => this.windows.send(windowId, IPC.chatBatchResult, { ...result, results: [...result.results] });
    const seen = new Set<string>();
    const targets: { item: ListItemRef; record: SessionRecord; key: string }[] = [];
    try {
      publish();
      for (const item of items) {
        const existing = item.kind === 'app' ? this.state.sessions[item.sessionId] : undefined;
        const id = item.kind === 'app' ? (existing?.claudeSessionId ?? item.sessionId) : item.claudeSessionId;
        const distro = item.kind === 'app' ? existing && this.wslPath(existing.cwd)?.distro : item.distro;
        const key = chatKey(id, distro);
        if (seen.has(key)) continue;
        seen.add(key);
        try {
          const record = await this.inactiveChat(item);
          if (this.historyBusy.has(record.claudeSessionId)) throw Error('A history operation is already running.');
          targets.push({ item, record, key });
        } catch (error) {
          result.results.push({ id: key, label: existing?.name || id, status: 'skipped', detail: String(error) });
        }
      }
      if (operation === 'archive' || operation === 'delete') {
        if (targets.length)
          for (const outcome of await this.retireChats(windowId, targets, operation)) {
            result.results.push(outcome);
            publish();
          }
        return;
      }
      for (const { item, record, key } of targets) {
        const entry: ChatBatchResult['results'][number] = {
          id: key,
          label: this.labelOf(record),
          status: 'completed',
        };
        try {
          if (!provider) throw Error('Choose an explicit provider.');
          await this.selectProvider(
            windowId,
            item,
            provider,
            operation === 'model' ? model?.trim() || null : undefined,
          );
          result.results.push(entry);
        } catch (error) {
          result.results.push({ ...entry, status: 'skipped', detail: String(error) });
        }
        publish();
      }
    } finally {
      result.running = false;
      this.chatBatchRunning = false;
      publish();
      this.listChanged();
    }
  }

  /** Exact inactive histories can change route or leave the live history store. */
  private chatActions(windowId: string, item: ListItemRef): MenuItemConstructorOptions[] {
    const record = item.kind === 'app' ? this.state.sessions[item.sessionId] : undefined;
    if (item.kind === 'app' && !record) return [];
    const identity = item.kind === 'app' ? record!.claudeSessionId : item.claudeSessionId;
    const distro = item.kind === 'app' ? this.wslPath(record!.cwd)?.distro : item.distro;
    const key = chatKey(identity, distro);
    const pinned = this.state.pinnedChats?.includes(key) || false;
    const pinActions: MenuItemConstructorOptions[] = [
      { type: 'separator' },
      {
        label: pinned ? 'Unpin chat' : 'Pin chat',
        click: () => {
          this.state = {
            ...this.state,
            pinnedChats: pinned
              ? (this.state.pinnedChats || []).filter((entry) => entry !== key)
              : [...new Set([...(this.state.pinnedChats || []), key])],
          };
          this.save();
          this.listChanged();
        },
      },
    ];
    if (item.kind === 'running') return pinActions;
    const tid = record?.claudeSessionId || (item.kind === 'found' ? item.claudeSessionId : '');
    if (!tid || (item.kind === 'found' && item.distro) || (record && this.wslPath(record.cwd))) return pinActions;
    const inactive =
      (!record || (!this.sessions.isRunning(record.id) && record.lifecycle === 'ended')) && !this.historyBusy.has(tid);
    const run = (operation: 'archive' | 'delete') =>
      void this.trackHistory(() => this.retireChat(windowId, item, operation)).catch((error) =>
        this.showError(windowId, String(error)),
      );
    return [
      ...pinActions,
      { type: 'separator' },
      {
        label: inactive ? 'Model provider' : 'Model provider (end chat first)',
        submenu: (['chatgpt', 'anthropic'] as Provider[]).map((provider) => ({
          label: providerName(provider),
          type: 'radio',
          checked: (record?.launch.provider ?? (record ? 'chatgpt' : undefined)) === provider,
          enabled: inactive,
          click: () =>
            void this.trackHistory(() => this.selectProvider(windowId, item, provider)).catch((error) =>
              this.showError(windowId, String(error)),
            ),
        })),
      },
      {
        label: 'Change model…',
        enabled: inactive && !this.chatBatchRunning,
        click: () => this.editChatModels(windowId, [item]),
      },
      {
        label: inactive ? 'Archive chat…' : 'Archive chat (end chat first)',
        enabled: inactive,
        click: () => run('archive'),
      },
      {
        label: inactive ? 'Delete chat…' : 'Delete chat (end chat first)',
        enabled: inactive,
        click: () => run('delete'),
      },
    ];
  }

  private async inactiveChat(item: ListItemRef): Promise<SessionRecord> {
    if (item.kind === 'running' || (item.kind === 'found' && item.distro))
      throw Error('End the native Windows chat before changing it.');
    const existing =
      item.kind === 'app'
        ? this.state.sessions[item.sessionId]
        : Object.values(this.state.sessions).find((r) => r.claudeSessionId === item.claudeSessionId);
    if (existing) {
      if (
        existing.lifecycle !== 'ended' ||
        this.sessions.isRunning(existing.id) ||
        this.startsPending.has(existing.id) ||
        this.wslPath(existing.cwd)
      )
        throw Error('End this chat before changing its provider or history.');
      return existing;
    }
    if (item.kind !== 'found') throw Error('Chat no longer exists.');
    const info = await this.transcripts.find(item.claudeSessionId);
    if (!info?.cwd) throw Error('Chat transcript/folder could not be resolved.');
    const record = { ...(await this.adopted(item.claudeSessionId, info.cwd, info.title)), lifecycle: 'ended' as const };
    this.assertChatStillInactive(record);
    return record;
  }

  /** Recheck under the history lock after asynchronous discovery, before any native operation. */
  private assertChatStillInactive(record: SessionRecord): void {
    for (const current of Object.values(this.state.sessions).filter(
      (entry) => entry.claudeSessionId === record.claudeSessionId,
    )) {
      if (
        current.lifecycle !== 'ended' ||
        this.sessions.isRunning(current.id) ||
        this.startsPending.has(current.id) ||
        this.wslPath(current.cwd)
      )
        throw Error('This chat started or changed ownership; unchanged.');
    }
  }

  private async selectProvider(
    _windowId: string,
    item: ListItemRef,
    provider: Provider,
    model?: string | null,
  ): Promise<void> {
    const record = await this.inactiveChat(item);
    if (this.historyBusy.has(record.claudeSessionId)) throw Error('A history operation is already running.');
    this.historyBusy.add(record.claudeSessionId);
    try {
      // Only Reframe's record changes (the route of the next start): no native process census is needed.
      this.assertChatStillInactive(record);
      const next = {
        ...record,
        launch: {
          ...record.launch,
          provider,
          model:
            model === undefined
              ? modelForProviderChange(record.launch.provider, provider, record.launch.model)
              : model || undefined,
        },
      };
      this.state = { ...this.state, sessions: { ...this.state.sessions, [next.id]: next } };
      this.save();
      this.pushSession(next.id);
      this.listChanged();
    } finally {
      this.historyBusy.delete(record.claudeSessionId);
    }
  }

  private async retireChat(windowId: string, item: ListItemRef, operation: 'archive' | 'delete'): Promise<void> {
    const record = await this.inactiveChat(item);
    const [outcome] = await this.retireChats(windowId, [{ record, key: record.claudeSessionId }], operation);
    if (outcome?.status === 'failed') throw Error(outcome.detail);
  }

  /**
   * Archives or deletes inactive chats. Reframe's own state is checked before the confirmation and again right
   * before the change; the backend then takes one live-process census for the whole selection and refuses any chat
   * that runs or whose files changed since planning. An archive is verified once, after it is written and before
   * the chat's files are removed. Two Python processes in all, however many chats.
   */
  private async retireChats(
    windowId: string,
    targets: { record: SessionRecord; key: string }[],
    operation: 'archive' | 'delete',
  ): Promise<ChatBatchResult['results']> {
    const results: ChatBatchResult['results'] = [];
    const label = (record: SessionRecord) => this.labelOf(record);
    const skip = (record: SessionRecord, key: string, detail: string, status: 'skipped' | 'failed' = 'skipped') =>
      results.push({ id: key, label: label(record), status, detail });
    const locked = targets.filter(({ record, key }) => {
      if (!this.historyBusy.has(record.claudeSessionId)) {
        this.historyBusy.add(record.claudeSessionId);
        return true;
      }
      skip(record, key, 'A history operation is already running.');
      return false;
    });
    try {
      if (!locked.length) return results;
      // A Reframe entry whose Claude chat never wrote any history (for example a chat closed before its first
      // message) has nothing for the helper to plan. Delete removes only Reframe's entry; archive has nothing to keep.
      const recordOnly: typeof locked = [];
      const withHistory: typeof locked = [];
      for (const target of locked) {
        if (this.hasClaudeHistory(target.record.claudeSessionId)) withHistory.push(target);
        else if (operation === 'delete') recordOnly.push(target);
        else
          skip(
            target.record,
            target.key,
            'This chat has no Claude history to archive; Delete removes its entry.',
            'failed',
          );
      }
      const prepared = new Map<string, PreparedHistory>();
      const ids = withHistory.map(({ record }) => record.claudeSessionId);
      for (const outcome of ids.length ? await this.history.prepare(this.claudeHome, ids) : []) {
        if (outcome.ok) prepared.set(outcome.id, outcome);
        else {
          const target = withHistory.find(({ record }) => record.claudeSessionId === outcome.id);
          if (target) skip(target.record, target.key, outcome.error, 'failed');
        }
      }
      const ready = withHistory.filter(({ record }) => prepared.has(record.claudeSessionId));
      const confirmed = [...ready, ...recordOnly];
      if (!confirmed.length) return results;
      const win = this.windows.get(windowId);
      if (!win) throw Error('The requesting window closed; chats preserved.');
      const plan = (record: SessionRecord) => prepared.get(record.claudeSessionId)!;
      const files = ready.reduce((sum, { record }) => sum + plan(record).files, 0);
      const bytes = ready.reduce((sum, { record }) => sum + plan(record).bytes, 0);
      const verb = operation === 'archive' ? 'Archive' : 'Permanently delete';
      const answer = await dialog.showMessageBox(win, {
        signal: this.confirmations.signal,
        type: 'warning',
        defaultId: 1,
        cancelId: 1,
        buttons: [operation === 'archive' ? 'Archive' : 'Delete permanently', 'Cancel'],
        message:
          confirmed.length === 1
            ? `${verb} “${label(confirmed[0]!.record)}”?`
            : `${verb} ${confirmed.length} selected chats?`,
        detail:
          confirmed
            .map(
              ({ record }) =>
                `${label(record)} · ${record.claudeSessionId}\n${record.cwd}` +
                (prepared.has(record.claudeSessionId) ? '' : '\nNo Claude history: only the Reframe entry is removed.'),
            )
            .join('\n\n') +
          (ready.length ? `\n\n${files} exact chat files (${bytes.toLocaleString()} bytes).\n` : '\n\n') +
          (operation === 'archive'
            ? 'Each chat gets a verified restorable ZIP before its live files are removed. Restore remains available in Archived chats.'
            : 'Transcripts, owned session/subagent files and matching history entries are permanently removed. This cannot be undone.') +
          '\nProject files and other chats are preserved. A chat that is running by then is skipped.',
      });
      if (answer.response !== 0 || this.shuttingDown) {
        for (const { record, key } of confirmed) skip(record, key, 'Cancelled; unchanged.');
        return results;
      }
      let changed = false;
      for (const { record, key } of recordOnly) {
        try {
          this.assertChatStillInactive(record);
          if (this.hasClaudeHistory(record.claudeSessionId))
            throw Error('Claude history appeared since the confirmation; nothing was deleted.');
          this.state = this.withoutHistory(record.claudeSessionId);
          changed = true;
          results.push({ id: key, label: label(record), status: 'completed' });
        } catch (error) {
          skip(record, key, String(error));
        }
      }
      const jobs: RetireJob[] = [];
      for (const { record, key } of ready) {
        try {
          this.assertChatStillInactive(record);
          jobs.push({
            prepared: plan(record),
            mode: operation,
            metadata: {
              id: record.claudeSessionId,
              label: label(record),
              cwd: record.cwd,
              provider: record.launch.provider ?? 'chatgpt',
            },
          });
        } catch (error) {
          skip(record, key, String(error));
        }
      }
      for (const outcome of jobs.length ? await this.history.retire(this.claudeHome, jobs) : []) {
        const target = ready.find(({ record }) => record.claudeSessionId === outcome.id);
        const job = jobs.find((entry) => entry.prepared.id === outcome.id);
        if (!target || !job) continue;
        if (!outcome.ok) {
          skip(target.record, target.key, outcome.error, outcome.code === 'chat_active' ? 'skipped' : 'failed');
          continue;
        }
        this.state = this.withoutHistory(outcome.id);
        if (outcome.archive && outcome.sha256) {
          const archive = { ...job.metadata, archive: outcome.archive, sha256: outcome.sha256 };
          this.state = {
            ...this.state,
            archives: [...(this.state.archives || []), { ...archive, operation: job.prepared.operation }],
          };
        }
        changed = true;
        results.push({ id: target.key, label: label(target.record), status: 'completed' });
      }
      if (!changed) return results;
      this.save();
      this.transcripts.invalidate('all');
      this.outside = null;
      for (const w of this.state.windows) this.push(w.id);
      this.listChanged();
      return results;
    } finally {
      for (const { record } of locked) this.historyBusy.delete(record.claudeSessionId);
    }
  }

  private async restoreChat(id: string): Promise<{ ok: boolean; error?: string }> {
    const entry = this.state.archives?.find((a) => a.id === id);
    if (!entry) return { ok: false, error: 'Archive not found.' };
    if (this.historyBusy.has(id)) return { ok: false, error: 'A history operation is already running.' };
    this.historyBusy.add(id);
    try {
      await this.history.restore(this.claudeHome, entry);
      const record: SessionRecord = {
        id: crypto.randomUUID(),
        claudeSessionId: id,
        cwd: entry.cwd,
        name: entry.label,
        launch: { provider: entry.provider },
        lifecycle: 'ended',
        used: true,
        createdAt: Date.now(),
        lastActiveAt: Date.now(),
      };
      this.state = {
        ...this.state,
        sessions: { ...this.state.sessions, [record.id]: record },
        archives: this.state.archives!.filter((a) => a.id !== id),
      };
      this.save();
      this.transcripts.invalidate('all');
      this.listChanged();
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    } finally {
      this.historyBusy.delete(id);
    }
  }

  /** Whether Claude wrote anything for this session that the history helper would plan (read-only check). */
  private hasClaudeHistory(id: string): boolean {
    const home = this.claudeHome;
    const exists = (...parts: string[]) => fs.existsSync(path.join(home, ...parts));
    let projects: string[];
    try {
      projects = fs
        .readdirSync(path.join(home, 'projects'), { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name);
    } catch {
      projects = [];
    }
    return (
      projects.some((project) => exists('projects', project, `${id}.jsonl`) || exists('projects', project, id)) ||
      ['file-history', 'session-env', 'tasks'].some((root) => exists(root, id)) ||
      exists('debug', `${id}.txt`)
    );
  }

  private withoutHistory(tid: string): AppState {
    const removed = Object.values(this.state.sessions)
      .filter((r) => r.claudeSessionId === tid)
      .map((r) => r.id);
    return {
      ...this.state,
      sessions: Object.fromEntries(Object.entries(this.state.sessions).filter(([, r]) => r.claudeSessionId !== tid)),
      pinnedChats: (this.state.pinnedChats || []).filter((key) => {
        const pin = parseChatKey(key);
        return !pin || pin.id !== tid || !!pin.distro;
      }),
      windows: this.state.windows.map((w) => removed.reduce((next, id) => dropSession(next, id), w)),
    };
  }

  /** Test hook (REFRAME_E2E): clicks the item labeled `label` in the menu shown last. */
  clickMenuItem(label: string): boolean {
    const find = (items: MenuItem[]): MenuItem | undefined =>
      items.reduce<MenuItem | undefined>(
        (found, item) => found ?? (item.label === label ? item : item.submenu ? find(item.submenu.items) : undefined),
        undefined,
      );
    const item = this.lastMenu && find(this.lastMenu.items);
    if (!item) return false;
    item.click();
    return true;
  }

  private showGroupMenu(windowId: string, groupId: string, colorsOnly: boolean): void {
    const browserWindow = this.windows.get(windowId);
    const group = this.findGroup(groupId);
    if (!browserWindow || !group || this.windowOfGroup(groupId)?.id !== windowId) return;
    const recolor = (hex: string) => this.recolorGroup(groupId, hex);
    const menu = colorsOnly
      ? colorMenu(this.t, group, recolor)
      : groupMenu(this.t, group, {
          rename: () => this.windows.send(windowId, IPC.renameGroup, groupId),
          recolor,
          dissolve: () => this.dissolveGroup(groupId),
          endAll: () => this.endGroup(groupId),
        });
    menu.popup({ window: browserWindow });
  }

  // ---------- session list ----------

  private async sessionList(query: string, limit: number, folder?: string, provider?: Provider): Promise<SessionList> {
    const q = query.trim().toLowerCase();
    const matches = (...fields: (string | undefined)[]) => !q || fields.some((f) => f?.toLowerCase().includes(q));
    const records = Object.values(this.state.sessions);
    const recordedProviders = new Map<string, Provider>(
      records.map((record) => [
        chatKey(record.claudeSessionId, this.wslPath(record.cwd)?.distro),
        record.launch.provider ?? 'chatgpt',
      ]),
    );
    const providerOf = (id: string, distro?: string): Provider =>
      recordedProviders.get(chatKey(id, distro)) ?? 'chatgpt';

    // Where Claude Code keeps its sessions (on Windows, and in each running WSL distribution that has it) and what
    // runs there outside reframe: other sessions, and reframe's own ones that someone resumed elsewhere.
    if (!this.outside) await this.scanOutside(0);
    const { sources: scanned, stopped } = this.outside ?? { sources: [], stopped: [] };
    const sources = scanned.map(({ source }) => source);
    const ownIds = new Set(records.map((r) => r.claudeSessionId));
    const resumedElsewhere = new Set(scanned.flatMap(({ running }) => running.map((e) => e.sessionId)));
    // When what runs outside reframe was last used: when its transcript was last written. Sources where nothing
    // runs are not asked.
    const usedOutside = new Map<string, number>();
    await Promise.all(
      scanned
        .filter(({ running }) => running.length > 0)
        .map(async ({ source, running: entries }) => {
          const times = await source.transcripts.modifiedTimes();
          for (const entry of entries) {
            const time = times.get(entry.sessionId) ?? entry.startedAt;
            if (time) usedOutside.set(entry.sessionId, time);
          }
        }),
    );

    const app: AppListItem[] = records
      .map((record): AppListItem => {
        const view = this.sessionView(record);
        const win = this.windowOf(record.id);
        const elsewhere = resumedElsewhere.has(record.claudeSessionId) && !this.sessions.keepsRunning(record.id);
        return {
          kind: 'app',
          claudeSessionId: record.claudeSessionId,
          ...(this.wslPath(record.cwd)?.distro ? { distro: this.wslPath(record.cwd)!.distro } : {}),
          provider: view.provider,
          id: record.id,
          label: view.label,
          cwd: record.cwd,
          status: view.status,
          lifecycle: record.lifecycle,
          windowId: win?.id ?? null,
          windowNumber: win ? this.state.windows.indexOf(win) + 1 : null,
          // Resumed in another terminal, it may have been used there since.
          lastActiveAt: Math.max(record.lastActiveAt, (elsewhere && usedOutside.get(record.claudeSessionId)) || 0),
          elsewhere,
        };
      })
      .filter(
        (item) =>
          matches(item.label, item.cwd) &&
          (!folder || workspaceKey(item.cwd) === workspaceKey(folder)) &&
          (!provider || (item.provider ?? 'chatgpt') === provider),
      )
      // Sessions in a window first, then the rest; most recently used first within each.
      .sort((a, b) => Number(!a.windowId) - Number(!b.windowId) || b.lastActiveAt - a.lastActiveAt);

    const running = scanned
      .flatMap(({ source, running: entries }) =>
        entries
          .filter((entry) => !ownIds.has(entry.sessionId))
          .map((entry): RunningListItem => ({
            kind: 'running',
            pid: entry.pid,
            claudeSessionId: entry.sessionId,
            cwd: source.windowsPath(entry.cwd!),
            name: entry.name,
            ...(recordedProviders.has(chatKey(entry.sessionId, source.distro))
              ? { provider: providerOf(entry.sessionId, source.distro) }
              : {}),
            ...(usedOutside.has(entry.sessionId) ? { lastActiveAt: usedOutside.get(entry.sessionId) } : {}),
            ...(source.distro ? { distro: source.distro } : {}),
          })),
      )
      .filter(
        (item) =>
          matches(item.name, item.cwd) &&
          (!folder || workspaceKey(item.cwd) === workspaceKey(folder)) &&
          (!provider || providerOf(item.claudeSessionId, item.distro) === provider),
      );

    const exclude = new Set([...ownIds, ...running.map((r) => r.claudeSessionId)]);
    const pages = await Promise.all(
      sources.map(async (source) => ({
        source,
        page: await source.transcripts.list({ query, limit, exclude, folder }),
      })),
    );
    const found = pages
      .flatMap(({ source, page }) =>
        page.items
          .filter((info) => info.cwd)
          .map((info) => ({
            kind: 'found' as const,
            claudeSessionId: info.sessionId,
            cwd: source.windowsPath(info.cwd!),
            title: info.title,
            modifiedAt: info.modifiedAt,
            ...(recordedProviders.has(chatKey(info.sessionId, source.distro))
              ? { provider: providerOf(info.sessionId, source.distro) }
              : {}),
            ...(source.distro ? { distro: source.distro } : {}),
          })),
      )
      .filter((item) => !provider || providerOf(item.claudeSessionId, item.distro) === provider)
      .sort((a, b) => b.modifiedAt - a.modifiedAt);
    const hasMore =
      found.length > limit || ((!provider || provider === 'chatgpt') && pages.some(({ page }) => page.hasMore));
    const pinnedItems: FoundListItem[] = [];
    const knownKeys = new Set([
      ...records.map((record) => chatKey(record.claudeSessionId, this.wslPath(record.cwd)?.distro)),
      ...scanned.flatMap(({ source, running: entries }) =>
        entries.map((entry) => chatKey(entry.sessionId, source.distro)),
      ),
    ]);
    for (const key of this.state.pinnedChats || []) {
      const pin = parseChatKey(key);
      if (!pin || knownKeys.has(key) || found.some((item) => chatKey(item.claudeSessionId, item.distro) === key))
        continue;
      const source = sources.find((entry) => (entry.distro?.toLowerCase() || '') === (pin.distro || ''));
      const info = await source?.transcripts.find(pin.id);
      if (!source || !info?.cwd) continue;
      const cwd = source.windowsPath(info.cwd);
      if (provider && providerOf(pin.id, source.distro) !== provider) continue;
      if (!matches(info.title, cwd) || (folder && workspaceKey(folder) !== workspaceKey(cwd))) continue;
      pinnedItems.push({
        kind: 'found',
        claudeSessionId: pin.id,
        cwd,
        title: info.title,
        modifiedAt: info.modifiedAt,
        ...(source.distro ? { distro: source.distro } : {}),
      });
    }
    const automatic = folderWorkspaces([
      ...records.map((r) => ({
        cwd: r.cwd,
        id: r.claudeSessionId,
        provider: r.launch.provider ?? ('chatgpt' as Provider),
      })),
      ...scanned.flatMap((s) =>
        s.running
          .filter((r) => r.cwd)
          .map((r) => ({
            cwd: s.source.windowsPath(r.cwd!),
            id: r.sessionId,
            provider: providerOf(r.sessionId, s.source.distro),
          })),
      ),
      ...pages.flatMap(({ source, page }) =>
        page.folders.flatMap((f) =>
          f.locations.flatMap((location) =>
            location.sessionIds.map((id) => ({
              cwd: source.windowsPath(location.cwd),
              id,
              provider: providerOf(id, source.distro),
            })),
          ),
        ),
      ),
    ]);
    this.listedProjects = new Set(automatic.map((project) => project.id));
    const previousExpanded = this.state.expandedProjects || [];
    const expandedProjects = [...new Set(previousExpanded.map(expandedWorkspaceKey))];
    if (JSON.stringify(expandedProjects) !== JSON.stringify(previousExpanded)) {
      this.state = { ...this.state, expandedProjects };
      this.store.saveSoon(this.state);
    }
    return {
      pinnedChats: this.state.pinnedChats || [],
      pinnedItems,
      expandedProjects,
      folderWorkspaces: automatic,
      archives: this.state.archives || [],
      app,
      running,
      found: found.slice(0, limit),
      hasMore,
      wslStopped: stopped,
    };
  }

  /**
   * Where Claude Code keeps sessions and transcripts: on Windows, and in each running WSL distribution that has it
   * (a stopped one is not started for this; `stopped` names those Claude Code was found in before).
   */
  private async claudeSources(
    maxAgeMs?: number,
    asked = false,
  ): Promise<{ sources: ClaudeSource[]; stopped: string[] }> {
    // In the background WSL is asked only when that is turned on in Settings; `asked`: for something the user did.
    const lookInWsl = asked || this.state.preferences.lookInWsl;
    const windows = this.ownSource();
    if (!this.wsl || !lookInWsl) return { sources: [windows], stopped: [] };
    const running = await discoverable(this.wsl, lookInWsl, maxAgeMs);
    const inWsl = await Promise.all(running.map((distro) => this.wslSource(distro)));
    const stopped = (this.state.wslDistros ?? []).filter(
      (d) => !running.some((r) => r.toLowerCase() === d.toLowerCase()),
    );
    return { sources: [windows, ...inWsl.filter((s): s is ClaudeSource => !!s)], stopped };
  }

  /** Claude Code's sessions on this system itself (on Windows: not the ones in WSL). */
  private ownSource(): ClaudeSource {
    return {
      home: this.claudeHome,
      transcripts: this.transcripts,
      alive: async (entry) => isRunningEntry(entry),
      windowsPath: (folder) => folder,
    };
  }

  /** Claude Code's sessions in a WSL distribution, if it has Claude Code (asking it starts it). */
  private async wslSource(distro: string): Promise<ClaudeSource | null> {
    const wsl = this.wsl!;
    const info = await wsl.info(distro);
    if (!info?.claude) return null;
    const home = wslWindowsPath(distro, info.claudeHome, wsl.share);
    let transcripts = this.wslTranscripts.get(home);
    if (!transcripts) this.wslTranscripts.set(home, (transcripts = new TranscriptIndex(home)));
    return {
      distro,
      home,
      transcripts,
      alive: (entry) => wsl.isClaude(distro, entry.pid),
      windowsPath: (folder) => wslWindowsPath(distro, folder, wsl.share),
    };
  }
  private wslTranscripts = new Map<string, TranscriptIndex>();

  // ---------- keeping the session list up to date ----------

  private outside: OutsideScan | null = null;
  private wslLookedAt = 0;
  private scans: Promise<void> = Promise.resolve();
  private listWatcher: ClaudeWatcher | null = null;
  private listLook: NodeJS.Timeout | undefined;
  private quotaLook: NodeJS.Timeout | undefined;
  /** The latest quota per subscription, shown below the session list. */
  private quota = new Map<ProviderQuota['source'], ProviderQuota>();

  private setQuota(next: ProviderQuota): void {
    const before = this.quota.get(next.source);
    this.quota.set(next.source, next);
    if (before && JSON.stringify(before.windows) === JSON.stringify(next.windows)) return;
    for (const w of this.state.windows) this.push(w.id);
  }

  /** Codex quota from LLM Coordinator, now and every five minutes (it observes the account itself). */
  private watchCodexQuota(): void {
    const exe = process.env.REFRAME_E2E ? null : coordinatorExecutable();
    if (!exe) return;
    const read = () =>
      void readCodexQuota(exe).then((windows) => {
        if (windows) this.setQuota({ source: 'codex', windows, at: Date.now() });
      });
    read();
    this.quotaLook = setInterval(read, 5 * 60_000);
    this.quotaLook.unref();
  }

  /** The window shows the project of this session (other projects' tiles collapse). */
  private showProjectOf(windowId: string, sessionId: string): void {
    const record = this.state.sessions[sessionId];
    if (record) this.showProjectIn(windowId, workspaceKey(record.cwd));
  }

  private showProjectIn(windowId: string, key: string): void {
    const projectOf = (id: string) => {
      const r = this.state.sessions[id];
      return r ? workspaceKey(r.cwd) : undefined;
    };
    this.mutateWindow(windowId, (w) => showProject(w, key, projectOf));
  }
  private listPush: NodeJS.Timeout | undefined;
  /** The list as the windows got it last, to send it only when it changed. */
  private listSent = '';

  /**
   * The windows get the session list from here, when it changed, instead of each asking for it every few seconds:
   * that read every registry entry and looked at every transcript's time (thousands in a long history) and asked
   * wsl.exe which distributions run, all the time, with or without anything changing. Windows' own Claude Code folder
   * is watched; what runs in WSL is looked at every little while.
   */
  private watchList(): void {
    this.watchCodexQuota();
    this.listWatcher = new ClaudeWatcher(this.claudeHome);
    this.listWatcher.on('changed', ({ projects }) => {
      this.transcripts.invalidate(projects);
      void this.scanOutside(null).then(() => this.listChanged());
    });
    this.listWatcher.start();
    this.listLook = setInterval(() => {
      const shown = this.state.windows.some((w) => w.sidebarOpen && !w.hidden);
      void this.scanOutside(shown ? WSL_LOOK_MS.shown : WSL_LOOK_MS.hidden).then(() => this.listChanged());
    }, LIST_LOOK_MS);
    this.listLook.unref();
  }

  /**
   * Looks again what runs outside reframe: on Windows always (cheap), in WSL when that was last done `wslMaxAgeMs`
   * ago or longer (null: not now), and then WSL's transcripts are listed again too. One look at a time.
   */
  private scanOutside(wslMaxAgeMs: number | null): Promise<void> {
    this.scans = this.scans
      .then(async () => {
        const own = this.ownSource();
        const here = { source: own, running: await this.externalRunning(own) };
        // Turned off in Settings: nothing is asked of WSL and nothing from it is listed.
        const lookInWsl = !!this.wsl && this.state.preferences.lookInWsl;
        let inWsl = lookInWsl ? (this.outside?.sources.slice(1) ?? []) : [];
        let stopped = lookInWsl ? (this.outside?.stopped ?? []) : [];
        if (lookInWsl && wslMaxAgeMs !== null && Date.now() - this.wslLookedAt >= wslMaxAgeMs) {
          this.wslLookedAt = Date.now();
          const found = await this.claudeSources(wslMaxAgeMs);
          inWsl = await Promise.all(
            found.sources
              .filter((source) => source.distro)
              .map(async (source) => {
                source.transcripts.invalidate('all');
                return { source, running: await this.externalRunning(source) };
              }),
          );
          stopped = found.stopped;
        }
        this.outside = { sources: [here, ...inWsl], stopped };
      })
      .catch((error: unknown) =>
        this.log('warn', 'could not look at the sessions outside reframe', { error: String(error) }),
      );
    return this.scans;
  }

  /** Something the session list shows may have changed: the windows get it in a moment, if it did. */
  private listChanged(): void {
    if (this.listPush || this.shuttingDown || !this.listWatcher) return;
    this.listPush = setTimeout(() => {
      this.listPush = undefined;
      void this.sessionList('', LIST_PAGE)
        .then((list) => {
          const sent = JSON.stringify(list);
          if (sent === this.listSent || this.shuttingDown) return;
          this.listSent = sent;
          for (const w of this.state.windows) this.windows.send(w.id, IPC.sessionListChanged, list);
        })
        .catch((error: unknown) => this.log('warn', 'could not make the session list', { error: String(error) }));
    }, LIST_PUSH_MS);
  }

  /**
   * Claudes running outside reframe (with a known folder), reframe's own sessions resumed elsewhere among them; a
   * distribution that does not answer in time has none.
   */
  private async externalRunning(source: ClaudeSource): Promise<RegistryEntry[]> {
    const exclude = { pids: this.ownProcesses(source.distro), sessionIds: new Set<string>() };
    const running = source.distro
      ? await withTimeout(listRunningAsync(source.home, exclude, source.alive), SHARE_TIMEOUT_MS, [])
      : listRunning(source.home, exclude);
    return running.filter((entry) => entry.cwd);
  }

  /**
   * The same for other sessions only, for the window watcher, which asks while a window is dragged: Windows' own
   * claudes, and those in running WSL distributions with the wsl.exe of their terminal (one whose terminal cannot be
   * told is left out).
   */
  private async outsideClaudes(): Promise<OutsideClaude[]> {
    const ownIds = new Set(Object.values(this.state.sessions).map((r) => r.claudeSessionId));
    const here = listRunning(this.claudeHome, { pids: this.ownProcesses(), sessionIds: ownIds })
      .filter((entry) => entry.cwd)
      .map((entry): OutsideClaude => ({ entry, consolePid: entry.pid }));
    const wsl = this.wsl;
    if (!wsl) return here;
    // Asked for a terminal window that appeared or is dragged over reframe (a handover), not in the background.
    const { sources } = await this.claudeSources(undefined, true);
    const inWsl = await Promise.all(
      sources
        .filter((source) => source.distro)
        .map(async (source) => {
          const running = (await this.externalRunning(source)).filter((entry) => !ownIds.has(entry.sessionId));
          return Promise.all(
            running.map(async (entry): Promise<OutsideClaude | null> => {
              const terminal = await wsl.terminalOf(source.distro!, entry);
              return terminal ? { entry, distro: source.distro, consolePid: terminal } : null;
            }),
          );
        }),
    );
    return [...here, ...inWsl.flat().filter((c): c is OutsideClaude => !!c)];
  }

  /** The processes of reframe's sessions (in a WSL distribution their Linux ids, as far as known). */
  private ownProcesses(distro?: string): Set<number> {
    return new Set(
      Object.values(this.state.sessions).flatMap((r) => {
        const runtime = this.sessions.status(r.id);
        if (!runtime) return [];
        // Through cmd.exe the pty's process is cmd's, and claude's own is known once its entry was found.
        const pids = distro ? [r.linuxPid] : [runtime.pid, runtime.launched?.pid];
        return pids.filter((pid): pid is number => !!pid);
      }),
    );
  }

  /** Where a list entry's Claude Code keeps its sessions: on Windows, or in its WSL distribution. */
  private async sourceOf(item: { distro?: string }): Promise<ClaudeSource | null> {
    if (!item.distro) return this.ownSource();
    return this.wsl ? this.wslSource(item.distro) : null;
  }

  /**
   * The record for a list entry: reframe's own, or a new one for a session found or running elsewhere. One running
   * elsewhere is ended there first (after asking, unless already `confirmed`), so a conversation never runs twice.
   * Null when there is nothing (any more) to open.
   */
  private async recordFor(windowId: string, item: ListItemRef, confirmed: boolean): Promise<SessionRecord | null> {
    const historyId = item.kind === 'app' ? this.state.sessions[item.sessionId]?.claudeSessionId : item.claudeSessionId;
    if (historyId && this.historyBusy.has(historyId)) return null;
    if (item.kind === 'app') return this.state.sessions[item.sessionId] ?? null;

    // A conversation reframe already manages is opened as that session, not a second copy.
    const known = Object.values(this.state.sessions).find((r) => r.claudeSessionId === item.claudeSessionId);
    if (known) return known;
    // Asked for twice at once (two clicks, a click and a drop): one adoption, which both get.
    const pending = this.adopting.get(item.claudeSessionId);
    if (pending) return pending;
    const adopting = this.adopt(windowId, item, confirmed).finally(() => this.adopting.delete(item.claudeSessionId));
    this.adopting.set(item.claudeSessionId, adopting);
    return adopting;
  }
  private adopting = new Map<string, Promise<SessionRecord | null>>();

  /** A new record for a session found or running elsewhere (see recordFor). */
  private async adopt(
    windowId: string,
    item: Exclude<ListItemRef, { kind: 'app' }>,
    confirmed: boolean,
  ): Promise<SessionRecord | null> {
    const source = await this.sourceOf(item);
    if (!source) return null;
    if (item.kind === 'found') {
      const info = await source.transcripts.find(item.claudeSessionId);
      if (info?.cwd) return this.adopted(item.claudeSessionId, source.windowsPath(info.cwd), undefined, source.home);
      this.log('warn', 'found session has no folder', { claudeSession: item.claudeSessionId });
      return null;
    }

    const entry = await withTimeout(readRegistryEntryAsync(source.home, item.pid), SHARE_TIMEOUT_MS, null);
    const alive = !!entry && (await withTimeout(source.alive(entry), SHARE_TIMEOUT_MS, false));
    if (!entry || entry.sessionId !== item.claudeSessionId || !entry.cwd || !alive) {
      this.log('info', 'running session is gone, nothing to take over', { pid: item.pid, wsl: source.distro });
      return null;
    }
    if (!confirmed && !(await this.confirmTakeover(windowId, [entry.name ?? path.basename(entry.cwd)]))) {
      return null;
    }
    this.log('info', 'taking over session from another terminal', {
      pid: item.pid,
      claudeSession: entry.sessionId,
      ...(source.distro ? { wsl: source.distro } : {}),
    });
    if (!(await this.stopOutside(item.pid, source.distro))) {
      await this.showError(windowId, this.t('takeover.failed'));
      return null;
    }
    // A name the user gave it survives; one Claude made up is made up again.
    const name = entry.nameSource === 'user' ? entry.name : undefined;
    return this.adopted(entry.sessionId, source.windowsPath(entry.cwd), name, source.home);
  }

  /** Asks once before ending sessions that run in other terminals. */
  private async confirmTakeover(windowId: string, labels: string[]): Promise<boolean> {
    const browserWindow = this.windows.get(windowId);
    const options = {
      type: 'question' as const,
      buttons: [this.t('takeover.confirm'), this.t('dialog.cancel')],
      defaultId: 0,
      cancelId: 1,
      title: this.t('takeover.title'),
      message:
        labels.length === 1
          ? this.t('takeover.message', { label: labels[0]! })
          : this.t('takeover.messageMany', { count: labels.length, labels: labels.join(', ') }),
      detail: this.t('takeover.detail'),
    };
    const { response } = browserWindow
      ? await dialog.showMessageBox(browserWindow, options)
      : await dialog.showMessageBox(options);
    return response === 0;
  }

  private async showError(windowId: string, message: string): Promise<void> {
    const browserWindow = this.windows.get(windowId);
    const options = { type: 'error' as const, message };
    if (browserWindow) await dialog.showMessageBox(browserWindow, options);
    else await dialog.showMessageBox(options);
  }

  /** Opens a list entry in `windowId` at `target`; a session already shown somewhere is just brought up. */
  private async openSession(windowId: string, item: ListItemRef, target: DropTarget | null): Promise<void> {
    const record = await this.recordFor(windowId, item, false);
    if (!record) return;
    if (this.windowOf(record.id)) return this.focusSession(record.id);
    this.placeAndResume(windowId, { ...record, lifecycle: 'open' }, target);
  }

  /**
   * Opens several list entries at once: one after the other (at `target`, each below the one before), or together
   * in a new group, in this window or a new one. Sessions running elsewhere are taken over after one question for
   * all of them. Opening sessions that are shown already just brings them up; dropping them somewhere or grouping
   * them moves them there.
   */
  async openSessions(windowId: string, items: ListItemRef[], mode: OpenMode, target: DropTarget | null): Promise<void> {
    let wanted = items;
    const running = items.flatMap((i) => (i.kind === 'running' ? [i] : []));
    if (running.length > 0) {
      const labels = await Promise.all(
        running.map(async (i) => {
          const source = await this.sourceOf(i);
          const entry =
            source && (await withTimeout(readRegistryEntryAsync(source.home, i.pid), SHARE_TIMEOUT_MS, null));
          return entry?.name ?? (entry?.cwd ? path.basename(entry.cwd) : String(i.pid));
        }),
      );
      if (!(await this.confirmTakeover(windowId, labels))) wanted = items.filter((i) => i.kind !== 'running');
    }
    const records: SessionRecord[] = [];
    for (const item of wanted) {
      const record = await this.recordFor(windowId, item, true);
      if (record && !records.some((r) => r.id === record.id)) records.push(record);
    }
    if (records.length === 0) return;
    this.log('info', 'opening sessions from the list', { sessions: records.length, mode, at: target ?? 'end' });

    if (mode === 'open' && !target) {
      for (const record of records) {
        if (this.windowOf(record.id)) this.focusSession(record.id);
        else this.placeAndResume(windowId, { ...record, lifecycle: 'open' }, null);
      }
      return;
    }

    const into = mode === 'groupInNewWindow' ? this.newWindow(windowId) : windowId;
    const ids = records.map((r) => r.id);
    this.state = {
      ...this.state,
      sessions: {
        ...this.state.sessions,
        ...Object.fromEntries(records.map((r) => [r.id, { ...(this.state.sessions[r.id] ?? r), lifecycle: 'open' }])),
      },
    };
    // Out of wherever they are shown now; a window left empty by that closes only after they have landed.
    const left = new Set(ids.map((id) => this.detach(id)).filter((w): w is string => !!w && w !== into));
    if (mode === 'open') {
      let next: DropTarget | null = target;
      for (const id of ids) {
        this.mutateWindow(into, (w) => putNode(w, tile(id), next));
        next = { key: tileKey(id), side: 'bottom' };
      }
    } else {
      let child: LayoutNode | null = null;
      for (const id of ids) child = appendNode(child, tile(id));
      const group: GroupNode = { kind: 'group', id: crypto.randomUUID(), ...this.newGroupLook(), child: child! };
      this.mutateWindow(into, (w) => putNode(w, group, target));
      this.log('info', 'group created from the list', { sessions: ids.length, name: group.name });
    }
    for (const id of ids) void this.startOrTakeOver(id, 'resume', into);
    for (const id of left) this.closeIfEmpty(id);
    if (ids[0]) this.showProjectOf(into, ids[0]);
    this.windows.show(into);
    this.windows.send(into, IPC.focusTile, ids[0]);
    this.refreshTray();
  }

  /** Takes a session's tile out of the window showing it (a collapsed one is put back first); that window's id. */
  private detach(sessionId: string): string | null {
    const win = this.windowOf(sessionId);
    if (!win) return null;
    let next = win;
    for (const entry of win.dock) {
      if (entry.kind === 'tile' && entry.sessionId === sessionId) {
        next = applyLayoutAction(next, { type: 'tile/expand', sessionId });
      } else if (entry.kind === 'group' && leaves(entry.node).includes(sessionId)) {
        next = applyLayoutAction(next, { type: 'group/expand', groupId: entry.node.id });
      }
    }
    const { win: rest } = takeNode(next, tileKey(sessionId));
    this.mutateWindow(win.id, () => rest);
    return win.id;
  }

  /** A new record for a Claude Code session started outside reframe. */
  /** `claudeHome`: where its Claude Code keeps transcripts (in WSL, its distribution's). */
  private async adopted(
    claudeSessionId: string,
    cwd: string,
    name?: string,
    claudeHome = this.claudeHome,
  ): Promise<SessionRecord> {
    // Without a transcript (no prompt yet) it starts fresh under the same id instead of failing to resume. A
    // distribution whose share does not answer in time is taken to have one (a resume without one says so).
    // Its last use is when its transcript was last written: opening it here is not using it.
    const modifiedAt = await withTimeout(transcriptModifiedAsync(claudeHome, claudeSessionId), SHARE_TIMEOUT_MS, 0);
    const now = Date.now();
    return {
      id: crypto.randomUUID(),
      claudeSessionId,
      cwd,
      name,
      launch: {},
      lifecycle: 'open',
      createdAt: now,
      lastActiveAt: modifiedAt || now,
      used: modifiedAt !== null,
    };
  }

  private placeAndResume(windowId: string, record: SessionRecord, target: DropTarget | null): void {
    this.state = { ...this.state, sessions: { ...this.state.sessions, [record.id]: record } };
    this.mutateWindow(windowId, (w) => putNode(w, tile(record.id), target));
    this.showProjectOf(windowId, record.id);
    this.log('info', 'session opened from the list', { session: record.id, window: windowId });
    void this.startOrTakeOver(record.id, 'resume', windowId);
    this.windows.send(windowId, IPC.focusTile, record.id);
  }

  /** A name the user gives a session (in reframe only); an empty one goes back to the automatic name. */
  renameSession(sessionId: string, name: string): void {
    const record = this.state.sessions[sessionId];
    if (!record) return;
    const trimmed = name.trim().slice(0, 100);
    // Claude Code keeps the name it was started with; renaming is reframe's own (shown instead of it).
    const claudeName = record.claudeName === undefined ? (record.name ?? null) : record.claudeName;
    this.updateSession(sessionId, { name: trimmed || undefined, claudeName });
    this.log('info', 'session renamed', { session: sessionId, automatic: !trimmed });
    this.refreshTray();
  }

  /** Forgets an ended session that is in no window; Claude Code's transcript is left alone. */
  private removeSession(sessionId: string): void {
    if (!this.state.sessions[sessionId] || this.windowOf(sessionId)) return;
    void this.sessions.stop(sessionId); // still ending right after its tile's ×: let it finish in the background
    const { [sessionId]: _removed, ...rest } = this.state.sessions;
    this.state = { ...this.state, sessions: rest };
    this.save();
    fs.rmSync(path.join(app.getPath('userData'), 'hooks', `${sessionId}.json`), { force: true });
    this.log('info', 'session removed from the list', { session: sessionId });
  }

  // ---------- terminal windows dragged onto reframe (Windows) ----------

  private foreign: ForeignWindowWatcher | null = null;
  /** Open questions to windows about a terminal window dropped on them, by request id. */
  private foreignRequests = new Map<
    string,
    { windowId: string; resolve: (answer: ForeignDropAnswer | null) => void }
  >();
  private takingOver = new Set<string>();

  private watchForeignWindows(): void {
    const api = win32();
    if (!api) return;
    this.foreign = new ForeignWindowWatcher({
      win32: api,
      windowAt: (point) => this.windows.windowAt(point),
      externalSessions: () => this.outsideClaudes(),
      log: this.log,
    });
    this.foreign.on('hover', (windowId, point) => {
      this.windows.send(windowId, IPC.foreignDrag, point && this.toContent(windowId, point));
    });
    this.foreign.on('drop', (drop) => {
      void this.takeOverWindow(drop).catch((error: unknown) =>
        this.log('error', 'taking over a terminal window failed', { error: String(error) }),
      );
    });
    try {
      this.foreign.start();
    } catch (error) {
      this.log('warn', 'cannot follow window drags', { error: String(error) });
    }
  }

  private toContent(windowId: string, point: { x: number; y: number }): { x: number; y: number } {
    const bounds = this.windows.get(windowId)?.getContentBounds() ?? { x: 0, y: 0 };
    return { x: point.x - bounds.x, y: point.y - bounds.y };
  }

  /** Asks (in the window it was dropped on) which sessions to take over; resumes them; closes the terminal. */
  private async takeOverWindow(drop: ForeignDrop): Promise<void> {
    // Some terminals report the end of a window move twice; one question per window is enough.
    if (this.takingOver.has(drop.hwnd)) return this.log('info', 'repeated drop of the same terminal window ignored');
    this.takingOver.add(drop.hwnd);
    try {
      await this.takeOverWindowOnce(drop);
    } finally {
      setTimeout(() => this.takingOver.delete(drop.hwnd), 2000);
    }
  }

  private async takeOverWindowOnce(drop: ForeignDrop): Promise<void> {
    this.log('info', 'terminal window dropped on reframe', { title: drop.title, sessions: drop.sessions.length });
    this.windows.show(drop.windowId);
    const active = activeByTitle(
      drop.title,
      drop.sessions.map((s) => s.entry),
    );
    const candidates: ForeignCandidate[] = drop.sessions.map(({ entry, distro }) => ({
      claudeSessionId: entry.sessionId,
      cwd: distro ? wslWindowsPath(distro, entry.cwd!, this.wsl!.share) : entry.cwd!,
      name: entry.name,
      preselected: drop.sessions.length === 1 || entry === active,
    }));
    const requestId = crypto.randomUUID();
    const answer = await new Promise<ForeignDropAnswer | null>((resolve) => {
      this.foreignRequests.set(requestId, { windowId: drop.windowId, resolve });
      const { x, y } = this.toContent(drop.windowId, drop.point);
      this.windows.send(drop.windowId, IPC.foreignDrop, { requestId, x, y, candidates });
    });
    const chosen = drop.sessions.filter((s) => answer?.claudeSessionIds.includes(s.entry.sessionId));
    if (chosen.length === 0) return this.log('info', 'terminal window takeover cancelled');
    this.log('info', 'terminal window takeover confirmed', {
      sessions: chosen.length,
      wsl: chosen.filter((s) => s.distro).length,
      target: answer?.target ?? 'end of the window',
    });

    let target = answer?.target ?? null;
    const closeTabs: (() => Promise<void>)[] = [];
    for (const { entry, distro, consolePid } of chosen) {
      const source = distro ? await this.wslSource(distro) : this.ownSource();
      if (!source) continue;
      // Remember a shell in that console, to ask it afterwards what is left there. In WSL that is one on Windows
      // that started wsl.exe, if any (not wsl.exe itself, nor the wsl.exe in System32 that starts the real one),
      // and the shells in the distribution that claude was typed into.
      const before = await consoleHelper({ type: 'console-processes', pid: consolePid });
      const shell = before.ok
        ? before.processes?.find(
            (p) =>
              isShell(p.name) &&
              !(distro && (p.pid === consolePid || /^(wsl|conhost|openconsole)\.exe$/i.test(p.name ?? ''))),
          )?.pid
        : undefined;
      const linuxShells = distro ? await this.wsl!.shellsOf(distro, entry.pid) : [];
      this.log(
        'info',
        'running in that terminal tab',
        before.ok
          ? { processes: before.processes?.map((p) => p.name), ...(distro ? { shellsInWsl: linuxShells.length } : {}) }
          : { error: before.error },
      );
      const latest =
        (distro
          ? await withTimeout(readRegistryEntryAsync(source.home, entry.pid), SHARE_TIMEOUT_MS, null)
          : readRegistryEntry(this.claudeHome, entry.pid)) ?? entry;
      if (!(await this.stopOutside(entry.pid, distro))) continue;
      const name = latest.nameSource === 'user' ? latest.name : undefined;
      const cwd = source.windowsPath(latest.cwd ?? entry.cwd!);
      const record = await this.adopted(latest.sessionId, cwd, name, source.home);
      this.placeAndResume(drop.windowId, record, target);
      target = { key: tileKey(record.id), side: 'right' };
      // Without a shell, the tab closed on its own together with claude.
      if (distro) closeTabs.push(() => this.closeIdleWslTab(distro, consolePid, linuxShells, shell));
      else if (shell !== undefined) closeTabs.push(() => this.closeIdleConsole(shell));
    }

    for (const close of closeTabs) await close();
  }

  /**
   * Closes the terminal tab of a claude taken over from WSL when nothing but shells is left there, as for a tab on
   * Windows: each shell in the distribution that claude was typed into gets an "exit" (innermost first) once nothing
   * runs in it, and nothing but shells besides wsl.exe (`wslPid`) on Windows; the last one ends wsl.exe. A shell on
   * Windows that started wsl.exe is closeIdleConsole's then.
   */
  private async closeIdleWslTab(
    distro: string,
    wslPid: number,
    linuxShells: number[],
    windowsShell?: number,
  ): Promise<void> {
    const wsl = this.wsl!;
    const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
    for (const shell of linuxShells) {
      await sleep(500);
      if (!(await wsl.idle(distro, shell))) {
        return this.log('info', 'terminal tab stays open, something else runs there in wsl', { distro });
      }
      const left = await consoleHelper({ type: 'console-processes', pid: wslPid });
      if (!left.ok) return this.log('info', 'terminal tab closed');
      if (!onlyShells((left.processes ?? []).filter((p) => p.pid !== wslPid))) {
        return this.log('info', 'terminal tab stays open, something else runs there', {
          running: left.processes?.map((p) => p.name),
        });
      }
      this.log('info', 'only a shell is left in the terminal tab, typing exit', { shell: 'wsl', distro });
      await consoleHelper({ type: 'type', pid: wslPid, text: 'exit\r' });
      for (let waited = 0; waited < 3000 && (await wsl.alive(distro, shell)); waited += 200) await sleep(200);
    }
    if (windowsShell === undefined) return;
    for (let waited = 0; waited < 3000 && isAlive(wslPid); waited += 200) await sleep(200);
    if (!isAlive(wslPid)) await this.closeIdleConsole(windowsShell);
  }

  /**
   * Closes a terminal tab (console) that has nothing but shells left, by typing "exit" as its user would. Only
   * that tab goes; other tabs of the same terminal window are never touched, and the last one takes the
   * window with it. Nested shells (e.g. bash started from PowerShell) get one "exit" each.
   */
  private async closeIdleConsole(shellPid: number): Promise<void> {
    for (let attempt = 0; attempt < 3; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 500));
      const left = await consoleHelper({ type: 'console-processes', pid: shellPid });
      if (!left.ok) return this.log('info', 'terminal tab closed'); // nothing to attach to any more
      if (!onlyShells(left.processes ?? [])) {
        return this.log('info', 'terminal tab stays open, something else runs there', {
          running: left.processes?.map((p) => p.name),
        });
      }
      // Any shell of the tab will do: they share the console input, and the one in front reads the "exit".
      const shell = left.processes?.find((p) => isShell(p.name) && !/^(conhost|openconsole)\.exe$/i.test(p.name ?? ''));
      if (!shell) return;
      this.log('info', 'only a shell is left in the terminal tab, typing exit', { shell: shell.name });
      await consoleHelper({ type: 'type', pid: shell.pid, text: 'exit\r' });
    }
  }

  /** Test hook (REFRAME_E2E): the console window or hosting terminal window of a process. */
  async terminalWindowOf(pid: number): Promise<string | null> {
    const result = await consoleHelper({ type: 'owners', pids: [pid] });
    const owner = result.ok ? result.owners?.[0] : undefined;
    return owner ? (owner.owner ?? owner.consoleWindow) : null;
  }

  /** Test hook (REFRAME_E2E): terminal window `hwnd` as if it had just appeared at a screen point (torn-out tab). */
  terminalWindowAppeared(hwnd: string, point?: { x: number; y: number }): void {
    this.foreign?.appeared(hwnd, point);
  }

  /** Test hook (REFRAME_E2E): the terminal window a simulated tab drag comes from. */
  tabDragSource(hwnd: string | null): void {
    if (this.foreign) this.foreign.tabDragSource = hwnd;
  }

  /** Test hook (REFRAME_E2E): what the clipboard holds as far as reframe asks (null: the real one). */
  fakeClipboard(contents: ClipboardContents | null): void {
    this.clipboardOverride = contents;
  }
  private clipboardOverride: ClipboardContents | null = null;

  /** Test hook (REFRAME_E2E): ends the PTY host from outside, as a crash would. */
  crashPtyHost(): boolean {
    const pid = this.host.pid;
    if (!pid) return false;
    process.kill(pid);
    return true;
  }

  /** Test hook (REFRAME_E2E): this copy checks for updates, as an installed one does (without checking). */
  fakeUpdateChecks(): void {
    this.updates.checks = true;
    for (const w of this.state.windows) this.push(w.id);
  }

  /**
   * A file dropped onto a tile that exists only in memory (an image dragged from a browser, say) is written to a
   * temporary folder, so the session can get a path for it like for any other file.
   */
  private saveDroppedFile(name: string, data: ArrayBuffer | Uint8Array): string | null {
    if (data.byteLength > 200 * 1024 * 1024) return null;
    // A plain file name: no folders, nothing Windows refuses in names.
    const cleaned = [...path.basename(name)].map((c) => (c < ' ' || '<>:"/\\|?*'.includes(c) ? '_' : c)).join('');
    const safe = cleaned.slice(0, 120) || 'dropped';
    const dir = path.join(dropsDir(), crypto.randomUUID());
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, safe);
    fs.writeFileSync(file, data instanceof Uint8Array ? data : Buffer.from(data));
    this.log('info', 'dropped file saved for a session', { bytes: data.byteLength });
    return file;
  }

  /** Test hook (REFRAME_E2E): what releasing terminal window `hwnd` at a screen point does. */
  async dropTerminalWindow(hwnd: string, point: { x: number; y: number }): Promise<boolean> {
    const windowId = this.windows.windowAt(point);
    const sessions = this.foreign && windowId ? await this.foreign.sessionsIn(hwnd) : [];
    if (!windowId || sessions.length === 0) return false;
    void this.takeOverWindow({ hwnd, title: win32()?.title(hwnd) ?? '', windowId, point, sessions });
    return true;
  }

  // ---------- notifications and tray ----------

  private notify(sessionId: string, kind: NotifyKind): void {
    const win = this.windowOf(sessionId);
    const record = this.state.sessions[sessionId];
    if (!win || !record) return;
    const skip = (reason: string) => this.log('info', 'notification skipped', { session: sessionId, kind, reason });
    const seen = this.windows.isAttentive(win.id) && !isCollapsed(win, sessionId);
    // Waiting where nobody looks: its window's taskbar button flashes too, until the window is looked at.
    if (kind === 'waiting' && !seen && !process.env.REFRAME_DISABLE_NOTIFICATIONS) this.windows.flash(win.id);
    if (!Notification.isSupported() || process.env.REFRAME_DISABLE_NOTIFICATIONS) return skip('notifications off');
    if (seen) return skip('window in focus, tile visible');
    const label = this.sessionView(record).label;
    const title = translate(this.locale, kind === 'done' ? 'notify.done' : 'notify.waiting', { label });
    const body = translate(this.locale, kind === 'done' ? 'notify.doneBody' : 'notify.waitingBody');
    const notification = new Notification(
      // On Windows the toast's launch argument names the session: a click reaches handleActivation (below) even from
      // Action Center or after this object is gone, so the instance 'click' is only listened to elsewhere.
      process.platform === 'win32'
        ? { toastXml: sessionToastXml(sessionId, title, body) }
        : { title, body, icon: this.assets.icon },
    );
    if (process.platform !== 'win32') notification.on('click', () => this.focusFromNotification(sessionId));
    notification.on('failed', (_event, error) =>
      this.log('warn', 'notification failed', { session: sessionId, error: String(error) }),
    );
    this.notifications.keep(sessionId, notification);
    notification.show();
    this.log('info', 'notification shown', { session: sessionId, kind });
  }

  /** Toast clicks on Windows, also from Action Center: the session named in the activation arguments comes up. */
  private watchToastActivation(): void {
    if (process.platform !== 'win32' || typeof Notification.handleActivation !== 'function') return;
    Notification.handleActivation((details) => {
      const sessionId = sessionFromActivation(details.arguments);
      if (!sessionId) return this.log('info', 'notification activation ignored', { type: details.type });
      this.focusFromNotification(sessionId);
    });
  }

  private focusFromNotification(sessionId: string): void {
    this.log('info', 'notification clicked', { session: sessionId, known: !!this.windowOf(sessionId) });
    if (!this.windowOf(sessionId)) return this.revealWindows();
    // Windows only lets a background process take the foreground with steal; the click is the user's request.
    app.focus({ steal: true });
    this.focusSession(sessionId);
  }

  private focusSession(sessionId: string): void {
    const found = this.windowOf(sessionId);
    if (!found) return;
    this.windows.show(found.id);
    // Its project comes up in the window (a notification may be about one that is hidden there).
    this.showProjectOf(found.id, sessionId);
    const win = this.windowOf(sessionId) ?? found;
    for (const entry of win.dock) {
      if (entry.kind === 'tile' && entry.sessionId === sessionId) {
        this.mutateWindow(win.id, (w) => applyLayoutAction(w, { type: 'tile/expand', sessionId }));
      } else if (entry.kind === 'group' && leaves(entry.node).includes(sessionId)) {
        this.mutateWindow(win.id, (w) => applyLayoutAction(w, { type: 'group/expand', groupId: entry.node.id }));
      }
    }
    this.windows.send(win.id, IPC.focusTile, sessionId);
  }

  /** Asks whether to create a missing session folder; creates it (with parents) when confirmed. */
  private async offerToCreateFolder(windowId: string, folder: string): Promise<boolean> {
    if (!folder || !path.isAbsolute(folder) || process.env.REFRAME_E2E) return false;
    const win = this.windows.get(windowId);
    const options = {
      type: 'question' as const,
      defaultId: 0,
      cancelId: 1,
      buttons: [this.t('dialog.createFolder.create'), this.t('dialog.createFolder.cancel')],
      message: this.t('dialog.createFolder.message', { folder }),
    };
    const answer = win ? await dialog.showMessageBox(win, options) : await dialog.showMessageBox(options);
    if (answer.response !== 0) return false;
    try {
      await fs.promises.mkdir(folder, { recursive: true });
    } catch (error) {
      this.log('warn', 'could not create session folder', { folder, error: String(error) });
      return false;
    }
    return isFolder(folder);
  }

  private t = (key: MessageKey, params?: Record<string, string | number>): string =>
    translate(this.locale, key, params);

  private createTray(): void {
    const t = this.t;
    const { refresh, refreshTooltip } = createTray(this.assets.trayIcon, this.assets.trayIcon2x, {
      labels: () => ({
        tooltip: this.waitingTotal > 0 ? this.waitingText(this.waitingTotal) : t('tray.tooltip'),
        newSession: t('tray.newSession'),
        newWindow: t('tray.newWindow'),
        about: t('tray.about'),
        quit: t('tray.quit'),
        endSessionsAndQuit: t('tray.endSessionsAndQuit'),
      }),
      windows: () =>
        this.state.windows.map((w, i) => {
          const labels = windowSessions(w)
            .map((id) => this.state.sessions[id])
            .filter((r): r is SessionRecord => !!r)
            .map((r) => this.sessionView(r).label);
          const names = labels.length > 3 ? `${labels.slice(0, 3).join(', ')}, …` : labels.join(', ');
          const label = t('tray.window', { n: i + 1 }) + (names ? ` – ${names}` : '');
          return { id: w.id, label, visible: !w.hidden };
        }),
      toggleWindow: (id, visible) => (visible ? this.windows.show(id) : this.windows.get(id)?.hide()),
      showPrimary: () => this.windows.show(this.state.windows[0]!.id),
      newSession: () => {
        const id = this.state.windows[0]!.id;
        this.windows.show(id);
        this.windows.send(id, IPC.openNewSession, null);
      },
      newWindow: () => this.newWindow(),
      about: () => {
        const id = this.state.windows[0]!.id;
        this.windows.show(id);
        this.windows.send(id, IPC.openAbout, null);
      },
      update: () => {
        if (this.updates.ready) {
          return {
            label: t('tray.restartToUpdate', { version: this.updates.ready }),
            install: () => this.restartToUpdate(),
          };
        }
        // The portable copy: the new version is downloaded by hand.
        if (this.updates.available) {
          return {
            label: t('tray.downloadUpdate', { version: this.updates.available }),
            install: () => void shell.openExternal(RELEASES_URL),
          };
        }
        return null;
      },
      quit: () => app.quit(),
      endSessionsAndQuit: () => this.endSessionsAndQuit(),
    });
    this.refreshTray = refresh;
    this.refreshTrayTooltip = refreshTooltip;
  }

  /** Whether the system starts reframe (no if that cannot be read). The end-to-end tests leave the system alone. */
  private readStartWithSystem(): boolean {
    if (process.env.REFRAME_E2E) return false;
    try {
      return autostartEnabled();
    } catch {
      return false;
    }
  }

  private setStartWithSystem(enabled: boolean): void {
    try {
      if (!process.env.REFRAME_E2E) setAutostart(enabled);
      this.startsWithSystem = enabled;
      this.log('info', enabled ? 'starts with the system now' : 'no longer starts with the system');
    } catch (error) {
      this.log('warn', 'could not change starting with the system', { error: String(error) });
    }
    for (const w of this.state.windows) this.push(w.id);
  }

  private watchUpdates(): void {
    this.updates.on('ready', (version) => {
      this.refreshTray();
      if (!Notification.isSupported() || process.env.REFRAME_DISABLE_NOTIFICATIONS) return;
      new Notification({
        title: this.t('update.readyTitle'),
        body: this.t('update.readyBody', { version }),
        icon: this.assets.icon,
      }).show();
    });
    this.updates.on('available', (version) => {
      this.refreshTray();
      if (!Notification.isSupported() || process.env.REFRAME_DISABLE_NOTIFICATIONS) return;
      const notification = new Notification({
        title: this.t('update.availableTitle', { version }),
        body: this.t('update.availableBody'),
        icon: this.assets.icon,
      });
      notification.on('click', () => void shell.openExternal(RELEASES_URL));
      notification.show();
    });
    this.updates.start();
  }

  /** Ends the sessions as on quit (they resume after the restart), then installs the update. */
  private async restartToUpdate(): Promise<void> {
    this.log('info', 'restarting to install an update', { version: this.updates.ready });
    await this.shutdown();
    this.updates.install();
  }

  // ---------- IPC ----------

  private registerIpc(): void {
    // Only reframe's own windows are listened to: each handler gets the id of the window that sent the message. The
    // pages are reframe's own and cannot navigate away, so anything else is a bug (or worse) and is ignored.
    const from = (channel: string, event: IpcMainEvent | IpcMainInvokeEvent): string | null => {
      const id = this.windows.idOf(event.sender);
      if (!id && !this.strangeSenders.has(channel)) {
        this.strangeSenders.add(channel);
        this.log('warn', 'message from something that is no window of reframe, ignored', { channel });
      }
      return id ?? null;
    };
    const on = <A extends unknown[]>(
      channel: string,
      listener: (id: string, event: IpcMainEvent, ...args: A) => void,
    ) =>
      ipcMain.on(channel, (event, ...args) => {
        const id = from(channel, event);
        if (id) listener(id, event, ...(args as A));
      });
    const handle = <A extends unknown[]>(
      channel: string,
      listener: (id: string, event: IpcMainInvokeEvent, ...args: A) => unknown,
    ) =>
      ipcMain.handle(channel, (event, ...args) => {
        const id = from(channel, event);
        return id ? listener(id, event, ...(args as A)) : null;
      });
    /** Whether the window shows the session (tile or collapsed). */
    const shows = (id: string, sessionId: unknown): sessionId is string => {
      const win = this.findWindow(id);
      return typeof sessionId === 'string' && !!win && windowHasSession(win, sessionId);
    };

    handle(IPC.getView, (id) => this.view(id));
    on(IPC.dispatch, (id, _event, action: unknown) => {
      if (!isLayoutAction(action)) return;
      this.mutateWindow(id, (w) => applyLayoutAction(w, action), action.type === 'split/resize' ? 'soon' : 'now');
    });
    handle(IPC.createSession, (id, _event, request: NewSessionRequest | null) => {
      if (!request || typeof request.cwd !== 'string') return { ok: false, error: 'folderMissing' };
      return this.createSession(id, request);
    });
    on(IPC.restartSession, (id, _event, sessionId: unknown, fresh: unknown) => {
      if (shows(id, sessionId)) this.restartSession(id, sessionId, fresh === true);
    });
    on(IPC.endSession, (id, _event, sessionId: unknown) => {
      if (shows(id, sessionId)) this.endSession(id, sessionId);
    });
    on(IPC.attachTerminal, (id, event, sessionId: unknown) => {
      if (shows(id, sessionId)) this.host.attach(sessionId, event.sender);
    });
    handle(IPC.pickFolder, async (id) => {
      const win = this.windows.get(id);
      const options = { properties: ['openDirectory' as const] };
      const result = win ? await dialog.showOpenDialog(win, options) : await dialog.showOpenDialog(options);
      return result.canceled ? null : (result.filePaths[0] ?? null);
    });
    on(IPC.transferNode, (id, _event, request: Partial<TransferRequest> | null) => {
      if (!request || typeof request.key !== 'string' || typeof request.fromWindowId !== 'string') return;
      if (request.target !== null && !isDropTarget(request.target)) return;
      this.transferNode(id, { key: request.key, fromWindowId: request.fromWindowId, target: request.target });
    });
    on(IPC.newWindow, (id) => this.newWindow(id));
    on(IPC.toggleFullscreen, (id) => this.windows.toggleFullscreen(id));
    on(IPC.focusNextWaiting, (id, _event, after: unknown) =>
      this.focusNextWaiting(id, typeof after === 'string' ? after : null),
    );
    handle(IPC.readClipboard, () => this.clipboardOverride ?? readClipboard());
    handle(IPC.saveDroppedFile, (_id, _event, name: unknown, data: unknown) =>
      typeof name === 'string' && data instanceof ArrayBuffer ? this.saveDroppedFile(name, data) : null,
    );
    on(IPC.tearOut, (id, _event, key: unknown, size: Partial<TileSize> | null) => {
      if (typeof key !== 'string') return;
      const valid = !!size && Number.isFinite(size.width) && Number.isFinite(size.height);
      this.tearOut(id, key, screen.getCursorScreenPoint(), valid ? (size as TileSize) : undefined);
    });
    on(IPC.tileMenu, (id, _event, sessionId: unknown) => {
      if (typeof sessionId === 'string') this.showTileMenu(id, sessionId);
    });
    on(IPC.renameSession, (_id, _event, sessionId: unknown, name: unknown) => {
      if (typeof sessionId === 'string' && typeof name === 'string') this.renameSession(sessionId, name);
    });
    on(IPC.listMenu, (id, _event, items: unknown) => {
      if (isListItems(items)) this.showListMenu(id, items);
    });
    on(IPC.showProject, (id, _event, key: unknown) => {
      if (typeof key === 'string') this.showProjectIn(id, key);
    });
    on(IPC.setProjectExpanded, (_id, _event, folder: unknown, expanded: unknown) => {
      if (typeof folder !== 'string' || typeof expanded !== 'boolean') return;
      const key = workspaceKey(folder);
      if (!this.listedProjects.has(key)) return;
      this.state = {
        ...this.state,
        expandedProjects: expanded
          ? [...new Set([...(this.state.expandedProjects || []), key])]
          : (this.state.expandedProjects || []).filter((entry) => entry !== key),
      };
      this.save();
      this.listChanged();
    });
    on(IPC.openSessions, (id, _event, request: Partial<OpenSessionsRequest> | null) => {
      if (!isListItems(request?.items) || !OPEN_MODES.includes(request.mode as OpenMode)) return;
      if (request.target !== null && !isDropTarget(request.target)) return;
      void this.openSessions(id, request.items, request.mode as OpenMode, request.target ?? null).catch(
        (error: unknown) => this.log('error', 'opening sessions from the list failed', { error: String(error) }),
      );
    });
    on(IPC.groupMenu, (id, _event, groupId: unknown) => {
      if (typeof groupId === 'string') this.showGroupMenu(id, groupId, false);
    });
    on(IPC.groupColorMenu, (id, _event, groupId: unknown) => {
      if (typeof groupId === 'string') this.showGroupMenu(id, groupId, true);
    });
    on(IPC.openExternal, (_id, _event, url: unknown) => {
      if (typeof url === 'string' && /^https?:\/\//i.test(url)) void shell.openExternal(url);
    });
    handle(IPC.dialogDefaults, () => ({
      providerWorkspaceRoots: {
        chatgpt: path.join(app.getPath('documents'), 'ChatGPT'),
        anthropic: path.join(app.getPath('documents'), 'Anthropic'),
      },
      recentFolders: this.state.recentFolders.filter((folder) => !!this.wslPath(folder) || isDirectory(folder)),
      preferences: this.state.preferences,
      claude: this.findClaude()?.source ?? null,
    }));
    handle(IPC.pickClaude, async (id) => {
      const win = this.windows.get(id);
      const options: OpenDialogOptions = {
        properties: ['openFile'],
        ...(process.platform === 'win32'
          ? {
              filters: [
                { name: this.t('settings.programs'), extensions: ['exe', 'cmd', 'bat'] },
                { name: this.t('settings.allFiles'), extensions: ['*'] },
              ],
            }
          : {}),
      };
      const result = win ? await dialog.showOpenDialog(win, options) : await dialog.showOpenDialog(options);
      return result.canceled ? null : (result.filePaths[0] ?? null);
    });
    on(IPC.setPreferences, (_id, _event, patch: PreferencesPatch | null) => {
      if (!patch || typeof patch !== 'object') return;
      const next = { ...this.state.preferences };
      if (typeof patch.archiveRoot === 'string' && isArchiveRoot(patch.archiveRoot))
        next.archiveRoot = patch.archiveRoot;
      if (typeof patch.nameFromFolder === 'boolean') next.nameFromFolder = patch.nameFromFolder;
      if (LANGUAGES.includes(patch.language as Language)) next.language = patch.language as Language;
      const size = patch.fontSize;
      if (Number.isInteger(size) && size! >= FONT_SIZE.min && size! <= FONT_SIZE.max) next.fontSize = size!;
      if (typeof patch.sidebarWidth === 'number') next.sidebarWidth = clampSidebarWidth(patch.sidebarWidth);
      if (typeof patch.snapToGrid === 'boolean') next.snapToGrid = patch.snapToGrid;
      if (typeof patch.checkForUpdates === 'boolean') next.checkForUpdates = patch.checkForUpdates;
      if (typeof patch.lookInWsl === 'boolean') next.lookInWsl = patch.lookInWsl;
      if (CLOSE_WINDOW.includes(patch.closeWindow as CloseWindow)) next.closeWindow = patch.closeWindow as CloseWindow;
      if (typeof patch.claudeBin === 'string') next.claudeBin = patch.claudeBin.trim().slice(0, 1024) || undefined;
      if (typeof patch.defaultModel === 'string')
        next.defaultModel = patch.defaultModel.trim().slice(0, 100) || undefined;
      if (patch.defaultEffort === '') next.defaultEffort = undefined;
      else if (EFFORT_LEVELS.includes(patch.defaultEffort as (typeof EFFORT_LEVELS)[number]))
        next.defaultEffort = patch.defaultEffort as (typeof EFFORT_LEVELS)[number];
      if (patch.defaultPermissionMode === '') next.defaultPermissionMode = undefined;
      else if (PERMISSION_MODES.includes(patch.defaultPermissionMode as PermissionMode)) {
        next.defaultPermissionMode = patch.defaultPermissionMode as PermissionMode;
      }
      if (next.claudeBin !== this.state.preferences.claudeBin) {
        this.log('info', 'claude to run changed', { claude: next.claudeBin ?? 'the one on the PATH' });
      }
      const relabel = next.language !== this.state.preferences.language;
      const was = this.state.preferences;
      const shown =
        next.archiveRoot !== was.archiveRoot ||
        next.fontSize !== was.fontSize ||
        next.sidebarWidth !== was.sidebarWidth ||
        next.snapToGrid !== was.snapToGrid ||
        next.checkForUpdates !== was.checkForUpdates ||
        next.lookInWsl !== was.lookInWsl ||
        next.closeWindow !== was.closeWindow ||
        next.claudeBin !== was.claudeBin ||
        next.defaultModel !== was.defaultModel ||
        next.defaultEffort !== was.defaultEffort ||
        next.defaultPermissionMode !== was.defaultPermissionMode;
      this.state = { ...this.state, preferences: next };
      this.save();
      if (relabel) {
        // Every window, the tray and whatever comes next (menus, dialogs, notifications) speak it right away.
        this.applyLanguage();
        this.log('info', 'language changed', { language: next.language, locale: this.locale });
        this.refreshTray();
      }
      if (relabel || shown) for (const w of this.state.windows) this.push(w.id);
      if (next.lookInWsl !== was.lookInWsl) {
        // On: looked in now; off: what was found there leaves the list.
        this.log('info', 'looking for Claude Code in WSL', { on: next.lookInWsl });
        void this.scanOutside(next.lookInWsl ? 0 : null).then(() => this.listChanged());
      }
    });
    on(IPC.setStartWithSystem, (_id, _event, enabled: unknown) => {
      if (typeof enabled === 'boolean') this.setStartWithSystem(enabled);
    });
    handle(IPC.folderExists, (_id, _event, folder: unknown) => typeof folder === 'string' && isFolder(folder));
    on(IPC.startWsl, (_id, _event, distro: unknown) => {
      if (typeof distro !== 'string' || !(this.state.wslDistros ?? []).includes(distro) || !this.wsl) return;
      void this.wsl
        .keep(distro)
        .then(() => this.scanOutside(0))
        .then(() => this.listChanged())
        .catch((error: unknown) =>
          this.log('warn', 'could not start WSL distribution', { distro, error: String(error) }),
        );
    });
    handle(IPC.saveClipboardImage, async () => {
      const png = this.clipboardOverride ? (this.clipboardOverride.image ? TEST_PNG : null) : await clipboardImage();
      return png && this.saveDroppedFile('clipboard.png', png);
    });
    handle(IPC.restoreArchive, async (_id, _event, chat: unknown) => {
      if (typeof chat !== 'string') return { ok: false, error: 'Invalid archive' };
      return this.trackHistory(() => this.restoreChat(chat));
    });
    handle(IPC.changeChatModels, async (id, _event, items: unknown, provider: unknown, model: unknown) => {
      if (
        !isListItems(items) ||
        !isProvider(provider) ||
        typeof model !== 'string' ||
        model.length > 300 ||
        Array.from(model).some((character) => character.charCodeAt(0) < 32)
      )
        throw Error('Invalid selected chats/provider/model.');
      return this.trackHistory(() => this.runChatBatch(id, items, 'model', provider, model));
    });
    handle(IPC.sessionList, async (_id, _event, query: unknown, limit: unknown, folder: unknown, provider: unknown) => {
      // Asked for by the user (opening the list, searching, after an action there): as fresh as before.
      await this.scanOutside(LIST_ASKED_MS);
      return this.sessionList(
        typeof query === 'string' ? query : '',
        typeof limit === 'number' && Number.isSafeInteger(limit) && limit > 0 ? limit : LIST_PAGE,
        typeof folder === 'string' ? folder : undefined,
        isProvider(provider) ? provider : undefined,
      );
    });
    on(IPC.openSession, (id, _event, request: Partial<OpenSessionRequest> | null) => {
      const item = request?.item;
      if (!item || !isListItemRef(item)) return;
      if (request.target !== null && !isDropTarget(request.target)) return;
      void this.openSession(id, item, request.target ?? null).catch((error: unknown) =>
        this.log('error', 'opening a session from the list failed', { error: String(error) }),
      );
    });
    on(IPC.foreignPointer, (id, _event, report: Partial<ForeignPointerReport> | null) => {
      if (typeof report?.x !== 'number' || typeof report.y !== 'number') return this.foreign?.notePointer(id, null, []);
      const types = Array.isArray(report.types) ? report.types.filter((t) => typeof t === 'string') : [];
      this.foreign?.notePointer(id, { x: report.x, y: report.y }, types);
    });
    on(IPC.foreignDropAnswer, (id, _event, requestId: unknown, answer: Partial<ForeignDropAnswer> | null) => {
      const request = typeof requestId === 'string' ? this.foreignRequests.get(requestId) : undefined;
      // Answered by the window that was asked, once.
      if (!request || request.windowId !== id) return;
      this.foreignRequests.delete(requestId as string);
      const valid =
        !!answer &&
        Array.isArray(answer.claudeSessionIds) &&
        answer.claudeSessionIds.every((s) => typeof s === 'string') &&
        (answer.target === null || isDropTarget(answer.target));
      request.resolve(valid ? { claudeSessionIds: answer.claudeSessionIds!, target: answer.target ?? null } : null);
    });
    on(IPC.removeSession, (_id, _event, sessionId: unknown) => {
      if (typeof sessionId === 'string') this.removeSession(sessionId);
    });
  }
  /**
   * The claude sessions run (in WSL folders their distribution's instead): the one chosen in Settings, else
   * REFRAME_CLAUDE_BIN (tests: a .js fake run by REFRAME_NODE), else the one on the PATH.
   */
  private findClaude(provider?: Provider): ClaudeBinary | null {
    // A binary the launch config refused (missing or changed) never falls back to the PATH or the other provider.
    const refused = refusedProviders[provider ?? 'chatgpt'];
    if (refused) {
      this.log('error', 'provider refused by launch config', { provider: provider ?? 'chatgpt', reason: refused });
      return null;
    }
    const node = process.env.REFRAME_NODE;
    // Adopted clients (managed install): each new session starts on the newest adopted copy, verified by hash. It
    // wins over the Settings choice, which the managed launcher pins to its own route anyway.
    const adoption = process.env.REFRAME_CLIENT_ADOPTION;
    if (adoption) {
      const adopted = adoptedClient(adoption, provider ?? 'chatgpt');
      if (!adopted.ok) {
        this.log('error', 'adopted client refused', { provider: provider ?? 'chatgpt', reason: adopted.reason });
        return null;
      }
      return resolveClaude({ env: process.env, platform: process.platform, override: adopted.path });
    }
    return resolveClaude({
      env: process.env,
      platform: process.platform,
      override:
        (provider === 'anthropic' ? process.env.REFRAME_ANTHROPIC_CLAUDE_BIN : undefined) ||
        this.state.preferences.claudeBin ||
        process.env.REFRAME_CLAUDE_BIN,
      nodeRunner: node ? { file: node, env: {} } : undefined,
    });
  }

  /** Channels a message came in on from something that is no window of reframe (logged once each). */
  private strangeSenders = new Set<string>();
}

const OPEN_MODES: OpenMode[] = ['open', 'group', 'groupInNewWindow'];

function isListItems(value: unknown): value is ListItemRef[] {
  return Array.isArray(value) && value.length > 0 && value.length <= 200 && value.every(isListItemRef);
}

function isListItemRef(value: unknown): value is ListItemRef {
  if (!value || typeof value !== 'object') return false;
  const v = value as Record<string, unknown>;
  if (v.kind === 'app') return typeof v.sessionId === 'string';
  if (v.distro !== undefined && typeof v.distro !== 'string') return false;
  if (v.kind === 'found') return typeof v.claudeSessionId === 'string';
  if (v.kind === 'running') return typeof v.pid === 'number' && typeof v.claudeSessionId === 'string';
  return false;
}

/** Whether a folder is there, without holding up the app (a WSL folder may wait for its distribution to start). */
function isFolder(folder: string): Promise<boolean> {
  return fs.promises.stat(folder).then(
    (stats) => stats.isDirectory(),
    () => false,
  );
}

/** A 1×1 PNG, for the clipboard image the tests fake. */
const TEST_PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

/** Whether two paths name the same folder, as this system compares them. */
function samePath(a: string, b: string): boolean {
  const norm = (p: string) => {
    const resolved = path.resolve(p);
    return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
  };
  return norm(a) === norm(b);
}

function isDirectory(folder: string): boolean {
  try {
    return fs.statSync(folder).isDirectory();
  } catch {
    return false;
  }
}
