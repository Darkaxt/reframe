// Hardware acceleration is a setting, off unless chosen: reframe draws terminals, which need no GPU, and NVIDIA
// driver resets (TDRs) were seen while it used one. Chromium must be told before the app is ready, before the state
// store loads, so the saved preference is read here directly; anything unreadable means off.
import fs from 'node:fs';

/** What this run uses, decided once at startup (a change in Settings applies after a restart). */
export const gpu = { accelerated: false };

/** Whether state.json asks for hardware acceleration: only an explicit `true` does. */
export function savedGpuAcceleration(stateFile: string): boolean {
  try {
    const saved = JSON.parse(fs.readFileSync(stateFile, 'utf8')) as { preferences?: { gpuAcceleration?: unknown } };
    return saved?.preferences?.gpuAcceleration === true;
  } catch {
    return false;
  }
}
