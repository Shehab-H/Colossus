import { addons } from 'storybook/manager-api';
import { create } from 'storybook/theming/create';

addons.setConfig({
  theme: create({
    base: 'light',
    brandTitle: 'Colossus UI',
    brandUrl: '/',
    colorPrimary: '#e60000',
    colorSecondary: '#bd0000',
    appBg: '#f4f4f4',
    appContentBg: '#ffffff',
    appBorderColor: '#d8d8d8',
    barSelectedColor: '#e60000',
  }),
});
