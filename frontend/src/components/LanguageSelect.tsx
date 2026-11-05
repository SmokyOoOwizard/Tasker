import { setLanguagePreference, useLanguagePreference, useT, type LanguagePreference } from '../i18n'

const options: LanguagePreference[] = ['system', 'ru', 'en']

function LanguageSelect({ id }: { id: string }) {
  const t = useT()
  const preference = useLanguagePreference()
  return (
    <div className="form-group">
      <label htmlFor={id}>{t('web.language.label')}</label>
      <select id={id} value={preference} onChange={(e) => setLanguagePreference(e.target.value as LanguagePreference)}>
        {options.map((option) => (
          <option key={option} value={option}>
            {t(`web.language.${option}`)}
          </option>
        ))}
      </select>
    </div>
  )
}

export default LanguageSelect
