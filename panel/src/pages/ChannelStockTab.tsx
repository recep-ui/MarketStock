import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, get, qs, type Channel, type ChannelPriceInfo, type ChannelStockList, type ChannelStockListRow, type Paged, type SaleState, type UnmatchedItem } from '../api';
import { useAuth } from '../auth';
import { Pager } from '../components/DataTable';
import { ListingEditor, draftOf, draftProblem, isDirty, saveDraft, type ListingDraft } from '../components/ListingEditor';
import { Card, ChannelLink, ErrorBox, Stat, Thumb, useLoad } from '../components/ui';
import { channelNames, formatDateTime, formatRelative } from '../format';

const basisLabels: Record<ChannelStockListRow['matchBasis'], { text: string; title: string; tone?: 'ok' }> = {
  BARKOD: { text: '✓ Barkod', title: 'Kanalın barkodu SKU barkoduyla aynı', tone: 'ok' },
  STOK_KODU: { text: '✓ Stok kodu', title: 'Kanalın stok kodu SKU koduyla aynı (barkod farklı ya da bilinmiyor)', tone: 'ok' },
  DIGER: { text: 'Kodla doğrulanamadı', title: 'Barkod ve stok kodu eşleşmeyi doğrulamıyor (elle bağlama, model eşleşmesi veya kanal kodu bilinmiyor)' }
};

const warningLabels: Record<string, string> = {
  BARKOD_FARKLI: 'Barkod farklı', STOK_KODU_FARKLI: 'Stok kodu farklı', RENK_FARKLI: 'Renk farklı',
  BEDEN_FARKLI: 'Beden farklı', AD_BENZEMIYOR: 'Ad benzemiyor'
};

const variantText = (color: string | null, size: string | null) => [color, size].filter(Boolean).join(' / ');

const capabilityText: Record<string, string> = {
  ACIK: 'açık: kaydedilen fiyat kanala gönderilir',
  DENEME: 'deneme: her fiyat değişikliği önce Yönetici onayına düşer',
  KAPALI: 'kapalı: fiyat kaydedilir ama kanala gönderilmez'
};

/**
 * Stok Listesi → kanal sekmesi (ADR-035). Kanalın çekilen stok listesi: her satırda kanalın bildirdiği barkod/kod/ad/stok ve
 * bağlandığı SKU yan yana. Kanal bilgisi SKU ile uyuşmuyorsa "Kontrol et" (yanlış eşleştirme olabilir). Eşleştirmeyi değiştirmez.
 */
/** ADR-043: kanal durumu sınıfları (filtre ve rozet). */
const saleStateLabels: Record<SaleState, { text: string; tone?: string; title: string }> = {
  SATISTA: { text: 'Satışta', tone: 'ok', title: 'Kanalda satışta.' },
  STOK_YOK: { text: 'Stoğu tükendi', tone: 'warn', title: 'İlan açık ama kanalda stok yok; stok gönderilince satışa döner.' },
  SATIS_DISI: { text: 'Satış dışı', tone: 'danger', title: 'Kanalda satışta değil (satışa kapalı, arşiv, kara liste, kilitli, seçilmedi veya revize bekliyor).' },
  BILINMIYOR: { text: 'Bilinmiyor', title: 'Kanal durum bildirmedi veya durum metni tanınmıyor.' },
};
const saleStates: SaleState[] = ['SATISTA', 'STOK_YOK', 'SATIS_DISI', 'BILINMIYOR'];

