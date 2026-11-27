import { useState } from 'react';
import { get, post, put, qs, type Paged } from '../api';
import { useAuth } from '../auth';
import { Pager } from '../components/DataTable';
import { Card, ErrorBox, Field, Notice, useLoad } from '../components/ui';
import { formatDateTime } from '../format';

export type FeedbackNote = {
  id: number; text: string; userName: string; fullName: string; status: 'YENI' | 'TAMAMLANDI' | 'REDDEDILDI';
  createdAtUtc: string; statusChangedBy: string | null; statusChangedAtUtc: string | null;
};

const statusLabels: Record<FeedbackNote['status'], string> = { YENI: 'Yeni', TAMAMLANDI: 'Tamamlandı', REDDEDILDI: 'Reddedildi' };
const statusClass: Record<FeedbackNote['status'], string> = { YENI: 'badge', TAMAMLANDI: 'badge ok', REDDEDILDI: 'badge danger' };
const maxLength = 2000;

/** Öneri ve İstekler (ADR-064): her kullanıcı not yazar, not yazanın adıyla kaydedilir; durumu Yönetici değiştirir. */
export function FeedbackPage() {
  const { me, can } = useAuth();
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const notes = useLoad(() => get<Paged<FeedbackNote>>(`/api/feedback${qs({ status, page, pageSize: 50 })}`), [status, page]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setSaveError(null);
    try {
      await post<FeedbackNote>('/api/feedback', { text });
      setText('');
      setNotice('Notunuz kaydedildi. Teşekkürler!');
      setPage(1);
      notes.reload();
    } catch (err) {
      setSaveError(err);
    } finally {
      setSaving(false);
    }
  };

  const changeStatus = async (id: number, next: FeedbackNote['status']) => {
    try {
      await put<FeedbackNote>(`/api/feedback/${id}/status`, { status: next });
      notes.reload();
    } catch (err) {
      setSaveError(err);
    }
  };

  return (
    <>
      <h1>Öneri ve İstekler</h1>
      <Card title="Yeni not">
        <p className="muted small">
          Uygulamayla ilgili önerinizi, isteğinizi veya karşılaştığınız sorunu yazın. Not, <b>{me?.fullName || me?.userName}</b> adıyla kaydedilir.
        </p>
        <form className="feedback-form" onSubmit={submit}>
          <Field label="Notunuz" help={`${text.length} / ${maxLength}`}>
            <textarea rows={4} maxLength={maxLength} value={text} onChange={e => setText(e.target.value)} aria-label="Not"
              placeholder="Örn. Sipariş listesinde kargo firmasına göre filtre olsun" data-testid="feedback-text" />
          </Field>
          <button type="submit" disabled={saving || !text.trim()} data-testid="feedback-submit">{saving ? 'Kaydediliyor…' : 'Kaydet'}</button>
        </form>
        <Notice text={notice} onClose={() => setNotice(null)} />
        <ErrorBox error={saveError} />
      </Card>

      <Card title="Notlar">
        <div className="filters">
          <select aria-label="Durum" value={status} onChange={e => { setPage(1); setStatus(e.target.value); }}>
            <option value="">Tüm notlar</option>
            {Object.entries(statusLabels).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </div>
        <ErrorBox error={notes.error} onRetry={notes.reload} />
        {notes.loading && <div className="loading">Yükleniyor…</div>}
        {notes.data && notes.data.items.length === 0 && <p className="muted">Henüz not yok.</p>}
        <ul className="feedback-list">
          {(notes.data?.items ?? []).map(n => (
            <li key={n.id} className="feedback-item" data-testid="feedback-row">
              <div className="feedback-head">
                <b data-testid="feedback-author">{n.fullName}</b>
                <span className="muted small">{n.userName !== n.fullName ? `(${n.userName}) · ` : ''}{formatDateTime(n.createdAtUtc)}</span>
                <span className={statusClass[n.status]}>{statusLabels[n.status]}</span>
                {n.statusChangedBy && <span className="muted small">{n.statusChangedBy} · {formatDateTime(n.statusChangedAtUtc!)}</span>}
              </div>
              <div className="feedback-text">{n.text}</div>
              {can('admin') && (
                <div className="feedback-actions">
                  {n.status !== 'TAMAMLANDI' && <button className="link" onClick={() => changeStatus(n.id, 'TAMAMLANDI')}>Tamamlandı</button>}
                  {n.status !== 'REDDEDILDI' && <button className="link" onClick={() => changeStatus(n.id, 'REDDEDILDI')}>Reddet</button>}
                  {n.status !== 'YENI' && <button className="link" onClick={() => changeStatus(n.id, 'YENI')}>Yeniye al</button>}
                </div>
              )}
            </li>
          ))}
        </ul>
        {notes.data && <Pager page={page} pageSize={notes.data.pageSize} total={notes.data.total} onPage={setPage} />}
      </Card>
    </>
  );
}
