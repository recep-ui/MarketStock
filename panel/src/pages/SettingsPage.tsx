import { useEffect, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, del, get, post, put, type Channel, type ChannelStatus, type User, type Warehouse } from '../api';
import { roleLabels, useAuth, type Role } from '../auth';
import { ChannelStatusGrid } from '../components/ChannelStatus';
import { ExportButton } from '../components/ExportButton';
import { Card, ErrorBox, Field, Notice, useLoad } from '../components/ui';
import { channelNames, formatDateTime } from '../format';
import { resolveSku } from './MovementsPage';
import { ChannelReferences } from './ChannelReferences';
import { capabilityLabels, ChannelWriteSettings } from './ChannelWrite';

type Tab = 'kanallar' | 'stok' | 'kullanicilar';

/**
 * Ekran 8: Ayarlar (ADR-033). Sekmeler: Kanallar (durum kartları + seçili kanalın ayarları), Stok kuralları (eşikler,
 * güvenlik stoğu), Kullanıcılar. Sekme ve seçili kanal adres çubuğunda tutulur (?sekme=…&kanal=…).
 */
export function SettingsPage() {
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const [notice, setNotice] = useState<string | null>(null);
  const tab = (params.get('sekme') as Tab | null) ?? 'kanallar';
  const tabs: [Tab, string][] = [['kanallar', 'Kanallar'], ['stok', 'Stok kuralları']];
  if (can('admin')) tabs.push(['kullanicilar', 'Kullanıcılar']);

  function setTab(t: Tab) {
    const next = new URLSearchParams(params);
    next.set('sekme', t);
    setParams(next, { replace: true });
  }

  return (
    <>
      <h1>Ayarlar</h1>
      <div className="tabs" role="tablist" aria-label="Ayar bölümleri">
        {tabs.map(([t, label]) => (
          <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'active' : undefined} onClick={() => setTab(t)}>{label}</button>
        ))}
      </div>
      <Notice text={notice} onClose={() => setNotice(null)} />
      {tab === 'kanallar' && <ChannelsTab onNotice={setNotice} />}
      {tab === 'stok' && (
        <div className="grid-2">
          <ThresholdSettings editable={can('admin')} onSaved={setNotice} />
          {can('stockWrite') && <SafetyStockForm onSaved={setNotice} />}
        </div>
      )}
      {tab === 'kullanicilar' && can('admin') && <UserSettings onSaved={setNotice} />}
    </>
  );
}

const stateShort: Record<string, string> = { KAPALI: 'Kapalı', DENEME: 'Onaylı', ACIK: 'Açık' };

