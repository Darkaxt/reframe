//! What a session uses: a job object per session's process, so its descendants stay in it even once the process that
//! started them ended, what the job's processes use now, and the GPU per process (Windows' "GPU Engine" counters,
//! as Task Manager reads them).

use std::collections::HashMap;
use std::ffi::{c_void, OsStr};
use std::os::windows::io::{AsRawHandle, OwnedHandle};
use std::ptr;

use windows_sys::Win32::Foundation::{GetLastError, ERROR_MORE_DATA, FILETIME, HANDLE};
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectBasicAccountingInformation, JobObjectBasicProcessIdList,
    JobObjectExtendedLimitInformation, QueryInformationJobObject, SetInformationJobObject,
    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION, JOBOBJECT_BASIC_PROCESS_ID_LIST, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JOB_OBJECT_LIMIT_BREAKAWAY_OK,
};
use windows_sys::Win32::System::Performance::{
    PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterArrayW, PdhOpenQueryW,
    PDH_CSTATUS_NEW_DATA, PDH_CSTATUS_VALID_DATA, PDH_FMT_COUNTERVALUE_ITEM_W, PDH_FMT_DOUBLE, PDH_HCOUNTER,
    PDH_HQUERY, PDH_MORE_DATA,
};
use windows_sys::Win32::System::ProcessStatus::{
    K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS, PROCESS_MEMORY_COUNTERS_EX,
};
use windows_sys::Win32::System::Threading::{
    GetProcessTimes, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_VM_READ,
};

use crate::{owned, wide_z, Opened};

/// A new job holding `process` (created suspended, so none of its children can start outside the job). Its
/// processes may still leave it when they ask to (CREATE_BREAKAWAY_FROM_JOB, as gated launchers do); closing the
/// job ends nothing. None when Windows refuses (the session then runs without one).
pub(crate) fn job_for(process: HANDLE) -> Option<OwnedHandle> {
    // SAFETY: default security and no name; a null result is an error.
    let job = unsafe { CreateJobObjectW(ptr::null(), ptr::null()) };
    if job.is_null() {
        return None;
    }
    let job = owned(job);
    // SAFETY: a plain C struct for which all zeroes is a valid value (no limits).
    let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
    // SAFETY: the job is open; the pointer and size are those of the class's struct, which outlives the call.
    let limited = unsafe {
        SetInformationJobObject(
            job.as_raw_handle(),
            JobObjectExtendedLimitInformation,
            (&raw const limits).cast::<c_void>(),
            std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        )
    } != 0;
    // SAFETY: both handles are open.
    let assigned = limited && unsafe { AssignProcessToJobObject(job.as_raw_handle(), process) } != 0;
    assigned.then_some(job)
}

/// What a job's processes use now.
#[derive(Clone, Debug, Default)]
pub struct JobUsage {
    /// The CPU time of all its processes so far, ended ones included (100 ns units).
    pub cpu: u64,
    pub processes: Vec<ProcessUsage>,
}

/// One process of a job.
#[derive(Clone, Debug)]
pub struct ProcessUsage {
    pub pid: u32,
    /// Its executable's file name, e.g. "grep.exe" (empty if it could not be read).
    pub exe: String,
    /// Its CPU time so far (100 ns units).
    pub cpu: u64,
    /// Its private bytes (commit), as Task Manager's "Commit size".
    pub memory: u64,
    /// Its creation time (FILETIME: 100 ns since 1601).
    pub start: u64,
}

fn filetime(t: FILETIME) -> u64 {
    (u64::from(t.dwHighDateTime) << 32) | u64::from(t.dwLowDateTime)
}

/// The job's CPU time and its processes now; None if it cannot be read.
pub(crate) fn job_usage(job: HANDLE) -> Option<JobUsage> {
    // SAFETY: a plain C struct for which all zeroes is a valid value.
    let mut accounting: JOBOBJECT_BASIC_ACCOUNTING_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: the job is open; the pointer and size are those of the class's struct.
    let ok = unsafe {
        QueryInformationJobObject(
            job,
            JobObjectBasicAccountingInformation,
            (&raw mut accounting).cast::<c_void>(),
            std::mem::size_of::<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() as u32,
            ptr::null_mut(),
        )
    } != 0;
    if !ok {
        return None;
    }
    let cpu = (accounting.TotalUserTime.max(0) + accounting.TotalKernelTime.max(0)) as u64;
    let processes = job_pids(job).into_iter().filter_map(process_usage).collect();
    Some(JobUsage { cpu, processes })
}

