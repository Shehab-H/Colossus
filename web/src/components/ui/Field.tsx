import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from 'react';
import { cx } from '@/utils/classNames';

export function Field({ label, description, error, className, children }: {
  label: ReactNode;
  description?: ReactNode;
  error?: ReactNode;
  className?: string;
  children: ReactNode;
}) {
  return (
    <label className={cx('ui-field', className)}>
      <span className="ui-field__label">{label}</span>
      {description && <span className="ui-field__description">{description}</span>}
      {children}
      {error && <span className="ui-field__error">{error}</span>}
    </label>
  );
}

export function Input({ className, ...props }: InputHTMLAttributes<HTMLInputElement>) {
  return <input className={cx('ui-input', className)} {...props} />;
}

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className={cx('ui-select', className)} {...props} />;
}

export function Textarea({ className, ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={cx('ui-textarea', className)} {...props} />;
}

export function Checkbox({ label, className, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: ReactNode }) {
  return <label className={cx('ui-checkbox', className)}><input type="checkbox" {...props} /><span>{label}</span></label>;
}
