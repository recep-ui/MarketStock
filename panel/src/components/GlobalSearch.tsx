import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { get, type SearchResult } from '../api';
import { ChannelBadge, StatusBadge } from './ui';
import { formatDateTime, formatMoney } from '../format';

type Option = { key: string; href: string; render: () => JSX.Element };

const matchedLabels: Record<string, string> = { SKU: 'SKU kodu', BARKOD: 'Barkod', URUN_ADI: 'Ürün adı', KANAL_KODU: 'Kanal kodu' };

/**
 * Üst çubukta genel arama (ADR-034): SKU kodu/barkod/ürün adı/kanal kodu ve sipariş no.
 * Klavye: "/" veya Ctrl+K odaklar, ↑/↓ seçer, Enter açar, Esc kapatır. En az 2 karakter.
 */
export function GlobalSearch() {
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const [text, setText] = useState('');
  const [result, setResult] = useState<SearchResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);

  // "/" veya Ctrl+K ile odak (bir form alanına yazarken değil)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      const typing = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable);
      if ((e.key === 'k' && (e.ctrlKey || e.metaKey)) || (e.key === '/' && !typing)) {
        e.preventDefault();
        inputRef.current?.focus();
        inputRef.current?.select();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Yazma bitince (250 ms) ara; eski yanıt yenisinin üstüne yazmasın
  useEffect(() => {
    const term = text.trim();
    if (term.length < 2) {
      setResult(null);
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    const timer = setTimeout(() => {
      get<SearchResult>(`/api/search?q=${encodeURIComponent(term)}`)
        .then(r => { if (!cancelled) { setResult(r); setActive(0); } })
        .catch(() => { if (!cancelled) setResult({ term, skus: [], orders: [] }); })
        .finally(() => { if (!cancelled) setLoading(false); });
    }, 250);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [text]);

  const options: Option[] = [
    ...(result?.skus ?? []).map<Option>(s => ({
      key: `sku-${s.skuId}`,
      href: `/stok/${s.skuId}`,
      render: () => (
        <>
          <span className="search-main"><strong>{s.skuCode}</strong> {s.productName}{s.variant && <span className="muted"> · {s.variant}</span>}</span>
          <span className="search-meta">{matchedLabels[s.matchedBy] ?? s.matchedBy} · satılabilir {s.available}</span>
        </>
      )
    })),
    ...(result?.orders ?? []).map<Option>(o => ({
      key: `order-${o.orderId}`,
      href: `/siparisler?order=${o.orderId}`,
      render: () => (
        <>
          <span className="search-main"><ChannelBadge code={o.channelCode} /> <strong>{o.channelOrderNo}</strong> <StatusBadge status={o.status} /></span>
          <span className="search-meta">{formatDateTime(o.orderDateUtc)} · {formatMoney(o.total)}</span>
        </>
      )
    }))
  ];

  const go = (option: Option | undefined) => {
    if (!option) return;
    navigate(option.href);
    setOpen(false);
    setText('');
    inputRef.current?.blur();
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setActive(a => Math.min(a + 1, options.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); go(options[active]); }
    else if (e.key === 'Escape') { setOpen(false); inputRef.current?.blur(); }
  };

  const showPanel = open && text.trim().length >= 2;
  const skuCount = result?.skus.length ?? 0;

  return (
    <div className="global-search">
      <input
        ref={inputRef}
        type="search"
        role="combobox"
        aria-label="Hızlı bul"
        aria-expanded={showPanel}
        aria-controls="global-search-results"
        aria-activedescendant={showPanel && options[active] ? `gs-${options[active].key}` : undefined}
        placeholder="SKU, barkod, ürün veya sipariş no ara…  ( / )"
        value={text}
        onChange={e => { setText(e.target.value); setOpen(true); }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        onKeyDown={onKeyDown}
      />
      {showPanel && (
        <div className="search-panel" id="global-search-results" role="listbox" aria-label="Bulunanlar" data-testid="search-results">
          {loading && !result && <div className="search-empty">Aranıyor…</div>}
          {result && options.length === 0 && <div className="search-empty">“{text.trim()}” için sonuç yok.</div>}
          {options.map((o, i) => (
            <div key={o.key}>
              {i === 0 && skuCount > 0 && <div className="search-group">Ürünler</div>}
              {i === skuCount && <div className="search-group">Siparişler</div>}
              <div
                id={`gs-${o.key}`}
                role="option"
                aria-selected={i === active}
                className={`search-option${i === active ? ' active' : ''}`}
                onMouseEnter={() => setActive(i)}
                onMouseDown={e => { e.preventDefault(); go(o); }}
              >
                {o.render()}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
