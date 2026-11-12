//! ConPTY for reframe's PTY host: the only crate of the host with unsafe code. Everything here is a thin wrapper
//! of Win32 calls; what to start, how to quote it and what to do with its output is decided by the safe caller.
//!
//! Like node-pty (which the Node host uses with `useConptyDll`), the ConPTY that comes with the application
//! (`conpty.dll`, which runs `OpenConsole.exe` from its own folder) is preferred over the one built into Windows,
//! which crashed its host now and then when several sessions ended at once. Without it, Windows' own is used.
#![cfg(windows)]
#![deny(unsafe_op_in_unsafe_fn)]
#![warn(clippy::undocumented_unsafe_blocks)]

use std::ffi::{c_void, OsStr};
use std::fs::File;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr;
use std::sync::{Mutex, OnceLock};

use windows_sys::core::{HRESULT, PCSTR};
use windows_sys::Win32::Foundation::{
    CloseHandle, GetLastError, ERROR_ACCESS_DENIED, FILETIME, HANDLE, HMODULE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0,
};
use windows_sys::Win32::System::Console::{
    ClosePseudoConsole, CreatePseudoConsole, GetStdHandle, ResizePseudoConsole, SetStdHandle, COORD, HPCON,
    STD_OUTPUT_HANDLE,
};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_WITH_ALTERED_SEARCH_PATH};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CreateProcessW, DeleteProcThreadAttributeList, GetExitCodeProcess, GetProcessTimes,
    InitializeProcThreadAttributeList, OpenProcess, TerminateProcess, UpdateProcThreadAttribute, WaitForSingleObject,
    CREATE_UNICODE_ENVIRONMENT, EXTENDED_STARTUPINFO_PRESENT, INFINITE, PROCESS_INFORMATION,
    PROCESS_QUERY_LIMITED_INFORMATION, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, STARTF_USESTDHANDLES, STARTUPINFOEXW,
};

type CreateFn = unsafe extern "system" fn(COORD, HANDLE, HANDLE, u32, *mut HPCON) -> HRESULT;
type ResizeFn = unsafe extern "system" fn(HPCON, COORD) -> HRESULT;
type CloseFn = unsafe extern "system" fn(HPCON);

/// The ConPTY functions in use: those of the bundled conpty.dll, or Windows' own.
struct Api {
    create: CreateFn,
    resize: ResizeFn,
    close: CloseFn,
    /// conpty.dll only: lets OpenConsole end with its last client (so the output ends when the process does).
    release: Option<CloseFn>,
    bundled: bool,
}

fn kernel_api() -> Api {
    Api {
        create: CreatePseudoConsole,
        resize: ResizePseudoConsole,
        close: ClosePseudoConsole,
        release: None,
        bundled: false,
    }
}

fn api() -> &'static Api {
    static API: OnceLock<Api> = OnceLock::new();
    API.get_or_init(|| bundled_api().unwrap_or_else(kernel_api))
}

/// conpty.dll next to the executable, by its full path (never through the DLL search order).
fn bundled_api() -> Option<Api> {
    let dll = std::env::current_exe().ok()?.parent()?.join("conpty.dll");
    if !dll.is_file() {
        return None;
    }
    let wide = wide_z(dll.as_os_str());
    // SAFETY: `wide` is a NUL-terminated UTF-16 path that outlives the call; no file handle is passed.
    let module = unsafe { LoadLibraryExW(wide.as_ptr(), ptr::null_mut(), LOAD_WITH_ALTERED_SEARCH_PATH) };
    if module.is_null() {
        return None;
    }
    // SAFETY: the names are the documented exports of conpty.dll, whose signatures match the pseudoconsole
    // functions of kernel32 (CreatePseudoConsole, ResizePseudoConsole, ClosePseudoConsole, ReleasePseudoConsole).
    unsafe {
        let create = proc(module, c"ConptyCreatePseudoConsole")?;
        let resize = proc(module, c"ConptyResizePseudoConsole")?;
        let close = proc(module, c"ConptyClosePseudoConsole")?;
        let release = proc(module, c"ConptyReleasePseudoConsole");
        Some(Api {
            create: std::mem::transmute::<unsafe extern "system" fn() -> isize, CreateFn>(create),
            resize: std::mem::transmute::<unsafe extern "system" fn() -> isize, ResizeFn>(resize),
            close: std::mem::transmute::<unsafe extern "system" fn() -> isize, CloseFn>(close),
            release: release.map(|f| std::mem::transmute::<unsafe extern "system" fn() -> isize, CloseFn>(f)),
            bundled: true,
        })
    }
}