/** Kanallar sekmesi: tüm kanalların durum kartları, seçili kanalın ayrıntılı ayarları ve yetenek özeti. */
function ChannelsTab({ onNotice }: { onNotice: (m: string) => void }) {
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const statuses = useLoad(() => get<ChannelStatus[]>('/api/channels/status'), []);
  const channels = useLoad(() => get<Channel[]>('/api/channels'), []);
  const list = statuses.data ?? [];
  const selectedId = Number(params.get('kanal')) || null;
  const selected = list.find(s => s.id === selectedId) ?? null;
  const channel = channels.data?.find(c => c.id === selectedId) ?? null;

  function select(id: number | null) {
    const next = new URLSearchParams(params);
    if (id === null) next.delete('kanal'); else next.set('kanal', String(id));
    setParams(next, { replace: true });
  }

  function changed(message?: string) {
    if (message) onNotice(message);
    statuses.reload();
    channels.reload();
  }

  const locked = list.some(s => s.writeLocked);

  return (
    <>
      {locked && (
        <div className="alert alert-warn" role="status" data-testid="write-lock-banner">
          <strong>Kanala yazma kilitli (deneme modu).</strong> Siparişler ve ürünler kanallardan okunabilir; stok, fiyat, sipariş durumu ve
          cevaplar hiçbir pazaryerine gönderilmez. Yetenekler "Açık" görünse bile kilit önce gelir.
        </div>
      )}
      <ErrorBox error={statuses.error ?? channels.error} onRetry={() => { statuses.reload(); channels.reload(); }} />
      {statuses.loading && !statuses.data && <div className="loading">Kanal durumu yükleniyor…</div>}
      <ChannelStatusGrid statuses={list} selectedId={selectedId} onSelect={s => select(selectedId === s.id ? null : s.id)} />

      {selected && channel ? (
        <ChannelDetail key={channel.id} status={selected} channel={channel} channels={channels.data ?? []} editable={can('admin')}
          onChanged={changed} onClose={() => select(null)} />
      ) : (
        list.length > 0 && <p className="muted">Ayarlarını değiştirmek için bir kanal kartında "Ayarlar"a tıklayın.</p>
      )}

      {list.length > 0 && (
        <Card title="Kanala yazma yetenekleri (özet)" actions={<ExportButton fileName="kanal-yetenekleri" sheetName="Kanal yetenekleri" />}>
          <table className="table" data-testid="capability-matrix">
            <thead>
              <tr><th>Yetenek</th>{list.map(s => <th key={s.id}>{channelNames[s.code] ?? s.name}</th>)}</tr>
            </thead>
            <tbody>
              {Object.keys(capabilityLabels).map(cap => (
                <tr key={cap}>
                  <td>{capabilityLabels[cap]}</td>
                  {list.map(s => {
                    const state = s.capabilities.find(c => c.capability === cap)?.state ?? 'KAPALI';
                    const lockedHere = s.writeLocked && s.mode === 'GERCEK' && state !== 'KAPALI';
                    return (
                      <td key={s.id} className={state === 'KAPALI' ? 'muted' : undefined}>
                        {stateShort[state] ?? state}{lockedHere && <span className="warn-text small"> · kilitli</span>}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted small">"Onaylı": her kayıt Senkron Merkezi → Kanal işlemleri'nden Yönetici onayıyla tek tek gider. Değiştirmek için kanalı seçin.</p>
        </Card>
      )}
    </>
  );
}

function readCap(settingsJson: string | null): string {
  try {
    const v = settingsJson ? JSON.parse(settingsJson).stokUstLimit : undefined;
    return typeof v === 'number' ? String(v) : '';
  } catch {
    return '';
  }
}

/** Seçili kanalın ayarları: genel (pay, üst limit, aktif), API bilgisi, yazma yetenekleri, referans verisi. */
function ChannelDetail({ status, channel, channels, editable, onChanged, onClose }: {
  status: ChannelStatus; channel: Channel; channels: Channel[]; editable: boolean; onChanged: (m?: string) => void; onClose: () => void;
}) {
  const initial = { share: String(channel.sharePercent), active: channel.active, cap: readCap(channel.settingsJson) };
  const [form, setForm] = useState(initial);
  const [error, setError] = useState<unknown>(null);
  const [showCredentials, setShowCredentials] = useState(!channel.hasCredentials && status.realAdapterAvailable);
  const dirty = form.share !== initial.share || form.active !== initial.active || form.cap !== initial.cap;
  const name = channelNames[channel.code] ?? channel.name;

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    let settings: Record<string, unknown> = {};
    try {
      settings = channel.settingsJson ? JSON.parse(channel.settingsJson) : {};
    } catch {
      settings = {};
    }
    if (form.cap === '') delete settings.stokUstLimit;
    else settings.stokUstLimit = Number(form.cap);
    try {
      await put(`/api/channels/${channel.id}`, {
        name: channel.name, sharePercent: Number(form.share.replace(',', '.')), active: form.active,
        settingsJson: Object.keys(settings).length ? JSON.stringify(settings) : null
      });
      onChanged(`${name} kaydedildi; kanalın stokları yeniden hesaplanıp gönderilecek.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title={`${name} ayarları`} actions={<button className="small inline" onClick={onClose}>Kapat</button>}>
      <div data-testid="channel-detail">
        <h3>Genel</h3>
        <p className="muted small">Kanala gidecek stok = MIN( ⌊satılabilir × pay % / 100⌋, üst limit ). Pay %100: tam ortak havuz.</p>
        <ErrorBox error={error} />
        <form className="filters" onSubmit={save}>
          <Field label="Pay %"><input className="narrow" aria-label={`${channel.name} pay`} disabled={!editable} value={form.share} onChange={e => setForm({ ...form, share: e.target.value })} /></Field>
          <Field label="Üst limit"><input className="narrow" aria-label={`${channel.name} üst limit`} disabled={!editable} placeholder="yok" value={form.cap} onChange={e => setForm({ ...form, cap: e.target.value })} /></Field>
          <label className="check"><input type="checkbox" aria-label={`${channel.name} aktif`} disabled={!editable} checked={form.active} onChange={e => setForm({ ...form, active: e.target.checked })} /> Aktif</label>
          {editable && <button className="primary" type="submit" disabled={!dirty} data-testid="channel-save">Kaydet</button>}
          {dirty && <span className="warn-text small" role="status">Kaydedilmemiş değişiklik var</span>}
        </form>

        <h3>API bilgisi</h3>
        {!status.realAdapterAvailable ? (
          <p className="muted">Bu kanalın gerçek entegrasyonu henüz yok (altyapı teyit bekliyor); sahte veriyle çalışır.</p>
        ) : (
          <>
            <p>
              {channel.hasCredentials ? <><span aria-hidden="true" className="ok-text">✓</span> Girildi</> : <><span aria-hidden="true" className="text-danger">✕</span> Girilmedi</>}
              {status.environment && <span className="muted"> · ortam: {status.environment === 'Prod' ? 'Canlı' : 'Test'}</span>}
              <span className="muted"> · sunucu modu: {status.mode === 'GERCEK' ? 'Gerçek' : 'Sahte (mock)'}</span>
              {editable && <> <button className="small inline" onClick={() => setShowCredentials(v => !v)}>{showCredentials ? 'Formu kapat' : channel.hasCredentials ? 'API bilgisini değiştir' : 'API bilgisi gir'}</button></>}
            </p>
            {status.mode === 'MOCK' && (
              <div className="alert alert-warn">
                Sunucu bu kanalı sahte (mock) modda çalıştırıyor: API bilgisi girilse de gerçek pazaryerine bağlanılmaz.
                Gerçek bağlantı için sunucuda "Kanallar:{channel.code}:Mod" = Gercek olmalıdır (deneme modunda: -GercekKanal {channel.code}).
              </div>
            )}
            {editable && showCredentials && channel.code in credentialSpecs && (
              <CredentialsForm channel={channel} onDone={message => { setShowCredentials(false); onChanged(message); }} />
            )}
          </>
        )}
      </div>
      <ChannelWriteSettings channels={channels} editable={editable} onNotice={m => onChanged(m)} channelId={String(channel.id)} onChanged={() => onChanged()} />
      <ChannelReferences channels={channels} canRefresh={editable} onNotice={m => onChanged(m)} channelId={String(channel.id)} />
    </Card>
  );
}

type CredentialField = { name: string; label: string; secret?: boolean; numeric?: boolean; help?: string; placeholder?: string; optional?: boolean };
type CredentialSpec = { title: string; hint: string; fields: CredentialField[]; environments: [string, string][] };

/** Kanal başına API bilgisi alanları (sunucudaki *Credentials.Parse ile aynı). */
const credentialSpecs: Record<string, CredentialSpec> = {
  TRENDYOL: {
    title: 'Trendyol API bilgileri',
    hint: 'Satıcı paneli → Hesap Bilgilerim → Entegrasyon Bilgileri.',
    fields: [
      { name: 'sellerId', label: 'Satıcı (supplier) no', numeric: true },
      { name: 'apiKey', label: 'API Key' },
      { name: 'apiSecret', label: 'API Secret', secret: true }
    ],
    environments: [['Prod', 'Canlı'], ['Stage', 'Test (IP beyaz listesi gerekir)']]
  },
  HEPSIBURADA: {
    title: 'Hepsiburada API bilgileri',
    // ADR-054: SIT'te çalışan giriş biçimi: kullanıcı adı = Mağaza ID, şifre = servis anahtarı, User-Agent = entegratör kullanıcı adı
    hint: 'Hepsiburada satıcı paneli → Bilgilerim → Entegrasyon → Entegratör Bilgileri.',
    fields: [
      { name: 'merchantId', label: 'Mağaza ID (Merchant ID)', placeholder: 'ör. a1b2c3d4-…', help: '"Mağaza ID" kutusundaki kod.' },
      { name: 'userName', label: 'Kullanıcı adı', placeholder: 'Mağaza ID ile aynı', help: 'Mağaza ID\'nin aynısı yazılır. Entegratör adı buraya değil, User-Agent alanına yazılır.' },
      { name: 'password', label: 'Servis anahtarı (şifre)', secret: true, help: 'Entegratörlerim → ilgili entegratör → "Servis Anahtarı" ile kopyalanan anahtar.' },
      { name: 'userAgent', label: 'User-Agent (entegratör kullanıcı adı)', placeholder: 'ör. firmaadi_dev', help: 'Entegratörlerim listesindeki entegratör kullanıcı adı.' }
    ],
    environments: [['Prod', 'Canlı'], ['Test', 'Test (SIT)']]
  },
  BEYMEN: {
    title: 'Beymen API bilgileri',
    hint: 'Partner Portal → Hesap Bilgileri → Entegrasyon Bilgileri.',
    fields: [
      { name: 'apiKey', label: 'API Key' },
      { name: 'apiPassword', label: 'API Password', secret: true }
    ],
    environments: [['Prod', 'Canlı'], ['Stage', 'Test (stage)']]
  }
};

/**
 * Kanal API bilgileri formu. Değerler sunucuda şifreli saklanır, hiçbir zaman geri okunmaz/gösterilmez;
 * değiştirmek için yeniden girilir. Gerçek çağrılar için sunucuda "Kanallar:{KOD}:Mod" = Gercek olmalıdır.
 */
function CredentialsForm({ channel, onDone }: { channel: Channel; onDone: (m: string) => void }) {
  const spec = credentialSpecs[channel.code];
  const empty = () => ({ ...Object.fromEntries(spec.fields.map(f => [f.name, ''])), environment: spec.environments[0][0] }) as Record<string, string>;
  const [form, setForm] = useState<Record<string, string>>(empty);
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const body: Record<string, unknown> = { ...form };
      for (const f of spec.fields) {
        if (f.numeric) body[f.name] = Number(form[f.name]);
        if (!f.numeric && form[f.name] === '') delete body[f.name];
      }
      await put(`/api/channels/${channel.id}/credentials`, body);
      setForm(empty());
      onDone(`${spec.title} şifreli olarak kaydedildi.`);
    } catch (err) {
      setError(err);
    }
  }

  async function remove() {
    setError(null);
    try {
      await api('DELETE', `/api/channels/${channel.id}/credentials`);
      onDone(`${spec.title} silindi.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <div className="excel-box">
      <h3>{spec.title}</h3>
      <p className="muted small">
        {spec.hint} Bilgiler şifreli saklanır ve bir daha gösterilmez. Gerçek çağrılar için sunucuda "Kanallar:{channel.code}:Mod" = Gercek olmalıdır.
      </p>
      <ErrorBox error={error} />
      <form className="filters" onSubmit={submit} autoComplete="off">
        {spec.fields.map(f => (
          <Field key={f.name} label={f.label} help={f.help}>
            <input
              type={f.secret ? 'password' : 'text'}
              inputMode={f.numeric ? 'numeric' : undefined}
              autoComplete={f.secret ? 'new-password' : 'off'}
              value={form[f.name]}
              placeholder={f.placeholder}
              data-testid={`cred-${f.name}`}
              onChange={e => setForm({ ...form, [f.name]: e.target.value })}
              required={!f.optional && !f.label.includes('opsiyonel')}
            />
          </Field>
        ))}
        <Field label="Ortam">
          <select value={form.environment} onChange={e => setForm({ ...form, environment: e.target.value })}>
            {spec.environments.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </Field>
        <button className="primary" type="submit">Kaydet</button>
        {channel.hasCredentials && <button type="button" className="danger" onClick={remove}>Sil</button>}
      </form>
      {channel.code === 'HEPSIBURADA' && form.userName.trim() !== '' && form.merchantId.trim() !== ''
        && form.userName.trim().toLowerCase() !== form.merchantId.trim().toLowerCase() && (
        <div className="alert alert-warn" data-testid="hb-username-warning">
          Kullanıcı adı Mağaza ID'den farklı. Hepsiburada'da kullanıcı adı olarak Mağaza ID yazılır; entegratör adı
          (ör. firmaadi_dev) User-Agent alanına girilir. Aksi hâlde Hepsiburada "API bilgileri hatalı" (401) döner.
        </div>
      )}
    </div>
  );
}

function ThresholdSettings({ editable, onSaved }: { editable: boolean; onSaved: (m: string) => void }) {
  const settings = useLoad(() => get<{ criticalStockThreshold: number; lowStockThreshold: number | null }>('/api/settings'), []);
  const [value, setValue] = useState('');
  const [lowStock, setLowStock] = useState('');
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (settings.data) {
      setValue(String(settings.data.criticalStockThreshold));
      setLowStock(String(settings.data.lowStockThreshold ?? 0));
    }
  }, [settings.data]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await put('/api/settings', { criticalStockThreshold: Number(value), lowStockThreshold: Number(lowStock) });
      onSaved('Eşikler kaydedildi. Düşük stok eşiği değiştiyse tüm ilanlar yeniden hesaplanıp kanallara gönderilir.');
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Uyarı eşikleri">
      <p className="muted">
        Kritik stok eşiği yalnızca panelde uyarı gösterir. Kanala 0 gönderme eşiği tüm ürün ve kanallar için geçerlidir: satılabilir stok bu
        değere eşit veya altındaysa tüm kanallardaki tüm ilanlara 0 gönderilir (0 = kapalı).
      </p>
      <ErrorBox error={error ?? settings.error} />
      <form className="filters" onSubmit={submit}>
        <Field label="Kritik stok eşiği (satılabilir ≤)">
          <input type="number" min={0} disabled={!editable} value={value} onChange={e => setValue(e.target.value)} />
        </Field>
        <Field label="Kanala 0 gönderme eşiği (satılabilir ≤)">
          <input type="number" min={0} disabled={!editable} value={lowStock} onChange={e => setLowStock(e.target.value)}
            aria-label="Kanala 0 gönderme eşiği" />
        </Field>
        {editable && <button className="primary" type="submit">Kaydet</button>}
      </form>
    </Card>
  );
}

function SafetyStockForm({ onSaved }: { onSaved: (m: string) => void }) {
  const warehouses = useLoad(() => get<Warehouse[]>('/api/warehouses'), []);
  const [form, setForm] = useState({ sku: '', warehouseId: '', quantity: '' });
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const sku = await resolveSku(form.sku);
      if (!sku) throw new Error(`SKU bulunamadı: ${form.sku}`);
      const result = await put<{ available: number }>('/api/stock/safety-stock', {
        skuId: sku.id, warehouseId: Number(form.warehouseId || warehouses.data?.[0]?.id), quantity: Number(form.quantity)
      });
      onSaved(`${sku.skuCode} güvenlik stoğu kaydedildi; satılabilir ${result.available}.`);
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Güvenlik stoğu">
      {error ? <div className="alert alert-error">{(error as Error).message}</div> : null}
      <form className="filters" onSubmit={submit}>
        <Field label="SKU kodu"><input value={form.sku} onChange={e => setForm({ ...form, sku: e.target.value })} required /></Field>
        <Field label="Depo">
          <select value={form.warehouseId} onChange={e => setForm({ ...form, warehouseId: e.target.value })}>
            {(warehouses.data ?? []).map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
        </Field>
        <Field label="Adet"><input type="number" min={0} value={form.quantity} onChange={e => setForm({ ...form, quantity: e.target.value })} required /></Field>
        <button className="primary" type="submit">Kaydet</button>
      </form>
    </Card>
  );
}

function UserSettings({ onSaved }: { onSaved: (m: string) => void }) {
  const { me } = useAuth();
  const users = useLoad(() => get<User[]>('/api/users'), []);
  const [form, setForm] = useState({ userName: '', fullName: '', role: 'Depo' as Role, password: '' });
  const [error, setError] = useState<unknown>(null);
  const roles = Object.keys(roleLabels) as Role[];

  async function run(action: () => Promise<unknown>, message: string) {
    setError(null);
    try {
      await action();
      onSaved(message);
      users.reload();
    } catch (err) {
      setError(err);
    }
  }

  return (
    <Card title="Kullanıcılar ve roller" actions={<ExportButton fileName="kullanicilar" sheetName="Kullanıcılar" />}>
      <p className="muted small">Yönetici: tüm işlemler · Depo: stok, sipariş durumu, iade girişi · Muhasebe: yalnızca okuma · Destek: okuma, müşteri bilgisi maskeli.</p>
      <p className="muted small">Pasife alınan veya silinen kullanıcının açık oturumu hemen kapanır; rol değişikliği anında geçerli olur. Kendi hesabınızı silemez, pasife alamaz, yönetici yetkinizi kaldıramazsınız; son aktif yönetici de kaldırılamaz. Kim ne yaptı: <Link to="/islem-gecmisi">İşlem Geçmişi</Link>.</p>
      <ErrorBox error={error ?? users.error} />
      <table className="table">
        <thead><tr><th>Kullanıcı</th><th>Ad soyad</th><th>Rol</th><th>Aktif</th><th>Son giriş</th><th /></tr></thead>
        <tbody>
          {(users.data ?? []).map(u => (
            <tr key={u.id}>
              <td>{u.userName}</td>
              <td>{u.fullName}</td>
              <td>
                <select aria-label={`${u.userName} rol`} value={u.role}
                  onChange={e => run(() => put(`/api/users/${u.id}`, { fullName: u.fullName, role: e.target.value, active: u.active }), `${u.userName} rolü güncellendi.`)}>
                  {roles.map(r => <option key={r} value={r}>{roleLabels[r]}</option>)}
                </select>
              </td>
              <td>
                <input type="checkbox" aria-label={`${u.userName} aktif`} checked={u.active}
                  onChange={e => run(() => put(`/api/users/${u.id}`, { fullName: u.fullName, role: u.role, active: e.target.checked }), `${u.userName} güncellendi.`)} />
              </td>
              <td>{formatDateTime(u.lastLoginUtc)}</td>
              <td>
                <button className="small" onClick={() => {
                  const password = window.prompt(`${u.userName} için yeni parola (en az 10 karakter):`);
                  if (password) run(() => post(`/api/users/${u.id}/password`, { password }), `${u.userName} parolası değiştirildi.`);
                }}>Parola</button>{' '}
                <button className="small danger" disabled={u.userName === me?.userName} title={u.userName === me?.userName ? 'Kendi hesabınızı silemezsiniz' : undefined}
                  onClick={() => {
                    if (window.confirm(`${u.fullName} (${u.userName}) silinsin mi? Açık oturumu hemen kapanır. Geçmiş kayıtlarda adı kalır.`))
                      run(() => del(`/api/users/${u.id}`), `${u.userName} silindi.`);
                  }}>Sil</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <h3>Yeni kullanıcı</h3>
      <form className="filters" onSubmit={e => {
        e.preventDefault();
        run(() => post('/api/users', form), `${form.userName} oluşturuldu.`).then(() => setForm({ ...form, userName: '', fullName: '', password: '' }));
      }}>
        <Field label="Kullanıcı adı"><input value={form.userName} onChange={e => setForm({ ...form, userName: e.target.value })} required /></Field>
        <Field label="Ad soyad"><input value={form.fullName} onChange={e => setForm({ ...form, fullName: e.target.value })} required /></Field>
        <Field label="Rol">
          <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value as Role })}>
            {roles.map(r => <option key={r} value={r}>{roleLabels[r]}</option>)}
          </select>
        </Field>
        <Field label="Parola"><input type="password" autoComplete="new-password" value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} required minLength={10} /></Field>
        <button className="primary" type="submit">Oluştur</button>
      </form>
    </Card>
  );
}
