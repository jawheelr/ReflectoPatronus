# The updater: the only piece that may restart the stack. It has no open ports; it watches a shared folder for a request
# written by the API (an administrator clicked "Update now"), then updates the project folder it is given:
# back up, download, build, migrate, restart, health-check, and roll back by itself if the new version does not come up.
FROM alpine:3.21
RUN apk add --no-cache bash git jq curl coreutils docker-cli docker-cli-compose tzdata
COPY updater.sh /usr/local/bin/updater
RUN chmod 0755 /usr/local/bin/updater
ENTRYPOINT ["/usr/local/bin/updater"]
