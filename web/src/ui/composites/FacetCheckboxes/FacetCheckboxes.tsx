import { Checkbox } from '../../primitives/Checkbox';
import styles from './FacetCheckboxes.module.css';

/** Values and counts describe the supplied collection, before facet filtering. */
export function FacetCheckboxes({ label, values, selected, onChange }: {
  label: string;
  values: (string | null | undefined)[];
  selected: string[];
  onChange: (values: string[]) => void;
}) {
  const options = [...new Set([...values.map(value => value ?? ''), ...selected])].sort();
  if (options.length < 2 && selected.length === 0) return null;
  return <fieldset className={styles.facet}>
    <legend>{label}</legend>
    {options.map(value => <Checkbox key={value} checked={selected.includes(value)}
      label={<>{value || 'Not recorded'} <small>({values.filter(item => (item ?? '') === value).length})</small></>}
      onChange={event => onChange(event.target.checked ? [...selected, value] : selected.filter(item => item !== value))} />)}
  </fieldset>;
}
