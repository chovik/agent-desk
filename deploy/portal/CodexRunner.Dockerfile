ARG AX_RUNNER_IMAGE
FROM ${AX_RUNNER_IMAGE}
ARG REVISION
LABEL org.opencontainers.image.revision=$REVISION
COPY codex-bundle /opt/codex
COPY guest/codex_driver.py /opt/agent-desk/codex_driver.py
COPY publish/CODEX-LICENSE /opt/agent-desk/CODEX-LICENSE
RUN ln -s /opt/codex/bin/codex /usr/local/bin/codex && chmod 644 /opt/agent-desk/*
