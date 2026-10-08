import { IconUser } from './Icons'

/** Круг с первой буквой имени; без имени (десктоп) — значок человека. */
function Avatar({ name, className }: { name?: string; className: string }) {
  const letter = name?.trim().charAt(0).toUpperCase()
  return <div className={className}>{letter ? <span>{letter}</span> : <IconUser />}</div>
}

export default Avatar
