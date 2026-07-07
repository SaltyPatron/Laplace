import { Field, type FieldProps } from '../Field';

export type FormRowProps = Omit<FieldProps, 'layout'>;

/** Horizontal label + control row — shorthand for `Field layout="row"`. */
export function FormRow(props: FormRowProps) {
  return <Field layout="row" {...props} />;
}
