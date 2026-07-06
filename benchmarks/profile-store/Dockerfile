FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /workspace

RUN apt-get update \
    && apt-get install -y --no-install-recommends libsnappy1v5 \
    && rm -rf /var/lib/apt/lists/*

COPY README.md LICENSE ./
COPY src/ZoneTree ./src/ZoneTree
COPY benchmarks/profile-store/src ./benchmarks/profile-store/src

WORKDIR /workspace/benchmarks/profile-store/src

RUN dotnet restore

ENTRYPOINT ["dotnet", "run", "-c", "Release", "--no-restore", "--"]
