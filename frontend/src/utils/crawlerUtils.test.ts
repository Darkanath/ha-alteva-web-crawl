import { describe, it, expect } from 'vitest';
import { formatRatio, getStatusBadgeVariant } from './crawlerUtils';

describe('crawlerUtils', () => {
  describe('formatRatio', () => {
    it('formats 1.0 as 100.0%', () => {
      expect(formatRatio(1.0)).toBe('100.0%');
    });

    it('formats 0.5 as 50.0%', () => {
      expect(formatRatio(0.5)).toBe('50.0%');
    });

    it('formats 0.6667 as 66.7%', () => {
      expect(formatRatio(0.6667)).toBe('66.7%');
    });

    it('formats 0 as 0.0%', () => {
      expect(formatRatio(0)).toBe('0.0%');
    });

    it('handles null and undefined gracefully', () => {
      expect(formatRatio(null)).toBe('0%');
      expect(formatRatio(undefined)).toBe('0%');
    });
  });

  describe('getStatusBadgeVariant', () => {
    it('returns green for completed', () => {
      expect(getStatusBadgeVariant('Completed').text).toBe('#137333');
    });

    it('returns blue for running', () => {
      expect(getStatusBadgeVariant('Running').text).toBe('#1a73e8');
    });

    it('returns red for failed', () => {
      expect(getStatusBadgeVariant('Failed').text).toBe('#c5221f');
    });

    it('returns yellow for pending', () => {
      expect(getStatusBadgeVariant('Pending').text).toBe('#b06000');
    });
  });
});
