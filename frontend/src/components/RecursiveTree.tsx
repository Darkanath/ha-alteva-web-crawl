import { useState } from 'react';
import type { JobTreeNode } from '../api/crawlerApi';
import { formatRatio } from '../utils/crawlerUtils';

interface RecursiveTreeProps {
  node: JobTreeNode;
}

export function RecursiveTree({ node }: RecursiveTreeProps) {
  const [isExpanded, setIsExpanded] = useState(true);
  
  const hasChildren = node.children && node.children.length > 0;

  return (
    <div className="tree-node">
      <div 
        className="tree-item"
        style={{ cursor: hasChildren ? 'pointer' : 'default' }}
        onClick={() => hasChildren && setIsExpanded(!isExpanded)}
      >
        {hasChildren && (
          <span style={{ fontSize: '0.875rem', width: '1rem', display: 'inline-block', textAlign: 'center' }}>
            {isExpanded ? '▼' : '▶'}
          </span>
        )}
        {!hasChildren && (
          <span style={{ fontSize: '0.875rem', width: '1rem', display: 'inline-block', textAlign: 'center' }}>
            •
          </span>
        )}
        
        <span className="tree-url" title={node.url}>
          {node.url}
        </span>
        
        {node.status === 'Done' ? (
          <span className="ratio-pill">{formatRatio(node.domainLinkRatio)}</span>
        ) : (
          <span className="ratio-pill" title="Not crawled, so no Domain Link Ratio">{node.status}</span>
        )}
      </div>
      
      {hasChildren && isExpanded && (
        <div className="tree-children animate-fade-in">
          {node.children.map((child, index) => (
            <RecursiveTree key={`${child.url}-${index}`} node={child} />
          ))}
        </div>
      )}
    </div>
  );
}
