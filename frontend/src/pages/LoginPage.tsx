import { useEffect, useState, type FormEvent } from 'react'
import { register, signIn } from '../auth'
import { apiUrl } from '../workspace'
import '../styles/forms.css'
import '../styles/auth.css'

/**
 * Экран входа (только сервер). Пока на сервере нет ни одного пользователя, вместо входа —
 * регистрация первого, он становится администратором.
 */
function LoginPage() {
  // null — ещё не знаем, открыта ли регистрация.
  const [registrationOpen, setRegistrationOpen] = useState<boolean | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    fetch(apiUrl('/auth/registration'))
      .then((r) => (r.ok ? (r.json() as Promise<{ open: boolean }>) : { open: false }))
      .then((r) => setRegistrationOpen(r.open))
      .catch(() => setRegistrationOpen(false))
  }, [])

  const submit = (action: (form: FormData) => Promise<unknown>) => async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      // Успех меняет сессию — App сам покажет приложение вместо этого экрана.
      await action(new FormData(e.currentTarget))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
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
          <h1>{registrationOpen ? 'Добро пожаловать' : 'Вход'}</h1>
          <p>{registrationOpen ? 'Создайте первого пользователя — он станет администратором' : 'Войдите в Tasker'}</p>
        </div>

        {registrationOpen ? (
          <form
            className="form"
            onSubmit={submit((f) => register(field(f, 'username'), field(f, 'email'), field(f, 'password')))}
          >
            <div className="form-group">
              <label htmlFor="username">Имя пользователя</label>
              <input id="username" name="username" autoComplete="username" required autoFocus />
            </div>
            <div className="form-group">
              <label htmlFor="email">Почта</label>
              <input id="email" name="email" type="email" autoComplete="email" required />
            </div>
            <div className="form-group">
              <label htmlFor="password">Пароль</label>
              <input id="password" name="password" type="password" autoComplete="new-password" minLength={8} required />
              <span className="form-hint">Не короче 8 символов</span>
            </div>
            {error && <div className="error-message">{error}</div>}
            <button className="button" type="submit" disabled={busy}>
              Создать и войти
            </button>
          </form>
        ) : (
          <form className="form" onSubmit={submit((f) => signIn(field(f, 'login'), field(f, 'password')))}>
            <div className="form-group">
              <label htmlFor="login">Имя или почта</label>
              <input id="login" name="login" autoComplete="username" required autoFocus />
            </div>
            <div className="form-group">
              <label htmlFor="password">Пароль</label>
              <input id="password" name="password" type="password" autoComplete="current-password" required />
            </div>
            {error && <div className="error-message">{error}</div>}
            <button className="button" type="submit" disabled={busy}>
              Войти
            </button>
          </form>
        )}
      </div>
    </div>
  )
}

export default LoginPage
