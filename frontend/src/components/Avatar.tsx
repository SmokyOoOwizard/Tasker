import { IconUser } from './Icons'

function Avatar({ name, className }: { name?: string; className: string }) {
  const letter = name?.trim().charAt(0).toUpperCase()
  return <div className={className}>{letter ? <span>{letter}</span> : <IconUser />}</div>
}

export default Avatar
