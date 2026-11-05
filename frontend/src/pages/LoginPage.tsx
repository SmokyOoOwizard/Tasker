import { useEffect, useState, type FormEvent } from 'react'
import { register, signIn } from '../auth'
import { errorText } from '../errors'
import { useT } from '../i18n'
import LanguageSelect from '../components/LanguageSelect'
import { loadRegistrationOpen } from '../reads'
import '../styles/forms.css'
import '../styles/auth.css'

function LoginPage() {
  const t = useT()
  const [registrationOpen, setRegistrationOpen] = useState<boolean | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    loadRegistrationOpen()
      .then(setRegistrationOpen)
      .catch(() => setRegistrationOpen(false))
  }, [])

  const submit = (action: (form: FormData) => Promise<unknown>) => async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await action(new FormData(e.currentTarget))
    } catch (err) {
      setError(errorText(err))
      setBusy(false)
    }
  }

  const field = (form: FormData, name: string) => String(form.get(name) ?? '')

  if (registrationOpen === null) return <div className="auth-container" />

  return (
    <div className="auth-container">
      <div className="auth-card">
        <div className="auth-header">
          <div className="auth-logo">T</div>
          <h1>{registrationOpen ? t('web.login.welcome') : t('web.login.title')}</h1>
          <p>{registrationOpen ? t('web.login.firstUserHint') : t('web.login.hint')}</p>
        </div>

        {registrationOpen ? (
          <form
            className="form"
            onSubmit={submit((f) => register(field(f, 'username'), field(f, 'email'), field(f, 'password')))}
          >
            <div className="form-group">
              <label htmlFor="username">{t('web.common.username')}</label>
              <input id="username" name="username" autoComplete="username" required autoFocus />
            </div>
            <div className="form-group">
              <label htmlFor="email">{t('web.common.email')}</label>
              <input id="email" name="email" type="email" autoComplete="email" required />
            </div>
            <div className="form-group">
              <label htmlFor="password">{t('web.common.password')}</label>
              <input id="password" name="password" type="password" autoComplete="new-password" minLength={8} required />
              <span className="form-hint">{t('web.common.passwordMin')}</span>
            </div>
            {error && <div className="error-message">{error}</div>}
            <button className="button" type="submit" disabled={busy}>
              {t('web.login.register')}
            </button>
          </form>
        ) : (
          <form className="form" onSubmit={submit((f) => signIn(field(f, 'login'), field(f, 'password')))}>
            <div className="form-group">
              <label htmlFor="login">{t('web.login.loginLabel')}</label>
              <input id="login" name="login" autoComplete="username" required autoFocus />
            </div>
            <div className="form-group">
              <label htmlFor="password">{t('web.common.password')}</label>
              <input id="password" name="password" type="password" autoComplete="current-password" required />
            </div>
            {error && <div className="error-message">{error}</div>}
            <button className="button" type="submit" disabled={busy}>
              {t('web.login.signIn')}
            </button>
          </form>
        )}
        <LanguageSelect id="login-language" />
      </div>
    </div>
  )
}

export default LoginPage
