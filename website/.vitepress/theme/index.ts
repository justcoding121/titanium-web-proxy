import { h } from 'vue'
import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import './custom.css'
import CopyPackageCommand from './components/CopyPackageCommand.vue'
import HomeQuickStart from './components/HomeQuickStart.vue'

export default {
  extends: DefaultTheme,
  Layout() {
    return h(DefaultTheme.Layout, null, {
      'home-hero-actions-after': () => h(CopyPackageCommand),
      'home-hero-after': () => h(HomeQuickStart),
    })
  },
} satisfies Theme
