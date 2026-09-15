export interface CreateCrawlJobRequest {
  url: string;
  maxDepth?: number;
}

export interface CreateCrawlJobResponse {
  jobId: string;
}

export interface JobTreeNode {
  url: string;
  domainLinkRatio: number;
  children: JobTreeNode[];
}

export interface CrawlJobDetailsResponse {
  id: string;
  inputUrl: string;
  maxDepth: number;
  status: 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled';
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  failureReason?: string;
  tree?: JobTreeNode;
}

export interface CrawlJobSummaryResponse {
  id: string;
  inputUrl: string;
  maxDepth: number;
  status: 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled';
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  failureReason?: string;
}

export interface PaginatedListResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

const API_BASE_URL = 'http://localhost:8080/api';

const mapStatus = (status: any): any => {
  if (typeof status === 'number') {
    return ['Pending', 'Running', 'Completed', 'Failed', 'Canceled'][status] || 'Unknown';
  }
  return status;
};

export const crawlerApi = {
  startJob: async (request: CreateCrawlJobRequest): Promise<CreateCrawlJobResponse> => {
    const response = await fetch(`${API_BASE_URL}/jobs`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(request),
    });
    
    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new Error(errorData.detail || 'Failed to start crawl job');
    }
    
    return response.json();
  },

  getJobDetails: async (id: string): Promise<CrawlJobDetailsResponse> => {
    const response = await fetch(`${API_BASE_URL}/jobs/${id}`);
    
    if (!response.ok) {
      if (response.status === 404) {
        throw new Error('Job not found');
      }
      throw new Error('Failed to fetch job details');
    }
    
    const data = await response.json();
    if (data) data.status = mapStatus(data.status);
    return data;
  },

  getJobHistory: async (page = 1, pageSize = 20): Promise<PaginatedListResponse<CrawlJobSummaryResponse>> => {
    const response = await fetch(`${API_BASE_URL}/jobs?page=${page}&pageSize=${pageSize}`);
    
    if (!response.ok) {
      throw new Error('Failed to fetch job history');
    }
    
    const data = await response.json();
    if (data && data.items) {
      data.items = data.items.map((item: any) => ({
        ...item,
        status: mapStatus(item.status)
      }));
    }
    return data;
  },

  cancelJob: async (id: string): Promise<void> => {
    const response = await fetch(`${API_BASE_URL}/jobs/${id}/cancel`, {
      method: 'POST',
    });
    
    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new Error(errorData.detail || 'Failed to cancel job');
    }
  },

  deleteJob: async (id: string): Promise<void> => {
    const response = await fetch(`${API_BASE_URL}/jobs/${id}`, {
      method: 'DELETE',
    });
    
    if (!response.ok) {
      const errorData = await response.json().catch(() => ({}));
      throw new Error(errorData.detail || 'Failed to delete job');
    }
  }
};