export function ChannelStockTab({ channel }: { channel: Channel }) {
  const [form, setForm] = useState({ search: '', onlyDeviations: false, onlyReview: false, saleState: '' as SaleState | '' });
  const [applied, setApplied] = useState(form);
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState<number | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const [drafts, setDrafts] = useState<Record<number, ListingDraft>>({});
  const [saving, setSaving] = useState(false);
  const [saveResult, setSaveResult] = useState<string | null>(null);
  const [showUnmatched, setShowUnmatched] = useState(false);
  const { can } = useAuth();
  const editable = can('catalogWrite');
  const name = channelNames[channel.code] ?? channel.name;
  const priceInfo = useLoad(() => get<ChannelPriceInfo[]>('/api/prices/channels'), []);
  const priceState = priceInfo.data?.find(c => c.channelId === channel.id)?.capabilityState;

  const data = useLoad(
    () => get<ChannelStockList>(`/api/stock-overview/channels/${channel.id}${qs({
      search: applied.search, onlyDeviations: applied.onlyDeviations, onlyReview: applied.onlyReview,
      saleState: applied.saleState || undefined, page, pageSize: 50
    })}`),
    [channel.id, applied, page]
  );
  const d = data.data;
  // "Doğru eşleşme" onayı (ADR-036): yalnızca uyarıyı kapatır; eşleşme bilgisi değişirse uyarı geri gelir
  const setApproval = async (row: ChannelStockListRow, approve: boolean) => {
    const question = approve
      ? `${row.skuCode} ↔ ${row.channel.barcode ?? row.channel.productCode ?? 'kanal ilanı'} eşleşmesi doğru mu?\n\n` +
        'Uyarı "doğru eşleşme" olarak kapanır. Eşleştirme değişmez; barkod, ad, renk/beden veya bağlı SKU sonradan değişirse uyarı yeniden gösterilir.'
      : `${row.skuCode} için "doğru eşleşme" onayı geri alınsın mı? Uyarı yeniden "Kontrol et" listesine girer.`;
    if (!window.confirm(question)) return;
    setBusy(row.listingId);
    setActionError(null);
    try {
      await api<ChannelStockListRow>(approve ? 'POST' : 'DELETE', `/api/listings/${row.listingId}/match-approval`);
      data.reload();
    } catch (e) {
      setActionError(e);
    } finally {
      setBusy(null);
    }
  };

  const rows = d?.rows.items ?? [];
  const dirtyRows = rows.filter(r => isDirty(r, drafts[r.listingId]));
  const invalid = dirtyRows.some(r => draftProblem(drafts[r.listingId]) !== null);

  // Tek "Kaydet": değişen her ilan için stok kuralı ve/veya ilan fiyatı yazılır; kanala gönderim mevcut güvenli hattan gider.
  const saveAll = async () => {
    setSaving(true);
    setActionError(null);
    setSaveResult(null);
    const failed: string[] = [];
    for (const r of dirtyRows) {
      try {
        await saveDraft(r, drafts[r.listingId]);
      } catch (e) {
        failed.push(r.skuCode);
        setActionError(e);
      }
    }
    setDrafts({});
    setSaving(false);
    setSaveResult(failed.length === 0
      ? `${dirtyRows.length} ilan kaydedildi. Kanala gönderim senkron hattından gider (Senkron Merkezi'nden izlenir).`
      : `${dirtyRows.length - failed.length} ilan kaydedildi; kaydedilemeyen: ${failed.join(', ')}.`);
    data.reload();
  };

  const quick = (patch: Partial<typeof form>) => {
    const next = { ...form, ...patch };
    setForm(next);
    setApplied(next);
    setPage(1);
  };

  return (
    <>
      {d && (
        <div className="stats" data-testid="channel-stock-summary">
          <Stat label={`${name} ilanı`} value={d.totalListings} />
          <Stat
            label="Kontrol et (olası yanlış eşleşme)"
            value={<button className="link stat-link" onClick={() => quick({ onlyReview: true, onlyDeviations: false })}>{d.reviewCount}</button>}
            tone={d.reviewCount > 0 ? 'warn' : undefined}
            testId="channel-review-count"
          />
          <Stat
            label="Stok sapması"
            value={<button className="link stat-link" onClick={() => quick({ onlyDeviations: true, onlyReview: false })}>{d.deviationCount}</button>}
            tone={d.deviationCount > 0 ? 'danger' : undefined}
          />
          <Stat label="Doğru eşleşme olarak onaylı" value={d.approvedCount} testId="channel-approved-count" />
          <Stat label="Kanal stoğu hiç çekilmemiş" value={d.notFetchedCount} />
          <Stat label="Son çekimde kanalda bulunamadı" value={d.missingCount ?? 0} tone={(d.missingCount ?? 0) > 0 ? 'warn' : undefined} testId="channel-missing-count" />
          <Stat label="Kanalda var, eşleşmemiş" value={<Link to="/eslestirme">{d.unmatchedCount}</Link>} tone={d.unmatchedCount > 0 ? 'warn' : undefined} />
          <Stat
            label="Son stok çekimi"
            value={<span title={formatDateTime(d.lastFetchedUtc)}>{d.lastFetchedUtc ? formatRelative(d.lastFetchedUtc) : '—'}</span>}
          />
        </div>
      )}

      <Card>
        <form className="filters" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(form); }}>
          <input
            placeholder="Kanal barkodu, kanal kodu, kanaldaki ad veya SKU"
            aria-label="Kanal ilanlarında bul"
            value={form.search}
            onChange={e => setForm({ ...form, search: e.target.value })}
          />
          <label className="check">
            <input type="checkbox" checked={form.onlyReview} onChange={e => quick({ onlyReview: e.target.checked })} /> Yalnızca kontrol gerekenler
          </label>
          <label className="check">
            <input type="checkbox" checked={form.onlyDeviations} onChange={e => quick({ onlyDeviations: e.target.checked })} /> Yalnızca stok sapmaları
          </label>
          <select aria-label="Kanaldaki durum" data-testid="sale-state-filter" value={form.saleState}
            onChange={e => quick({ saleState: e.target.value as SaleState | '' })}>
            <option value="">Kanaldaki durum: tümü</option>
            {saleStates.map(s => (
              <option key={s} value={s}>{saleStateLabels[s].text}{d?.saleStateCounts ? ` (${d.saleStateCounts[s] ?? 0})` : ''}</option>
            ))}
          </select>
          <button type="submit">Filtrele</button>
          {d && d.unmatchedCount > 0 && (
            <label className="check">
              <input type="checkbox" checked={showUnmatched} onChange={e => setShowUnmatched(e.target.checked)} /> Eşleşmemiş kanal ürünlerini de göster
            </label>
          )}
        </form>
        <p className="muted small">
          Sol: {name}'in bildirdiği (son katalog çekimi / mutabakat). Sağ: bağlı olduğu SKU ve deftere göre gitmesi gereken stok.
          <strong> Kontrol et</strong>: kanalın bilgisi SKU ile uyuşmuyor, yanlış eşleşme olabilir; düzeltmek için <Link to="/eslestirme">Eşleştirme</Link>.
          Çoklanan ilanlarda barkod farklı olabilir. Doğru olduğunu bildiğiniz eşleşmeyi "Doğru eşleşme, onayla" ile kapatabilirsiniz
          (Yönetici); bilgiler sonradan değişirse uyarı geri gelir. Bu ekran eşleştirmeyi değiştirmez.
          <strong> Stok ve fiyat</strong>: ilana özel stok kuralı (üst limit / sabit; defteri asla aşmaz) ve satış fiyatı buradan
          tek seferde kaydedilir. Görseller {name}'in kendi API'sinden gelir; diğer kanallardaki görsellerle karşılaştırmak için SKU'yu açın.
        </p>
        {priceState && (
          <p className="small" data-testid="price-capability">
            {name} fiyat gönderimi <strong>{capabilityText[priceState] ?? priceState}</strong>. <Link to="/ayarlar">Ayarlar →</Link>
          </p>
        )}
        {saveResult && <p className="small" role="status" data-testid="save-result">{saveResult}</p>}
        <ErrorBox error={data.error ?? actionError} onRetry={data.reload} />
        {data.loading && !d && <div className="loading">Yükleniyor…</div>}
        {d && (
          <div className="table-wrap">
            <table className="table channel-stock-table" data-testid="channel-stock-table" aria-busy={data.loading}>
              <thead>
                <tr>
                  <th>{name}'de görünen ürün</th>
                  <th>Kanaldaki durum</th>
                  <th className="num">Kanal stoğu</th>
                  <th aria-label="bağlı">→</th>
                  <th>Bağlı SKU (defter)</th>
                  <th className="num">Gitmesi gereken</th>
                  <th>Eşleşme kontrolü</th>
                  <th>Stok ve fiyat</th>
                </tr>
              </thead>
              <tbody>
                {d.rows.items.map(r => (
                  <tr
                    key={r.listingId}
                    data-testid="channel-stock-row"
                    className={r.needsReview ? 'row-review' : r.hasDeviation ? 'row-deviation' : undefined}
                  >
                    <td className="wrap-cell"><div className="with-thumb"><Thumb url={r.imageUrl} alt={r.channel.name ?? r.skuCode} /><div>
                      <div><strong>{r.channel.name ?? <span className="muted">(kanal adı bilinmiyor)</span>}</strong></div>
                      <div className="small muted">
                        {[r.channel.barcode && `Barkod ${r.channel.barcode}`, r.channel.productCode && `Kod ${r.channel.productCode}`,
                          r.channel.stockCode && `Stok kodu ${r.channel.stockCode}`].filter(Boolean).join(' · ')}
                      </div>
                      {(r.channel.variant || variantText(r.channel.color, r.channel.size)) && (
                        <div className="small">{r.channel.variant ?? variantText(r.channel.color, r.channel.size)}</div>
                      )}
                      <div className="small">
                        {r.status === 'PASIF' && <span className="badge">Pasif</span>}
                        <ChannelLink code={channel.code} url={r.productUrl} />
                      </div>
                    </div></div></td>
                    <td data-testid="channel-sale-state">
                      {(() => {
                        const s = saleStateLabels[r.saleState ?? 'BILINMIYOR'];
                        return <span className={`badge${s.tone ? ' ' + s.tone : ''}`} title={s.title}>{s.text}</span>;
                      })()}
                      {r.channelStatus && <div className="small muted">{r.channelStatus}</div>}
                    </td>
                    <td className="num">
                      {r.channelStock ?? (r.missingInChannel
                        ? <span className="muted small" title="Son mutabakatta bu ilan kanalda bulunamadı; sapma hesabına girmez.">kanalda yok</span>
                        : <span className="muted small">çekilmedi</span>)}
                      {r.fetchedAtUtc && <div className="small muted" title={formatDateTime(r.fetchedAtUtc)}>{formatRelative(r.fetchedAtUtc)}</div>}
                    </td>
                    <td className="muted" aria-hidden="true">→</td>
                    <td className="wrap-cell">
                      <div><Link to={`/stok/${r.skuId}`}>{r.skuCode}</Link></div>
                      <div className="small">{r.productName}</div>
                      <div className="small muted">
                        {[variantText(r.color, r.size), r.skuBarcode && `Barkod ${r.skuBarcode}`].filter(Boolean).join(' · ')}
                      </div>
                    </td>
                    <td className="num">
                      {r.expected}
                      {r.hasDeviation && <div className="small cell-deviation">sapma: kanalda {r.channelStock}</div>}
                    </td>
                    <td className="wrap-cell">
                      <span className={`badge${basisLabels[r.matchBasis].tone ? ' ' + basisLabels[r.matchBasis].tone : ''}`} title={basisLabels[r.matchBasis].title}>
                        {basisLabels[r.matchBasis].text}
                      </span>
                      {r.approval?.valid && (
                        <div className="small" data-testid="match-approved">
                          <span className="badge ok">✓ Doğru eşleşme</span>{' '}
                          <span className="muted" title={formatDateTime(r.approval.approvedAtUtc)}>
                            {r.approval.approvedBy ?? '—'} · {formatRelative(r.approval.approvedAtUtc)}
                          </span>
                        </div>
                      )}
                      {r.approval && !r.approval.valid && (
                        <div className="small warn-text" data-testid="match-approval-outdated">
                          Önceki onay geçersiz: eşleşme bilgisi onaydan sonra değişti ({r.approval.approvedBy ?? '—'}, {formatDateTime(r.approval.approvedAtUtc)}).
                        </div>
                      )}
                      {r.warnings.length > 0 && (
                        <ul className={`match-warnings${r.approval?.valid ? ' approved' : ''}`} aria-label={r.approval?.valid ? 'Onaylanmış uyarılar' : 'Kontrol et'}>
                          {r.warnings.map(w => (
                            <li key={w.code} title={w.message}>
                              <span aria-hidden="true">⚠ </span><strong>{warningLabels[w.code] ?? w.code}</strong>
                              <span className="small">: {w.message.includes(': ') ? w.message.slice(w.message.indexOf(': ') + 2) : w.message}</span>
                            </li>
                          ))}
                        </ul>
                      )}
                      {can('catalogWrite') && r.needsReview && (
                        <button className="small inline" disabled={busy === r.listingId} onClick={() => setApproval(r, true)}>
                          Doğru eşleşme, onayla
                        </button>
                      )}
                      {can('catalogWrite') && r.approval?.valid && (
                        <button className="link small" disabled={busy === r.listingId} onClick={() => setApproval(r, false)}>
                          Onayı geri al
                        </button>
                      )}
                    </td>
                    <td>
                      <ListingEditor
                        row={r}
                        draft={drafts[r.listingId] ?? draftOf(r)}
                        editable={editable && !saving}
                        onChange={next => setDrafts(prev => ({ ...prev, [r.listingId]: next }))}
                      />
                    </td>
                  </tr>
                ))}
                {d.rows.items.length === 0 && (
                  <tr><td colSpan={8} className="empty">
                    {d.totalListings === 0 ? `${name} için henüz eşleşmiş ilan yok.` : 'Filtreye uyan ilan yok.'}
                  </td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
        {dirtyRows.length > 0 && (
          <div className="save-bar" data-testid="save-bar">
            <strong>{dirtyRows.length} ilanda kaydedilmemiş değişiklik</strong>
            <button onClick={saveAll} disabled={saving || invalid} data-testid="save-all">{saving ? 'Kaydediliyor…' : 'Değişiklikleri kaydet'}</button>
            <button className="link" onClick={() => setDrafts({})} disabled={saving}>Vazgeç</button>
            {invalid && <span className="small cell-deviation">Hatalı alanları düzeltin.</span>}
          </div>
        )}
        {d && <Pager page={page} pageSize={d.rows.pageSize} total={d.rows.total} onPage={p => {
          if (dirtyRows.length > 0 && !window.confirm('Kaydedilmemiş değişiklikler var; sayfa değişirse kaybolur. Devam edilsin mi?')) return;
          setDrafts({});
          setPage(p);
        }} />}
      </Card>
      {showUnmatched && <UnmatchedList channel={channel} />}
    </>
  );
}

/**
 * Kanalda olup hiçbir SKU'ya bağlanmamış ürünler (salt okuma): kanalın tüm ürünlerini tek tek görmek için. Bağlama
 * Eşleştirme ekranında yapılır; stok/fiyat düzenlemesi ancak bir SKU'ya bağlandıktan sonra yapılabilir (defter esas).
 */
function UnmatchedList({ channel }: { channel: Channel }) {
  const [page, setPage] = useState(1);
  const items = useLoad(
    () => get<Paged<UnmatchedItem>>(`/api/channel-catalog/unmatched${qs({ channelId: channel.id, status: 'BEKLIYOR', page, pageSize: 50 })}`),
    [channel.id, page]
  );
  const name = channelNames[channel.code] ?? channel.name;
  return (
    <Card title={`${name}: eşleşmemiş ürünler`} actions={<Link to="/eslestirme">Eşleştirmede bağla →</Link>}>
      <ErrorBox error={items.error} onRetry={items.reload} />
      {items.data && (
        <div className="table-wrap">
          <table className="table channel-stock-table" data-testid="unmatched-table">
            <thead><tr><th>{name}'de görünen ürün</th><th>Kanal durumu</th><th>Neden eşleşmedi</th><th>Öneri</th></tr></thead>
            <tbody>
              {items.data.items.map(u => (
                <tr key={u.id} data-testid="unmatched-row">
                  <td className="wrap-cell"><div className="with-thumb"><Thumb url={u.imageUrl} alt={u.productName ?? u.channelBarcode ?? 'ürün'} /><div>
                    <div><strong>{u.productName ?? <span className="muted">(ad bilinmiyor)</span>}</strong></div>
                    <div className="small muted">
                      {[u.channelBarcode && `Barkod ${u.channelBarcode}`, u.channelProductCode && `Kod ${u.channelProductCode}`,
                        u.channelStockCode && `Stok kodu ${u.channelStockCode}`].filter(Boolean).join(' · ')}
                    </div>
                    {variantText(u.color, u.size) && <div className="small">{variantText(u.color, u.size)}</div>}
                  </div></div></td>
                  <td className="small">{u.channelStatus ?? '—'}</td>
                  <td className="wrap-cell small">{u.reasonDetail ?? u.reason}</td>
                  <td className="small">{u.suggestedSkuCode ? <>{u.suggestedSkuCode}<div className="muted">{u.suggestedProductName}</div></> : '—'}</td>
                </tr>
              ))}
              {items.data.items.length === 0 && <tr><td colSpan={4} className="empty">Bekleyen eşleşmemiş ürün yok.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      {items.data && <Pager page={page} pageSize={items.data.pageSize} total={items.data.total} onPage={setPage} />}
    </Card>
  );
}
