FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl systemd util-linux procps && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 1001 runner && mkdir -p /run/systemd/system /fixture
COPY artifacts/package-linux-x64/ /fixture/package/
RUN chmod +x /fixture/package/RunnerRoom && \
    tar -czf /fixture/runner-room-linux-x64.tar.gz -C /fixture/package . && \
    cd /fixture && sha256sum runner-room-linux-x64.tar.gz > runner-room-linux-x64.tar.gz.sha256
COPY install.sh /fixture/install.sh
COPY tests/installer-mocks/ /usr/local/bin/
COPY tests/installer-smoke.sh /fixture/test.sh
RUN chmod +x /usr/local/bin/curl /usr/local/bin/systemctl
CMD ["bash", "/fixture/test.sh"]
