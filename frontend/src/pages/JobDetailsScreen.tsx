import { useEffect, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import { crawlerApi } from '../api/crawlerApi';
import type { CrawlJobDetailsResponse } from '../api/crawlerApi';
import { RecursiveTree } from '../components/RecursiveTree';

export function JobDetailsScreen() {
  const { id } = useParams<{ id: string }>();
  const [job, setJob] = useState<CrawlJobDetailsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [isCanceling, setIsCanceling] = useState(false);

  const fetchJob = async () => {
    if (!id) return;
    try {
      const data = await crawlerApi.getJobDetails(id);
      setJob(data);
      setError(null);
    } catch (err: any) {
      setError(err.message || 'Failed to load job details');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchJob();
  }, [id]);

  useEffect(() => {
    if (!job) return;

    // Set up polling if the job is still in progress
    if (job.status === 'Pending' || job.status === 'Running') {
      const interval = setInterval(fetchJob, 3000); // Poll every 3 seconds
      return () => clearInterval(interval);
    }
  }, [job?.status]);

  const handleCancel = async () => {
    if (!id) return;
    try {
      setIsCanceling(true);
      await crawlerApi.cancelJob(id);
      await fetchJob();
    } catch (err: any) {
      alert(err.message || 'Failed to cancel job');
    } finally {
      setIsCanceling(false);
    }
  };

  if (loading && !job) {
    return (
      <div className="flex" style={{ justifyContent: 'center', marginTop: '4rem' }}>
        <span className="spinner"></span>
      </div>
    );
  }

  if (error && !job) {
    return (
      <div className="card text-center" style={{ marginTop: '2rem' }}>
        <h3 className="text-muted">Error</h3>
        <p>{error}</p>
        <Link to="/" className="btn btn-primary mt-4">Go Back</Link>
      </div>
    );
  }

  if (!job) return null;

  const isTerminal = job.status === 'Completed' || job.status === 'Failed' || job.status === 'Canceled';

  return (
    <div className="animate-fade-in" style={{ marginTop: '2rem' }}>
      <div className="flex justify-between items-center mb-4">
        <h2>Job Details</h2>
        {!isTerminal && (
          <button 
            className="btn btn-secondary" 
            onClick={handleCancel}
            disabled={isCanceling}
          >
            {isCanceling ? 'Canceling...' : 'Cancel Job'}
          </button>
        )}
      </div>

      <div className="card mb-4">
        <div className="flex justify-between items-center mb-4">
          <div>
            <h3 style={{ margin: 0 }}>Target URL</h3>
            <a href={job.inputUrl} target="_blank" rel="noreferrer" style={{ color: 'hsl(var(--text-primary))', wordBreak: 'break-all' }}>
              {job.inputUrl}
            </a>
          </div>
          <div 
            className={`badge badge-${job.status.toLowerCase()}`}
          >
            {job.status === 'Running' || job.status === 'Pending' ? (
              <span className="spinner" style={{ width: '12px', height: '12px', borderWidth: '2px', marginRight: '6px' }}></span>
            ) : null}
            {job.status}
          </div>
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: '1.5rem', marginTop: '2rem' }}>
          <div>
            <div className="form-label">Job ID</div>
            <div style={{ fontFamily: 'monospace' }}>{job.id}</div>
          </div>
          <div>
            <div className="form-label">Max Depth</div>
            <div>{job.maxDepth}</div>
          </div>
          <div>
            <div className="form-label">Created At</div>
            <div>{new Date(job.createdAt).toLocaleString()}</div>
          </div>
          {job.startedAt && (
            <div>
              <div className="form-label">Started At</div>
              <div>{new Date(job.startedAt).toLocaleString()}</div>
            </div>
          )}
          {job.completedAt && (
            <div>
              <div className="form-label">Completed At</div>
              <div>{new Date(job.completedAt).toLocaleString()}</div>
            </div>
          )}
        </div>

        {job.failureReason && (
          <div className="alert alert-error" style={{ marginTop: '2rem', marginBottom: 0 }}>
            <strong>Failure Reason:</strong> {job.failureReason}
          </div>
        )}
      </div>

      {job.status === 'Completed' && job.tree && (
        <div className="card animate-fade-in">
          <h3 className="mb-4">Crawl Results Tree</h3>
          <div style={{ backgroundColor: 'hsl(var(--bg-main) / 0.5)', padding: '1.5rem', borderRadius: 'var(--radius-md)', overflowX: 'auto' }}>
            <RecursiveTree node={job.tree} />
          </div>
        </div>
      )}
    </div>
  );
}
