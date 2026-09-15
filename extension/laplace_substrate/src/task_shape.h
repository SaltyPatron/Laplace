#ifndef LAPLACE_TASK_SHAPE_H
#define LAPLACE_TASK_SHAPE_H

struct LaplacePromptIntent;

/* Compile explicitly witnessed reusable shapes against complete current
 * structural alternatives and semantic occurrence bindings. Every lookup is a
 * bounded index/set read. No word, frame or historical request is itself a call. */
void laplace_task_shape_compile(struct LaplacePromptIntent *intent, int fanout);

#endif
