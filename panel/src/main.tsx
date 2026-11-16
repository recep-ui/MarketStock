import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Navigate, NavLink, Outlet, Route, Routes } from 'react-router-dom';
import { AuthProvider, roleLabels, useAuth } from './auth';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { StockPage } from './pages/StockPage';
import { MovementsPage } from './pages/MovementsPage';
import { OrdersPage } from './pages/OrdersPage';
import { ReturnsPage } from './pages/ReturnsPage';
import { MappingPage } from './pages/MappingPage';
import { SyncPage } from './pages/SyncPage';
import { SettingsPage } from './pages/SettingsPage';
import './styles.css';

const nav = [
  { to: '/', label: 'Genel Bakış', end: true },
  { to: '/stok', label: 'Stok Listesi' },
  { to: '/hareketler', label: 'Stok Hareketleri' },
  { to: '/siparisler', label: 'Siparişler' },
  { to: '/iadeler', label: 'İade / İptal' },
  { to: '/eslestirme', label: 'Eşleştirme' },
  { to: '/senkron', label: 'Senkron Merkezi' },
  { to: '/ayarlar', label: 'Ayarlar' }
];

function Layout() {
  const { me, loading, logout } = useAuth();
  if (loading) return <div className="loading full">Yükleniyor…</div>;
  if (!me) return <Navigate to="/giris" replace />;
  return (
    <div className="layout">
      <aside className="sidebar">
        <div className="brand">PazaryeriHub</div>
        <nav>
          {nav.map(n => (
            <NavLink key={n.to} to={n.to} end={n.end} className={({ isActive }) => (isActive ? 'active' : undefined)}>
              {n.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <div className="main">
        <header className="topbar">
          <span data-testid="current-user">
            {me.fullName} <small>({roleLabels[me.role]})</small>
          </span>
          <button className="link" onClick={() => logout()}>Çıkış</button>
        </header>
        <main className="content">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/giris" element={<LoginPage />} />
          <Route element={<Layout />}>
            <Route index element={<OverviewPage />} />
            <Route path="stok" element={<StockPage />} />
            <Route path="hareketler" element={<MovementsPage />} />
            <Route path="siparisler" element={<OrdersPage />} />
            <Route path="iadeler" element={<ReturnsPage />} />
            <Route path="eslestirme" element={<MappingPage />} />
            <Route path="senkron" element={<SyncPage />} />
            <Route path="ayarlar" element={<SettingsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  </StrictMode>
);
