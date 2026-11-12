"""UI smoke test of the WPF shell in a disposable Windows Sandbox guest, through LLM Coordinator's sandbox gate.

usage: python -I run.py --shell <published shell dir> --host <host dir> --scratch <empty scratch dir>
                        [--owner-task TEXT] [--thread-name TEXT]

Starts the PTY host on guest disk, creates a cmd.exe session over its pipe, waits for the guest logon, starts the
shell as the logged-in user and checks it through WinApp UIA. Exits 0 only when every check passed; workflow.end is
always sent. Never runs anything on the host desktop. See README.md.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path

GUEST = Path(__file__).resolve().parent / 'guest'
WORK = 'C:\\Coordinator\\Work\\'
APP = 'Reframe.Shell'
SESSION = 'smoke-1'
# Typed into the terminal; its output (1042) appears only if cmd.exe received the input and answered.
COMMAND, ANSWER = 'set /a 6*7+1000', '1042'


class Gate:
    """The JSON-lines session of `coordinator-client gate run --tool windows-sandbox`."""

    def __init__(self, scratch: Path, config: Path, owner_task: str, thread_name: str | None):
        client = Path(os.environ['LOCALAPPDATA']) / 'Tools' / 'llm-coordinator' / 'bin' / 'coordinator-client.exe'
        cmd = [str(client), 'gate', 'run', '--tool', 'windows-sandbox', '--owner-task', owner_task,
               '--scratch-root', str(scratch / 'work'), '--sandbox-config', str(config)]
        if thread_name:
            cmd[5:5] = ['--thread-name', thread_name]
        self.log = open(scratch / 'session.jsonl', 'a', encoding='utf-8')
        self.cv = threading.Condition()
        self.answers: dict[str, dict] = {}
        self.eof = False
        self.ticket = None
        self.n = 0
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, encoding='utf-8', bufsize=1)
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._errors, daemon=True).start()

    def _note(self, kind, data):
        with self.cv:
            self.log.write(json.dumps({'at': time.strftime('%H:%M:%S'), kind: data}) + '\n')
            self.log.flush()

    def _read(self):
        for line in self.p.stdout:
            self._note('out', line.rstrip('\n'))
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if isinstance(msg, dict):
                self.ticket = self.ticket or msg.get('ticket_id') or msg.get('ticket')
                if 'id' in msg:
                    with self.cv:
                        self.answers[str(msg['id'])] = msg
                        self.cv.notify_all()
        with self.cv:
            self.eof = True
            self.cv.notify_all()

    def _errors(self):
        for line in self.p.stderr:
            self._note('err', line.rstrip('\n'))

    def call(self, method, params=None, timeout=900):
        self.n += 1
        rid = f'r{self.n}-{method}'
        req = {'id': rid, 'method': method, 'params': params or {}}
        self._note('send', req)
        try:
            self.p.stdin.write(json.dumps(req) + '\n')
            self.p.stdin.flush()
        except OSError as e:
            self._note('send-failed', str(e))
            return None
        with self.cv:
            self.cv.wait_for(lambda: rid in self.answers or self.eof, timeout)
            return self.answers.get(rid)

    def end(self):
        answer = None if self.eof else self.call('workflow.end', timeout=600)
        try:
            self.p.stdin.close()
        except OSError:
            pass
        try:
            self.p.wait(600)
        except subprocess.TimeoutExpired:
            self._note('wait', 'client still running after 600 s')
        self._note('exit', self.p.returncode)
        return answer, self.p.returncode


def guest_ok(answer):
    """The guest command itself succeeded (transport ok alone is not success)."""
    r = (answer or {}).get('result') or {}
    return bool(answer and answer.get('ok') and not r.get('isError') and (r.get('guest_result') or {}).get('ExitCode') == 0)


def stdout(answer):
    out = (((answer or {}).get('result') or {}).get('output') or {}).get('stdout') or {}
    return out.get('text') or json.dumps(out.get('json')) or ''


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--shell', required=True, type=Path, help='self-contained publish of Reframe.Shell (reframe-shell)')
    ap.add_argument('--host', required=True, type=Path, help='reframe-host.exe and its ConPTY (reframe-host)')
    ap.add_argument('--scratch', required=True, type=Path, help='new scratch folder (screenshots, logs, evidence)')
    ap.add_argument('--owner-task', default='Reframe WPF shell UI smoke')
    ap.add_argument('--thread-name')
    a = ap.parse_args()
    shell, host, scratch = a.shell.resolve(), a.host.resolve(), a.scratch.resolve()
    for exe in (shell / 'Reframe.Shell.exe', host / 'reframe-host.exe'):
        if not exe.is_file():
            sys.exit(f'missing {exe}')
    work = scratch / 'work'
    if work.exists() and any(work.iterdir()):
        sys.exit(f'{work} is not empty: use a new scratch folder')
    work.mkdir(parents=True, exist_ok=True)
    for f in GUEST.iterdir():
        shutil.copy2(f, work / f.name)
    config = scratch / 'sandbox-config.json'
    config.write_text(json.dumps({'memory_mib': 4096, 'networking': False, 'inputs': [
        {'host': str(shell), 'guest': 'C:\\In\\shell'}, {'host': str(host), 'guest': 'C:\\In\\host'}]}), 'utf-8')

    checks: list[tuple[str, bool, str]] = []

    def check(name, ok, detail=''):
        checks.append((name, bool(ok), detail))
        print(('PASS ' if ok else 'FAIL ') + name + (f': {detail}' if detail and not ok else ''), flush=True)
        return ok

    def ps(script, *args):
        sha = hashlib.sha256((work / script).read_bytes()).hexdigest()  # the gate wants it lower-case
        return gate.call('sandbox.powershell', {'script': WORK + script, 'sha256': sha, 'args': list(args)})

    def ui(*argv):
        return gate.call('sandbox.winapp', {'argv': list(argv), 'run_as': 'ExistingLogin'})

    def shot(name):
        ui('ui', 'screenshot', '-a', APP, '-o', WORK + name)
        check(f'screenshot {name}', (work / name).is_file())

    def steps():
        if not check('host started and session spawned', guest_ok(ps('Start-Host.ps1')), 'see work/start-host.log'):
            return
        connected = gate.call('sandbox.connect')
        if not check('guest desktop connected (minimised)', connected and connected.get('ok')
                     and (connected.get('result') or {}).get('connected'), json.dumps(connected)[:300]):
            return
        if not check('guest logon', guest_ok(ps('Wait-Login.ps1'))):
            return
        started = gate.call('sandbox.exec', {'argv': ['C:\\Windows\\System32\\cmd.exe', '/d', '/c',
                                                      WORK + 'start-shell.cmd'], 'run_as': 'ExistingLogin'})
        if not check('shell started as the logged-in user', guest_ok(started)):
            return

        # The session row: a ListItem named after the session (not "Reframe.Shell.SessionItem").
        item = ui('ui', 'wait-for', SESSION, '-a', APP, '--type', 'ListItem', '--timeout', '30000')
        windows = ui('ui', 'list-windows', '-a', APP, '--json')
        check('shell window exists', guest_ok(windows) and 'Reframe Shell' in stdout(windows), stdout(windows)[:300])
        if not check(f'session item is named "{SESSION}"', guest_ok(item), stdout(item)[:300]):
            return
        tree = ui('ui', 'inspect', '-a', APP, '--depth', '8')
        check('no item reads as a type name', guest_ok(tree) and 'Reframe.Shell.' not in stdout(tree))
        shot('shot1-attached.png')

        # Enter opens the tab, and keyboard focus moves into the terminal.
        slug = re.search(rf'(\S+) ListItem "{SESSION}"', stdout(item))
        ui('ui', 'invoke', slug.group(1) if slug else SESSION, '-a', APP)
        ui('ui', 'send-keys', 'enter', '-a', APP)
        opened = ui('ui', 'wait-for', 'Text Area', '-a', APP, '--timeout', '15000')
        if not check('Enter opens the tab', guest_ok(opened), stdout(opened)[:300]):
            return
        focus = ui('ui', 'wait-for', 'Text Area', '-a', APP, '-p', 'HasKeyboardFocus', '--value', 'True',
                   '--timeout', '10000')
        focused = ui('ui', 'get-focused', '-a', APP)
        check('focus lands in the terminal', guest_ok(focus) and 'Text Area' in stdout(focused), stdout(focused)[:300])
        tab = ui('ui', 'wait-for', SESSION, '-a', APP, '--type', 'TabItem', '--timeout', '5000')
        check(f'tab is named "{SESSION}"', guest_ok(tab), stdout(tab)[:300])
        close = ui('ui', 'wait-for', f'Close {SESSION}', '-a', APP, '--type', 'Button', '--timeout', '5000')
        check('close button has an accessible name', guest_ok(close), stdout(close)[:300])
        shot('shot2-tab-open.png')

        # Typed input round-trips through the host: OS-level input (posted window messages do not reach it).
        ui('ui', 'focus', 'Text Area', '-a', APP)  # the shell's window in front for SendInput; focus was checked above
        typed = ui('ui', 'send-keys', COMMAND, '--verbatim', '--via', 'send-input', '-a', APP)
        entered = ui('ui', 'send-keys', 'enter', '--via', 'send-input', '-a', APP)
        check('input sent', guest_ok(typed) and guest_ok(entered))
        echoed = ui('ui', 'wait-for', 'Text Area', '-a', APP, '--value', ANSWER, '--contains', '--timeout', '15000')
        check(f'typed input round-trips ({COMMAND} -> {ANSWER})', guest_ok(echoed), stdout(echoed)[:300])
        shot('shot3-input.png')

    gate = Gate(scratch, config, a.owner_task, a.thread_name)
    try:
        steps()
    finally:
        try:
            ps('Collect.ps1', '-Tag', 'final')
        except Exception as e:  # evidence only; never skips workflow.end
            print(f'collect failed: {e}')
        end, code = gate.end()
        check('workflow.end', end and end.get('ok'), json.dumps(end)[:300])
        failed = [c for c in checks if not c[1]]
        (scratch / 'results.json').write_text(json.dumps({
            'ticket': gate.ticket, 'client_exit': code, 'passed': not failed and code == 0,
            'checks': [{'name': n, 'ok': ok, 'detail': d} for n, ok, d in checks]}, indent=1), 'utf-8')
        print(f'ticket {gate.ticket}, client exit {code}, {len(checks) - len(failed)}/{len(checks)} checks passed')
        print(f'evidence: {work}')
    sys.exit(0 if not failed and code == 0 else 1)


if __name__ == '__main__':
    main()
