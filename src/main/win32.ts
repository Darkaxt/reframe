// Win32 calls the main process makes itself (no console attaching; see the console helper for that):
// following window drags and new windows of other apps, and reading a foreign window. Loaded only on Windows.
import koffi from 'koffi';

export interface Win32 {
  /** Calls back when the user starts and ends moving a window of another app (title-bar drag). */
  onMoveSize(callback: (phase: 'start' | 'end', hwnd: string) => void): void;
  /** Calls back when another app creates or shows a top-level window. */
  onTopLevel(callback: (phase: 'created' | 'shown', hwnd: string) => void): void;
  /** Window rectangle in physical screen pixels. */
  rect(hwnd: string): { x: number; y: number; width: number; height: number } | null;
  /** The window in front (the one the user works in), if any. */
  foreground(): string | null;
  processId(hwnd: string): number;
  /** Executable file name of a process, e.g. "WindowsTerminal.exe". */
  imageName(pid: number): string | null;
  /** When a process was created (a FILETIME as a decimal string, as Claude Code's registry has it). */
  processStart(pid: number): string | null;
  /**
   * The direct children of a process (one toolhelp snapshot of the system's processes), with their executable's
   * file name and creation time (as processStart tells it; null if it cannot be opened).
   */
  childProcesses(pid: number): { pid: number; exe: string; start: string | null }[];
  title(hwnd: string): string;
  /** Whether the window still exists. */
  isWindow(hwnd: string): boolean;
  /** Files copied in Explorer (or another file manager) that are on the clipboard. */
  clipboardFiles(): string[];
  /**
   * Removes the window event hooks (onMoveSize, onTopLevel). Done on quitting, before Electron tears down the
   * JavaScript that their native callbacks call into: an event delivered after that would fail inside the callback.
   */
  unhookAll(): void;
  /** Has Windows close reframe before other programs (its claudes among them) when it shuts down or logs off. */
  shutDownFirst(): void;
  /**
   * Starts a process that outlives reframe (the PTY host): hidden, without a console, out of reframe's job if it may
   * leave it, and inheriting none of reframe's handles (whoever started reframe with pipes as its standard handles
   * would otherwise wait for that process to end too). Returns its process id.
   */
  spawnDetached(file: string, args: string[], env: Record<string, string>, cwd: string): number;
}

let instance: Win32 | null = null;

export function win32(): Win32 | null {
  if (process.platform !== 'win32') return null;
  instance ??= load();
  return instance;
}

