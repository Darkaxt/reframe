//! How many service requests run at once: up to `running` on threads of their own, up to `waiting` more queued in
//! order behind them (a burst on reframe's start, say), and beyond that `busy`.

use std::collections::VecDeque;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, Mutex, PoisonError};

use crate::transcripts::SvcError;

type Job = Box<dyn FnOnce() + Send>;

struct Queue {
    running: usize,
    waiting: VecDeque<Job>,
}

#[derive(Clone)]
pub struct Limit {
    name: &'static str,
    running: usize,
    waiting: usize,
    queue: Arc<Mutex<Queue>>,
}

impl Limit {
    pub fn new(name: &'static str, running: usize, waiting: usize) -> Self {
        Self {
            name,
            running,
            waiting,
            queue: Arc::new(Mutex::new(Queue {
                running: 0,
                waiting: VecDeque::new(),
            })),
        }
    }

    /// Runs `job` now or when one running ends; `busy` when the queue is full, `io` when no thread can be started.
    pub fn run(&self, what: &str, job: impl FnOnce() + Send + 'static) -> Result<(), SvcError> {
        let mut queue = self.queue.lock().unwrap_or_else(PoisonError::into_inner);
        if queue.running >= self.running {
            if queue.waiting.len() >= self.waiting {
                return Err(SvcError {
                    code: "busy",
                    message: format!("too many {what} requests at once"),
                });
            }
            queue.waiting.push_back(Box::new(job));
            return Ok(());
        }
        queue.running += 1;
        drop(queue);
        let shared = self.queue.clone();
        let started = std::thread::Builder::new().name(self.name.into()).spawn(move || {
            let mut next: Option<Job> = Some(Box::new(job));
            while let Some(job) = next {
                // A job answers for its own panics; this keeps the thread's place in the count.
                let _ = catch_unwind(AssertUnwindSafe(job));
                let mut queue = shared.lock().unwrap_or_else(PoisonError::into_inner);
                next = queue.waiting.pop_front();
                if next.is_none() {
                    queue.running -= 1;
                }
            }
        });
        if let Err(error) = started {
            self.queue.lock().unwrap_or_else(PoisonError::into_inner).running -= 1;
            return Err(SvcError {
                code: "io",
                message: error.to_string(),
            });
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;
    use std::time::Duration;

    #[test]
    fn queues_a_burst_and_refuses_beyond_the_bound() {
        let limit = Limit::new("test", 2, 3);
        let (release, gate) = mpsc::channel::<()>();
        let gate = Arc::new(Mutex::new(gate));
        let (done, finished) = mpsc::channel::<usize>();
        for n in 0..5 {
            let gate = gate.clone();
            let done = done.clone();
            limit
                .run("test", move || {
                    gate.lock().unwrap().recv().unwrap();
                    done.send(n).unwrap();
                })
                .unwrap();
        }
        let refused = limit.run("test", || {}).unwrap_err();
        assert_eq!(refused.code, "busy");
        for _ in 0..5 {
            release.send(()).unwrap();
        }
        let mut all: Vec<usize> = (0..5)
            .map(|_| finished.recv_timeout(Duration::from_secs(5)).unwrap())
            .collect();
        all.sort_unstable();
        assert_eq!(all, [0, 1, 2, 3, 4]);
        // Room again.
        let (tx, rx) = mpsc::channel();
        limit.run("test", move || tx.send(()).unwrap()).unwrap();
        rx.recv_timeout(Duration::from_secs(5)).unwrap();
    }
}
