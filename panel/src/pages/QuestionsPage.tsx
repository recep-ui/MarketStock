import { useState } from 'react';
import { ApiError, get, post, qs, type Channel, type Paged } from '../api';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ErrorBox, Notice, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { ChannelFetch } from '../components/ChannelFetch';
import { commandStateLabels } from './ChannelWrite';

export type QuestionListItem = {
  id: number; channelId: number; channelCode: string; questionNo: string; text: string; productName: string | null; productCode: string | null;
  skuCode: string | null; state: string; channelStatus: string | null; askedAtUtc: string; deadlineUtc: string | null; channelAnswer: string | null;
  lastAnswerState: string | null; lastUpdatedUtc: string;
};
type QuestionDetail = {
  summary: QuestionListItem;
  answers: { id: number; text: string; user: string | null; atUtc: string; commandId: number | null; commandState: string | null; commandError: string | null }[];
  rule: { supported: boolean; minLength: number; maxLength: number; note: string | null };
  canAnswer: boolean; cannotAnswerReason: string | null;
};

const stateLabels: Record<string, string> = { CEVAP_BEKLIYOR: 'Cevap bekliyor', CEVAPLANDI: 'Cevaplandı', KAPANDI: 'Kapandı' };

/**
 * Soru-Cevap (ADR-029). Sorular kanallardan çekilir (müşteri adı/iletişim bilgisi saklanmaz). Cevabı Yönetici veya Destek yazar;
 * "Gönderilecek cevap" onay kutusu işaretlenmeden gönderilmez. Cevap kanal işlemleri kuyruğundan gider; her cevap kullanıcı adıyla kaydedilir.
 */
export function QuestionsPage() {
  const [channelId, setChannelId] = useState('');
  const [state, setState] = useState('CEVAP_BEKLIYOR');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const list = useLoad(() => get<Paged<QuestionListItem>>(`/api/questions${qs({ channelId: channelId || undefined, state: state || undefined, search: search || undefined, pageSize: 100 })}`),
    [channelId, state, search]);

  return (
    <>
      <h1>Soru-Cevap</h1>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <Card>
        <p className="muted small">
          Kanallardan gelen müşteri soruları. Müşteri adı ve iletişim bilgisi saklanmaz. Cevap, kanalın süresi dolmadan gönderilmelidir.
        </p>
        <div className="filters">
          <select aria-label="Soru kanalı" value={channelId} onChange={e => { setChannelId(e.target.value); setSelected(null); }}>
            <option value="">Tüm kanallar</option>
            {(channels.data ?? []).map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}</option>)}
          </select>
          <select aria-label="Soru durumu" value={state} onChange={e => { setState(e.target.value); setSelected(null); }}>
            <option value="">Tümü</option>
            {Object.entries(stateLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
          <input placeholder="Soru / ürün / soru no" aria-label="Soru ara" value={search} onChange={e => setSearch(e.target.value)} />
          <button onClick={list.reload}>Yenile</button>
        </div>
        <ChannelFetch channels={channels.data ?? []} channelId={channelId} endpoint="/api/questions/fetch" jobType="SORU_CEK" label="Kanaldan soru çek" onDone={list.reload} />
        <ErrorBox error={list.error} />
        <div className={selected ? 'split' : undefined}>
          <table className="table" data-testid="question-table">
            <thead><tr><th>Tarih</th><th>Kanal</th><th>Soru</th><th>Ürün</th><th>Durum</th><th>Son cevap</th></tr></thead>
            <tbody>
              {(list.data?.items ?? []).map(q => (
                <tr key={q.id} data-testid="question-row" className={q.id === selected ? 'selected' : 'clickable'} onClick={() => setSelected(q.id)}>
                  <td>{formatDateTime(q.askedAtUtc)}</td>
                  <td><ChannelBadge code={q.channelCode} /></td>
                  <td className="wrap">{q.text}</td>
                  <td>{q.productName ?? '—'}{q.skuCode && <div className="muted small">{q.skuCode}</div>}</td>
                  <td>
                    {stateLabels[q.state] ?? q.state}
                    {q.lastAnswerState && <div className="muted small">cevap: {commandStateLabels[q.lastAnswerState] ?? q.lastAnswerState}</div>}
                  </td>
                  <td>{formatDateTime(q.deadlineUtc)}</td>
                </tr>
              ))}
              {list.data?.items.length === 0 && <tr><td colSpan={6} className="empty">Soru yok.</td></tr>}
            </tbody>
          </table>
          {selected && <QuestionDetailPanel id={selected} onChanged={list.reload} onClose={() => setSelected(null)} onNotice={setNotice} />}
        </div>
      </Card>
    </>
  );
}

