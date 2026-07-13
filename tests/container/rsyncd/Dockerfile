FROM alpine:3.21@sha256:48b0309ca019d89d40f670aa1bc06e426dc0931948452e8491e3d65087abc07d

RUN apk add --no-cache rsync=3.4.3-r0 \
    && test "$(rsync --version | awk 'NR == 1 { print $3 }')" = "3.4.3" \
    && addgroup -g 1654 rsyncwin \
    && adduser -D -H -u 1654 -G rsyncwin rsyncwin \
    && mkdir -p /srv/tree/nested /srv/push \
    && printf 'colon\n' > '/srv/tree/a:b.txt' \
    && printf 'underscore\n' > '/srv/tree/a_b.txt' \
    && printf 'upper\n' > '/srv/tree/Case.txt' \
    && printf 'lower\n' > '/srv/tree/case.txt' \
    && printf 'nested\n' > '/srv/tree/nested/deep.txt' \
    && head -c 65536 /dev/zero > '/srv/tree/binary.bin' \
    && chown -R rsyncwin:rsyncwin /srv/push

COPY rsyncd.conf /etc/rsyncd.conf

EXPOSE 873
ENTRYPOINT ["rsync", "--daemon", "--no-detach", "--config=/etc/rsyncd.conf"]
