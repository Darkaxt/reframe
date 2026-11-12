// The Codex quota, the status line check and the adopted clients, from the PTY host when it serves
// them (`services.quota` and `clients` in its welcome, since 0.10.1-claudegpt.21); else (an older host taken
// over in a hot swap, no host attached, or a request that failed) read here, as before. Both give the same answers:
// the host runs the same code (Node host) or its port (Rust host, rust/reframe-services), and the conformance tests
// hold them to it. What is not asked of a host stays synchronous, as it was.
import { adoptedClient, type AdoptedClient } from './client-adoption';
import { codexWindows, coordinatorExecutable, readCodexUsage, userHasStatusLine } from './quota';
import type { PtyHostClient } from './ptyhost-client';
import type { HostServices } from '../ptyhost/protocol';
import type { Provider } from '@shared/providers';
import type { QuotaWindow } from '@shared/quota';
import type { LogFn } from './log';

/** The part of PtyHostClient used here. */
export type ReadHost = Pick<PtyHostClient, 'services' | 'call'>;

export interface CodexQuota {
  /** Whether LLM Coordinator is installed (without it, there is nothing to ask again). */
  coordinator: boolean;
  windows: QuotaWindow[] | null;
}

export class HostReads {
  constructor(
    private host: () => ReadHost | undefined,
    private log?: LogFn,
  ) {}

  private served(service: keyof HostServices): ReadHost | null {
    const host = this.host();
    return host && (host.services[service] ?? 0) >= 1 ? host : null;
  }

  /** The host's answer; the local one when the host cannot give it. */
  private async ask<T>(
    host: ReadHost,
    service: keyof HostServices,
    method: string,
    params: Record<string, unknown>,
    local: () => T | Promise<T>,
  ): Promise<T> {
    try {
      return (await host.call(service, method, params)) as T;
    } catch (error) {
      this.log?.('warn', 'the pty host did not answer; reading here', {
        request: `${service}.${method}`,
        error: String(error),
      });
      return local();
    }
  }

  /** The Codex quota windows LLM Coordinator observed (a Coordinator that does not answer gives none). */
  async codexQuota(localAppData = process.env.LOCALAPPDATA ?? null): Promise<CodexQuota> {
    const local = async (): Promise<CodexQuota> => {
      const exe = coordinatorExecutable(localAppData ?? undefined);
      return { coordinator: !!exe, windows: exe ? codexWindows(await readCodexUsage(exe)) : null };
    };
    const host = this.served('quota');
    if (!host) return local();
    const answer = await this.ask<{ coordinator: boolean; usage: unknown } | CodexQuota>(
      host,
      'quota',
      'codex',
      { localAppData },
      local,
    );
    return 'usage' in answer ? { coordinator: answer.coordinator, windows: codexWindows(answer.usage) } : answer;
  }

  /** Whether the user's Claude Code settings set a status line. */
  userHasStatusLine(claudeHome: string): boolean | Promise<boolean> {
    const host = this.served('quota');
    const local = () => userHasStatusLine(claudeHome);
    return host ? this.ask(host, 'quota', 'statusLine', { claudeHome }, local) : local();
  }

  /** The provider's adopted client from the record, verified by hash. */
  adoptedClient(record: string, provider: Provider): AdoptedClient | Promise<AdoptedClient> {
    const host = this.served('clients');
    const local = () => adoptedClient(record, provider);
    return host ? this.ask(host, 'clients', 'adopted', { record, provider }, local) : local();
  }
}