/// The ids of the job's processes (the list grows until they fit).
fn job_pids(job: HANDLE) -> Vec<u32> {
    let header = std::mem::offset_of!(JOBOBJECT_BASIC_PROCESS_ID_LIST, ProcessIdList) / std::mem::size_of::<usize>();
    let mut capacity = 64usize;
    loop {
        let mut buffer = vec![0usize; header + capacity];
        // SAFETY: the job is open; the buffer is usize-aligned (as the struct) and its size is given in bytes.
        let ok = unsafe {
            QueryInformationJobObject(
                job,
                JobObjectBasicProcessIdList,
                buffer.as_mut_ptr().cast::<c_void>(),
                (buffer.len() * std::mem::size_of::<usize>()) as u32,
                ptr::null_mut(),
            )
        } != 0;
        // SAFETY: no preconditions.
        if !ok && unsafe { GetLastError() } == ERROR_MORE_DATA && capacity < 65_536 {
            capacity *= 4;
            continue;
        }
        if !ok {
            return Vec::new();
        }
        // SAFETY: the buffer starts with the struct's header, which Windows filled in.
        let list = unsafe { &*buffer.as_ptr().cast::<JOBOBJECT_BASIC_PROCESS_ID_LIST>() };
        let count = (list.NumberOfProcessIdsInList as usize).min(capacity);
        return buffer[header..header + count].iter().map(|&pid| pid as u32).collect();
    }
}

fn process_usage(pid: u32) -> Option<ProcessUsage> {
    let process = Opened::with(pid, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ)
        .or_else(|_| Opened::new(pid))
        .ok()?;
    let zero = FILETIME {
        dwLowDateTime: 0,
        dwHighDateTime: 0,
    };
    let (mut creation, mut exit, mut kernel, mut user) = (zero, zero, zero, zero);
    // SAFETY: the handle is open; the four are valid out pointers.
    if unsafe { GetProcessTimes(process.0, &mut creation, &mut exit, &mut kernel, &mut user) } == 0 {
        return None;
    }
    // SAFETY: a plain C struct for which all zeroes is a valid value.
    let mut memory: PROCESS_MEMORY_COUNTERS_EX = unsafe { std::mem::zeroed() };
    memory.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32;
    // SAFETY: the handle is open; the EX struct starts as PROCESS_MEMORY_COUNTERS and its size is given.
    let read = unsafe {
        K32GetProcessMemoryInfo(
            process.0,
            (&raw mut memory).cast::<PROCESS_MEMORY_COUNTERS>(),
            memory.cb,
        )
    } != 0;
    let mut name = vec![0u16; 1024];
    let mut size = name.len() as u32;
    // SAFETY: the handle is open; `name` holds `size` UTF-16 units and `size` is a valid in/out pointer.
    let named = unsafe { QueryFullProcessImageNameW(process.0, PROCESS_NAME_WIN32, name.as_mut_ptr(), &mut size) } != 0;
    let exe = if named {
        let path = String::from_utf16_lossy(&name[..size as usize]);
        path.rsplit(['\\', '/']).next().unwrap_or_default().to_owned()
    } else {
        String::new()
    };
    Some(ProcessUsage {
        pid,
        exe,
        cpu: filetime(kernel) + filetime(user),
        memory: if read { memory.PrivateUsage as u64 } else { 0 },
        start: filetime(creation),
    })
}

/// Windows' GPU use per process ("\GPU Engine(*)\Utilization Percentage"): one query, collected at each sample. A
/// rate needs two collections, so the first sample gives nothing.
pub struct GpuCounters {
    query: PDH_HQUERY,
    counter: PDH_HCOUNTER,
}

// SAFETY: the PDH query handle may be used from any thread; GpuCounters is used by one at a time (&mut self).
unsafe impl Send for GpuCounters {}

impl GpuCounters {
    /// None when the counters are not there (no GPU, or performance counters disabled).
    pub fn new() -> Option<Self> {
        let mut query: PDH_HQUERY = ptr::null_mut();
        // SAFETY: a real-time query (null data source); `query` is a valid out pointer.
        if unsafe { PdhOpenQueryW(ptr::null(), 0, &mut query) } != 0 {
            return None;
        }
        let path = wide_z(OsStr::new(r"\GPU Engine(*)\Utilization Percentage"));
        let mut counter: PDH_HCOUNTER = ptr::null_mut();
        // SAFETY: the query is open; the path is NUL-terminated and outlives the call; `counter` is an out pointer.
        if unsafe { PdhAddEnglishCounterW(query, path.as_ptr(), 0, &mut counter) } != 0 {
            // SAFETY: the query was opened above and is closed once, here.
            unsafe { PdhCloseQuery(query) };
            return None;
        }
        // SAFETY: the query is open. The first collection only gives the rates a start.
        unsafe { PdhCollectQueryData(query) };
        Some(Self { query, counter })
    }

