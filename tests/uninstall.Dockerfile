FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends util-linux shellcheck && rm -rf /var/lib/apt/lists/*
COPY install.sh uninstall.sh /fixture/
COPY tests/uninstall-smoke.sh /fixture/test.sh
RUN bash -n /fixture/install.sh /fixture/uninstall.sh /fixture/test.sh && shellcheck /fixture/install.sh /fixture/uninstall.sh /fixture/test.sh
CMD ["bash", "/fixture/test.sh"]
