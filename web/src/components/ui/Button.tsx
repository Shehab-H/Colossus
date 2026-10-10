import type { AnchorHTMLAttributes, ButtonHTMLAttributes } from 'react';
import { buttonClassName, type ButtonStyleProps } from './buttonStyles';

export function Button({ variant, size, iconOnly, className, type = 'button', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & ButtonStyleProps) {
  return <button className={buttonClassName({ variant, size, iconOnly }, className)} type={type} {...props} />;
}

export function ButtonLink({ variant, size, iconOnly, className, ...props }: AnchorHTMLAttributes<HTMLAnchorElement> & ButtonStyleProps) {
  return <a className={buttonClassName({ variant, size, iconOnly }, className)} {...props} />;
}
