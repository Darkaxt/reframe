import { describe, expect, it } from 'vitest';
import { WEBGL_MAX_LOSSES, WebglRecovery, terminalRenderer } from './webgl-recovery';

describe('WebGL recovery', () => {
  it('retries after a lost context and gives up after repeated losses', () => {
    const recovery = new WebglRecovery();
    const answers = Array.from({ length: WEBGL_MAX_LOSSES }, () => recovery.lost());
    expect(answers).toEqual([...Array(WEBGL_MAX_LOSSES - 1).fill(true), false]);
    expect(recovery.givenUp).toBe(true);
  });

  it('has not given up before any loss', () => {
    expect(new WebglRecovery().givenUp).toBe(false);
  });
});

describe('terminal renderer', () => {
  it('uses WebGL only while this run uses the GPU', () => {
    expect(terminalRenderer(true)).toBe('webgl');
    expect(terminalRenderer(false)).toBe('dom');
  });
});
