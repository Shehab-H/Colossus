import { Link, type LinkProps } from 'react-router';
import { buttonClassName, type ButtonStyleProps } from '@shared/ui/buttonStyles';

export function RouteLink(props: LinkProps) {
  return <Link {...props} />;
}

export function ButtonRouteLink({
  variant,
  size,
  iconOnly,
  className,
  ...props
}: LinkProps & ButtonStyleProps) {
  return <Link className={buttonClassName({ variant, size, iconOnly }, className)} {...props} />;
}
