import { useEffect, useState, type CSSProperties } from 'react';
import { quotaLevel, untilLabel, windowLabel, type ProviderQuota } from '@shared/quota';
import { useApp } from '../context';
import { exactTime } from '../time';

/** Subscription quota at the foot of the session list: one bar per window the source reports. */
export function QuotaPanel({ quota }: { quota: ProviderQuota[] }) {
  const { t, locale } = useApp();
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);
  const shown = [...quota].sort((a, b) => a.source.localeCompare(b.source)).filter((q) => q.windows.length);
  if (!shown.length) return null;
  return (
    <section className="sidebar-quota" aria-label={t('quota.label')}>
      {shown.map((q) => (
        <div className="quota-source" key={q.source} data-quota={q.source}>
          <h3 className="sidebar-heading">{t(q.source === 'claude' ? 'quota.claude' : 'quota.codex')}</h3>
          {q.windows.map((w) => {
            const level = quotaLevel(w.usedPercent);
            const used = Math.round(w.usedPercent);
            const reset = w.resetsAt ? t('quota.resets', { when: untilLabel(w.resetsAt, now) }) : '';
            const title = [
              t('quota.used', { percent: used }),
              w.resetsAt ? t('quota.resetsAt', { when: exactTime(w.resetsAt, locale) }) : '',
              t('quota.observed', { when: exactTime(q.at, locale) }),
            ]
              .filter(Boolean)
              .join('\n');
            return (
              <div className={`quota-row quota-${level}`} key={w.id} title={title}>
                <span className="quota-window">{windowLabel(w.minutes) || w.id}</span>
                <span
                  className="quota-bar"
                  role="meter"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={used}
                  aria-label={`${windowLabel(w.minutes) || w.id}: ${t('quota.used', { percent: used })}`}
                >
                  <span className="quota-fill" style={{ '--quota-used': `${used}%` } as CSSProperties} />
                </span>
                <span className="quota-percent">{used}%</span>
                <span className="quota-reset">{reset}</span>
              </div>
            );
          })}
        </div>
      ))}
    </section>
  );
}
