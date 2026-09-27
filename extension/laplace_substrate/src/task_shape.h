#ifndef LAPLACE_TASK_SHAPE_H
#define LAPLACE_TASK_SHAPE_H

struct LaplacePromptIntent;

/* ORIENT the observation against witnessed task shapes: a positively standing
 * IS_EXAMPLE_OF from a parse trajectory to a shape, with CALLS and HAS_INPUT for
 * every slot, whose exemplar matches the observation's forms and every supported
 * parse. Each typed binding combination becomes a declared operation on the
 * intent; conflicting parses or distinct operations mark it ambiguous. All reads
 * are set reads bounded by fanout; overflow marks the budget exhausted and
 * discards the operations. */
void laplace_task_shape_compile(struct LaplacePromptIntent *intent, int fanout);

#endif
