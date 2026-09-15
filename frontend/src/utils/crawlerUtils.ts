/**
 * Utility functions for formatting crawl metrics and statuses in the UI.
 */

export function formatRatio(ratio: number | null | undefined): string {
  if (ratio === null || ratio === undefined || Number.isNaN(ratio)) {
    return '0%';
  }
  const percentage = (ratio * 100).toFixed(1);
  return `${percentage}%`;
}

export function getStatusBadgeVariant(status: string): { bg: string; text: string } {
  switch (status?.toLowerCase()) {
    case 'completed':
      return { bg: '#e6f4ea', text: '#137333' };
    case 'running':
      return { bg: '#e8f0fe', text: '#1a73e8' };
    case 'failed':
      return { bg: '#fce8e6', text: '#c5221f' };
    case 'canceled':
      return { bg: '#f1f3f4', text: '#5f6368' };
    case 'pending':
    default:
      return { bg: '#fef7e0', text: '#b06000' };
  }
}
