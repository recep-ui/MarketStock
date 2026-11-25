import { useState } from 'react';
import { get, type Channel } from '../api';
import { Notice, useLoad } from '../components/ui';
import { SkuChannelPrices } from './SkuChannelPrices';

/**
 * Fiyat & Stok Düzenle (ADR-050): eşleşen ürünlerin tüm kanallardaki fiyatı ve defter stoğu tek satırda, tek yerden.
 * Fiyat sayfasındaki "Ürün bazında fiyat ve stok" bölümüyle aynı bileşen; burada tek başına ve tam genişlikte.
 */
export function ProductEditPage() {
  const [notice, setNotice] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  return (
    <>
      <h1>Fiyat &amp; Stok Düzenle</h1>
      <Notice text={notice} onClose={() => setNotice(null)} />
      <SkuChannelPrices channels={channels.data ?? []} refreshKey={refreshKey}
        onChanged={m => { setNotice(m); setRefreshKey(k => k + 1); }} />
    </>
  );
}
