import { useEffect, useRef, useState } from 'react';
import { get, post, type Channel, type ChannelErrorInfo } from '../api';
import { useAuth } from '../auth';
import { channelNames, formatDateTime } from '../format';
import { ErrorBox, ErrorExplain } from './ui';

type JobState = {
  jobType: string; lastSuccessUtc: string | null; lastAttemptUtc: string | null; lastError: string | null; consecutiveFailures: number;
  errorInfo?: ChannelErrorInfo | null;
};

/**
 * Kanaldan elle çekme (yalnızca okuma; kanala yazmaz): kanal seçimi, "Kanaldan … çek" (Yönetici) ve son çekim durumu.
 * Çekim arka planda çalışır; bileşen son çekim kaydını birkaç saniye izler ve bitince onDone'u çağırır.
 */
export function ChannelFetch({ channels, endpoint, jobType, label, onDone, channelId: controlled }: {
  channels: Channel[]; endpoint: string; jobType: string; label: string; onDone: () => void; channelId?: string;
}) {
  const { can } = useAuth();
  const [own, setOwn] = useState('');
  const channelId = controlled ?? own;
  const channel = channels.find(c => String(c.id) === channelId);
  const [state, setState] = useState<JobState | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const timer = useRef<number | undefined>(undefined);

  async function loadState(id: string) {
    const rows = await get<JobState[]>(`/api/channels/${id}/job-states`);
    const s = rows.find(r => r.jobType === jobType) ?? null;
    setState(s);
    return s;
  }

  useEffect(() => {
    setState(null);
    if (channelId) void loadState(channelId).catch(() => undefined);
    return () => window.clearInterval(timer.current);
  }, [channelId, jobType]); // eslint-disable-line react-hooks/exhaustive-deps

  async function start() {
    if (!channel) return;
    setError(null);
    setRunning(true);
    const before = state?.lastAttemptUtc ?? null;
    try {
      await post(`${endpoint}/${channel.code.toLowerCase()}`);
      let tries = 0;
      window.clearInterval(timer.current);
      timer.current = window.setInterval(async () => {
        tries++;
        const s = await loadState(String(channel.id)).catch(() => null);
        if ((s && s.lastAttemptUtc !== before) || tries > 40) {
          window.clearInterval(timer.current);
          setRunning(false);
          onDone();
        }
      }, 2000);
    } catch (err) {
      setError(err);
      setRunning(false);
    }
  }

  return (
    <div className="channel-fetch" data-testid={`channel-fetch-${jobType}`}>
      {controlled === undefined && (
        <select aria-label={`${label} kanalı`} value={own} onChange={e => setOwn(e.target.value)}>
          <option value="">Kanal seçin</option>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}{c.active ? '' : ' (pasif)'}</option>)}
        </select>
      )}
      {can('admin') && (
        <button disabled={!channel || running} onClick={start} title={channel ? undefined : 'Önce kanal seçin'}>
          {running ? <span aria-live="polite">Çekiliyor…</span> : label}
        </button>
      )}
      {channel && !channel.active && <span className="muted small">Kanal pasif: çekim yapılmaz (Ayarlar → Kanallar).</span>}
      {channel && state && (
        <span className="muted small" data-testid="fetch-state" aria-live="polite">
          Son çekim: {formatDateTime(state.lastAttemptUtc)}
          {state.lastError && state.consecutiveFailures > 0
            ? <span className="text-danger"> · başarısız</span>
            : state.lastSuccessUtc ? ' · başarılı' : ''}
        </span>
      )}
      {channel && state && state.lastError && state.consecutiveFailures > 0 && (
        <ErrorExplain compact info={state.errorInfo} raw={state.lastError} />
      )}
      {channel && !state && <span className="muted small">Bu kanaldan henüz çekim yapılmadı.</span>}
      <ErrorBox error={error} />
    </div>
  );
}
