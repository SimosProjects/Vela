import { B } from '../../styles/theme.js';

export function Card({ children, style = {}, noPad = false }) {
  return (
    <div style={{
      background: B.card,
      border: `1px solid ${B.bd}`,
      borderRadius: 8,
      ...(noPad ? {} : { padding: 14 }),
      ...style,
    }}>
      {children}
    </div>
  );
}
