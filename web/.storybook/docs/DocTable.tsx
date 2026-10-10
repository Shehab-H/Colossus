import { Fragment } from 'react';

function Inline({ text }: { text: string }) {
  return (
    <>
      {text.split('`').map((part, index) => (
        <Fragment key={index}>{index % 2 === 1 ? <code>{part}</code> : part}</Fragment>
      ))}
    </>
  );
}

export function DocTable({ columns, rows }: { columns: string[]; rows: string[][] }) {
  return (
    <table>
      <thead>
        <tr>{columns.map((column) => <th key={column}>{column}</th>)}</tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row[0]}>{row.map((cell, index) => <td key={index}><Inline text={cell} /></td>)}</tr>
        ))}
      </tbody>
    </table>
  );
}
