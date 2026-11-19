FROM postgres:17-bookworm
COPY --chmod=0644 pg_hba.conf /etc/postgresql/pg_hba.conf
COPY --chmod=0755 docker-entrypoint-wrapper.sh /usr/local/bin/reflectopatronus-entrypoint.sh
RUN chmod 755 /usr/local/bin/reflectopatronus-entrypoint.sh
ENTRYPOINT ["/usr/local/bin/reflectopatronus-entrypoint.sh"]
CMD ["postgres", "-c", "hba_file=/etc/postgresql/pg_hba.conf", "-c", "ssl=on", "-c", "ssl_min_protocol_version=TLSv1.3", "-c", "ssl_ca_file=/var/lib/postgresql/certs/ca.crt", "-c", "ssl_cert_file=/var/lib/postgresql/certs/server.crt", "-c", "ssl_key_file=/var/lib/postgresql/certs/server.key"]
