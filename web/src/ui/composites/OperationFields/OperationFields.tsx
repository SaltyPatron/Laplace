import { useId } from 'react';
import { Field } from '../Field';
import { Input } from '../../primitives/Input';
import { Select } from '../../primitives/Select';
import { TextArea } from '../../primitives/TextArea';
import { Muted } from '../../primitives/Text';
import type { OperationDraft, OperationParameter } from '../../lib/operationFields';

export interface OperationFieldsProps {
  parameters: readonly OperationParameter[];
  value: OperationDraft;
  onChange: (value: OperationDraft) => void;
  disabled?: boolean;
}

/** Reusable declared-type input controls; never infer an operation from its name. */
export function OperationFields({ parameters, value, onChange, disabled = false }: OperationFieldsProps) {
  const prefix = useId();
  return <fieldset disabled={disabled} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
    <legend>Arguments</legend>
    {parameters.length === 0 && <Muted>This signature takes no arguments.</Muted>}
    {parameters.map((parameter, index) => {
      const id = `${prefix}-${index}`;
      const field = value[parameter.name] ?? { mode: parameter.optional ? 'omit' : 'value', value: '' };
      const update = (change: Partial<typeof field>) => onChange({ ...value, [parameter.name]: { ...field, ...change } });
      const type = parameter.type.toLowerCase();
      const help = `${parameter.type}; ${parameter.optional ? 'omission uses the server default' : 'argument must be supplied'}.`;
      return <div key={`${parameter.name}-${index}`} style={{ display: 'grid', gap: '0.35rem', marginBlock: '0.7rem' }}>
        <Field label={`${parameter.name || '(unnamed)'} — input mode`} htmlFor={`${id}-mode`} help={help}>
          <Select id={`${id}-mode`} value={field.mode} onChange={(event) => update({ mode: event.target.value as typeof field.mode })}>
            {parameter.optional && <option value="omit">Use server default</option>}
            <option value="value">Supply a value</option><option value="null">Explicit SQL NULL</option>
          </Select>
        </Field>
        {field.mode === 'value' && <Field label={parameter.name || '(unnamed)'} htmlFor={id}
          help={type.endsWith('[]') ? 'JSON array of strings or null. Quote numeric values; null and the string "NULL" remain different.' : type === 'bytea' ? 'Binary value in PostgreSQL hex form, beginning with \\x.' : undefined}>
          {type === 'boolean' || type === 'bool' ? <Select id={id} value={field.value} onChange={(event) => update({ value: event.target.value })}>
            <option value="false">false</option><option value="true">true</option>
          </Select> : type === 'json' || type === 'jsonb' || type.endsWith('[]') ?
            <TextArea id={id} rows={3} value={field.value} onChange={(event) => update({ value: event.target.value })} /> :
            <Input id={id} value={field.value} onChange={(event) => update({ value: event.target.value })} />}
        </Field>}
      </div>;
    })}
  </fieldset>;
}
