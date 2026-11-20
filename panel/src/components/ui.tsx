import { useEffect, useState, type ReactNode } from 'react';
import { ApiError } from '../api';
import { channelNames, statusLabels, syncStatusLabels } from '../format';
import type { ChannelErrorInfo, ChannelStatus, OrderStatus } from '../api';

/** Ürünün kanaldaki sayfası (yalnızca kanal API'sinin verdiği https adresi; ADR-031). Yeni sekmede, referrer gönderilmeden açılır. */
export function ChannelLink({ code, url, label }: { code: string; url: string | null | undefined; label?: string }) {
  if (!url) return null;
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" className="channel-link" data-testid={`link-${code}`} title="Ürünü kanalda aç">
      {label ?? (code.charAt(0) + code.slice(1).toLowerCase())} ↗
    </a>
  );
}

/**
 * Kanalın kendi API'sinden gelen küçük resim (ADR-042): yanlış eşleşmeyi gözle yakalamak için. Görsel kanalın CDN'inden
 * yüklenir (referrer gönderilmez); yüklenemezse yer tutucu gösterilir. Tıklayınca büyük hali yeni sekmede açılır.
 */
export function Thumb({ url, alt, size = 48 }: { url: string | null | undefined; alt: string; size?: number }) {
  const [failed, setFailed] = useState(false);
  if (!url || failed) {
    return <span className="thumb thumb-empty" style={{ width: size, height: size }} title="Kanal görsel bildirmedi" aria-label="görsel yok">—</span>;
  }
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" className="thumb" title="Görseli büyük aç" style={{ width: size, height: size }}>
      <img src={url} alt={alt} loading="lazy" referrerPolicy="no-referrer" width={size} height={size} onError={() => setFailed(true)} data-testid="channel-thumb" />
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

export function ErrorBox({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (!error) return null;
  const message = error instanceof ApiError ? error.message : 'Beklenmeyen bir hata oluştu.';
  const retryable = !(error instanceof ApiError) || error.status >= 500 || error.status === 0;
  return (
    <div className="alert alert-error alert-row" role="alert">
      <span>{message}</span>
      {onRetry && retryable && <button className="small inline" onClick={onRetry}>Tekrar dene</button>}
    </div>
  );
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

/**
 * Kanal hatasının anlaşılır gösterimi (ADR-033): ne oldu (başlık) + ne yapmalı + düğme; ham metin "Teknik ayrıntı"da.
 * Geçici hatalar uyarı (sarı), kalıcılar hata (kırmızı) tonunda; durum her zaman metinle de yazılır (yalnızca renk değil).
 */
export function ErrorExplain({ info, raw, compact, action }: { info?: ChannelErrorInfo | null; raw?: string | null; compact?: boolean; action?: ReactNode }) {
  if (!info && !raw) return null;
  if (!info) return <span className="wrap">{raw}</span>;
  const tone = info.transient ? 'warn' : 'error';
  return (
    <div className={`error-explain ${tone}${compact ? ' compact' : ''}`} data-testid="error-explain" data-kind={info.kind}>
      <div className="error-explain-title">
        <span aria-hidden="true">{info.transient ? '⏳' : '⚠'}</span> {info.title}
      </div>
      <div className="error-explain-advice">{info.advice}</div>
      {action && <div className="error-explain-action">{action}</div>}
      <details>
        <summary>Teknik ayrıntı</summary>
        <ul>
          {info.problems.map((p, i) => <li key={i}>{p}</li>)}
          {info.moreCount > 0 && <li className="muted">+{info.moreCount} sorun daha</li>}
        </ul>
      </details>
    </div>
  );
}

const healthText: Record<ChannelStatus['health'], { icon: string; label: string }> = {
  CALISIYOR: { icon: '●', label: 'Çalışıyor' },
  DIKKAT: { icon: '▲', label: 'Dikkat' },
  HATA: { icon: '✕', label: 'Hata' },
  SAHTE: { icon: '◌', label: 'Sahte veri' },
  KAPALI: { icon: '○', label: 'Kapalı' }
};

/** Kanal genel durumu: simge + metin + renk. */
export function HealthBadge({ health }: { health: ChannelStatus['health'] }) {
  const h = healthText[health];
  return (
    <span className={`badge health health-${health.toLowerCase()}`} data-testid="health-badge">
      <span aria-hidden="true">{h.icon}</span> {h.label}
    </span>
  );
}
