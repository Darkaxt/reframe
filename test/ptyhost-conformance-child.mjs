// The stand-in for claude in the PTY host conformance tests (src/ptyhost/host.conformance.ts): it runs in the host's
// ConPTY, says READY with its terminal size, and answers each line typed into it.
//   size      prints SIZE <cols>x<rows>
//   color     prints a line with colors and attributes
//   lines N   prints N numbered lines (to fill the scrollback)
//   exit N    exits with code N
//   shell start / shell stop   starts a long-lived tool shell (cmd.exe, a child of this process) / ends it
//   other     prints ECHO: <line>
import { execFileSync, spawn } from 'node:child_process';

const out = (text) => process.stdout.write(text);
let shell = null;
const endShell = () => {
  if (!shell) return;
  try {
    execFileSync('taskkill', ['/PID', String(shell.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
  } catch {
    // gone already
  }
  shell = null;
};
process.on('exit', endShell);
const size = () => `${process.stdout.columns}x${process.stdout.rows}`;
out(`READY ${size()}\r\n`);
if (process.stdin.isTTY) process.stdin.setRawMode(true);
process.stdin.setEncoding('utf8');
let line = '';
process.stdin.on('data', (chunk) => {
  for (const c of chunk) {
    if (c !== '\r' && c !== '\n') {
      line += c;
      continue;
    }
    const typed = line;
    line = '';
    if (!typed) continue;
    const [word, arg] = typed.split(' ');
    if (word === 'size') out(`SIZE ${size()}\r\n`);
    else if (word === 'color')
      out('\x1b[1;31mred\x1b[0m \x1b[4;38;5;200munder\x1b[0m \x1b[7;48;2;10;20;30minverse\x1b[0m\r\n');
    else if (word === 'lines') for (let i = 0; i < Number(arg); i++) out(`line ${i}\r\n`);
    else if (word === 'exit') process.exit(Number(arg));
    else if (word === 'shell' && arg === 'start')
      shell ??= spawn('cmd.exe', ['/d', '/c', 'ping -n 120 127.0.0.1 >nul'], { stdio: 'ignore', windowsHide: true });
    else if (word === 'shell') endShell();
    else out(`ECHO: ${typed}\r\n`);
  }
});