/// # Safety
/// `module` must be a loaded module (it is never unloaded).
unsafe fn proc(module: HMODULE, name: &std::ffi::CStr) -> Option<unsafe extern "system" fn() -> isize> {
    // SAFETY: the caller passes a loaded module; `name` is NUL-terminated.
    unsafe { GetProcAddress(module, name.as_ptr() as PCSTR) }
}

/// Whether the bundled conpty.dll is in use (Windows' own otherwise).
pub fn bundled() -> bool {
    api().bundled
}

fn wide_z(text: &OsStr) -> Vec<u16> {
    text.encode_wide().chain(std::iter::once(0)).collect()
}

fn coord(cols: u16, rows: u16) -> COORD {
    // ConPTY takes SHORTs; larger sizes are clamped rather than wrapped around.
    let clamp = |v: u16| i16::try_from(v).unwrap_or(i16::MAX);
    COORD {
        X: clamp(cols),
        Y: clamp(rows),
    }
}

fn check(hr: HRESULT) -> io::Result<()> {
    if hr < 0 {
        Err(io::Error::from_raw_os_error(hr))
    } else {
        Ok(())
    }
}

fn owned(handle: HANDLE) -> OwnedHandle {
    // SAFETY: only called with a handle that was just returned to this process by Windows and is owned by no one else.
    unsafe { OwnedHandle::from_raw_handle(handle) }
}

/// What to start in a new pseudoconsole.
pub struct Spawn<'a> {
    /// The whole command line, as CreateProcessW gets it (its first token names the program).
    pub command_line: &'a str,
    /// Empty: the host's own folder.
    pub cwd: &'a str,
    /// `NAME=value` entries in the order given; empty: the host's own environment.
    pub env: &'a [String],
    pub cols: u16,
    pub rows: u16,
}

/// A process in a pseudoconsole. Its output is read from, and its input written to, the files `spawn` returns.
pub struct Pty {
    /// None once closed.
    console: Mutex<Option<HPCON>>,
    process: OwnedHandle,
    pid: u32,
}

/// Starts a process in a new pseudoconsole: returns it, its output (to read) and its input (to write).
pub fn spawn(request: &Spawn<'_>) -> io::Result<(Pty, File, File)> {
    let api = api();
    let (in_read, in_write) = pipe()?;
    let (out_read, out_write) = pipe()?;
    let mut console: HPCON = 0;
    // SAFETY: the pipe handles are valid and stay open for the call; `console` receives the new pseudoconsole.
    check(unsafe {
        (api.create)(
            coord(request.cols, request.rows),
            in_read.as_raw_handle(),
            out_write.as_raw_handle(),
            0,
            &mut console,
        )
    })?;
    // From here on the pseudoconsole is closed with the Pty, or right here should starting the process fail.
    let started = start(console, request);
    // The pseudoconsole holds its own duplicates of these ends.
    drop(in_read);
    drop(out_write);
    match started {
        Ok((process, pid)) => {
            if let Some(release) = api.release {
                // SAFETY: `console` is the live pseudoconsole created above.
                unsafe { release(console) };
            }
            Ok((
                Pty {
                    console: Mutex::new(Some(console)),
                    process,
                    pid,
                },
                File::from(out_read),
                File::from(in_write),
            ))
        }
        Err(error) => {
            // SAFETY: `console` was created above and is closed exactly once, here.
            unsafe { (api.close)(console) };
            Err(error)
        }
    }
}

