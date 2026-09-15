import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { crawlerApi } from '../api/crawlerApi';

export function StartCrawlScreen() {
  const navigate = useNavigate();
  const [url, setUrl] = useState('');
  const [maxDepth, setMaxDepth] = useState<number>(2);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!url.trim()) {
      setError('Please enter a valid URL');
      return;
    }

    try {
      setIsSubmitting(true);
      const response = await crawlerApi.startJob({
        url: url.trim(),
        maxDepth: maxDepth
      });
      
      // Navigate to the job details page
      navigate(`/jobs/${response.jobId}`);
    } catch (err: any) {
      setError(err.message || 'An unexpected error occurred while starting the crawl');
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <div className="flex justify-between" style={{ justifyContent: 'center', marginTop: '4rem' }}>
      <div className="card" style={{ maxWidth: '500px', width: '100%' }}>
        <h2 className="text-center mb-4">Start New Crawl</h2>
        
        {error && (
          <div className="alert alert-error">
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label className="form-label" htmlFor="url">Target URL</label>
            <input
              id="url"
              type="url"
              className="form-input"
              placeholder="https://example.com"
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              required
              disabled={isSubmitting}
            />
          </div>

          <div className="form-group">
            <label className="form-label" htmlFor="depth">Max Depth</label>
            <div className="flex items-center gap-4">
              <input
                id="depth"
                type="range"
                min="1"
                max="10"
                className="form-input"
                style={{ padding: '0', flex: 1 }}
                value={maxDepth}
                onChange={(e) => setMaxDepth(parseInt(e.target.value))}
                disabled={isSubmitting}
              />
              <span className="text-muted" style={{ fontWeight: 600, width: '2rem' }}>
                {maxDepth}
              </span>
            </div>
          </div>

          <div className="mt-4" style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <button 
              type="submit" 
              className="btn btn-primary" 
              disabled={isSubmitting || !url}
              style={{ width: '100%' }}
            >
              {isSubmitting ? (
                <>
                  <span className="spinner" style={{ marginRight: '0.5rem', width: '1rem', height: '1rem', borderWidth: '2px' }}></span>
                  Starting...
                </>
              ) : 'Start Crawl'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
