import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { NewSessionResult } from '@shared/ipc';
import { defaultsFor, parseAddDirs, MAX_PROMPT } from '@shared/new-session';
import type { Preferences } from '@shared/state';
import { providerName, PROVIDERS, type Provider } from '@shared/providers';
import {
  EFFORT_LEVELS,
  MODEL_SUGGESTIONS,
  PERMISSION_MODES,
  type EffortLevel,
  type PermissionMode,
} from '@shared/launch';
import { useApp } from '../context';
import { folderKey, rebaseFolder } from '@shared/folder-workspaces';

const folderName = (folder: string): string => folder.trim().split(/[\\/]/).filter(Boolean).pop() ?? '';

export interface NewSessionPreset {
  cwd: string;
  provider: Provider;
  locations?: { cwd: string; provider: Provider }[];
}

export function NewSessionDialog({ onClose, initial }: { onClose: () => void; initial?: NewSessionPreset }) {
  const { t } = useApp();
  const [cwd, setCwd] = useState(initial?.cwd ?? '');
  // Only a name the user types is given to the session (and to Claude Code as --name). Without one, Claude Code's own
  // title names it, and the folder until there is one.
  const [customName, setCustomName] = useState('');
  const [worktree, setWorktree] = useState(false);
  const [worktreeName, setWorktreeName] = useState('');
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState<EffortLevel | '' | null>(null);
  const effortPicked = useRef(false);
  const [provider, setProvider] = useState<Provider>(initial?.provider ?? 'chatgpt');
  const providerRef = useRef<Provider>(initial?.provider ?? 'chatgpt');
  const [providerRoots, setProviderRoots] = useState<Record<Provider, string>>({ chatgpt: '', anthropic: '' });
  const [permissionMode, setPermissionMode] = useState<PermissionMode | ''>('');
  const [addDirs, setAddDirs] = useState('');
  const [initialPrompt, setInitialPrompt] = useState('');
  const currentPreferences = useRef<Preferences | null>(null);
  const [recent, setRecent] = useState<string[]>([]);
  const [error, setError] = useState<Extract<NewSessionResult, { ok: false }> | null>(null);
  const [busy, setBusy] = useState(false);
  const folderInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    void window.reframe.getDialogDefaults().then(({ recentFolders, preferences, providerWorkspaceRoots }) => {
      setRecent(recentFolders);
      setProviderRoots(providerWorkspaceRoots);
      setCwd((current) => current || providerWorkspaceRoots[providerRef.current]);
      // What was used last for this provider (else Settings' defaults), unless something was picked here already.
      const remembered = defaultsFor(preferences, providerRef.current);
      currentPreferences.current = preferences;
      setModel((current) => current || remembered.model || '');
      setPermissionMode((current) => current || remembered.permissionMode || '');
      setAddDirs((current) => current || remembered.addDirs.join('\n'));
      if (!effortPicked.current) setEffort(remembered.effort || '');
    });
    folderInput.current?.focus();
  }, []);

  const name = customName;
  const otherRoot = folderKey(providerRoots[provider === 'chatgpt' ? 'anthropic' : 'chatgpt']);
  const providerRecent = recent.filter((folder) => {
    const key = folderKey(folder);
    return (
      !providerRoots[provider === 'chatgpt' ? 'anthropic' : 'chatgpt'] ||
      (key !== otherRoot && !key.startsWith(otherRoot + '/'))
    );
  });

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!cwd.trim() || busy) return;
    setBusy(true);
    const result = await window.reframe.createSession({
      cwd: cwd.trim(),
      name: name.trim() || undefined,
      worktree,
      worktreeName: worktree ? worktreeName.trim() || undefined : undefined,
      model: model.trim() || undefined,
      effort: effort ?? undefined,
      provider,
      permissionMode: permissionMode || undefined,
      addDirs: parseAddDirs(addDirs),
      initialPrompt: initialPrompt.trim() || undefined,
    });
    setBusy(false);
    if (result.ok) onClose();
    else setError(result);
  };

  const pickProvider = (next: Provider) => {
    const previous = providerRef.current;
    providerRef.current = next;
    setProvider(next);
    // The provider's own location of this workspace; else the folder moved from one workspace root to the other.
    const location = initial?.locations?.find((l) => l.provider === next)?.cwd;
    setCwd(location || rebaseFolder(cwd, providerRoots[previous], providerRoots[next]));
    // Each provider starts with its own remembered choices.
    if (!currentPreferences.current) return setModel('');
    const remembered = defaultsFor(currentPreferences.current, next);
    setModel(remembered.model || '');
    if (!effortPicked.current) setEffort(remembered.effort || ''); // an effort picked here stays across providers
    setPermissionMode(remembered.permissionMode || '');
    setAddDirs(remembered.addDirs.join('\n'));
  };

  const browse = async () => {
    const folder = await window.reframe.pickFolder();
    if (folder) {
      setCwd(folder);
      setError(null);
    }
  };

  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <form
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-session-title"
        onSubmit={submit}
        onKeyDown={(e) => e.key === 'Escape' && onClose()}
      >
        <h2 id="new-session-title">{t('dialog.title')}</h2>
        <label className="field">
          <span>{t('dialog.provider')}</span>
          <select
            aria-label={t('dialog.provider')}
            value={provider}
            onChange={(e) => pickProvider(e.target.value as Provider)}
          >
            {PROVIDERS.map((p) => (
              <option key={p} value={p}>
                {providerName(p)}
              </option>
            ))}
          </select>
        </label>
        <p className="provider-note">
          {t(provider === 'anthropic' ? 'dialog.providerNote.anthropic' : 'dialog.providerNote.chatgpt')}
        </p>
        <label className="field">
          <span>{t('dialog.effort')}</span>
          <select
            aria-label={t('dialog.effort')}
            value={effort ?? ''}
            onChange={(event) => {
              effortPicked.current = true;
              setEffort(event.target.value as EffortLevel | '');
            }}
          >
            <option value="">{t('dialog.effortDefault')}</option>
            {EFFORT_LEVELS.map((level) => (
              <option key={level} value={level}>
                {level}
              </option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>{t('dialog.folder')}</span>
          <div className="field-row">
            <input
              ref={folderInput}
              value={cwd}
              placeholder={t('dialog.folderPlaceholder')}
              spellCheck={false}
              onChange={(e) => {
                setCwd(e.target.value);
                setError(null);
              }}
            />
            <button type="button" className="button" onClick={browse}>
              {t('dialog.browse')}
            </button>
          </div>
        </label>
        {providerRecent.length > 0 && (
          <div className="recent" aria-label={t('dialog.recent')}>
            {providerRecent.map((folder) => (
              <button
                type="button"
                key={folder}
                className={`chip${folder === cwd ? ' is-selected' : ''}`}
                title={folder}
                onClick={() => {
                  setCwd(folder);
                  setError(null);
                }}
              >
                {folderName(folder) || folder}
              </button>
            ))}
          </div>
        )}

        <label className="field">
          <span>{t('dialog.name')}</span>
          <input
            value={name}
            placeholder={t('dialog.namePlaceholder')}
            onChange={(e) => setCustomName(e.target.value)}
          />
        </label>

        <label className="field">
          <span>{t('dialog.prompt')}</span>
          <textarea
            value={initialPrompt}
            rows={3}
            maxLength={MAX_PROMPT}
            placeholder={t('dialog.promptPlaceholder')}
            onChange={(e) => setInitialPrompt(e.target.value)}
            onKeyDown={(e) => {
              // Ctrl+Enter starts, as the Start button; Enter alone is a new line.
              if (e.key === 'Enter' && e.ctrlKey) e.currentTarget.form?.requestSubmit();
            }}
          />
        </label>

        <label className="checkbox">
          <input type="checkbox" checked={worktree} onChange={(e) => setWorktree(e.target.checked)} />
          <span>{t('dialog.worktree')}</span>
        </label>
        {worktree && (
          <label className="field nested">
            <span>{t('dialog.worktreeName')}</span>
            <input value={worktreeName} spellCheck={false} onChange={(e) => setWorktreeName(e.target.value)} />
          </label>
        )}

        <details className="advanced">
          <summary>{t('dialog.advanced')}</summary>
          <label className="field">
            <span>{t('dialog.model')}</span>
            <input
              value={model}
              list="model-suggestions"
              placeholder={t('dialog.modelPlaceholder')}
              spellCheck={false}
              onChange={(e) => setModel(e.target.value)}
            />
            <datalist id="model-suggestions">
              {MODEL_SUGGESTIONS.map((m) => (
                <option key={m} value={m} />
              ))}
            </datalist>
          </label>
          <label className="field">
            <span>{t('dialog.permissionMode')}</span>
            <select value={permissionMode} onChange={(e) => setPermissionMode(e.target.value as PermissionMode | '')}>
              <option value="">{t('dialog.default')}</option>
              {PERMISSION_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {mode}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>{t('dialog.addDirs')}</span>
            <textarea
              value={addDirs}
              rows={2}
              spellCheck={false}
              placeholder={t('dialog.addDirsPlaceholder')}
              onChange={(e) => setAddDirs(e.target.value)}
            />
          </label>
        </details>

        {error && (
          <p className="form-error">
            {error.distro ? t('error.claudeNotFoundInWsl', { distro: error.distro }) : t(`dialog.error.${error.error}`)}
            {error.error === 'claudeNotFound' &&
              !error.distro &&
              window.reframe.platform === 'win32' &&
              ` ${t('error.claudeOnlyInWsl')}`}
          </p>
        )}

        <div className="button-row end">
          <button type="button" className="button" onClick={onClose}>
            {t('dialog.cancel')}
          </button>
          <button type="submit" className="button primary" disabled={!cwd.trim() || busy}>
            {t('dialog.start')}
          </button>
        </div>
      </form>
    </div>
  );
}
