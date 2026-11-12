//! A direct start's reframe-config.json (src/main/launch-config.ts, evaluateLaunchConfig): the fixed profile, runtime
//! folder and each provider's CLI binary with its sha256, or the client adoption record that replaces both. What it
//! gives is a plan; reframe applies it to its own environment.

use std::fs;
use std::path::Path;

use serde_json::{json, Map, Value};

use crate::adoption::{adopted_client, Adopted, Hashes};
use crate::{is_absolute, sha256_file};

/// `null` when a binding is fine, else why it is refused (`check` in launch-config.ts).
fn check(binary: Option<&Value>, what: &str) -> Option<String> {
    let Some(path) = binary
        .and_then(|b| b.get("path"))
        .and_then(Value::as_str)
        .filter(|p| is_absolute(p))
    else {
        return Some(format!("{what}: no binary configured"));
    };
    // Only a string can match: String(other) is never a sha256.
    let expected = binary
        .and_then(|b| b.get("sha256"))
        .and_then(Value::as_str)
        .unwrap_or("");
    match sha256_file(Path::new(path)) {
        Ok(actual) if actual == expected.to_lowercase() => None,
        Ok(actual) => Some(format!("{what}: {path} changed (sha256 {actual})")),
        Err(_) => Some(format!("{what}: {path} cannot be read")),
    }
}

/// JavaScript's truthiness of a JSON value.
pub(crate) fn truthy(value: &Value) -> bool {
    match value {
        Value::Null => false,
        Value::Bool(b) => *b,
        Value::Number(n) => n.as_f64().is_some_and(|n| n != 0.0),
        Value::String(s) => !s.is_empty(),
        _ => true,
    }
}

fn absolute<'a>(config: &'a Value, key: &str) -> Option<&'a str> {
    config.get(key).and_then(Value::as_str).filter(|p| is_absolute(p))
}

