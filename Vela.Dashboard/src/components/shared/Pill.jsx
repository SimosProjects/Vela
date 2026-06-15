import { B } from '../../styles/theme.js';

export function Pill({ color, children, style = {} }) {
  return (
    <span style={{
      display: 'inline-block',
      padding: '1px 5px',
      borderRadius: 3,
      fontSize: 10,
      fontWeight: 700,
      letterSpacing: '0.03em',
      textTransform: 'uppercase',
      color,
      background: color + '18',
      border: `1px solid ${color}28`,
      ...style,
    }}>
      {children}
    </span>
  );
}
