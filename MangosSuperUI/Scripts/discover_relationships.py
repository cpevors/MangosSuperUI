#!/usr/bin/env python3
"""
VMaNGOS Schema Relationship Discovery
Discovers and proves foreign key relationships by analyzing actual data.
Run on the VMaNGOS server to minimize query latency.

Usage:
  python3 discover_relationships.py [--host 127.0.0.1] [--user mangos] [--password mangos]

Outputs:
  schema_discovery.json - Full schema + proven relationships
"""

import json
import sys
import time
import argparse
from collections import defaultdict

try:
    import mysql.connector
except ImportError:
    print("Installing mysql-connector-python...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "mysql-connector-python", "--break-system-packages"])
    import mysql.connector


# --- Config ---
DATABASES = ["mangos", "characters", "realmd", "logs"]
SAMPLE_SIZE = 200          # Distinct values to sample per column
OVERLAP_THRESHOLD = 0.80   # 80%+ overlap = probable FK
MIN_DISTINCT = 3           # Skip columns with fewer distinct values (booleans/flags)
MAX_DISTINCT_TARGET = 500000  # Skip PK targets with insane cardinality (perf guard)
BATCH_SIZE = 50            # Columns to test per progress update


def get_connection(host, user, password, database=None):
    return mysql.connector.connect(
        host=host, user=user, password=password,
        database=database, connect_timeout=10
    )


def discover_databases(conn, candidates):
    """Return which of our candidate DBs actually exist."""
    cursor = conn.cursor()
    cursor.execute("SHOW DATABASES")
    existing = {row[0] for row in cursor.fetchall()}
    cursor.close()
    return [db for db in candidates if db in existing]


def extract_schema(conn, databases):
    """Pull full schema from INFORMATION_SCHEMA."""
    schema = {}
    cursor = conn.cursor(dictionary=True)

    for db in databases:
        cursor.execute("""
            SELECT TABLE_NAME, ENGINE, TABLE_ROWS
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = %s AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME
        """, (db,))
        tables = cursor.fetchall()

        for t in tables:
            table_name = t["TABLE_NAME"]
            full_name = f"{db}.{table_name}"

            cursor.execute("""
                SELECT COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, IS_NULLABLE,
                       COLUMN_KEY, COLUMN_DEFAULT, ORDINAL_POSITION, EXTRA
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s
                ORDER BY ORDINAL_POSITION
            """, (db, table_name))
            columns = cursor.fetchall()

            # Get actual declared FKs (rare for MyISAM but check)
            cursor.execute("""
                SELECT COLUMN_NAME, REFERENCED_TABLE_SCHEMA,
                       REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s
                  AND REFERENCED_TABLE_NAME IS NOT NULL
            """, (db, table_name))
            declared_fks = cursor.fetchall()

            # Get indexes
            cursor.execute("""
                SELECT INDEX_NAME, COLUMN_NAME, NON_UNIQUE, SEQ_IN_INDEX
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = %s AND TABLE_NAME = %s
                ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """, (db, table_name))
            indexes_raw = cursor.fetchall()

            indexes = defaultdict(list)
            for idx in indexes_raw:
                indexes[idx["INDEX_NAME"]].append({
                    "column": idx["COLUMN_NAME"],
                    "unique": idx["NON_UNIQUE"] == 0,
                    "seq": idx["SEQ_IN_INDEX"]
                })

            schema[full_name] = {
                "database": db,
                "table": table_name,
                "engine": t["ENGINE"],
                "est_rows": t["TABLE_ROWS"],
                "columns": [{
                    "name": c["COLUMN_NAME"],
                    "type": c["COLUMN_TYPE"],
                    "data_type": c["DATA_TYPE"],
                    "nullable": c["IS_NULLABLE"] == "YES",
                    "key": c["COLUMN_KEY"],
                    "default": str(c["COLUMN_DEFAULT"]) if c["COLUMN_DEFAULT"] is not None else None,
                    "position": c["ORDINAL_POSITION"],
                    "extra": c["EXTRA"]
                } for c in columns],
                "indexes": {k: v for k, v in indexes.items()},
                "declared_fks": [{
                    "column": fk["COLUMN_NAME"],
                    "ref_schema": fk["REFERENCED_TABLE_SCHEMA"],
                    "ref_table": fk["REFERENCED_TABLE_NAME"],
                    "ref_column": fk["REFERENCED_COLUMN_NAME"]
                } for fk in declared_fks]
            }

    cursor.close()
    return schema


def identify_pk_targets(schema):
    """
    Build a dict of candidate FK targets: columns that are PK or UNI.
    Returns: { "db.table.column": { "full_table": "db.table", "column": "col", "data_type": "int" } }
    """
    targets = {}
    for full_table, info in schema.items():
        for col in info["columns"]:
            if col["key"] in ("PRI", "UNI"):
                key = f"{full_table}.{col['name']}"
                targets[key] = {
                    "full_table": full_table,
                    "column": col["name"],
                    "data_type": col["data_type"]
                }
    return targets


def identify_fk_candidates(schema):
    """
    Build list of columns that COULD be foreign keys.
    Integer-ish columns that are NOT the table's own primary key.
    """
    int_types = {"tinyint", "smallint", "mediumint", "int", "bigint"}
    candidates = []

    for full_table, info in schema.items():
        pk_cols = {c["name"] for c in info["columns"] if c["key"] == "PRI"}
        # If table has a single PK, exclude it. If composite PK, parts could still be FKs.
        single_pk = pk_cols if len(pk_cols) == 1 else set()

        for col in info["columns"]:
            if col["data_type"] not in int_types:
                continue
            if col["name"] in single_pk:
                continue
            # Composite PK members ARE candidates (they're often FKs)
            candidates.append({
                "full_table": full_table,
                "database": info["database"],
                "table": info["table"],
                "column": col["name"],
                "data_type": col["data_type"],
                "is_pk_part": col["name"] in pk_cols
            })

    return candidates


def type_compatible(candidate_type, target_type):
    """Check if two MySQL integer types are compatible for FK relationship."""
    int_types = {"tinyint", "smallint", "mediumint", "int", "bigint"}
    return candidate_type in int_types and target_type in int_types


def sample_distinct_values(conn, database, table, column, limit=200):
    """Get a sample of distinct non-null values from a column."""
    cursor = conn.cursor()
    try:
        cursor.execute(f"SELECT DISTINCT `{column}` FROM `{database}`.`{table}` "
                       f"WHERE `{column}` IS NOT NULL AND `{column}` != 0 LIMIT {limit}")
        values = [row[0] for row in cursor.fetchall()]
        return values
    except Exception as e:
        return []
    finally:
        cursor.close()


def count_distinct(conn, database, table, column):
    """Get count of distinct non-null non-zero values."""
    cursor = conn.cursor()
    try:
        cursor.execute(f"SELECT COUNT(DISTINCT `{column}`) FROM `{database}`.`{table}` "
                       f"WHERE `{column}` IS NOT NULL AND `{column}` != 0")
        return cursor.fetchone()[0]
    except:
        return 0
    finally:
        cursor.close()


def check_overlap(conn, sample_values, target_db, target_table, target_column):
    """Check what percentage of sample values exist in the target column."""
    if not sample_values:
        return 0.0, 0

    cursor = conn.cursor()
    try:
        placeholders = ",".join(["%s"] * len(sample_values))
        cursor.execute(
            f"SELECT COUNT(DISTINCT `{target_column}`) FROM `{target_db}`.`{target_table}` "
            f"WHERE `{target_column}` IN ({placeholders})",
            sample_values
        )
        matches = cursor.fetchone()[0]
        return matches / len(sample_values), matches
    except:
        return 0.0, 0
    finally:
        cursor.close()


def discover_relationships(conn, schema, pk_targets, fk_candidates):
    """The main discovery loop. Tests each FK candidate against compatible PK targets."""
    relationships = []
    name_match_cache = {}

    total = len(fk_candidates)
    print(f"\n{'='*60}")
    print(f"Testing {total} candidate columns against {len(pk_targets)} PK targets")
    print(f"{'='*60}\n")

    # Pre-group targets by data type for faster filtering
    targets_by_type = defaultdict(list)
    for key, t in pk_targets.items():
        targets_by_type[t["data_type"]].append((key, t))

    # Cache: target column distinct counts (avoid re-querying)
    target_distinct_cache = {}

    skipped = 0
    tested = 0
    found = 0

    for i, cand in enumerate(fk_candidates):
        if (i + 1) % BATCH_SIZE == 0 or i == 0:
            elapsed = ""
            print(f"  Progress: {i+1}/{total} columns | {found} relationships found | {skipped} skipped")

        cand_full = f"{cand['full_table']}.{cand['column']}"

        # Get sample values for this candidate
        sample = sample_distinct_values(
            conn, cand["database"], cand["table"], cand["column"], SAMPLE_SIZE
        )

        if len(sample) < MIN_DISTINCT:
            skipped += 1
            continue

        # Test against compatible PK targets
        compatible_types = {"tinyint", "smallint", "mediumint", "int", "bigint"}
        for target_key, target in pk_targets.items():
            if target["data_type"] not in compatible_types:
                continue

            # Skip self-references to own PK (unless it's a different column name)
            if target["full_table"] == cand["full_table"] and target["column"] == cand["column"]:
                continue

            # Quick name heuristic boost: if column names share a root, prioritize
            # But we test ALL compatible pairs regardless

            overlap, matches = check_overlap(
                conn, sample,
                schema[target["full_table"]]["database"],
                schema[target["full_table"]]["table"],
                target["column"]
            )

            tested += 1

            if overlap >= OVERLAP_THRESHOLD and matches >= MIN_DISTINCT:
                # Get actual distinct count for confidence scoring
                cand_distinct = len(sample)  # approximation from sample

                rel = {
                    "from_table": cand["full_table"],
                    "from_column": cand["column"],
                    "to_table": target["full_table"],
                    "to_column": target["column"],
                    "overlap_pct": round(overlap, 4),
                    "sample_matches": matches,
                    "sample_size": len(sample),
                    "confidence": "high" if overlap >= 0.95 else "medium" if overlap >= 0.85 else "low",
                    "from_is_pk_part": cand["is_pk_part"]
                }
                relationships.append(rel)
                found += 1

                print(f"    ✓ {cand['full_table']}.{cand['column']} → "
                      f"{target['full_table']}.{target['column']} "
                      f"({overlap:.0%} overlap, {matches}/{len(sample)} matches)")

    print(f"\n{'='*60}")
    print(f"Discovery complete: {found} relationships from {tested} tests ({skipped} columns skipped)")
    print(f"{'='*60}\n")

    return relationships


def discover_name_matches(schema):
    """Find columns with identical names across different tables (potential implicit FKs)."""
    col_locations = defaultdict(list)
    for full_table, info in schema.items():
        for col in info["columns"]:
            col_locations[col["name"]].append({
                "full_table": full_table,
                "key": col["key"],
                "data_type": col["data_type"]
            })

    # Only keep names that appear in 2+ tables
    shared = {name: locs for name, locs in col_locations.items()
              if len(locs) > 1 and name not in ("name", "comment", "type", "flags", "data")}

    return shared


def main():
    parser = argparse.ArgumentParser(description="VMaNGOS Schema Relationship Discovery")
    parser.add_argument("--host", default="127.0.0.1", help="MySQL host")
    parser.add_argument("--port", type=int, default=3306, help="MySQL port")
    parser.add_argument("--user", default="mangos", help="MySQL user")
    parser.add_argument("--password", default="mangos", help="MySQL password")
    parser.add_argument("--output", default="schema_discovery.json", help="Output file")
    parser.add_argument("--skip-overlap", action="store_true", help="Skip expensive overlap testing, schema only")
    args = parser.parse_args()

    print(f"Connecting to MySQL at {args.host}:{args.port}...")
    conn = get_connection(args.host, args.user, args.password)

    # 1. Find which DBs exist
    print("Discovering databases...")
    databases = discover_databases(conn, DATABASES)
    print(f"  Found: {databases}")

    # 2. Extract full schema
    print("Extracting schema...")
    schema = extract_schema(conn, databases)
    table_count = len(schema)
    col_count = sum(len(t["columns"]) for t in schema.values())
    print(f"  {table_count} tables, {col_count} columns")

    # 3. Identify PK targets and FK candidates
    pk_targets = identify_pk_targets(schema)
    fk_candidates = identify_fk_candidates(schema)
    print(f"  {len(pk_targets)} PK/UNI targets, {len(fk_candidates)} FK candidates")

    # 4. Name-match analysis (fast)
    print("Analyzing column name matches...")
    name_matches = discover_name_matches(schema)
    print(f"  {len(name_matches)} column names shared across tables")

    # 5. Data overlap analysis (the expensive part)
    relationships = []
    if not args.skip_overlap:
        relationships = discover_relationships(conn, schema, pk_targets, fk_candidates)
    else:
        print("Skipping overlap analysis (--skip-overlap)")

    conn.close()

    # 6. Build output
    output = {
        "_meta": {
            "generated": time.strftime("%Y-%m-%d %H:%M:%S"),
            "host": args.host,
            "databases": databases,
            "table_count": table_count,
            "column_count": col_count,
            "relationship_count": len(relationships),
            "settings": {
                "sample_size": SAMPLE_SIZE,
                "overlap_threshold": OVERLAP_THRESHOLD,
                "min_distinct": MIN_DISTINCT
            }
        },
        "schema": schema,
        "relationships": relationships,
        "name_matches": {k: v for k, v in sorted(name_matches.items())},
        # Summary: tables grouped by database
        "table_index": {
            db: sorted([t for full, t_info in schema.items()
                        if t_info["database"] == db
                        for t in [t_info["table"]]])
            for db in databases
        }
    }

    # Write output
    with open(args.output, "w") as f:
        json.dump(output, f, indent=2, default=str)

    print(f"\nOutput written to {args.output}")
    print(f"  File size: {len(json.dumps(output, default=str)):,} bytes")

    # Quick summary
    print(f"\n--- Summary ---")
    for db in databases:
        tables = output["table_index"].get(db, [])
        print(f"  {db}: {len(tables)} tables")
    print(f"  Proven relationships: {len(relationships)}")
    if relationships:
        by_conf = defaultdict(int)
        for r in relationships:
            by_conf[r["confidence"]] += 1
        for conf, count in sorted(by_conf.items()):
            print(f"    {conf}: {count}")


if __name__ == "__main__":
    main()
