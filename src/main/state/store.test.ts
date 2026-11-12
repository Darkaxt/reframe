import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { defaultState, type AppState } from '@shared/state';
import { StateConflict, StateFiles } from './files';
import { StateStore, type StateHost } from './store';

const temps: string[] = [];
function store(): StateStore {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'reframe-store-'));
  temps.push(dir);
  return new StateStore(path.join(dir, 'state.json'));
}
afterEach(() => {
  vi.useRealTimers();
  for (const dir of temps.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

const reloaded = (file: string): AppState => new StateStore(file).load().state;
const withFolder = (folder: string): AppState => ({ ...defaultState(), recentFolders: [folder] });

describe('StateStore', () => {
  it('gives files from before preferences existed the default preferences', () => {
    const s = store();
    const { preferences: _preferences, ...old } = defaultState();
    fs.writeFileSync(s.file, JSON.stringify(old));
    expect(s.load()).toMatchObject({ source: 'file', state: { preferences: { nameFromFolder: false } } });
  });

  it('does not look for Claude Code in WSL for files from before that setting existed', () => {
    const s = store();
    const state = defaultState();
    const { lookInWsl: _lookInWsl, ...preferences } = state.preferences;
    fs.writeFileSync(s.file, JSON.stringify({ ...state, preferences }));
    expect(s.load()).toMatchObject({ source: 'file', state: { preferences: { lookInWsl: false } } });
    expect(defaultState().preferences.lookInWsl).toBe(false);
  });

  it('keeps hardware acceleration off for new profiles and files from before that setting existed', () => {
    const s = store();
    const state = defaultState();
    const { gpuAcceleration: _gpuAcceleration, ...preferences } = state.preferences;
    fs.writeFileSync(s.file, JSON.stringify({ ...state, preferences }));
    expect(s.load()).toMatchObject({ source: 'file', state: { preferences: { gpuAcceleration: false } } });
    expect(defaultState().preferences.gpuAcceleration).toBe(false);
  });

  it('keeps new sessions on the Node host for new profiles and files from before the ptyHost setting existed', () => {
    const s = store();
    const state = defaultState();
    const { ptyHost: _ptyHost, ...preferences } = state.preferences;
    fs.writeFileSync(s.file, JSON.stringify({ ...state, preferences }));
    expect(s.load()).toMatchObject({ source: 'file', state: { preferences: { ptyHost: 'node' } } });
    expect(defaultState().preferences.ptyHost).toBe('node');
  });

  it('gives files from before hidden threads existed none, and keeps hidden threads across a save', async () => {
    const s = store();
    const { hiddenThreads: _hidden, ...old } = defaultState();
    fs.writeFileSync(s.file, JSON.stringify(old));
    const loaded = s.load();
    expect(loaded).toMatchObject({ source: 'file', state: { hiddenThreads: [] } });
    const hidden = [{ claudeSessionId: 'abc', label: 'Elsewhere', cwd: 'C:/work', hiddenAt: 5 }];
    s.save({ ...loaded.state, hiddenThreads: hidden });
    await s.flush();
    expect(reloaded(s.file).hiddenThreads).toEqual(hidden);
  });

  it('loads files with the removed saved workspaces and drops them on the next save', () => {
    const s = store();
    const old = defaultState();
    const win = { ...old.windows[0]!, workspaceId: 'ws1' };
    fs.writeFileSync(
      s.file,
      JSON.stringify({
        ...old,
        windows: [win],
        workspaces: [
          {
            id: 'ws1',
            name: 'Frontend',
            savedAt: 1,
            window: { bounds: null, maximized: false, layout: null, dock: [] },
            sessions: [],
          },
        ],
      }),
    );
    const { state, source } = s.load();
    expect(source).toBe('file');
    expect(state).not.toHaveProperty('workspaces');
    expect(state.windows[0]).not.toHaveProperty('workspaceId');
    expect(state.windows[0]!.id).toBe(win.id);
    s.save(state);
    const written = JSON.parse(fs.readFileSync(s.file, 'utf8'));
    expect(written).not.toHaveProperty('workspaces');
    expect(written.windows[0]).not.toHaveProperty('workspaceId');
  });

  it('starts from defaults without a file', () => {
    const { state, source } = store().load();
    expect(source).toBe('default');
    expect(state.windows).toHaveLength(1);
  });

  it('round-trips state and keeps the previous version as backup', () => {
    const s = store();
    s.save(withFolder('one'));
    s.save(withFolder('two'));
    expect(s.load()).toMatchObject({ source: 'file', state: { recentFolders: ['two'] } });
    expect(JSON.parse(fs.readFileSync(s.backupFile, 'utf8')).recentFolders).toEqual(['one']);
    expect(fs.readdirSync(path.dirname(s.file)).filter((f) => f.endsWith('.tmp'))).toEqual([]);
  });

  it('falls back to the backup when the file is corrupt or invalid', () => {
    const s = store();
    s.save(withFolder('one'));
    s.save(withFolder('two'));
    fs.writeFileSync(s.file, '{ "version": 1, "sessions": ');
    expect(s.load()).toMatchObject({ source: 'backup', state: { recentFolders: ['one'] } });
    fs.writeFileSync(s.file, JSON.stringify({ version: 1, sessions: 'nope' }));
    expect(s.load().source).toBe('backup');
  });

  it('sets an unusable file aside instead of writing over it', () => {
    const s = store();
    s.save(withFolder('one'));
    s.save(withFolder('two'));
    const broken = JSON.stringify({ version: 1, sessions: 'nope' });
    fs.writeFileSync(s.file, broken);
    const loaded = s.load();
    expect(loaded).toMatchObject({ source: 'backup', state: { recentFolders: ['one'] } });
    expect(loaded.rejected?.reason).toContain('sessions');
    expect(path.basename(loaded.rejected!.file)).toMatch(/^state\.json\.rejected-\d{8}-\d{6}$/);
    // Saved twice, as a start and a window move do: the set-aside file and the backup's content survive.
    s.save(loaded.state);
    s.save(withFolder('three'));
    expect(fs.readFileSync(loaded.rejected!.file, 'utf8')).toBe(broken);
    expect(JSON.parse(fs.readFileSync(s.backupFile, 'utf8')).recentFolders).toEqual(['one']);
  });

  it('sets aside an unusable file without a backup too, and keeps only the last five', () => {
    const s = store();
    for (let i = 0; i < 7; i++) {
      fs.writeFileSync(s.file, `garbage ${i}`);
      expect(s.load()).toMatchObject({ source: 'default', rejected: { reason: expect.stringContaining('not JSON') } });
    }
    const kept = fs.readdirSync(path.dirname(s.file)).filter((f) => f.includes('.rejected-'));
    expect(kept).toHaveLength(5);
    const contents = kept.map((f) => fs.readFileSync(path.join(path.dirname(s.file), f), 'utf8')).sort();
    expect(contents).toEqual(['garbage 2', 'garbage 3', 'garbage 4', 'garbage 5', 'garbage 6']);
  });

  it('leaves a backup from a newer reframe alone as well', () => {
    const s = store();
    const newer = JSON.stringify({ version: 2, sessions: {}, somethingNew: true });
    fs.writeFileSync(s.backupFile, newer);
    fs.writeFileSync(s.file, 'garbage');
    expect(s.load()).toMatchObject({ source: 'newer', rejected: { reason: expect.stringContaining('not JSON') } });
    s.save(withFolder('one'));
    expect(fs.existsSync(s.file)).toBe(false);
    expect(fs.readFileSync(s.backupFile, 'utf8')).toBe(newer);
  });

  it('leaves a file from a newer reframe alone and writes nothing', () => {
    const s = store();
    const newer = JSON.stringify({ version: 2, sessions: {}, somethingNew: true });
    fs.writeFileSync(s.file, newer);
    expect(s.load()).toMatchObject({ source: 'newer' });
    s.save(withFolder('one'));
    s.saveSoon(withFolder('two'));
    s.flush();
    expect(fs.readFileSync(s.file, 'utf8')).toBe(newer);
    expect(fs.readdirSync(path.dirname(s.file))).toEqual(['state.json']);
  });

  it('does not overwrite a good backup with a broken file', () => {
    const s = store();
    s.save(withFolder('one'));
    s.save(withFolder('two'));
    fs.writeFileSync(s.file, 'garbage');
    s.save(withFolder('three'));
    expect(JSON.parse(fs.readFileSync(s.backupFile, 'utf8')).recentFolders).toEqual(['one']);
  });

  it('coalesces debounced saves and flushes on demand', () => {
    vi.useFakeTimers();
    const s = store();
    s.saveSoon(withFolder('a'));
    s.saveSoon(withFolder('b'));
    expect(fs.existsSync(s.file)).toBe(false);
    vi.advanceTimersByTime(600);
    expect(s.load().state.recentFolders).toEqual(['b']);
    s.saveSoon(withFolder('c'));
    s.flush();
    expect(s.load().state.recentFolders).toEqual(['c']);
  });

  describe('through a pty host that serves state', () => {
    /** A host answering as the Node host does (src/ptyhost/services.ts), counting its writes. */
    function host(files = new StateFiles()) {
      const calls: string[] = [];
      let serves = true;
      const h: StateHost & { calls: string[]; stop(): void } = {
        get services() {
          return serves ? { state: 1 } : {};
        },
        calls,
        stop: () => (serves = false),
        async call(service, method, params) {
          await Promise.resolve();
          calls.push(`${service}.${method}`);
          const p = params as { file: string; content: string; revision: number; backup: boolean };
          if (method === 'read') return files.read(p.file);
          try {
            return { revision: files.write(p.file, p.content, p.revision, p.backup) };
          } catch (error) {
            throw Object.assign(new Error(String(error)), { code: error instanceof StateConflict ? 'conflict' : 'io' });
          }
        },
      };
      return h;
    }

    it('writes the same bytes and backup as the in-process store', async () => {
      const [one, two] = [withFolder('one'), withFolder('two')];
      const here = store();
      here.save(one);
      here.save(two);
      const there = store();
      const h = host();
      there.useHost(() => h);
      there.save(one);
      await there.settle();
      there.save(two);
      await there.settle();
      expect(fs.readFileSync(there.file)).toEqual(fs.readFileSync(here.file));
      expect(fs.readFileSync(there.backupFile)).toEqual(fs.readFileSync(here.backupFile));
      expect(h.calls).toEqual(['state.read', 'state.write', 'state.write']);
    });

    it('round-trips a file the in-process store wrote, and the in-process store reads what the host wrote', async () => {
      const s = store();
      s.save(withFolder('old'));
      const before = fs.readFileSync(s.file, 'utf8');
      const files = new StateFiles();
      expect(files.read(s.file).content).toBe(before);
      const h = host(files);
      s.useHost(() => h);
      s.save(withFolder('new'));
      await s.settle();
      expect(fs.readFileSync(s.backupFile, 'utf8')).toBe(before);
      expect(new StateStore(s.file).load()).toMatchObject({ source: 'file', state: { recentFolders: ['new'] } });
    });

    it('writes only the newest of saves made while one is being written', async () => {
      const s = store();
      const h = host();
      s.useHost(() => h);
      for (const f of ['a', 'b', 'c', 'd']) s.save(withFolder(f));
      await s.settle();
      expect(s.load().state.recentFolders).toEqual(['d']);
      expect(h.calls.filter((c) => c === 'state.write')).toHaveLength(2);
    });

    it('reads again after a write elsewhere, and keeps a bad file out of the backup', async () => {
      const s = store();
      const h = host();
      s.useHost(() => h);
      s.save(withFolder('one'));
      await s.settle();
      fs.writeFileSync(s.file, 'garbage, from somebody else');
      s.save(withFolder('two'));
      await s.settle();
      expect(s.load().state.recentFolders).toEqual(['two']);
      expect(fs.existsSync(s.backupFile)).toBe(false);
      expect(h.calls).toEqual(['state.read', 'state.write', 'state.write', 'state.read', 'state.write']);
    });

    it('saves here once the host is gone, after what it was writing', async () => {
      const s = store();
      const h = host();
      s.useHost(() => h);
      s.save(withFolder('one'));
      h.stop();
      s.save(withFolder('two'));
      await s.settle();
      expect(s.load().state.recentFolders).toEqual(['two']);
      // With nothing going on, here and at once.
      s.save(withFolder('three'));
      expect(s.load().state.recentFolders).toEqual(['three']);
    });

    it('saves here when the host fails', async () => {
      const s = store();
      const log = vi.fn();
      s.useHost(
        () => ({ services: { state: 1 }, call: () => Promise.reject(new Error('the pty host connection closed')) }),
        log,
      );
      s.save(withFolder('one'));
      await s.settle();
      expect(s.load().state.recentFolders).toEqual(['one']);
      expect(log).toHaveBeenCalledWith('warn', 'the pty host could not save the state; saving here', expect.anything());
    });

    it('flush resolves once the newest state, a debounced one too, is written by the host', async () => {
      const s = store();
      const h = host();
      s.useHost(() => h);
      s.save(withFolder('one'));
      expect(fs.existsSync(s.file)).toBe(false);
      s.saveSoon(withFolder('two'));
      await s.flush();
      expect(s.load().state.recentFolders).toEqual(['two']);
      expect(h.calls.filter((c) => c === 'state.write')).toHaveLength(2);
    });

    it('keeps one write going and writes them in order, the newest last', async () => {
      const s = store();
      const files = new StateFiles();
      const h = host(files);
      let inFlight = 0;
      let most = 0;
      const seen: string[] = [];
      s.useHost(() => ({
        services: h.services,
        async call(service, method, params) {
          most = Math.max(most, ++inFlight);
          try {
            const result = await h.call(service, method, params);
            if (method === 'write') seen.push(JSON.parse((params as { content: string }).content).recentFolders[0]);
            return result;
          } finally {
            inFlight--;
          }
        },
      }));
      s.save(withFolder('a'));
      s.save(withFolder('b'));
      await Promise.resolve();
      s.save(withFolder('c'));
      await s.flush();
      expect(most).toBe(1);
      expect(seen).toEqual(['a', 'c']);
      expect(s.load().state.recentFolders).toEqual(['c']);
    });

    it('retries a conflict with the newest state, and loses no update', async () => {
      const s = store();
      const h = host();
      s.useHost(() => h);
      s.save(withFolder('one'));
      await s.flush();
      // Somebody else writes the file: the host's revision is stale, and a newer save comes while that is found out.
      fs.writeFileSync(s.file, JSON.stringify(withFolder('elsewhere')));
      s.save(withFolder('two'));
      await Promise.resolve();
      s.save(withFolder('three'));
      await s.flush();
      expect(s.load().state.recentFolders).toEqual(['three']);
      expect(h.calls).toEqual(['state.read', 'state.write', 'state.write', 'state.read', 'state.write']);
    });

    it('writes here when the host does not answer within the deadline, and a late answer writes nothing older', async () => {
      const s = store();
      const log = vi.fn();
      let answer: (() => void) | undefined;
      const calls: string[] = [];
      s.useHost(
        () => ({
          services: { state: 1 },
          call: (_service, method) => {
            calls.push(method);
            return new Promise((_resolve, reject) => {
              answer = () => reject(Object.assign(new Error('conflict'), { code: 'conflict' }));
            });
          },
        }),
        log,
      );
      s.save(withFolder('one'));
      s.save(withFolder('two'));
      await s.flush(20);
      expect(s.load().state.recentFolders).toEqual(['two']);
      expect(log).toHaveBeenCalledWith('warn', 'the pty host did not save the state in time; saving here', {
        deadlineMs: 20,
      });
      // From then on, written here at once; the host is not asked again.
      s.save(withFolder('three'));
      expect(s.load().state.recentFolders).toEqual(['three']);
      answer!();
      await new Promise((resolve) => setTimeout(resolve, 10));
      expect(s.load().state.recentFolders).toEqual(['three']);
      expect(calls).toEqual(['read']);
    });

    it('writes here, at once, through a host that does not serve state (an older one swapped in)', () => {
      const s = store();
      const call = vi.fn();
      s.useHost(() => ({ services: {}, call }));
      s.save(withFolder('one'));
      expect(s.load().state.recentFolders).toEqual(['one']);
      expect(call).not.toHaveBeenCalled();
    });
  });
});
