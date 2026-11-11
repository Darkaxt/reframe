// A terminal's WebGL renderer dies with its GPU context (a driver reset, the GPU process restarting). xterm then draws
// with its DOM renderer, but only once it is told to redraw; WebGL is tried again a little later, and after repeated
// losses that terminal stays on the DOM renderer.

export const WEBGL_RETRY_MS = 3000;
export const WEBGL_MAX_LOSSES = 3;

export class WebglRecovery {
  private losses = 0;

  /** Records a lost context; whether WebGL may be tried again for this terminal. */
  lost(): boolean {
    this.losses += 1;
    return this.losses < WEBGL_MAX_LOSSES;
  }

  /** Whether this terminal gave up on WebGL. */
  get givenUp(): boolean {
    return this.losses >= WEBGL_MAX_LOSSES;
  }
}
