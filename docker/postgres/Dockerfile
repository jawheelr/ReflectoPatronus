# Build context is the repository root (see docker-compose.yml) so config is shared with the LXC deployment.
FROM postgres:17-bookworm
COPY --chmod=0644 deploy/common/pg_hba.conf /etc/reflectopatronus/pg_hba.conf
COPY --chmod=0644 deploy/common/rp-postgresql.conf /etc/reflectopatronus/conf.d/10-reflectopatronus.conf
COPY --chmod=0755 deploy/common/install-pki.sh /usr/local/bin/rp-install-pki.sh
COPY --chmod=0755 docker/postgres/docker-entrypoint-wrapper.sh /usr/local/bin/reflectopatronus-entrypoint.sh
ENTRYPOINT ["/usr/local/bin/reflectopatronus-entrypoint.sh"]
CMD ["postgres"]
