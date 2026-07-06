# MySQL Benchmark Docker

Use this for a dedicated benchmark MySQL container. The settings are applied at
container startup, so they remain active whenever this container is restarted.

## Remove Old Container And Volume

Only run this if you do not need the data already created:

```bash
docker rm -f mysql
docker volume rm mysql_data
```

## Create Fresh Volume

```bash
docker volume create mysql_data
```

## Start MySQL 9.7.1

```bash
docker run -d \
  --name mysql \
  --restart unless-stopped \
  -p 3306:3306 \
  -v mysql_data:/var/lib/mysql \
  -e MYSQL_ROOT_PASSWORD='DevMySql_123456!' \
  -e MYSQL_ROOT_HOST='%' \
  mysql:9.7.1 \
  --skip-log-bin \
  --innodb-flush-log-at-trx-commit=2 \
  --sync-binlog=0 \
  --innodb-buffer-pool-size=8G \
  --innodb-redo-log-capacity=4G
```

These settings keep InnoDB, disable the binary log, avoid forcing a disk sync on
every transaction, and give InnoDB enough memory and redo capacity for larger
profile-store runs.

## Check Logs

```bash
docker logs mysql --tail=100
```

Wait until MySQL says it is ready for connections.

## Test From The VM

```bash
docker exec -it mysql mysql -uroot -p
```

Password:

```text
DevMySql_123456!
```

Then paste:

```sql
SELECT VERSION();
CREATE DATABASE IF NOT EXISTS profilebench;
SHOW DATABASES;
```

## Verify Benchmark Settings

Paste this in the MySQL shell:

```sql
SHOW VARIABLES
WHERE Variable_name IN (
  'datadir',
  'log_bin',
  'innodb_buffer_pool_size',
  'innodb_redo_log_capacity',
  'innodb_flush_log_at_trx_commit',
  'sync_binlog'
);
```

Expected benchmark values:

```text
log_bin = OFF
innodb_buffer_pool_size = 8589934592
innodb_redo_log_capacity = 4294967296
innodb_flush_log_at_trx_commit = 2
sync_binlog = 0
```

## Connect From Local .NET App

Use your VM IP address:

```text
Server=YOUR_VM_PUBLIC_IP;Port=3306;Database=profilebench;User ID=root;Password=DevMySql_123456!;SslMode=None;AllowPublicKeyRetrieval=True;
```

From `benchmarks/profile-store`, run the benchmark with the external MySQL
container:

```bash
dotnet run --project src/ProfileStore.Benchmark.csproj -c Release -- --engine all --mysql-host YOUR_VM_PUBLIC_IP --mysql-port 3306 --mysql-user root --mysql-password "DevMySql_123456!" --mysql-database profilebench --output results --data data --clean --profiles 100000
```

## Open Firewall If Needed

```bash
sudo ufw allow 3306/tcp
sudo ufw status
```

Also open TCP `3306` in the VPS/provider firewall panel if it has one.
