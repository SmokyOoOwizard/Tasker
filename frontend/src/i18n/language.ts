import { useSyncExternalStore } from 'react'

export type Language = 'ru' | 'en'
export type LanguagePreference = 'system' | Language

const storageKey = 'tasker.language'

function readPreference(): LanguagePreference {
  try {
    const value = localStorage.getItem(storageKey)
    return value === 'ru' || value === 'en' ? value : 'system'
  } catch {
    return 'system'
  }
}

function systemLanguage(): Language {
  return navigator.language.toLowerCase().startsWith('ru') ? 'ru' : 'en'
}

function resolve(preference: LanguagePreference): Language {
  return preference === 'system' ? systemLanguage() : preference
}

let preference = readPreference()
let current = resolve(preference)
const listeners = new Set<() => void>()

function apply() {
  current = resolve(preference)
  document.documentElement.lang = current
  listeners.forEach((l) => l())
}

document.documentElement.lang = current

window.addEventListener('storage', (e) => {
  if (e.key !== storageKey) return
  preference = readPreference()
  apply()
})

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function setLanguagePreference(next: LanguagePreference) {
  preference = next
  try {
    if (next === 'system') localStorage.removeItem(storageKey)
    else localStorage.setItem(storageKey, next)
  } catch {
  }
  apply()
}

export function currentLanguage(): Language {
  return current
}

export function useLanguage(): Language {
  return useSyncExternalStore(subscribe, () => current)
}

export function useLanguagePreference(): LanguagePreference {
  return useSyncExternalStore(subscribe, () => preference)
}
