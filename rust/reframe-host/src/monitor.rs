//! The `monitor` service: what each running session uses now (its process and everything it started, through the
//! session's job): CPU and GPU in percent of the machine, memory in bytes, and its processes.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use reframe_conpty::{GpuCounters, JobUsage, Pty};
use serde_json::{json, Map, Value};

pub const SERVICE: &str = "monitor";
pub const VERSION: u32 = 1;

/// FILETIME of 1970-01-01, in 100 ns units since 1601.
const UNIX_EPOCH_FILETIME: u64 = 116_444_736_000_000_000;

/// What the last sample read, for the rates of the next one.
#[derive(Default)]
pub struct Monitor {
    /// Per session id: its job's CPU time then, and when.
    jobs: HashMap<String, (u64, Instant)>,
    /// Per process (id and creation time, as ids are reused): its CPU time then, and when.
    processes: HashMap<(u32, u64), (u64, Instant)>,
    /// Opened at the first sample (None when Windows has no GPU counters).
    gpu: Option<Option<GpuCounters>>,
}

/// Percent of the whole machine (every logical processor) that `cpu` (100 ns units) is of `wall`.
fn percent(cpu: u64, wall: f64, processors: f64) -> f64 {
    if wall <= 0.0 {
        return 0.0;
    }
    let value = cpu as f64 / 1e7 / (wall * processors) * 100.0;
    (value.clamp(0.0, 100.0) * 10.0).round() / 10.0
}

impl Monitor {
    /// One sample of the sessions given (id and pseudoconsole); a session's first sample has no CPU rate yet (0).
    pub fn sample(&mut self, sessions: &[(String, Arc<Pty>)]) -> Value {
        let now = Instant::now();
        let processors = std::thread::available_parallelism().map_or(1, |n| n.get()) as f64;
        let gpu = self
            .gpu
            .get_or_insert_with(GpuCounters::new)
            .as_mut()
            .map(GpuCounters::sample)
            .unwrap_or_default();
        let mut jobs = HashMap::new();
        let mut processes = HashMap::new();
        let mut answer = Map::new();
        for (id, pty) in sessions {
            let Some(JobUsage {
                cpu,
                processes: members,
            }) = pty.usage()
            else {
                continue;
            };
            let since = |then: Option<&(u64, Instant)>, total: u64| {
                then.map_or(0.0, |&(before, at)| {
                    percent(
                        total.saturating_sub(before),
                        now.duration_since(at).as_secs_f64(),
                        processors,
                    )
                })
            };
            let session_cpu = since(self.jobs.get(id), cpu);
            jobs.insert(id.clone(), (cpu, now));
            let mut memory = 0u64;
            let mut session_gpu = 0f64;
            let mut listed = Vec::new();
            for member in members {
                let key = (member.pid, member.start);
                let process_cpu = since(self.processes.get(&key), member.cpu);
                processes.insert(key, (member.cpu, now));
                let process_gpu = gpu.get(&member.pid).copied().unwrap_or(0.0);
                memory += member.memory;
                session_gpu += process_gpu;
                listed.push(json!({
                    "pid": member.pid,
                    "name": member.exe,
                    "cpu": process_cpu,
                    "memory": member.memory,
                    "gpu": (process_gpu.clamp(0.0, 100.0) * 10.0).round() / 10.0,
                    "startedAt": member.start.saturating_sub(UNIX_EPOCH_FILETIME) / 10_000,
                }));
            }
            answer.insert(
                id.clone(),
                json!({
                    "cpu": session_cpu,
                    "memory": memory,
                    "gpu": (session_gpu.clamp(0.0, 100.0) * 10.0).round() / 10.0,
                    "processes": listed,
                }),
            );
        }
        // Only what is still there is kept for the next sample.
        self.jobs = jobs;
        self.processes = processes;
        json!({ "sessions": answer })
    }
}

/// A sample on a thread of its own (reading every process takes a moment); `answer` gets it.
pub fn sample(
    monitor: &Arc<Mutex<Monitor>>,
    sessions: Vec<(String, Arc<Pty>)>,
    answer: impl FnOnce(Value) + Send + 'static,
) {
    let monitor = monitor.clone();
    std::thread::spawn(move || {
        let value = monitor.lock().unwrap_or_else(|e| e.into_inner()).sample(&sessions);
        answer(value);
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn percent_of_the_machine() {
        // One second of CPU in one second on 4 processors: a quarter of the machine.
        assert_eq!(percent(10_000_000, 1.0, 4.0), 25.0);
        assert_eq!(percent(0, 1.0, 4.0), 0.0);
        assert_eq!(percent(10_000_000, 0.0, 4.0), 0.0);
        assert_eq!(percent(100_000_000, 1.0, 1.0), 100.0);
    }

    #[test]
    fn an_empty_sample() {
        assert_eq!(Monitor::default().sample(&[]), json!({ "sessions": {} }));
    }
}
