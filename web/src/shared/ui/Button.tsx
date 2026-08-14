import type { AnchorHTMLAttributes, ButtonHTMLAttributes } from 'react';
import { cx } from './classNames';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

interface ButtonStyleProps {
  variant?: ButtonVariant;
  size?: ButtonSize;
  iconOnly?: boolean;
}

function buttonClassName(
  { variant = 'secondary', size = 'md', iconOnly = false }: ButtonStyleProps,
  className?: string,
): string {
  return cx(
    'ui-button',
    `ui-button--${variant}`,
    size !== 'md' && `ui-button--${size}`,
    iconOnly && 'ui-button--icon',
    className,
  );
}

export function Button({ variant, size, iconOnly, className, type = 'button', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & ButtonStyleProps) {
  return <button className={buttonClassName({ variant, size, iconOnly }, className)} type={type} {...props} />;
}

export function ButtonLink({ variant, size, iconOnly, className, ...props }: AnchorHTMLAttributes<HTMLAnchorElement> & ButtonStyleProps) {
  return <a className={buttonClassName({ variant, size, iconOnly }, className)} {...props} />;
}
