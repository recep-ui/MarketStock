import { useEffect, useState, type ReactNode } from 'react';
import { ApiError } from '../api';
import { channelNames, statusLabels, syncStatusLabels } from '../format';
import type { OrderStatus } from '../api';

/** Ürünün kanaldaki sayfası (yalnızca kanal API'sinin verdiği https adresi; ADR-031). Yeni sekmede, referrer gönderilmeden açılır. */
export function ChannelLink({ code, url, label }: { code: string; url: string | null | undefined; label?: string }) {
  if (!url) return null;
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" className="channel-link" data-testid={`link-${code}`} title="Ürünü kanalda aç">
      {label ?? (code.charAt(0) + code.slice(1).toLowerCase())} ↗
    </a>
  );
}

export function ChannelBadge({ code }: { code: string }) {
  return <span className={`badge channel-${code.toLowerCase()}`}>{channelNames[code] ?? code}</span>;
}

export function StatusBadge({ status }: { status: OrderStatus }) {
  return <span className={`badge status-${status.toLowerCase()}`}>{statusLabels[status]}</span>;
}

export function SyncBadge({ status }: { status: string }) {
  const label = status === 'GONDERILIYOR' ? 'Gönderimde' : syncStatusLabels[status] ?? status;
  return <span className={`badge sync-${status.toLowerCase()}`}>{label}</span>;
}

export function Card({ title, children, actions }: { title?: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="card">
      {(title || actions) && (
        <header className="card-header">
          {title && <h2>{title}</h2>}
          {actions && <div className="card-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

export function Stat({ label, value, tone, testId }: { label: string; value: ReactNode; tone?: 'danger' | 'warn'; testId?: string }) {
  return (
    <div className={`stat ${tone ?? ''}`} data-testid={testId}>
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof ApiError ? error.message : 'Beklenmeyen bir hata oluştu.';
  return <div className="alert alert-error" role="alert">{message}</div>;
}

export function Notice({ text, onClose }: { text: string | null; onClose: () => void }) {
  useEffect(() => {
    if (!text) return;
    const t = setTimeout(onClose, 5000);
    return () => clearTimeout(t);
  }, [text, onClose]);
  return text ? <div className="alert alert-ok" role="status">{text}</div> : null;
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}

/** Basit veri yükleme kancası. reload() ile yeniden çağrılır. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    load()
      .then(d => !cancelled && (setData(d), setError(null)))
      .catch(e => !cancelled && setError(e))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  return { data, error, loading, reload: () => setTick(t => t + 1) };
}
