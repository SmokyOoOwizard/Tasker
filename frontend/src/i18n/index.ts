import { useCallback } from 'react'
import { strings, type StringKey } from '../generated/strings'
import { currentLanguage, useLanguage, type Language } from './language'

export { currentLanguage, setLanguagePreference, useLanguage, useLanguagePreference } from './language'
export type { Language, LanguagePreference } from './language'
export type { StringKey } from '../generated/strings'

export type Arg = string | number

const pluralOrder: Record<Language, readonly Intl.LDMLPluralRule[]> = {
  ru: ['one', 'few', 'other'],
  en: ['one', 'other'],
}

const pluralRules: Record<Language, Intl.PluralRules> = {
  ru: new Intl.PluralRules('ru'),
  en: new Intl.PluralRules('en'),
}

function pluralForm(language: Language, n: number): number {
  const category = pluralRules[language].select(n)
  const order = pluralOrder[language]
  const index = order.indexOf(category)
  return index >= 0 ? index : order.length - 1
}

export function format(language: Language, template: string, args: readonly Arg[]): string {
  const withPlurals = template.replace(/\{(\d+)#([^}]*)\}/g, (_, index: string, forms: string) => {
    const variants = forms.split('|')
    const n = Number(args[Number(index)])
    const form = pluralForm(language, Number.isFinite(n) ? n : 0)
    return variants[Math.min(form, variants.length - 1)]
  })
  return withPlurals.replace(/\{(\d+)\}/g, (match, index: string) => {
    const value = args[Number(index)]
    return value === undefined ? match : String(value)
  })
}

export function translateIn(language: Language, key: StringKey, ...args: Arg[]): string {
  return format(language, strings[language][key], args)
}

export function hasKey(key: string): key is StringKey {
  return key in strings.en
}

export function translate(key: StringKey, ...args: Arg[]): string {
  return translateIn(currentLanguage(), key, ...args)
}

export type Translate = (key: StringKey, ...args: Arg[]) => string

export function useT(): Translate {
  const language = useLanguage()
  return useCallback((key, ...args) => translateIn(language, key, ...args), [language])
}

export function useLocale(): string {
  return useLanguage() === 'ru' ? 'ru-RU' : 'en-US'
}

export function localeOf(language: Language): string {
  return language === 'ru' ? 'ru-RU' : 'en-US'
}