/// The plan of the launch config `file`: `{ file, userData, runtime, claudeHome, env, refused, problems }`, or null
/// when it cannot be read or lacks an absolute profile, runtime or Claude folder.
pub fn evaluate(hashes: &Hashes, file: &str) -> Value {
    let Some(config) = fs::read(file)
        .ok()
        .and_then(|bytes| serde_json::from_str::<Value>(&String::from_utf8_lossy(&bytes)).ok())
    else {
        return Value::Null;
    };
    let (Some(user_data), Some(runtime), Some(claude_home)) = (
        absolute(&config, "userData"),
        absolute(&config, "runtime"),
        absolute(&config, "claudeHome"),
    ) else {
        return Value::Null;
    };
    let mut env = Map::new();
    let mut refused = Map::new();
    let mut problems: Vec<String> = Vec::new();

    let adoption = absolute(&config, "clientAdoption");
    let chatgpt = adoption.map(|a| adopted_client(hashes, a, "chatgpt"));
    let anthropic = adoption.map(|a| adopted_client(hashes, a, "anthropic"));
    if let (Some(Adopted::Client { path: c, .. }), Some(Adopted::Client { path: a, .. })) = (&chatgpt, &anthropic) {
        env.insert("REFRAME_CLIENT_ADOPTION".into(), json!(adoption));
        env.insert("REFRAME_CLAUDE_BIN".into(), json!(c));
        env.insert("REFRAME_ANTHROPIC_CLAUDE_BIN".into(), json!(a));
    } else {
        for c in [&chatgpt, &anthropic] {
            if let Some(Adopted::Refused(reason)) = c {
                problems.push(reason.clone());
            }
        }
        let bindings = [
            ("chatgpt", "claudeBin", "ChatGPT CLI", "REFRAME_CLAUDE_BIN"),
            (
                "anthropic",
                "anthropicClaudeBin",
                "Anthropic CLI",
                "REFRAME_ANTHROPIC_CLAUDE_BIN",
            ),
        ];
        for (provider, key, what, var) in bindings {
            match check(config.get(key), what) {
                Some(reason) => {
                    refused.insert(provider.into(), json!(reason));
                }
                None => {
                    env.insert(var.into(), config[key]["path"].clone());
                }
            }
        }
    }

    let history = config.get("history").filter(|h| truthy(h));
    let helper = history.map(|h| {
        let binding = json!({ "path": h.get("helper"), "sha256": h.get("helperSha256") });
        check(Some(&binding), "history helper")
    });
    let python = history
        .and_then(|h| h.get("python"))
        .and_then(Value::as_str)
        .filter(|p| is_absolute(p));
    match (history, helper, python) {
        (Some(h), Some(None), Some(python)) => {
            env.insert("REFRAME_HISTORY_PYTHON".into(), json!(python));
            env.insert("REFRAME_HISTORY_HELPER".into(), h["helper"].clone());
            env.insert("REFRAME_HISTORY_HELPER_SHA256".into(), h["helperSha256"].clone());
        }
        (_, helper, _) => {
            problems.push(
                helper
                    .flatten()
                    .unwrap_or_else(|| "history helper: not configured".into()),
            );
        }
    }
    json!({
        "file": file,
        "userData": user_data,
        "runtime": runtime,
        "claudeHome": claude_home,
        "env": env,
        "refused": refused,
        "problems": problems,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::adoption::SCHEMA;
    use crate::testdir::TestDir;

    #[test]
    fn plans_bindings_adoption_and_history() {
        let dir = TestDir::new();
        let bin = dir.write("claude.exe", "cli");
        let hash = sha256_file(&bin).unwrap();
        let bin = bin.to_string_lossy().into_owned();
        let base = json!({ "userData": dir.text("data"), "runtime": dir.text("rt"), "claudeHome": dir.text("home") });
        let write = |name: &str, extra: Value| {
            let mut config = base.clone();
            config
                .as_object_mut()
                .unwrap()
                .extend(extra.as_object().unwrap().clone());
            dir.write(name, config.to_string());
            dir.text(name)
        };

        let fixed = write(
            "fixed.json",
            json!({ "claudeBin": { "path": bin, "sha256": hash }, "anthropicClaudeBin": { "path": bin, "sha256": "0" } }),
        );
        let plan = evaluate(&Hashes::default(), &fixed);
        assert_eq!(plan["env"], json!({ "REFRAME_CLAUDE_BIN": bin }));
        assert_eq!(
            plan["refused"],
            json!({ "anthropic": format!("Anthropic CLI: {bin} changed (sha256 {hash})") })
        );
        assert_eq!(plan["problems"], json!(["history helper: not configured"]));

        dir.write(
            "adoption.json",
            json!({ "schema": SCHEMA, "clients": {
                "chatgpt": { "binary": bin, "sha256": hash }, "anthropic": { "binary": bin, "sha256": hash } } })
            .to_string(),
        );
        let adopted = write(
            "adopted.json",
            json!({ "clientAdoption": dir.text("adoption.json"),
                "history": { "python": bin, "helper": bin, "helperSha256": hash } }),
        );
        let plan = evaluate(&Hashes::default(), &adopted);
        assert_eq!(
            plan["env"],
            json!({ "REFRAME_CLIENT_ADOPTION": dir.text("adoption.json"), "REFRAME_CLAUDE_BIN": bin,
                "REFRAME_ANTHROPIC_CLAUDE_BIN": bin, "REFRAME_HISTORY_PYTHON": bin, "REFRAME_HISTORY_HELPER": bin,
                "REFRAME_HISTORY_HELPER_SHA256": hash })
        );
        assert_eq!(plan["refused"], json!({}));
        assert_eq!(plan["problems"], json!([]));

        dir.write(
            "relative.json",
            json!({ "userData": "data", "runtime": "/rt", "claudeHome": "/h" }).to_string(),
        );
        assert_eq!(evaluate(&Hashes::default(), &dir.text("relative.json")), Value::Null);
        assert_eq!(evaluate(&Hashes::default(), &dir.text("missing.json")), Value::Null);
    }
}
