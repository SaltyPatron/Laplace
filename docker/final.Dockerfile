# syntax=docker/dockerfile:1.7
# ==============================================================================
# Layer 4: Final runtime image. Adds entrypoint + initdb scripts on top of
# the pgext layer. This is what docker-compose runs.
#
# Result image:  laplace-pg:latest
# ==============================================================================

ARG IMG_NS=laplace

FROM ${IMG_NS}/pgext:dev

USER root

# Entrypoint: initdb on first boot, create laplace role + database, run
# /docker-entrypoint-initdb.d/*.sql, then exec postgres.
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# initdb scripts (CREATE EXTENSION postgis / laplace_pg).
COPY docker/initdb.d/ /docker-entrypoint-initdb.d/
RUN chown -R postgres:postgres /docker-entrypoint-initdb.d

USER postgres
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["postgres"]
