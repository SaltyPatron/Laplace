/** Input descriptors come from the installed operation catalog, not SQL parsed in the UI. */
export interface OperationParameter { name: string; type: string; optional: boolean }
export interface OperationField { mode: 'value' | 'omit' | 'null'; value: string }
export type OperationDraft = Record<string, OperationField>;

export function initialOperationDraft(parameters: readonly OperationParameter[]): OperationDraft {
  return Object.fromEntries(parameters.map((p) => [p.name, {
    mode: p.optional ? 'omit' : 'value', value: p.type === 'boolean' || p.type === 'bool' ? 'false' : '',
  }]));
}

/** Encoding/validation only. The server still owns type casts, overloads and effects. */
export function operationArguments(parameters: readonly OperationParameter[], draft: OperationDraft): Record<string, unknown> {
  const args: Record<string, unknown> = Object.create(null);
  for (const parameter of parameters) {
    if (!parameter.name) throw new Error('This signature has unnamed arguments; named invocation is not supported.');
    const field = draft[parameter.name];
    if (!field || field.mode === 'omit') {
      if (!parameter.optional) throw new Error(`${parameter.name}: supply a value or explicitly select NULL.`);
      continue;
    }
    if (field.mode === 'null') { args[parameter.name] = null; continue; }
    const type = parameter.type.toLowerCase();
    try {
      if (type === 'boolean' || type === 'bool') {
        if (field.value !== 'true' && field.value !== 'false') throw new Error('Choose true or false.');
        args[parameter.name] = field.value === 'true';
      } else if (type.endsWith('[]')) {
        const values: unknown = JSON.parse(field.value);
        if (!Array.isArray(values) || values.some((v) => v !== null && typeof v !== 'string'))
          throw new Error('Enter a JSON array of strings or null. Quote numeric values so their exact digits reach the server.');
        args[parameter.name] = values;
      } else {
        if (['smallint', 'integer', 'bigint', 'int2', 'int4', 'int8'].includes(type) && !/^[+-]?\d+$/.test(field.value))
          throw new Error('Enter integer digits, or explicitly select NULL.');
        if (type === 'json' || type === 'jsonb') JSON.parse(field.value); // Validate only; do not reserialize.
        // Scalar strings are bound as text and cast to the declared type by the
        // existing invoker. This keeps exact integers, decimals, JSON and text.
        args[parameter.name] = field.value;
      }
    } catch (error) {
      throw new Error(`${parameter.name}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }
  return args;
}
