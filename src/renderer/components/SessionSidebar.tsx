import { useEffect, useMemo, useRef, useState, type DragEvent, type MouseEvent } from 'react';
import type { ListItemRef, SessionList } from '@shared/ipc';
import { tileKey } from '@shared/layout';
import { workspaceKey } from '@shared/folder-workspaces';
import { sidebarChats, type SidebarChat } from '@shared/sidebar-chats';
import { useApp } from '../context';
import { dragKind, setListDrag, setNodeDrag } from '../dnd';
import { PAGE, type SessionListState } from '../session-list';
import { exactTime, shortTime } from '../time';
import { CloseIcon, CollapseIcon, FolderIcon, PlusIcon } from './icons';
import type { NewSessionPreset } from './NewSessionDialog';
import { NameInput } from './NameInput';
import { StatusDot } from './StatusDot';
import { ProviderIcon } from './ProviderIcon';

const folderName = (cwd: string) => cwd.split(/[\\/]/).filter(Boolean).pop() ?? cwd;

/** One conversation catalogue: pinned chats, project branches, then recents. */
export function SessionSidebar({
  sessionList,
  onNewSession,
}: {
  sessionList: SessionListState;
  onNewSession: (preset: NewSessionPreset) => void;
}) {
  const { t, locale, renamingSession, setRenamingSession, setDragging } = useApp();
  const { list, query, setQuery, limit, setLimit, fresh, reload } = sessionList;
  const [archiveError, setArchiveError] = useState('');
  const [restoring, setRestoring] = useState<string | null>(null);
  const [projectPages, setProjectPages] = useState<Record<string, SessionList>>({});
  const [projectErrors, setProjectErrors] = useState<Record<string, string>>({});
  const [projectLimits, setProjectLimits] = useState<Record<string, number>>({});
  const projectRequests = useRef(new Map<string, string>());
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const [selected, setSelected] = useState<string[]>([]);
  const [activeSection, setActiveSection] = useState('recents');
  const anchor = useRef<string | null>(null);
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, []);

  // Expanded branches request their own pages. Expanding a project never filters another section.
  useEffect(() => {
    for (const project of list?.folderWorkspaces || []) {
      if (!list?.expandedProjects?.includes(project.id)) continue;
      const requestKey = JSON.stringify([
        query,
        projectLimits[project.id] || PAGE,
        project.sessionIds,
        list.app,
        list.running,
        list.found,
      ]);
      if (projectRequests.current.get(project.id) === requestKey) continue;
      projectRequests.current.set(project.id, requestKey);
      void window.reframe.getSessionList(query, projectLimits[project.id] || PAGE, project.cwd).then(
        (page) => {
          if (!mounted.current || projectRequests.current.get(project.id) !== requestKey) return;
          setProjectPages((current) => ({ ...current, [project.id]: page }));
          setProjectErrors((current) => ({ ...current, [project.id]: '' }));
        },
        (error: unknown) => {
          if (mounted.current && projectRequests.current.get(project.id) === requestKey)
            setProjectErrors((current) => ({ ...current, [project.id]: String(error) }));
        },
      );
    }
  }, [list, query, projectLimits]);

  const catalogue = useMemo(() => (list ? sidebarChats(list) : []), [list]);
  const pinned = (list?.pinnedChats || []).flatMap((key) => {
    const chat = catalogue.find((entry) => entry.key === key);
    return chat ? [chat] : [];
  });
  const recent = catalogue.slice(0, limit);
  const branches = (list?.folderWorkspaces || []).map((project) => ({
    ...project,
    expanded: list?.expandedProjects?.includes(project.id) || false,
    page: projectPages[project.id],
    chats: sidebarChats(
      projectPages[project.id] ? { ...projectPages[project.id]!, app: list!.app, running: list!.running } : list!,
    ).filter((chat) => workspaceKey(chat.cwd) === project.id),
    limit: projectLimits[project.id] || PAGE,
  }));
  const rows = [
    ...new Map(
      [
        ...pinned,
        ...branches.filter((project) => project.expanded).flatMap((project) => project.chats.slice(0, project.limit)),
        ...recent,
      ].map((chat) => [chat.key, chat]),
    ).values(),
  ];
  const chosen = selected.filter((key) => rows.some((row) => row.key === key));
  const refsOf = (keys: string[]): ListItemRef[] => rows.filter((row) => keys.includes(row.key)).map((row) => row.ref);

  const onRowClick = (chat: SidebarChat, section: string, event: MouseEvent) => {
    setActiveSection(section);
    const from = anchor.current ? rows.findIndex((row) => row.key === anchor.current) : -1;
    if (event.shiftKey && from >= 0) {
      const to = rows.findIndex((row) => row.key === chat.key);
      setSelected(rows.slice(Math.min(from, to), Math.max(from, to) + 1).map((row) => row.key));
      return;
    }
    anchor.current = chat.key;
    if (event.ctrlKey || event.metaKey) {
      setSelected((keys) => (keys.includes(chat.key) ? keys.filter((key) => key !== chat.key) : [...keys, chat.key]));
      return;
    }
    setSelected([chat.key]);
    window.reframe.openSession({ item: chat.ref, target: null });
  };
  const onRowMenu = (chat: SidebarChat, section: string, event: MouseEvent) => {
    event.preventDefault();
    setActiveSection(section);
    const keys = chosen.includes(chat.key) ? chosen : [chat.key];
    if (!chosen.includes(chat.key)) {
      setSelected(keys);
      anchor.current = chat.key;
    }
    window.reframe.showListMenu(refsOf(keys));
  };
  const onRowDrag = (chat: SidebarChat, event: DragEvent) => {
    if (chosen.length > 1 && chosen.includes(chat.key)) setListDrag(event, refsOf(chosen));
    else if (chat.item.kind === 'app' && chat.item.windowId)
      setNodeDrag(event, 'tile', { key: tileKey(chat.item.id), windowId: chat.item.windowId });
    else setListDrag(event, [chat.ref]);
    const kind = dragKind(event);
    if (kind)
      setDragging({ key: kind === 'tile' && chat.ref.kind === 'app' ? tileKey(chat.ref.sessionId) : null, kind });
  };

  const renderChat = (chat: SidebarChat, section: string) => {
    const item = chat.item;
    const managed = item.kind === 'app' ? item : undefined;
    const stamp = chat.lastActiveAt ? shortTime(chat.lastActiveAt, now, t, locale) : undefined;
    const status = managed?.elsewhere
      ? 'Running'
      : managed
        ? managed.windowNumber
          ? t('list.window', { n: managed.windowNumber })
          : t('list.ended')
        : item.kind === 'running'
          ? 'Running'
          : t('list.ended');
    const meta = [section.startsWith('project:') ? '' : folderName(chat.cwd), item.distro, status]
      .filter(Boolean)
      .join(' · ');
    const hint = managed?.windowId ? t('list.showHint') : t('list.openHint');
    const tooltip = [
      chat.label,
      chat.cwd,
      hint,
      chat.lastActiveAt ? t('list.usedAt', { when: exactTime(chat.lastActiveAt, locale) }) : '',
    ]
      .filter(Boolean)
      .join('\n');
    return (
      <div className="sidebar-row-wrap" key={chat.key}>
        {managed &&
        renamingSession?.place === 'list' &&
        renamingSession.sessionId === managed.id &&
        activeSection === section ? (
          <div className="sidebar-row is-selected">
            <NameInput
              className="sidebar-name-input"
              initial={chat.label}
              placeholder={t('session.automaticName')}
              onCommit={(name) => window.reframe.renameSession(managed.id, name)}
              onDone={() => setRenamingSession(null)}
            />
          </div>
        ) : (
          <button
            className={`sidebar-row${chosen.includes(chat.key) ? ' is-selected' : ''}${fresh.has(item.claudeSessionId) ? ' is-new' : ''}`}
            data-session={managed?.id}
            data-chat={chat.key}
            aria-pressed={chosen.includes(chat.key)}
            title={tooltip}
            draggable
            onMouseDown={(event) => event.shiftKey && event.preventDefault()}
            onClick={(event) => onRowClick(chat, section, event)}
            onContextMenu={(event) => onRowMenu(chat, section, event)}
            onDragStart={(event) => onRowDrag(chat, event)}
          >
            {item.kind === 'running' ? (
              <span className="running-mark" aria-label="Running">
                ▶
              </span>
            ) : (
              <StatusDot status={chat.status} />
            )}
            <ProviderIcon provider={chat.provider} defaulted={chat.defaulted} />
            <span className="sidebar-text">
              <span className="sidebar-title-line">
                <span className="sidebar-title sidebar-chat-title" title={chat.label}>
                  {chat.label}
                </span>
                {stamp && <span className="sidebar-used">{stamp}</span>}
              </span>
              <span className="sidebar-meta">{meta}</span>
            </span>
          </button>
        )}
        {managed && !managed.windowId && (
          <button
            className="icon-button sidebar-remove"
            title={t('list.remove')}
            aria-label={t('list.remove')}
            onClick={() => window.reframe.removeSession(managed.id)}
          >
            <CloseIcon />
          </button>
        )}
      </div>
    );
  };

  return (
    <aside
      className="sidebar"
      aria-label={t('list.label')}
      onKeyDown={(event) => event.key === 'Escape' && setSelected([])}
    >
      <input
        className="sidebar-search"
        type="search"
        value={query}
        placeholder={t('list.search')}
        aria-label={t('list.search')}
        onChange={(event) => {
          setQuery(event.target.value);
          setLimit(() => PAGE);
          setProjectLimits({});
          setProjectPages({});
          projectRequests.current.clear();
        }}
      />
      {chosen.length > 1 && (
        <div className="sidebar-selection" role="group" aria-label="Selected chats">
          <span>{chosen.length} selected</span>
          <button className="button" onClick={() => window.reframe.showListMenu(refsOf(chosen))}>
            Selected chat actions
          </button>
          <button className="button" onClick={() => setSelected([])}>
            Clear selection
          </button>
        </div>
      )}
      <div className="sidebar-scroll">
        <section className="sidebar-pinned" aria-label="Pinned">
          <h3 className="sidebar-heading">Pinned</h3>
          {pinned.length ? (
            pinned.map((chat) => renderChat(chat, 'pinned'))
          ) : (
            <p className="sidebar-empty">Pin chats from their context menu.</p>
          )}
        </section>
        <section className="sidebar-projects" aria-label="Projects">
          <h3 className="sidebar-heading">Projects</h3>
          {!branches.length && <p className="sidebar-empty">No projects yet.</p>}
          {branches.map((project) => (
            <div className="sidebar-project" key={project.id} data-project={project.id}>
              <div className="sidebar-project-header">
                <button
                  className="sidebar-row folder-workspace"
                  title={project.locations
                    .map((location) => location.cwd)
                    .filter((cwd, index, paths) => paths.indexOf(cwd) === index)
                    .join('\n')}
                  aria-expanded={project.expanded}
                  onClick={() => window.reframe.setProjectExpanded(project.cwd, !project.expanded)}
                >
                  <span className={`project-chevron${project.expanded ? ' is-expanded' : ''}`}>
                    <CollapseIcon />
                  </span>
                  <FolderIcon />
                  <span className="sidebar-text">
                    <span className="sidebar-title">{project.name}</span>
                  </span>
                  <span className="sidebar-project-count">{project.sessionIds.length}</span>
                </button>
                <button
                  className="icon-button workspace-new-session"
                  title={`New session in ${project.name}`}
                  aria-label={`New session in ${project.name}`}
                  onClick={() =>
                    onNewSession({
                      cwd: project.cwd,
                      provider: project.locations[0]?.provider ?? 'chatgpt',
                      locations: project.locations,
                    })
                  }
                >
                  <PlusIcon />
                </button>
              </div>
              {project.expanded && (
                <div className="sidebar-project-chats" role="group" aria-label={`${project.name} chats`}>
                  {project.chats.slice(0, project.limit).map((chat) => renderChat(chat, `project:${project.id}`))}
                  {projectErrors[project.id] ? (
                    <p className="sidebar-error" role="alert">
                      {projectErrors[project.id]}{' '}
                      <button
                        className="button"
                        onClick={() => {
                          projectRequests.current.delete(project.id);
                          setProjectLimits((current) => ({ ...current }));
                        }}
                      >
                        Reload chats
                      </button>
                    </p>
                  ) : !project.page ? (
                    <p className="sidebar-empty" role="status">
                      Loading chats…
                    </p>
                  ) : !project.chats.length ? (
                    <p className="sidebar-empty">No matching chats.</p>
                  ) : null}
                  {(project.page?.hasMore || project.chats.length > project.limit) && (
                    <button
                      className="button sidebar-more"
                      onClick={() =>
                        setProjectLimits((current) => ({ ...current, [project.id]: project.limit + PAGE }))
                      }
                    >
                      {t('list.more')}
                    </button>
                  )}
                </div>
              )}
            </div>
          ))}
        </section>
        <section className="sidebar-recents" aria-label="Recents">
          <h3 className="sidebar-heading">Recents</h3>
          {recent.length ? (
            recent.map((chat) => renderChat(chat, 'recents'))
          ) : (
            <p className="sidebar-empty">No matching chats.</p>
          )}
          {(list?.hasMore || catalogue.length > limit) && (
            <button className="button sidebar-more" onClick={() => setLimit((n) => n + PAGE)}>
              {t('list.more')}
            </button>
          )}
        </section>
        {!!list?.archives?.length && (
          <details className="sidebar-archives">
            <summary>Archived chats ({list.archives.length})</summary>
            {list.archives.map((archive) => (
              <button
                className="sidebar-row"
                key={archive.id}
                title={`${archive.label}\n${archive.cwd}\n${archive.archive}`}
                disabled={restoring !== null}
                onClick={() => {
                  setArchiveError('');
                  setRestoring(archive.id);
                  void window.reframe
                    .restoreArchive(archive.id)
                    .then((result) => {
                      if (!result.ok) setArchiveError(result.error || 'Restore failed.');
                      reload();
                    })
                    .finally(() => setRestoring(null));
                }}
              >
                <ProviderIcon provider={archive.provider} />
                <span className="sidebar-text">
                  <span className="sidebar-title sidebar-chat-title" title={archive.label}>
                    {archive.label}
                  </span>
                  <span className="sidebar-meta">{restoring === archive.id ? 'Restoring…' : 'Restore chat'}</span>
                </span>
              </button>
            ))}
          </details>
        )}
        {archiveError && (
          <p className="sidebar-error" role="alert">
            {archiveError}
          </p>
        )}
        {list?.wslStopped.map((distro) => (
          <div className="sidebar-note" key={distro}>
            <span>{t('list.wslStopped', { distro })}</span>
            <button className="button" onClick={() => window.reframe.startWsl(distro)}>
              {t('list.wslStart')}
            </button>
          </div>
        ))}
      </div>
    </aside>
  );
}