fn pipe() -> io::Result<(OwnedHandle, OwnedHandle)> {
    let mut read: HANDLE = ptr::null_mut();
    let mut write: HANDLE = ptr::null_mut();
    // SAFETY: both out pointers are valid; default security, not inheritable.
    if unsafe { CreatePipe(&mut read, &mut write, ptr::null(), 0) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok((owned(read), owned(write)))
}

/// A process attribute list holding the pseudoconsole; deleted when dropped.
struct Attributes {
    buffer: Vec<u64>,
}

impl Attributes {
    fn new(console: HPCON) -> io::Result<Self> {
        let mut size = 0usize;
        // SAFETY: a size query (null list) as documented; it fails with ERROR_INSUFFICIENT_BUFFER and sets `size`.
        unsafe { InitializeProcThreadAttributeList(ptr::null_mut(), 1, 0, &mut size) };
        let mut list = Self {
            buffer: vec![0u64; size.div_ceil(8)],
        };
        // SAFETY: the buffer is at least `size` bytes and 8-aligned.
        if unsafe { InitializeProcThreadAttributeList(list.ptr(), 1, 0, &mut size) } == 0 {
            list.buffer.clear();
            return Err(io::Error::last_os_error());
        }
        // SAFETY: the list was initialized above; the pseudoconsole attribute takes the HPCON value itself.
        let ok = unsafe {
            UpdateProcThreadAttribute(
                list.ptr(),
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                console as *const c_void,
                std::mem::size_of::<HPCON>(),
                ptr::null_mut(),
                ptr::null(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(list)
    }

    fn ptr(&mut self) -> *mut c_void {
        self.buffer.as_mut_ptr().cast()
    }
}

impl Drop for Attributes {
    fn drop(&mut self) {
        if !self.buffer.is_empty() {
            // SAFETY: the list was initialized in `new` (an uninitialized one has an empty buffer).
            unsafe { DeleteProcThreadAttributeList(self.ptr()) };
        }
    }
}

fn start(console: HPCON, request: &Spawn<'_>) -> io::Result<(OwnedHandle, u32)> {
    let mut attributes = Attributes::new(console)?;
    // SAFETY: STARTUPINFOEXW is a plain C struct for which all zeroes is a valid value.
    let mut startup: STARTUPINFOEXW = unsafe { std::mem::zeroed() };
    startup.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;
    // No standard handles of the host's: the pseudoconsole provides them.
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.lpAttributeList = attributes.ptr();

    let mut command_line = wide_z(OsStr::new(request.command_line));
    let cwd = (!request.cwd.is_empty()).then(|| wide_z(OsStr::new(request.cwd)));
    let environment = (!request.env.is_empty()).then(|| {
        let mut block: Vec<u16> = Vec::new();
        for entry in request.env {
            block.extend(OsStr::new(entry).encode_wide());
            block.push(0);
        }
        block.push(0);
        block
    });
    // SAFETY: PROCESS_INFORMATION is a plain C struct for which all zeroes is a valid value.
    let mut info: PROCESS_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: every pointer is valid for the call: the mutable NUL-terminated command line, the optional
    // NUL-terminated folder and UTF-16 environment block (CREATE_UNICODE_ENVIRONMENT), and the startup info
    // with its initialized attribute list. No handles are inherited.
    let ok = unsafe {
        CreateProcessW(
            ptr::null(),
            command_line.as_mut_ptr(),
            ptr::null(),
            ptr::null(),
            0,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            environment.as_ref().map_or(ptr::null(), |b| b.as_ptr().cast()),
            cwd.as_ref().map_or(ptr::null(), |c| c.as_ptr()),
            &startup.StartupInfo,
            &mut info,
        )
    };
    if ok == 0 {
        return Err(io::Error::last_os_error());
    }
    drop(owned(info.hThread));
    Ok((owned(info.hProcess), info.dwProcessId))
}

impl Pty {
    pub fn pid(&self) -> u32 {
        self.pid
    }

    pub fn resize(&self, cols: u16, rows: u16) -> io::Result<()> {
        let console = self.console.lock().unwrap_or_else(|e| e.into_inner());
        match *console {
            // SAFETY: the pseudoconsole is open while it is in the mutex, which is held for the call.
            Some(hpc) => check(unsafe { (api().resize)(hpc, coord(cols, rows)) }),
            None => Ok(()),
        }
    }

    /// Waits for the process to end and returns its exit code.
    pub fn wait(&self) -> io::Result<u32> {
        // SAFETY: the process handle is owned by this Pty and open.
        if unsafe { WaitForSingleObject(self.process.as_raw_handle(), INFINITE) } != WAIT_OBJECT_0 {
            return Err(io::Error::last_os_error());
        }
        let mut code = 0u32;
        // SAFETY: as above; `code` is a valid out pointer.
        if unsafe { GetExitCodeProcess(self.process.as_raw_handle(), &mut code) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(code)
    }

    /// Closes the pseudoconsole: the output ends (once read to the end). Does nothing the second time.
    pub fn close(&self) {
        let taken = self.console.lock().unwrap_or_else(|e| e.into_inner()).take();
        if let Some(hpc) = taken {
            // SAFETY: the pseudoconsole was open (taken out of the mutex, so it is closed exactly once).
            unsafe { (api().close)(hpc) };
        }
    }

    /// Ends the process (exit code 1, as node-pty does) and closes the pseudoconsole.
    pub fn kill(&self) {
        // SAFETY: the process handle is owned by this Pty and open; a process that already ended is no error here.
        unsafe { TerminateProcess(self.process.as_raw_handle(), 1) };
        self.close();
    }
}

impl Drop for Pty {
    fn drop(&mut self) {
        self.close();
    }
}

// Other processes, as src/main/process.ts asks of them (the session status engine tells a claude's registry entry
// from one a killed claude left behind).

/// An open handle of another process, closed when dropped.
struct Opened(HANDLE);

impl Opened {
    fn new(pid: u32) -> Result<Self, u32> {
        // SAFETY: OpenProcess has no preconditions; a null result is an error (GetLastError tells which).
        let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if handle.is_null() {
            // SAFETY: no preconditions.
            Err(unsafe { GetLastError() })
        } else {
            Ok(Self(handle))
        }
    }
}

impl Drop for Opened {
    fn drop(&mut self) {
        // SAFETY: the handle was opened by Opened::new and is closed once, here.
        unsafe { CloseHandle(self.0) };
    }
}

/// Whether process `pid` runs, as Node's `process.kill(pid, 0)` tells it (one it may not open runs).
pub fn process_alive(pid: u32) -> bool {
    const STILL_ACTIVE: u32 = 259;
    match Opened::new(pid) {
        Err(error) => error == ERROR_ACCESS_DENIED,
        Ok(process) => {
            let mut code = 0u32;
            // SAFETY: the handle is open; `code` is a valid out pointer.
            let ok = unsafe { GetExitCodeProcess(process.0, &mut code) } != 0;
            ok && code == STILL_ACTIVE
        }
    }
}

/// When process `pid` was created: its FILETIME as a decimal string, as Claude Code's registry has it (procStart).
pub fn process_start(pid: u32) -> Option<String> {
    let process = Opened::new(pid).ok()?;
    let zero = FILETIME {
        dwLowDateTime: 0,
        dwHighDateTime: 0,
    };
    let (mut creation, mut exit, mut kernel, mut user) = (zero, zero, zero, zero);
    // SAFETY: the handle is open; the four are valid out pointers.
    if unsafe { GetProcessTimes(process.0, &mut creation, &mut exit, &mut kernel, &mut user) } == 0 {
        return None;
    }
    Some(((u64::from(creation.dwHighDateTime) << 32) | u64::from(creation.dwLowDateTime)).to_string())
}

/// Closes this process's standard output (its reader sees the end while the process goes on). For test stand-ins.
pub fn close_stdout() {
    // SAFETY: GetStdHandle has no preconditions; the handle it gives is closed once and then unset, so nothing in
    // this process uses it again (Rust's stdout asks for it each time and finds none).
    unsafe {
        let handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if !handle.is_null() && handle != INVALID_HANDLE_VALUE {
            SetStdHandle(STD_OUTPUT_HANDLE, ptr::null_mut());
            CloseHandle(handle);
        }
    }
}
