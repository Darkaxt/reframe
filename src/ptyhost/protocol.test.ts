import { describe, expect, it } from 'vitest';
import { checkHello, compatible, encode, LineDecoder, pipeName, PROTOCOL_VERSION } from './protocol';

const token = 'a'.repeat(64);

describe('pty host handshake', () => {
  it('welcomes the profile token with the same protocol version', () => {
    expect(checkHello({ type: 'hello', token, protocol: PROTOCOL_VERSION }, token)).toBe('ok');
  });

  it('refuses another token, a missing one, or anything but a hello', () => {
    expect(checkHello({ type: 'hello', token: 'b'.repeat(64), protocol: PROTOCOL_VERSION }, token)).toBe('token');
    expect(checkHello({ type: 'hello', token: 'short', protocol: PROTOCOL_VERSION }, token)).toBe('token');
    expect(checkHello({ type: 'hello', protocol: PROTOCOL_VERSION }, token)).toBe('token');
    expect(checkHello({ type: 'spawn', token, protocol: PROTOCOL_VERSION }, token)).toBe('token');
    expect(checkHello(null, token)).toBe('token');
  });

  it('refuses another protocol version (after the token, so it says nothing to strangers)', () => {
    expect(checkHello({ type: 'hello', token, protocol: PROTOCOL_VERSION + 1 }, token)).toBe('protocol');
    expect(checkHello({ type: 'hello', token: 'x', protocol: PROTOCOL_VERSION + 1 }, token)).toBe('token');
  });

  it('takes only hosts of its own protocol version as compatible', () => {
    expect(compatible({ protocol: PROTOCOL_VERSION })).toBe(true);
    expect(compatible({ protocol: PROTOCOL_VERSION - 1 })).toBe(false);
    expect(compatible({ protocol: PROTOCOL_VERSION + 1 })).toBe(false);
  });

  it('names a pipe per profile and host process', () => {
    const a = pipeName('D:\\Tools\\Reframe\\profiles\\ClaudeGPT\\ptyhost', 10);
    expect(a).toBe(pipeName('d:\\tools\\reframe\\profiles\\claudegpt\\ptyhost', 10));
    expect(a).not.toBe(pipeName('D:\\Tools\\Reframe\\profiles\\ClaudeGPT\\ptyhost', 11));
    expect(a).not.toBe(pipeName('D:\\Tools\\Reframe\\profiles\\Other\\ptyhost', 10));
  });
});

describe('LineDecoder', () => {
  it('reassembles messages split across chunks and drops lines that are not JSON', () => {
    const decoder = new LineDecoder();
    const wire = encode({ type: 'write', sessionId: 's', data: 'a\nb' }) + 'garbage\n' + encode({ type: 'shutdown' });
    const out = [
      ...decoder.push(wire.slice(0, 7)),
      ...decoder.push(wire.slice(7, 40)),
      ...decoder.push(wire.slice(40)),
    ];
    expect(out).toEqual([{ type: 'write', sessionId: 's', data: 'a\nb' }, { type: 'shutdown' }]);
  });
});
