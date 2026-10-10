import type { HTMLAttributes } from 'react';
import { cx } from '@/utils/classNames';

export type BadgeTone = 'neutral' | 'brand' | 'success' | 'warning' | 'danger' | 'info';

export function Badge({ tone = 'neutral', className, ...props }: HTMLAttributes<HTMLSpanElement> & { tone?: BadgeTone }) {
  return <span className={cx('ui-badge', tone !== 'neutral' && `ui-badge--${tone}`, className)} {...props} />;
}