function QuestionDetailPanel({ id, onChanged, onClose, onNotice }: { id: number; onChanged: () => void; onClose: () => void; onNotice: (m: string) => void }) {
  const { can } = useAuth();
  const detail = useLoad(() => get<QuestionDetail>(`/api/questions/${id}`), [id]);
  const [text, setText] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const d = detail.data;
  const length = text.trim().length;
  const lengthOk = d ? length >= d.rule.minLength && length <= d.rule.maxLength : false;

  async function send() {
    if (!d) return;
    if (!confirmed) { setError(new ApiError(400, 'Gönderilecek cevabı onaylayın.')); return; }
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ outcome: string }>(`/api/questions/${id}/answer`, { text: text.trim(), confirmed });
      onNotice(r.outcome === 'ONAY_BEKLIYOR'
        ? 'Cevap kaydedildi; kanal "deneme" modunda olduğu için Yönetici onayı bekliyor (Senkron Merkezi → Kanal işlemleri).'
        : 'Cevap kaydedildi; kanal işlemleri kuyruğundan gönderilecek.');
      setText('');
      setConfirmed(false);
      detail.reload();
      onChanged();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="detail" data-testid="question-detail">
      <header className="card-header">
        <h2>Soru {d?.summary.questionNo ?? ''}</h2>
        <button className="link" onClick={onClose} aria-label="Kapat">✕</button>
      </header>
      <ErrorBox error={detail.error ?? error} />
      {d && (
        <>
          <p><ChannelBadge code={d.summary.channelCode} /> {stateLabels[d.summary.state] ?? d.summary.state} · {d.summary.channelStatus ?? '—'}</p>
          <blockquote className="wrap" data-testid="question-text">{d.summary.text}</blockquote>
          <p className="muted small">
            {d.summary.productName ?? 'Ürün bilgisi yok'}{d.summary.productCode && ` · ${d.summary.productCode}`} · soruldu {formatDateTime(d.summary.askedAtUtc)}
            {d.summary.deadlineUtc && ` · son cevap ${formatDateTime(d.summary.deadlineUtc)}`}
          </p>
          {d.summary.channelAnswer && <p><strong>Kanaldaki cevap:</strong> <span className="wrap">{d.summary.channelAnswer}</span></p>}

          {can('questionAnswer') && (d.canAnswer
            ? (
              <div className="answer-form">
                <textarea aria-label="Cevap" rows={5} value={text} maxLength={d.rule.maxLength} onChange={e => { setText(e.target.value); setConfirmed(false); }}
                  placeholder={`Cevabınız (${d.rule.minLength}–${d.rule.maxLength} karakter)`} style={{ width: '100%' }} />
                <div className={`muted small${length > 0 && !lengthOk ? ' text-danger' : ''}`}>{length} / {d.rule.maxLength} karakter (en az {d.rule.minLength})</div>
                {d.rule.note && <div className="muted small">{d.rule.note}</div>}
                {lengthOk && (
                  <div className="alert alert-warn" data-testid="answer-preview">
                    <strong>Gönderilecek cevap:</strong> <span className="wrap">{text.trim()}</span>
                  </div>
                )}
                <label className="check">
                  <input type="checkbox" checked={confirmed} disabled={!lengthOk} onChange={e => setConfirmed(e.target.checked)} />
                  {' '}Gönderilecek cevabı okudum, müşteriye bu metin gidecek
                </label>
                <div className="actions">
                  <button className="primary" disabled={busy || !lengthOk || !confirmed} onClick={send}>Cevabı gönder</button>
                </div>
              </div>
            )
            : <div className="alert alert-warn" data-testid="cannot-answer">{d.cannotAnswerReason}</div>)}

          <h3>Gönderilen cevaplar</h3>
          <ul className="timeline" data-testid="question-answers">
            {d.answers.map(a => (
              <li key={a.id}>
                <span className="wrap">{a.text}</span>
                <div className="muted small">
                  {a.user ?? '—'} · {formatDateTime(a.atUtc)} · {commandStateLabels[a.commandState ?? ''] ?? a.commandState ?? ''}
                  {a.commandError && ` · ${a.commandError}`}
                </div>
              </li>
            ))}
            {d.answers.length === 0 && <li className="muted">Henüz cevap yok.</li>}
          </ul>
        </>
      )}
    </aside>
  );
}