function load(): Win32 {
  const user32 = koffi.load('user32.dll');
  const kernel32 = koffi.load('kernel32.dll');
  const WINEVENTPROC = koffi.proto(
    'void __stdcall WINEVENTPROC(void* hook, uint32_t event, void* hwnd, int32_t idObject, int32_t idChild, uint32_t thread, uint32_t time)',
  );
  const SetWinEventHook = user32.func(
    'void* __stdcall SetWinEventHook(uint32_t eventMin, uint32_t eventMax, void* module, WINEVENTPROC* callback, uint32_t pid, uint32_t thread, uint32_t flags)',
  );
  const UnhookWinEvent = user32.func('bool __stdcall UnhookWinEvent(void* hook)');
  const GetWindowThreadProcessId = user32.func(
    'uint32_t __stdcall GetWindowThreadProcessId(void* hwnd, _Out_ uint32_t* pid)',
  );
  const GetWindowTextW = user32.func('int __stdcall GetWindowTextW(void* hwnd, _Out_ uint16_t* text, int max)');
  const IsWindow = user32.func('bool __stdcall IsWindow(void* hwnd)');
  const GetForegroundWindow = user32.func('void* __stdcall GetForegroundWindow()');
  const GetAncestor = user32.func('void* __stdcall GetAncestor(void* hwnd, uint32_t flags)');
  koffi.struct('RECT', { left: 'int32_t', top: 'int32_t', right: 'int32_t', bottom: 'int32_t' });
  const GetWindowRect = user32.func('bool __stdcall GetWindowRect(void* hwnd, _Out_ RECT* rect)');
  const OpenProcess = kernel32.func('void* __stdcall OpenProcess(uint32_t access, bool inherit, uint32_t pid)');
  const QueryFullProcessImageNameW = kernel32.func(
    'bool __stdcall QueryFullProcessImageNameW(void* process, uint32_t flags, _Out_ uint16_t* name, _Inout_ uint32_t* size)',
  );
  const CloseHandle = kernel32.func('bool __stdcall CloseHandle(void* handle)');
  koffi.struct('FILETIME', { low: 'uint32_t', high: 'uint32_t' });
  const GetProcessTimes = kernel32.func(
    'bool __stdcall GetProcessTimes(void* process, _Out_ FILETIME* creation, _Out_ FILETIME* exit, _Out_ FILETIME* kernel, _Out_ FILETIME* user)',
  );
  // Toolhelp snapshots as integers (INVALID_HANDLE_VALUE is -1).
  const CreateToolhelp32Snapshot = kernel32.func(
    'intptr_t __stdcall CreateToolhelp32Snapshot(uint32_t flags, uint32_t pid)',
  );
  koffi.struct('PROCESSENTRY32W', {
    dwSize: 'uint32_t',
    cntUsage: 'uint32_t',
    th32ProcessID: 'uint32_t',
    th32DefaultHeapID: 'uintptr_t',
    th32ModuleID: 'uint32_t',
    cntThreads: 'uint32_t',
    th32ParentProcessID: 'uint32_t',
    pcPriClassBase: 'int32_t',
    dwFlags: 'uint32_t',
    szExeFile: koffi.array('char16_t', 260, 'String'),
  });
  const Process32FirstW = kernel32.func(
    'bool __stdcall Process32FirstW(intptr_t snapshot, _Inout_ PROCESSENTRY32W* entry)',
  );
  const Process32NextW = kernel32.func(
    'bool __stdcall Process32NextW(intptr_t snapshot, _Inout_ PROCESSENTRY32W* entry)',
  );
  const CloseSnapshot = kernel32.func('bool __stdcall CloseHandle(intptr_t handle)');
  const SetProcessShutdownParameters = kernel32.func(
    'bool __stdcall SetProcessShutdownParameters(uint32_t level, uint32_t flags)',
  );
  koffi.struct('STARTUPINFOW', {
    cb: 'uint32_t',
    lpReserved: 'void*',
    lpDesktop: 'void*',
    lpTitle: 'void*',
    dwX: 'uint32_t',
    dwY: 'uint32_t',
    dwXSize: 'uint32_t',
    dwYSize: 'uint32_t',
    dwXCountChars: 'uint32_t',
    dwYCountChars: 'uint32_t',
    dwFillAttribute: 'uint32_t',
    dwFlags: 'uint32_t',
    wShowWindow: 'uint16_t',
    cbReserved2: 'uint16_t',
    lpReserved2: 'void*',
    hStdInput: 'void*',
    hStdOutput: 'void*',
    hStdError: 'void*',
  });
  koffi.struct('PROCESS_INFORMATION', {
    hProcess: 'void*',
    hThread: 'void*',
    dwProcessId: 'uint32_t',
    dwThreadId: 'uint32_t',
  });
  const CreateProcessW = kernel32.func(
    'bool __stdcall CreateProcessW(void* app, void* commandLine, void* processAttributes, void* threadAttributes, bool inherit, uint32_t flags, void* environment, void* cwd, STARTUPINFOW* startup, _Out_ PROCESS_INFORMATION* info)',
  );
  const GetLastError = kernel32.func('uint32_t __stdcall GetLastError()');
  const shell32 = koffi.load('shell32.dll');
  const IsClipboardFormatAvailable = user32.func('bool __stdcall IsClipboardFormatAvailable(uint32_t format)');
  const OpenClipboard = user32.func('bool __stdcall OpenClipboard(void* owner)');
  const CloseClipboard = user32.func('bool __stdcall CloseClipboard()');
  const GetClipboardData = user32.func('void* __stdcall GetClipboardData(uint32_t format)');
  const DragQueryFileW = shell32.func(
    'uint32_t __stdcall DragQueryFileW(void* drop, uint32_t index, _Out_ uint16_t* file, uint32_t size)',
  );

  const EVENT_SYSTEM_MOVESIZESTART = 0x000a;
  const EVENT_SYSTEM_MOVESIZEEND = 0x000b;
  const EVENT_OBJECT_CREATE = 0x8000;
  const EVENT_OBJECT_SHOW = 0x8002;
  const CHILDID_SELF = 0;
  const GA_ROOT = 2;
  const WINEVENT_SKIPOWNPROCESS = 0x0002;
  const OBJID_WINDOW = 0;
  const PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
  const CF_HDROP = 15;
  // Programs start at 0x280; 0x300 to 0x3FF is the range of programs Windows closes first.
  const SHUTDOWN_LEVEL_FIRST = 0x300;

  const toHandle = (hwnd: string) => BigInt(hwnd);
  const callbacks: unknown[] = []; // keeps registered callbacks alive (also after unhooking: one may be under way)
  const hooks: unknown[] = [];
  let unhooked = false;
  const hook = (eventMin: number, eventMax: number, callback: (...args: never[]) => void) => {
    if (unhooked) return;
    // A JavaScript exception must not unwind through the native frame that called back.
    const guarded = (...args: never[]) => {
      if (unhooked) return;
      try {
        callback(...args);
      } catch {
        // Following other apps' windows is best effort.
      }
    };
    const registered = koffi.register(guarded, koffi.pointer(WINEVENTPROC));
    callbacks.push(registered);
    // Out-of-context hooks are delivered through the message loop of this (the UI) thread.
    const handle: unknown = SetWinEventHook(eventMin, eventMax, null, registered, 0, 0, WINEVENT_SKIPOWNPROCESS);
    if (handle) hooks.push(handle);
  };

  return {
    onMoveSize(callback) {
      hook(
        EVENT_SYSTEM_MOVESIZESTART,
        EVENT_SYSTEM_MOVESIZEEND,
        (_hook: unknown, event: number, hwnd: bigint | null, idObject: number) => {
          if (idObject !== OBJID_WINDOW || !hwnd) return;
          callback(event === EVENT_SYSTEM_MOVESIZESTART ? 'start' : 'end', String(hwnd));
        },
      );
    },
    onTopLevel(callback) {
      // The range includes EVENT_OBJECT_DESTROY, which is skipped like every other event.
      hook(
        EVENT_OBJECT_CREATE,
        EVENT_OBJECT_SHOW,
        (_hook: unknown, event: number, hwnd: bigint | null, idObject: number, idChild: number) => {
          if (idObject !== OBJID_WINDOW || idChild !== CHILDID_SELF || !hwnd) return;
          if (event !== EVENT_OBJECT_CREATE && event !== EVENT_OBJECT_SHOW) return;
          if (String(GetAncestor(hwnd, GA_ROOT)) !== String(hwnd)) return; // a child window
          callback(event === EVENT_OBJECT_CREATE ? 'created' : 'shown', String(hwnd));
        },
      );
    },
    rect(hwnd) {
      const rect = { left: 0, top: 0, right: 0, bottom: 0 };
      if (!GetWindowRect(toHandle(hwnd), rect)) return null;
      return { x: rect.left, y: rect.top, width: rect.right - rect.left, height: rect.bottom - rect.top };
    },
    foreground() {
      const hwnd: unknown = GetForegroundWindow();
      return hwnd ? String(hwnd) : null;
    },
    processId(hwnd) {
      const pid = [0];
      GetWindowThreadProcessId(toHandle(hwnd), pid);
      return pid[0]!;
    },
    imageName(pid) {
      const target = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
      if (!target) return null;
      try {
        const buffer = new Uint16Array(1024);
        const size = [buffer.length];
        if (!QueryFullProcessImageNameW(target, 0, buffer, size)) return null;
        const full = String.fromCharCode(...buffer.slice(0, size[0]));
        return full.split('\\').pop() ?? full;
      } finally {
        CloseHandle(target);
      }
    },
    processStart(pid) {
      const target = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
      if (!target) return null;
      try {
        const [creation, exit, kernel, user] = [{}, {}, {}, {}] as { low?: number; high?: number }[];
        if (!GetProcessTimes(target, creation, exit, kernel, user)) return null;
        return ((BigInt(creation!.high ?? 0) << 32n) | BigInt(creation!.low ?? 0)).toString();
      } finally {
        CloseHandle(target);
      }
    },
    childProcesses(pid) {
      const TH32CS_SNAPPROCESS = 0x2;
      const snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
      if (snapshot === -1 || snapshot === 0) return [];
      const children: { pid: number; exe: string }[] = [];
      try {
        const entry = {
          dwSize: koffi.sizeof('PROCESSENTRY32W'),
          cntUsage: 0,
          th32ProcessID: 0,
          th32DefaultHeapID: 0,
          th32ModuleID: 0,
          cntThreads: 0,
          th32ParentProcessID: 0,
          pcPriClassBase: 0,
          dwFlags: 0,
          szExeFile: '',
        };
        for (let more = Process32FirstW(snapshot, entry); more; more = Process32NextW(snapshot, entry)) {
          if (entry.th32ParentProcessID === pid && entry.th32ProcessID !== pid)
            children.push({ pid: entry.th32ProcessID, exe: entry.szExeFile });
        }
      } finally {
        CloseSnapshot(snapshot);
      }
      return children.map((c) => ({ ...c, start: this.processStart(c.pid) }));
    },
    isWindow(hwnd) {
      return IsWindow(toHandle(hwnd));
    },
    unhookAll() {
      unhooked = true;
      for (const handle of hooks.splice(0)) UnhookWinEvent(handle);
    },
    shutDownFirst() {
      SetProcessShutdownParameters(SHUTDOWN_LEVEL_FIRST, 0);
    },
    spawnDetached(file, args, env, cwd) {
      const wide = (text: string) => Buffer.from(`${text} `, 'utf16le');
      const commandLine = wide([file, ...args].map((a) => `"${a}"`).join(' '));
      // Sorted (as Windows wants it) NAME=value strings, each ended by a NUL, and one more at the end.
      const block = Object.keys(env)
        .sort((a, b) => a.toUpperCase().localeCompare(b.toUpperCase()))
        .map((key) => `${key}=${env[key]} `)
        .join('');
      const environment = Buffer.from(`${block} `, 'utf16le');
      const STARTF_USESHOWWINDOW = 0x1;
      const SW_HIDE = 0;
      const startup = { cb: koffi.sizeof('STARTUPINFOW'), dwFlags: STARTF_USESHOWWINDOW, wShowWindow: SW_HIDE };
      const DETACHED_PROCESS = 0x8;
      const CREATE_NEW_PROCESS_GROUP = 0x200;
      const CREATE_UNICODE_ENVIRONMENT = 0x400;
      const CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
      const flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT;
      const info: { hProcess?: unknown; hThread?: unknown; dwProcessId?: number } = {};
      const create = (extra: number) =>
        CreateProcessW(null, commandLine, null, null, false, flags | extra, environment, wide(cwd), startup, info);
      // Out of reframe's job where that is allowed (a job that ends its processes with it would end the host too).
      if (!create(CREATE_BREAKAWAY_FROM_JOB) && !create(0)) {
        throw new Error(`could not start ${file} (error ${GetLastError()})`);
      }
      CloseHandle(info.hThread);
      CloseHandle(info.hProcess);
      return info.dwProcessId!;
    },
    clipboardFiles() {
      if (!IsClipboardFormatAvailable(CF_HDROP) || !OpenClipboard(null)) return [];
      try {
        const drop: unknown = GetClipboardData(CF_HDROP);
        if (!drop) return [];
        const files: string[] = [];
        const count = DragQueryFileW(drop, 0xffffffff, null, 0) as number;
        for (let i = 0; i < count; i++) {
          const length = DragQueryFileW(drop, i, null, 0) as number;
          const buffer = new Uint16Array(length + 1);
          DragQueryFileW(drop, i, buffer, buffer.length);
          files.push(String.fromCharCode(...buffer.slice(0, length)));
        }
        return files;
      } finally {
        CloseClipboard();
      }
    },
    title(hwnd) {
      const buffer = new Uint16Array(512);
      const length = GetWindowTextW(toHandle(hwnd), buffer, buffer.length);
      return String.fromCharCode(...buffer.slice(0, Math.max(0, length)));
    },
  };
}
