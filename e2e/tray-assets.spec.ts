import { expect, test } from '@playwright/test';
import { cleanup, launch, quit, setup } from './helpers';

test('packaged tray and window icons load from their actual runtime paths', async () => {
  test.skip(!process.env.REFRAME_E2E_APP, 'Qualification must use a native packaged application.');
  const { dir, env } = setup();
  try {
    const { app } = await launch(env);
    const images = await app.evaluate(({ nativeImage }) => {
      const assets = (globalThis as unknown as { reframe: { assets: Record<string, string> } }).reframe.assets;
      return Object.entries(assets).map(([name, file]) => {
        const image = nativeImage.createFromPath(file);
        return { name, file, empty: image.isEmpty(), size: image.getSize() };
      });
    });
    expect(images).toHaveLength(3);
    expect(images.filter((image) => image.empty || image.size.width <= 0 || image.size.height <= 0)).toEqual([]);
    if (process.env.REFRAME_SCREENSHOTS)
      fs.writeFileSync(
        path.join(process.env.REFRAME_SCREENSHOTS, 'native-runtime-images.json'),
        JSON.stringify({ images, verifiedNonEmpty: true }, null, 2),
      );
    await quit(app);
  } finally {
    await cleanup(dir);
  }
});
import fs from 'node:fs';
import path from 'node:path';
