//! Project grouping of past sessions, as src/shared/folder-workspaces.ts does it.

use serde::Serialize;

/// folderKey: a lexical canonical form (Windows paths without case).
pub fn folder_key(folder: &str) -> String {
    let bytes = folder.as_bytes();
    let windows = (bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && (bytes[2] == b'\\' || bytes[2] == b'/'))
        || folder.starts_with(r"\\");
    let normalized = folder.replace('\\', "/");
    let normalized = normalized.trim_end_matches('/');
    let mut parts: Vec<&str> = Vec::new();
    for part in normalized.split('/') {
        if part == "." {
            continue;
        }
        if part == ".." && parts.len() > 1 {
            parts.pop();
        } else {
            parts.push(part);
        }
    }
    let mut key = parts.join("/");
    if key.is_empty() {
        key = "/".into();
    }
    if windows {
        key.to_lowercase()
    } else {
        key
    }
}

/// workspaceKey: sessions of folders with the same last name are one project.
pub fn workspace_key(folder: &str) -> String {
    let key = folder_key(folder);
    let last = key.split('/').rfind(|p| !p.is_empty()).map(str::to_lowercase);
    match last {
        Some(name) if !name.is_empty() => format!("project:{name}"),
        _ => format!("project:{key}"),
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderLocation {
    pub cwd: String,
    pub provider: String,
    pub session_ids: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FolderWorkspace {
    pub id: String,
    pub cwd: String,
    pub name: String,
    pub session_ids: Vec<String>,
    pub locations: Vec<FolderLocation>,
}

/// folderWorkspaces for chats without a provider (all "chatgpt", as the TypeScript defaults them). Sorted by name
/// case-insensitively, then as written: close to, not the same as, JavaScript's localeCompare (reframe sorts again).
pub fn folder_workspaces<'a>(chats: impl IntoIterator<Item = (&'a str, &'a str)>) -> Vec<FolderWorkspace> {
    const PROVIDER: &str = "chatgpt";
    let mut result: Vec<FolderWorkspace> = Vec::new();
    for (cwd, id) in chats {
        let key = workspace_key(cwd);
        let index = match result.iter().position(|g| g.id == key) {
            Some(index) => index,
            None => {
                let name = cwd
                    .split(['\\', '/'])
                    .rfind(|p| !p.is_empty())
                    .unwrap_or(cwd)
                    .to_string();
                result.push(FolderWorkspace {
                    id: key,
                    cwd: cwd.to_string(),
                    name,
                    session_ids: Vec::new(),
                    locations: Vec::new(),
                });
                result.len() - 1
            }
        };
        let group = &mut result[index];
        if !group.session_ids.iter().any(|s| s == id) {
            group.session_ids.push(id.to_string());
        }
        let folder = folder_key(cwd);
        let location = match group
            .locations
            .iter()
            .position(|l| folder_key(&l.cwd) == folder && l.provider == PROVIDER)
        {
            Some(index) => &mut group.locations[index],
            None => {
                group.locations.push(FolderLocation {
                    cwd: cwd.to_string(),
                    provider: PROVIDER.into(),
                    session_ids: Vec::new(),
                });
                group.locations.last_mut().expect("just pushed")
            }
        };
        if !location.session_ids.iter().any(|s| s == id) {
            location.session_ids.push(id.to_string());
        }
    }
    result.sort_by(|a, b| {
        a.name
            .to_lowercase()
            .cmp(&b.name.to_lowercase())
            .then_with(|| a.name.cmp(&b.name))
    });
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keys_match_the_typescript() {
        assert_eq!(folder_key(r"C:\Users\Me\Proj\"), "c:/users/me/proj");
        assert_eq!(folder_key("/home/me/./a/../Proj"), "/home/me/Proj");
        assert_eq!(folder_key("/"), "/");
        assert_eq!(workspace_key(r"D:\Work\Reframe"), "project:reframe");
        assert_eq!(workspace_key("/home/me/Reframe"), "project:reframe");
    }

    #[test]
    fn groups_by_last_folder_name() {
        let groups = folder_workspaces([
            ("C:\\a\\Proj", "1"),
            ("D:\\b\\proj", "2"),
            ("C:\\a\\Proj", "3"),
            ("/x/alpha", "4"),
        ]);
        assert_eq!(groups.len(), 2);
        assert_eq!(groups[0].name, "alpha");
        assert_eq!(groups[1].session_ids, ["1", "2", "3"]);
        assert_eq!(groups[1].locations.len(), 2);
        assert_eq!(groups[1].locations[0].session_ids, ["1", "3"]);
    }
}
