
import { BrowserRouter, Routes, Route, Link, useLocation } from 'react-router-dom';
import { StartCrawlScreen } from './pages/StartCrawlScreen';
import { JobDetailsScreen } from './pages/JobDetailsScreen';
import { JobHistoryScreen } from './pages/JobHistoryScreen';
import './App.css';

function Nav() {
  const location = useLocation();
  
  return (
    <nav className="navbar">
      <Link to="/" className="nav-brand">Alteva</Link>
      <div className="nav-links">
        <Link 
          to="/" 
          className={`nav-link ${location.pathname === '/' ? 'active' : ''}`}
        >
          New Crawl
        </Link>
        <Link 
          to="/history" 
          className={`nav-link ${location.pathname === '/history' ? 'active' : ''}`}
        >
          History
        </Link>
      </div>
    </nav>
  );
}

function App() {
  return (
    <BrowserRouter>
      <Nav />
      <main className="container animate-fade-in">
        <Routes>
          <Route path="/" element={<StartCrawlScreen />} />
          <Route path="/jobs/:id" element={<JobDetailsScreen />} />
          <Route path="/history" element={<JobHistoryScreen />} />
        </Routes>
      </main>
    </BrowserRouter>
  );
}

export default App;
