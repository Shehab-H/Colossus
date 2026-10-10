import type { HTMLAttributes } from 'react';
import { cx } from '@/utils/classNames';

export function Card({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cx('ui-card', className)} {...props} />;
}

export function PaddedCard({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cx('ui-card', 'ui-card--padded', className)} {...props} />;
}
