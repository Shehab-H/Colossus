import { cx } from '@/utils/classNames';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonStyleProps {
  variant?: ButtonVariant;
  size?: ButtonSize;
  iconOnly?: boolean;
}

export function buttonClassName(
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
