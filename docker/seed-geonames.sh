#!/usr/bin/env bash
# Load the GeoNames "allCountries" dump (~13M global place/feature points) into the dev ClickHouse,
# for the views/geonames.json stress-test view. Idempotent: re-running replaces the table.
#
#   docker compose -f docker/docker-compose.yml up -d      # ClickHouse must be running
#   bash docker/seed-geonames.sh
#
# Source: https://download.geonames.org/export/dump/ (CC BY 4.0). ~370 MB zip → ~1.5 GB TSV.
set -euo pipefail

CH=${CH:-colossus-clickhouse}          # container name
DL=${DL:-/tmp/geonames}                # scratch download dir
mkdir -p "$DL"

if [ ! -f "$DL/allCountries.txt" ]; then
  echo "downloading allCountries.zip ..."
  curl -sL -o "$DL/allCountries.zip" 'https://download.geonames.org/export/dump/allCountries.zip'
  echo "unzipping ..."
  unzip -o "$DL/allCountries.zip" -d "$DL"
fi

echo "creating table colossus.geonames ..."
docker exec -i "$CH" clickhouse-client -u colossus --password colossus --multiquery <<'SQL'
DROP TABLE IF EXISTS colossus.geonames;
CREATE TABLE colossus.geonames
(
    geonameid         Int64,
    name              String,
    asciiname         String,
    alternatenames    String,
    latitude          Float64,
    longitude         Float64,
    feature_class     LowCardinality(String),
    feature_code      LowCardinality(String),
    country_code      LowCardinality(String),
    cc2               String,
    admin1_code       String,
    admin2_code       String,
    admin3_code       String,
    admin4_code       String,
    population        Int64,
    elevation         Int32,
    dem               Int32,
    timezone          LowCardinality(String),
    modification_date Date
)
ENGINE = MergeTree
ORDER BY (feature_class, country_code);
SQL

echo "loading rows (this streams ~1.5 GB) ..."
docker exec -i "$CH" clickhouse-client -u colossus --password colossus \
  --input_format_tsv_empty_as_default 1 \
  --date_time_input_format best_effort \
  --query "INSERT INTO colossus.geonames FORMAT TSV" < "$DL/allCountries.txt"

docker exec -i "$CH" clickhouse-client -u colossus --password colossus \
  --query "SELECT count() AS rows, uniqExact(feature_class) AS classes, uniqExact(country_code) AS countries FROM colossus.geonames"
echo "done."
