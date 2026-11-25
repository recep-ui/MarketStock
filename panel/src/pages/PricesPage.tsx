import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import {
  ApiError, get, post, put, qs, type Channel, type ChannelPriceInfo, type ChannelPriceRule, type Paged, type PriceApproval, type PriceHistory,
  type PricePreview, type SkuPrice
} from '../api';
import { useAuth } from '../auth';
import { Card, ChannelBadge, ChannelLink, ErrorBox, Field, Notice, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime, formatMoney } from '../format';
import { resolveSku } from './MovementsPage';
import { ExcelBox } from './MappingPage';

const money = (v: number | null | undefined) => (v == null ? '—' : formatMoney(v));
const roundingLabels: Record<string, string> = { YOK: 'Yok (kuruş)', TAM: 'Tam lira', KURUS_99: ',99', KURUS_90: ',90' };
const previewLabels: Record<string, string> = {
  DEGISECEK: 'Değişecek', AYNI: 'Aynı', ONAY_GEREKIR: 'Onay gerekir', GONDERILEMEZ: 'Gönderilemez', REDDEDILDI: 'Reddedildi'
};
const reasonLabels: Record<string, string> = { ESIK: 'Eşik aşıldı', DENEME: 'Deneme modu' };
const approvalStateLabels: Record<string, string> = { BEKLIYOR: 'Bekliyor', ONAYLANDI: 'Onaylandı', REDDEDILDI: 'Reddedildi', GECERSIZ: 'Geçersiz' };
const historyLabels: Record<string, string> = {
  SKU_FIYAT: 'SKU fiyatı', ILAN_FIYATI: 'İlan fiyatı', KANAL_KURALI: 'Kanal kuralı', KANALA_GONDERILDI: 'Kanala gönderildi', ONAY: 'Onay kararı'
};

/**
 * Fiyat (ADR-028). SKU fiyatı + kanal kuralı (çarpan, ek, yuvarlama, taban) → kanala gidecek fiyat. Gönderim varsayılan KAPALI;
 * açmadan önce önizleme ("N ilanın fiyatı değişecek"). Son gönderilene göre eşiği aşan değişiklik Yönetici onayı bekler.
 * KDV'nin dahil olduğu resmi dokümanda doğrulanmayan kanalda gönderim açılamaz.
 */
export function PricesPage() {
  const [notice, setNotice] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const changed = (message: string) => { setNotice(message); setRefreshKey(k => k + 1); };

  return (
    <>
      <h1>Fiyat Kuralları</h1>
      <p className="muted" data-testid="price-page-hint">
        Ürün ürün fiyat ve stok değiştirmek için <Link to="/urun-duzenle">Fiyat &amp; Stok Düzenle</Link> sayfasını kullanın.
        Bu sayfa kanal kuralları, onay bekleyen fiyatlar, SKU ana fiyatı ve fiyat geçmişi içindir.
      </p>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <PriceApprovals refreshKey={refreshKey} onChanged={changed} />
      <ChannelPrices channels={channels.data ?? []} refreshKey={refreshKey} onChanged={changed} />
      <SkuPriceForm onChanged={changed} />
      <PriceSettings onChanged={changed} />
      <PriceHistoryCard refreshKey={refreshKey} />
    </>
  );
}

// ------------------------------------------------------------------ Onay bekleyenler

