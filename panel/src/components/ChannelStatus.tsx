import type { ReactNode } from 'react';
import type { ChannelStatus } from '../api';
import { environmentLabel, formatDateTime, formatRelative, jobLabels } from '../format';
import { ChannelBadge, ErrorExplain, HealthBadge } from './ui';

const capabilityShort: Record<string, string> = {
  STOK: 'Stok',
  SIPARIS_DURUM: 'Sipariş durumu',
  IADE_KARAR: 'İade kararı',
  FIYAT: 'Fiyat',
  SORU_CEVAP: 'Soru cevabı'
};

/** Panelde öne çıkan çekim işleri (sırasıyla); diğerleri "Tüm işler"de. */
const primaryJobs = ['SIPARIS_CEK', 'IADE_CEK', 'SORU_CEK', 'KATALOG_CEK'];

function connectionText(s: ChannelStatus): string {
  if (s.mode === 'MOCK') return s.realAdapterAvailable ? 'Sahte (mock) mod' : 'Sahte (mock) — gerçek entegrasyon yok';
  const env = environmentLabel(s.environment);
  return env ? `Gerçek API · ${env}` : 'Gerçek API';
}

function writeText(s: ChannelStatus): { text: string; tone?: 'warn' | 'muted' } {
  if (s.mode === 'MOCK') return { text: 'Sahte kanala yazılır (dışarı bir şey gitmez)', tone: 'muted' };
  if (s.writeLocked) return { text: 'Kilitli (deneme modu): kanala hiçbir şey gönderilmez', tone: 'warn' };
  if (!s.active) return { text: 'Kanal pasif: gönderim yapılmaz', tone: 'muted' };
  const open = s.capabilities.filter(c => c.state !== 'KAPALI');
  if (open.length === 0) return { text: 'Tüm yetenekler kapalı', tone: 'muted' };
  return { text: open.map(c => `${capabilityShort[c.capability] ?? c.capability}${c.state === 'DENEME' ? ' (onaylı)' : ''}`).join(', ') + ' açık' };
}

/**
 * Kanal durum kartı (ADR-033): bağlantı modu/ortamı, API bilgisi, kanala yazma durumu ve son çekimler tek bakışta.
 * Durum her zaman simge + metinle yazılır; hata varsa "ne oldu + ne yapmalı" gösterilir.
 */
export function ChannelStatusCard({ status: s, compact, actions, selected, onSelect }: {
  status: ChannelStatus; compact?: boolean; actions?: ReactNode; selected?: boolean; onSelect?: () => void;
}) {
  const write = writeText(s);
  const jobs = primaryJobs
    .map(type => s.jobs.find(j => j.jobType === type))
    .filter((j): j is ChannelStatus['jobs'][number] => !!j);
  const failing = s.jobs.find(j => j.errorInfo);
  return (
    <section className={`status-card health-border-${s.health.toLowerCase()}${selected ? ' selected' : ''}`}
      data-testid={`channel-status-${s.code}`} aria-label={`${s.name} durumu`}>
      <header className="status-card-header">
        <ChannelBadge code={s.code} />
        <HealthBadge health={s.health} />
      </header>
      <p className="status-reason">{s.healthReason}</p>
      <dl className="kv status-kv">
        <dt>Bağlantı</dt><dd>{connectionText(s)}</dd>
        <dt>API bilgisi</dt>
        <dd>{s.hasCredentials
          ? <><span aria-hidden="true" className="ok-text">✓</span> Girildi</>
          : s.realAdapterAvailable ? <><span aria-hidden="true" className="text-danger">✕</span> Girilmedi</> : <span className="muted">Gerekmiyor</span>}</dd>
        <dt>Kanala yazma</dt><dd className={write.tone === 'warn' ? 'warn-text' : write.tone === 'muted' ? 'muted' : undefined}>{write.text}</dd>
        {!compact && (
          <>
            <dt>Son çekimler</dt>
            <dd>
              {jobs.length === 0 && <span className="muted">Henüz çekim yapılmadı</span>}
              <ul className="job-list">
                {jobs.map(j => (
                  <li key={j.jobType} title={`Son deneme: ${formatDateTime(j.lastAttemptUtc)} · son başarılı: ${formatDateTime(j.lastSuccessUtc)}`}>
                    {j.errorInfo
                      ? <span className={j.errorInfo.transient ? 'warn-text' : 'text-danger'}><span aria-hidden="true">✕</span> {jobLabels[j.jobType] ?? j.jobType}</span>
                      : <span><span aria-hidden="true" className="ok-text">✓</span> {jobLabels[j.jobType] ?? j.jobType}</span>}
                    <span className="muted"> · {formatRelative(j.lastAttemptUtc)}</span>
                  </li>
                ))}
              </ul>
            </dd>
          </>
        )}
      </dl>
      {failing?.errorInfo && (
        <ErrorExplain compact info={failing.errorInfo} />
      )}
      {(actions || onSelect) && (
        <div className="status-card-actions">
          {actions}
          {onSelect && <button className="small inline" onClick={onSelect} aria-pressed={selected}>{selected ? 'Seçili' : 'Ayarlar'}</button>}
        </div>
      )}
    </section>
  );
}

/** Kanal kartları ızgarası (Genel Bakış'ta kompakt). */
export function ChannelStatusGrid({ statuses, compact, renderActions, selectedId, onSelect }: {
  statuses: ChannelStatus[]; compact?: boolean; renderActions?: (s: ChannelStatus) => ReactNode;
  selectedId?: number | null; onSelect?: (s: ChannelStatus) => void;
}) {
  return (
    <div className="status-grid" data-testid="channel-status-grid">
      {statuses.map(s => (
        <ChannelStatusCard key={s.id} status={s} compact={compact} actions={renderActions?.(s)}
          selected={selectedId === s.id} onSelect={onSelect ? () => onSelect(s) : undefined} />
      ))}
    </div>
  );
}
