import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { crawlerApi } from '../api/crawlerApi';
import type { CrawlJobSummaryResponse, PaginatedListResponse } from '../api/crawlerApi';

export function JobHistoryScreen() {
  const [data, setData] = useState<PaginatedListResponse<CrawlJobSummaryResponse> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const pageSize = 10;

  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    const fetchHistory = async () => {
      setLoading(true);
      try {
        const response = await crawlerApi.getJobHistory(page, pageSize);
        setData(response);
        setError(null);
      } catch (err: any) {
        setError(err.message || 'Failed to load job history');
      } finally {
        setLoading(false);
      }
    };

    fetchHistory();
  }, [page, refreshKey]);

  const handleDelete = async (id: string, status: string) => {
    if (status === 'Pending' || status === 'Running') {
      alert('Cannot delete an active job. Please cancel it first.');
      return;
    }
    
    if (window.confirm('Are you sure you want to delete this crawl? This action cannot be undone.')) {
      try {
        await crawlerApi.deleteJob(id);
        setRefreshKey(k => k + 1);
      } catch (err: any) {
        alert(err.message || 'Failed to delete job');
      }
    }
  };

  const totalPages = data ? Math.ceil(data.totalCount / pageSize) : 0;

  return (
    <div className="animate-fade-in" style={{ marginTop: '2rem' }}>
      <div className="flex justify-between items-center mb-4">
        <h2>Crawl History</h2>
        <Link to="/" className="btn btn-primary">New Crawl</Link>
      </div>

      {error && (
        <div className="alert alert-error">
          {error}
        </div>
      )}

      <div className="table-container">
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Target URL</th>
              <th>Depth</th>
              <th>Submitted</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {loading && !data && (
              <tr>
                <td colSpan={5} className="text-center" style={{ padding: '3rem' }}>
                  <span className="spinner"></span>
                </td>
              </tr>
            )}
            
            {!loading && data?.items.length === 0 && (
              <tr>
                <td colSpan={5} className="text-center text-muted" style={{ padding: '3rem' }}>
                  No crawl jobs found. Start your first crawl!
                </td>
              </tr>
            )}

            {data?.items.map(job => (
              <tr key={job.id}>
                <td>
                  <span className={`badge badge-${job.status.toLowerCase()}`}>
                    {job.status}
                  </span>
                </td>
                <td style={{ maxWidth: '300px' }}>
                  <div style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }} title={job.inputUrl}>
                    {job.inputUrl}
                  </div>
                </td>
                <td>{job.maxDepth}</td>
                <td>{new Date(job.createdAt).toLocaleString()}</td>
                <td>
                  <div className="flex gap-2">
                    <Link to={`/jobs/${job.id}`} className="btn btn-secondary" style={{ padding: '0.25rem 0.75rem', fontSize: '0.875rem' }}>
                      View Details
                    </Link>
                    <button 
                      className="btn" 
                      style={{ padding: '0.25rem 0.75rem', fontSize: '0.875rem', backgroundColor: 'var(--error)', color: 'white', border: 'none' }}
                      onClick={() => handleDelete(job.id, job.status)}
                    >
                      Delete
                    </button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {data && data.totalCount > 0 && (
        <div className="flex justify-between items-center mt-4">
          <div className="text-muted" style={{ fontSize: '0.875rem' }}>
            Showing {(page - 1) * pageSize + 1} to {Math.min(page * pageSize, data.totalCount)} of {data.totalCount} entries
          </div>
          <div className="flex gap-2">
            <button 
              className="btn btn-secondary" 
              disabled={page === 1 || loading}
              onClick={() => setPage(p => p - 1)}
            >
              Previous
            </button>
            <button 
              className="btn btn-secondary" 
              disabled={page >= totalPages || loading}
              onClick={() => setPage(p => p + 1)}
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
