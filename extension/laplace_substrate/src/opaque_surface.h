#ifndef LAPLACE_SUBSTRATE_OPAQUE_SURFACE_H
#define LAPLACE_SUBSTRATE_OPAQUE_SURFACE_H

#include <stdbool.h>
#include <string.h>

/* A surface that is an identifier rather than words: a 32-hex content id
 * (optionally elided), and with lexicon set an ILI (i46360) or a WordNet
 * offset with its type (01976841-v). Such a surface realizes through the words
 * a source binds to it; any other surface is the entity's own content and is
 * shown as itself. One law for REALIZE (realize_batch.c) and display labels
 * (display_native.c). */
static inline bool
laplace_opaque_surface(const char *s, bool lexicon)
{
    size_t n, i = 0;

    if (s == NULL || *s == '\0')
        return true;
    n = strlen(s);
    while (i < n && ((s[i] >= '0' && s[i] <= '9') || (s[i] >= 'a' && s[i] <= 'f') || (s[i] >= 'A' && s[i] <= 'F')))
        ++i;
    if (i == 32 && (n == 32 || strcmp(s + 32, "\xe2\x80\xa6") == 0 || strcmp(s + 32, "...") == 0))
        return true;
    if (!lexicon)
        return false;
    if (s[0] == 'i' && n > 1)
    {
        i = 1;
        while (i < n && s[i] >= '0' && s[i] <= '9')
            ++i;
        if (i == n)
            return true;
    }
    i = 0;
    while (i < n && s[i] >= '0' && s[i] <= '9')
        ++i;
    return i >= 6 && n == i + 2 && s[i] == '-' && strchr("nvarspNVARSP", s[i + 1]) != NULL;
}

#endif
