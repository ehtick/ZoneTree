# Bare Metal Server Setup

Use this on a fresh Linux benchmark server when running the profile-store
benchmark directly on the host instead of through Docker.

The commands below assume:

* the server user can install packages,
* the repository will be cloned under `/home/ubuntu/ZoneTree`.

## Install .NET SDK

```bash
apt-get update

apt-get install -y dotnet-sdk-10.0
```

## Install Docker

```bash
apt-get install -y docker.io docker-compose

systemctl enable --now docker

docker --version
docker-compose --version
```

## Clone The Repository

```bash
cd /home/ubuntu
git clone https://github.com/ZoneTree/ZoneTree.git
cd /home/ubuntu/ZoneTree/benchmarks/profile-store
```

## Install Native Dependencies

RocksDB uses the `RocksDbSharp` and `RocksDbNative` packages. On some Linux
systems, the native loader also needs a `libdl.so` name next to the copied
RocksDB native library.

```bash
apt-get update

apt-get install -y libc6-dev libstdc++6 zlib1g libsnappy1v5 liblz4-1 libzstd1
```

## Build

```bash
dotnet build src/ProfileStore.Benchmark.csproj -c Release
```

## Fix RocksDB libdl Lookup

```bash
mkdir -p src/bin/Release/net10.0/runtimes/linux-x64/native

ln -sf /lib/x86_64-linux-gnu/libdl.so.2 src/bin/Release/net10.0/runtimes/linux-x64/native/libdl.so
```

If `/lib/x86_64-linux-gnu/libdl.so.2` does not exist, find the installed
`libdl` path and use that path in the `ln -sf` command:

```bash
find /lib /usr/lib -name 'libdl.so*' 2>/dev/null
```

## Smoke Test RocksDB

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine rocksdb --profiles 100K
```

## Run A Reference Benchmark

Use the normal benchmark arguments for larger runs. For example:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine zonetree,rocksdb --output results --data data --update-latest --timeout-seconds 120000 --profiles 100K,500K,1M,2M,5M
```

With `--update-latest`, Linux reference reports are written under:

```text
reference/linux/profiles-<count>/
```
