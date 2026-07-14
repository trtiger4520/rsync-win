ARG BASE_IMAGE=alpine:3.21@sha256:48b0309ca019d89d40f670aa1bc06e426dc0931948452e8491e3d65087abc07d
FROM ${BASE_IMAGE}

ARG RSYNC_VERSION
ARG RSYNC_SHA256
ARG RSYNC_PACKAGE

RUN apk add --no-cache \
        openssh \
        build-base \
        ca-certificates \
        curl \
        gzip \
        linux-headers \
        lz4-dev \
        tar \
        xxhash-dev \
        zlib-dev \
        zstd-dev \
    && if [ -n "${RSYNC_PACKAGE}" ]; then \
         apk add --no-cache "${RSYNC_PACKAGE}"; \
       else \
         test -n "${RSYNC_VERSION}" \
         && test -n "${RSYNC_SHA256}" \
         && curl --fail --silent --show-error --location \
             "https://download.samba.org/pub/rsync/src/rsync-${RSYNC_VERSION}.tar.gz" \
             --output /tmp/rsync.tar.gz \
         && printf '%s  %s\n' "${RSYNC_SHA256}" /tmp/rsync.tar.gz | sha256sum -c - \
         && tar -xzf /tmp/rsync.tar.gz -C /tmp \
         && cd "/tmp/rsync-${RSYNC_VERSION}" \
         && ./configure --prefix=/usr --disable-openssl \
         && make -j2 \
         && make install; \
       fi \
    && rsync --version | grep -F "rsync  version ${RSYNC_VERSION} " \
    && rm -rf "/tmp/rsync-${RSYNC_VERSION}" /tmp/rsync.tar.gz

WORKDIR /t
