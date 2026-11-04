# Resolved from mcr.microsoft.com/dotnet/aspnet:10.0.13 on 2026-11-02.
FROM mcr.microsoft.com/dotnet/aspnet@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
ARG REVISION
LABEL org.opencontainers.image.revision=$REVISION
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates util-linux \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY codex /usr/local/bin/codex
COPY publish/ ./
COPY dist/ /app/dist/
RUN chmod -R a+rX /app && chmod 755 /usr/local/bin/codex
USER 10001:10001
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentDesk.Portal.dll"]