    /// Per process id: its GPU use in percent since the last sample (its busiest engine type, as Task Manager shows).
    pub fn sample(&mut self) -> HashMap<u32, f64> {
        const NOCAP100: u32 = 0x8000;
        let mut per_engine: HashMap<(u32, String), f64> = HashMap::new();
        // SAFETY: the query is open.
        if unsafe { PdhCollectQueryData(self.query) } != 0 {
            return HashMap::new();
        }
        let (mut size, mut count) = (0u32, 0u32);
        // SAFETY: a size query (null buffer) as documented; it answers PDH_MORE_DATA with the size needed.
        let status = unsafe {
            PdhGetFormattedCounterArrayW(
                self.counter,
                PDH_FMT_DOUBLE | NOCAP100,
                &mut size,
                &mut count,
                ptr::null_mut(),
            )
        };
        if status != PDH_MORE_DATA || size == 0 {
            return HashMap::new();
        }
        // The items, then the strings they point to: one 8-aligned buffer of `size` bytes.
        let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
        // SAFETY: the buffer holds `size` bytes, aligned for the items; the out pointers are valid.
        let status = unsafe {
            PdhGetFormattedCounterArrayW(
                self.counter,
                PDH_FMT_DOUBLE | NOCAP100,
                &mut size,
                &mut count,
                buffer.as_mut_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>(),
            )
        };
        if status != 0 {
            return HashMap::new();
        }
        // SAFETY: Windows wrote `count` items at the start of the buffer.
        let items = unsafe {
            std::slice::from_raw_parts(buffer.as_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>(), count as usize)
        };
        for item in items {
            let status = item.FmtValue.CStatus;
            if (status != PDH_CSTATUS_VALID_DATA && status != PDH_CSTATUS_NEW_DATA) || item.szName.is_null() {
                continue;
            }
            // SAFETY: szName points to a NUL-terminated name inside the buffer.
            let name = unsafe {
                let len = (0..).take_while(|&i| *item.szName.add(i) != 0).count();
                String::from_utf16_lossy(std::slice::from_raw_parts(item.szName, len))
            };
            let Some((pid, engine)) = engine_of(&name) else {
                continue;
            };
            // SAFETY: PDH_FMT_DOUBLE was asked for, so the union holds a double.
            let value = unsafe { item.FmtValue.Anonymous.doubleValue };
            *per_engine.entry((pid, engine.to_owned())).or_default() += value;
        }
        let mut per_process: HashMap<u32, f64> = HashMap::new();
        for ((pid, _), value) in per_engine {
            let busiest = per_process.entry(pid).or_default();
            *busiest = busiest.max(value);
        }
        per_process
    }
}

impl Drop for GpuCounters {
    fn drop(&mut self) {
        // SAFETY: the query was opened in `new` and is closed once, here (its counter with it).
        unsafe { PdhCloseQuery(self.query) };
    }
}

/// "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D": the process id and the engine type.
fn engine_of(instance: &str) -> Option<(u32, &str)> {
    let rest = instance.strip_prefix("pid_")?;
    let pid = rest.split('_').next()?.parse().ok()?;
    let engine = instance.rsplit_once("engtype_")?.1;
    Some((pid, engine))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_engine_instances() {
        assert_eq!(
            engine_of("pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D"),
            Some((1234, "3D"))
        );
        assert_eq!(
            engine_of("pid_7_luid_0x0_phys_0_eng_3_engtype_VideoDecode"),
            Some((7, "VideoDecode"))
        );
        assert_eq!(engine_of("_Total"), None);
    }

    #[test]
    fn a_job_holds_its_process_and_tells_its_use() {
        use std::os::windows::io::AsRawHandle;
        let child = std::process::Command::new("cmd.exe")
            .args(["/c", "ping -n 3 127.0.0.1 >nul"])
            .spawn()
            .unwrap();
        let handle = child.as_raw_handle();
        let job = job_for(handle).expect("a job");
        let usage = job_usage(job.as_raw_handle()).expect("its use");
        assert!(usage
            .processes
            .iter()
            .any(|p| p.pid == child.id() && p.exe.eq_ignore_ascii_case("cmd.exe")));
        let mut child = child;
        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn gpu_counters_sample_without_failing() {
        if let Some(mut gpu) = GpuCounters::new() {
            let _ = gpu.sample();
        }
    }
}