function PriceApprovals({ refreshKey, onChanged }: { refreshKey: number; onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const [state, setState] = useState('BEKLIYOR');
  const [error, setError] = useState<unknown>(null);
  const list = useLoad(() => get<Paged<PriceApproval>>(`/api/prices/approvals${qs({ state: state || undefined, pageSize: 200 })}`), [state, refreshKey]);

  async function decide(a: PriceApproval, approve: boolean) {
    const text = `${a.skuCode} (${channelNames[a.channelCode] ?? a.channelCode}): ${money(a.oldSale)} → ${money(a.newSale)}`;
    if (!window.confirm(approve ? `${text}\nBu fiyat kanala gönderilecek. Onaylıyor musunuz?` : `${text}\nBu fiyat reddedilecek ve gönderilmeyecek.`)) return;
    setError(null);
    try {
      await post(`/api/prices/approvals/${a.id}/${approve ? 'approve' : 'reject'}`);
      onChanged(approve ? `${a.skuCode}: fiyat onaylandı, senkron hattından gönderilecek.` : `${a.skuCode}: fiyat reddedildi.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Onay bekleyen fiyatlar">
      <p className="muted small">
        Son gönderilen fiyata göre değişim eşiği aşan (veya kanal "Deneme" modundaysa her) fiyat otomatik gitmez. Onaylanan fiyat bir sonraki senkronda gider; reddedilen gitmez.
      </p>
      <div className="filters">
        <select aria-label="Onay durumu" value={state} onChange={e => setState(e.target.value)}>
          <option value="">Tümü</option>
          {Object.entries(approvalStateLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
        <button onClick={list.reload}>Yenile</button>
      </div>
      <ErrorBox error={error ?? list.error} />
      <table className="table" data-testid="price-approvals">
        <thead><tr><th>Kanal</th><th>SKU</th><th>Eski</th><th>Yeni</th><th>Değişim</th><th>Neden</th><th>Durum</th><th></th></tr></thead>
        <tbody>
          {(list.data?.items ?? []).map(a => (
            <tr key={a.id} data-testid="price-approval-row">
              <td><ChannelBadge code={a.channelCode} /></td>
              <td>{a.skuCode}<div className="muted small">{a.productName}</div></td>
              <td>{money(a.oldSale)}</td>
              <td><strong>{money(a.newSale)}</strong>{a.newList !== a.newSale && <div className="muted small">liste {money(a.newList)}</div>}</td>
              <td>{a.changePercent == null ? 'ilk' : `%${a.changePercent}`}</td>
              <td>{reasonLabels[a.reason] ?? a.reason}</td>
              <td>{approvalStateLabels[a.state] ?? a.state}{a.decidedBy && <div className="muted small">{a.decidedBy} · {formatDateTime(a.decidedUtc)}</div>}</td>
              <td className="actions">
                {can('admin') && a.state === 'BEKLIYOR' && (
                  <>
                    <button className="primary" onClick={() => decide(a, true)}>Onayla</button>
                    <button className="danger" onClick={() => decide(a, false)}>Reddet</button>
                  </>
                )}
              </td>
            </tr>
          ))}
          {list.data?.items.length === 0 && <tr><td colSpan={8} className="empty">Kayıt yok.</td></tr>}
        </tbody>
      </table>
    </Card>
  );
}

// ------------------------------------------------------------------ Kanal kuralı + önizleme + açma

function ChannelPrices({ channels, refreshKey, onChanged }: { channels: Channel[]; refreshKey: number; onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const [channelId, setChannelId] = useState('');
  const [onlyChanges, setOnlyChanges] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const selected = channelId || String(channels[0]?.id ?? '');
  const infos = useLoad(() => get<ChannelPriceInfo[]>('/api/prices/channels'), [refreshKey]);
  const info = infos.data?.find(i => String(i.channelId) === selected);
  const preview = useLoad(
    () => (selected ? get<PricePreview>(`/api/prices/channels/${selected}/preview${qs({ onlyChanges })}`) : Promise.resolve(null)),
    [selected, onlyChanges, refreshKey]);
  const p = preview.data;

  async function enable(state: 'ACIK' | 'DENEME') {
    if (!p) return;
    const text = `${channelNames[p.channelCode] ?? p.channelCode}: ${p.changing} ilanın fiyatı değişecek (${p.firstSend} ilk gönderim), `
      + `${p.needsApproval} onay bekleyecek, ${p.blocked} gönderilemez.\n`
      + (state === 'DENEME' ? 'Deneme: her fiyat tek tek onayınızla gidecek.' : 'Açık: eşik içindeki değişiklikler otomatik gidecek.')
      + '\nFiyat gönderimi açılsın mı?';
    if (!window.confirm(text)) return;
    setError(null);
    try {
      await put(`/api/channels/${selected}/capabilities/FIYAT`, { state });
      onChanged(`Fiyat gönderimi ${state === 'ACIK' ? 'açıldı' : 'deneme modunda açıldı'}.`);
    } catch (err) {
      setError(err);
    }
  }

  async function disable() {
    setError(null);
    try {
      await put(`/api/channels/${selected}/capabilities/FIYAT`, { state: 'KAPALI' });
      onChanged('Fiyat gönderimi kapatıldı.');
    } catch (err) {
      setError(err);
    }
  }

  async function setOverride(listingId: number, skuCode: string, current: number | null) {
    const value = window.prompt(`${skuCode}: bu ilan için elle satış fiyatı (boş = kanal kuralı)`, current == null ? '' : String(current));
    if (value === null) return;
    const price = value.trim() === '' ? null : Number(value.replace(',', '.'));
    if (price !== null && (!Number.isFinite(price) || price <= 0)) { setError(new ApiError(400, 'Geçerli bir fiyat girin.')); return; }
    setError(null);
    try {
      await put(`/api/prices/listings/${listingId}/override`, { price });
      onChanged(`${skuCode}: ilan fiyatı ${price == null ? 'kaldırıldı' : 'kaydedildi'}.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kanal fiyatları ve önizleme">
      <div className="filters">
        <select aria-label="Fiyat kanalı" value={selected} onChange={e => setChannelId(e.target.value)}>
          {channels.map(c => <option key={c.id} value={c.id}>{channelNames[c.code] ?? c.name}{c.active ? '' : ' (pasif)'}</option>)}
        </select>
        <label className="check"><input type="checkbox" checked={onlyChanges} onChange={e => setOnlyChanges(e.target.checked)} /> Yalnızca değişecekler</label>
      </div>
      <ErrorBox error={error ?? infos.error ?? preview.error} />
      {info && (
        <div data-testid="price-channel-info">
          <p>
            Fiyat gönderimi: <strong data-testid="price-capability">{info.capabilityState === 'KAPALI' ? 'Kapalı' : info.capabilityState === 'DENEME' ? 'Deneme' : 'Açık'}</strong>
            {' · '}KDV: {info.vatIncluded === true ? 'dahil (dokümanda doğrulandı)' : info.vatIncluded === false ? 'hariç' : 'dokümanda doğrulanamadı'}
            {' · '}{info.sendsListPrice ? 'liste + satış fiyatı gider' : 'yalnızca satış fiyatı gider'}
          </p>
          <p className="muted small">{info.note}{info.source && <> Kaynak: {info.source}</>}</p>
          {!info.canEnable && <div className="alert alert-warn" data-testid="price-block-reason">{info.blockReason}</div>}
          {can('admin') && <RuleForm channelId={selected} rule={info.rule} onChanged={onChanged} />}
        </div>
      )}
      {p && (
        <>
          <p data-testid="price-preview-summary">
            <strong>{p.changing} ilanın fiyatı değişecek</strong> ({p.firstSend} ilk gönderim) · {p.needsApproval} onay gerekir · {p.unchanged} aynı ·{' '}
            {p.blocked} gönderilemez · eşik %{p.thresholdPercent}
          </p>
          {can('admin') && (
            <div className="actions">
              {p.capabilityState === 'KAPALI'
                ? (
                  <>
                    <button className="primary" disabled={!p.canEnable} onClick={() => enable('ACIK')}>Fiyat gönderimini aç</button>
                    <button disabled={!p.canEnable} onClick={() => enable('DENEME')}>Deneme olarak aç</button>
                  </>
                )
                : <button className="danger" onClick={disable}>Fiyat gönderimini kapat</button>}
            </div>
          )}
          <table className="table" data-testid="price-preview">
            <thead><tr><th>SKU</th><th>Kanal kodu</th><th>Kanalda görünen</th><th>Son gönderilen</th><th>Yeni</th><th>Değişim</th><th>Durum</th><th></th></tr></thead>
            <tbody>
              {p.rows.map(r => (
                <tr key={r.listingId} data-testid="price-preview-row" className={r.status === 'GONDERILEMEZ' ? 'row-danger' : undefined}>
                  <td><div className="with-thumb"><Thumb url={r.imageUrl} alt={r.productName} size={36} /><div>{r.skuCode}<div className="muted small">{r.productName}</div></div></div></td>
                  <td>{r.channelProductCode ?? r.channelBarcode ?? '—'} <ChannelLink code={p.channelCode} url={r.productUrl} /></td>
                  <td data-testid="channel-price">
                    {money(r.channelSalePrice)}
                    {r.channelListPrice != null && r.channelListPrice !== r.channelSalePrice && <div className="muted small">liste {money(r.channelListPrice)}</div>}
                  </td>
                  <td>{money(r.lastSentSale)}</td>
                  <td>
                    <strong>{money(r.newSale)}</strong>
                    {r.newList != null && r.newList !== r.newSale && <div className="muted small">liste {money(r.newList)}</div>}
                    {r.floorApplied && <span className="badge warn">taban</span>}
                    {r.fromOverride && <span className="badge">elle</span>}
                  </td>
                  <td>{r.changePercent == null ? (r.lastSentSale == null ? 'ilk' : '—') : `%${r.changePercent}`}</td>
                  <td>{previewLabels[r.status] ?? r.status}{r.problem && <div className="muted small wrap">{r.problem}</div>}</td>
                  <td>{can('admin') && <button className="link" onClick={() => setOverride(r.listingId, r.skuCode, r.fromOverride ? r.newSale : null)}>Elle fiyat</button>}</td>
                </tr>
              ))}
              {p.rows.length === 0 && <tr><td colSpan={8} className="empty">{onlyChanges ? 'Değişecek fiyat yok.' : 'Aktif ilan yok.'}</td></tr>}
            </tbody>
          </table>
        </>
      )}
    </Card>
  );
}

function RuleForm({ channelId, rule, onChanged }: { channelId: string; rule: ChannelPriceRule; onChanged: (m: string) => void }) {
  const [form, setForm] = useState({
    multiplier: String(rule.multiplier), fixedAdd: String(rule.fixedAdd), rounding: rule.rounding, floor: rule.floor == null ? '' : String(rule.floor)
  });
  const [key, setKey] = useState(channelId);
  const [error, setError] = useState<unknown>(null);
  if (key !== channelId) {
    setKey(channelId);
    setForm({ multiplier: String(rule.multiplier), fixedAdd: String(rule.fixedAdd), rounding: rule.rounding, floor: rule.floor == null ? '' : String(rule.floor) });
  }
  const num = (v: string) => Number(v.replace(',', '.'));

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await put(`/api/prices/channels/${channelId}/rule`, {
        multiplier: num(form.multiplier), fixedAdd: num(form.fixedAdd || '0'), rounding: form.rounding, floor: form.floor.trim() === '' ? null : num(form.floor)
      });
      onChanged('Kanal fiyat kuralı kaydedildi; önizleme güncellendi.');
    } catch (err) {
      setError(err);
    }
  }

  return (
    <form className="form-row" onSubmit={save} data-testid="price-rule-form">
      <Field label="Çarpan"><input aria-label="Çarpan" value={form.multiplier} onChange={e => setForm({ ...form, multiplier: e.target.value })} size={6} /></Field>
      <Field label="Sabit ek (TL)"><input aria-label="Sabit ek" value={form.fixedAdd} onChange={e => setForm({ ...form, fixedAdd: e.target.value })} size={8} /></Field>
      <Field label="Yuvarlama">
        <select aria-label="Yuvarlama" value={form.rounding} onChange={e => setForm({ ...form, rounding: e.target.value })}>
          {Object.entries(roundingLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      </Field>
      <Field label="Taban (TL)"><input aria-label="Taban" value={form.floor} onChange={e => setForm({ ...form, floor: e.target.value })} size={8} placeholder="yok" /></Field>
      <button type="submit">Kuralı kaydet</button>
      <ErrorBox error={error} />
    </form>
  );
}

// ------------------------------------------------------------------ SKU fiyatı + Excel

function SkuPriceForm({ onChanged }: { onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const [code, setCode] = useState('');
  const [sku, setSku] = useState<SkuPrice | null>(null);
  const [form, setForm] = useState({ sale: '', list: '' });
  const [error, setError] = useState<unknown>(null);
  const num = (v: string) => (v.trim() === '' ? null : Number(v.replace(',', '.')));

  async function find(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSku(null);
    try {
      const found = await resolveSku(code);
      if (!found) { setError(new ApiError(404, `SKU bulunamadı: ${code}`)); return; }
      const price = await get<SkuPrice>(`/api/prices/skus/${found.id}`);
      setSku(price);
      setForm({ sale: price.salePrice == null ? '' : String(price.salePrice), list: price.listPrice == null ? '' : String(price.listPrice) });
    } catch (err) {
      setError(err);
    }
  }

  async function save() {
    if (!sku) return;
    setError(null);
    try {
      const saved = await put<SkuPrice>(`/api/prices/skus/${sku.skuId}`, { salePrice: num(form.sale), listPrice: num(form.list) });
      setSku(saved);
      onChanged(`${saved.skuCode}: fiyat kaydedildi (${money(saved.salePrice)}).`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="SKU fiyatı (KDV dahil)">
      <form className="filters" onSubmit={find}>
        <input placeholder="SKU kodu" aria-label="Fiyat SKU kodu" value={code} onChange={e => setCode(e.target.value)} />
        <button type="submit">Getir</button>
      </form>
      <ErrorBox error={error} />
      {sku && (
        <div className="form-row" data-testid="sku-price-form">
          <span>{sku.skuCode} · {sku.productName}</span>
          <Field label="Satış fiyatı"><input aria-label="Satış fiyatı" value={form.sale} disabled={!can('admin')} onChange={e => setForm({ ...form, sale: e.target.value })} size={10} /></Field>
          <Field label="Liste fiyatı"><input aria-label="Liste fiyatı" value={form.list} disabled={!can('admin')} onChange={e => setForm({ ...form, list: e.target.value })} size={10} placeholder="= satış" /></Field>
          {can('admin') && <button className="primary" onClick={save}>Fiyatı kaydet</button>}
        </div>
      )}
      <div className="excel-grid">
        <ExcelBox kind="prices" label="Fiyat (Excel)" canImport={can('admin')} onImported={() => onChanged('Fiyatlar içe aktarıldı.')} />
      </div>
    </Card>
  );
}

// ------------------------------------------------------------------ Eşik ve geçmiş

function PriceSettings({ onChanged }: { onChanged: (m: string) => void }) {
  const { can } = useAuth();
  const settings = useLoad(() => get<{ changeThresholdPercent: number }>('/api/prices/settings'), []);
  const [value, setValue] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const current = value ?? (settings.data ? String(settings.data.changeThresholdPercent) : '');

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await put('/api/prices/settings', { changeThresholdPercent: Number(current.replace(',', '.')) });
      setValue(null);
      settings.reload();
      onChanged('Fiyat değişim eşiği kaydedildi.');
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Fiyat değişim eşiği">
      <form className="form-row" onSubmit={save}>
        <Field label="Eşik (%)">
          <input aria-label="Fiyat eşiği" value={current} disabled={!can('admin')} onChange={e => setValue(e.target.value)} size={6} />
        </Field>
        {can('admin') && <button type="submit">Kaydet</button>}
        <span className="muted small">Son gönderilen fiyata göre bu yüzdeden fazla değişim Yönetici onayı bekler (varsayılan %30).</span>
      </form>
      <ErrorBox error={error ?? settings.error} />
    </Card>
  );
}

function PriceHistoryCard({ refreshKey }: { refreshKey: number }) {
  const history = useLoad(() => get<Paged<PriceHistory>>(`/api/prices/history${qs({ pageSize: 50 })}`), [refreshKey]);
  return (
    <Card title="Fiyat geçmişi (son 50)">
      <ErrorBox error={history.error} />
      <table className="table" data-testid="price-history">
        <thead><tr><th>Tarih</th><th>Tür</th><th>SKU / kanal</th><th>Eski</th><th>Yeni</th><th>Kaynak</th><th>Kullanıcı</th></tr></thead>
        <tbody>
          {(history.data?.items ?? []).map(h => (
            <tr key={h.id}>
              <td>{formatDateTime(h.atUtc)}</td>
              <td>{historyLabels[h.type] ?? h.type}</td>
              <td>{h.skuCode ?? '—'}{h.channelCode && <> · <ChannelBadge code={h.channelCode} /></>}</td>
              <td>{money(h.oldSale)}</td>
              <td>{h.type === 'KANAL_KURALI' ? <span className="muted small wrap">{h.detail}</span> : money(h.newSale)}</td>
              <td>{h.source}</td>
              <td>{h.user ?? '—'}</td>
            </tr>
          ))}
          {history.data?.items.length === 0 && <tr><td colSpan={7} className="empty">Kayıt yok.</td></tr>}
        </tbody>
      </table>
    </Card>
  );
}
