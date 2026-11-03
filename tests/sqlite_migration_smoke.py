"""Supplemental SQLite checks; these do not compile or execute the C# application."""
import pathlib
import re
from contextlib import contextmanager
import hashlib
import shutil
import sqlite3
import tempfile
import unittest


@contextmanager
def managed_connect(target):
    """Commit on success and always close: Windows cannot delete an open database file."""
    connection = sqlite3.connect(target)
    try:
        with connection:
            yield connection
    finally:
        connection.close()


SOURCE = pathlib.Path(__file__).resolve().parents[1] / "src/Plinth.App/Migrations/20261101000000_InitialSchema.cs"
DDL = re.search(r'CreateSql = """(.*?)""";', SOURCE.read_text(), re.S).group(1)
LEGACY_SOURCE = SOURCE.with_name("LegacyEnsureCreatedSchema.cs").read_text()
SIX_DDL = re.search(r'SixColumnCreateSql = """(.*?)""";', LEGACY_SOURCE, re.S).group(1)
UPGRADE_SQL = re.search(r'UpgradeSixColumnSql = """(.*?)""";', LEGACY_SOURCE, re.S).group(1)
INITIALIZER = SOURCE.parents[1] / "Data/DatabaseInitializer.cs"
BASELINE_SQL = re.search(r'command.CommandText = """(.*?)""";', INITIALIZER.read_text(), re.S).group(1)
BASELINE_ID = "20261101000000_InitialSchema"
NOTES_SOURCE = SOURCE.with_name("20261101010000_NotesEntries.cs").read_text()
NOTES_DDL = re.search(r'CreateSql = """(.*?)""";', NOTES_SOURCE, re.S).group(1)
IDENTITY_SOURCE = SOURCE.with_name("20261102000000_CodeIdentityKeys.cs").read_text()
IDENTITY_SQL = re.search(r'UpgradeSql = """(.*?)""";', IDENTITY_SOURCE, re.S).group(1)
NOTES_ID = "20261101010000_NotesEntries"
IDENTITY_ID = "20261102000000_CodeIdentityKeys"
TABLES = ("demo_records", "notes_entries")


def code_key(code):
    # 独立参考算法：只折叠 ASCII，不 trim/Unicode lower/normalize，严格 UTF-8。
    folded = "".join(chr(ord(char) + 32) if "A" <= char <= "Z" else char for char in code)
    return folded.encode("utf-8", errors="strict").hex().upper()


def insert_record(db, table, row_id, code, *, keyed=False):
    content = ["Name", "Remark"] if table == "demo_records" else ["Text"]
    columns = ["Id", "Code"] + content + [
        "Version", "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy"]
    values = [row_id, code] + ["保留内容 " + name for name in content] + [
        17, "2024-02-29 23:59:58.1234567", "creator", "2025-03-01 01:02:03.7654321", "editor"]
    if keyed:
        columns.append("CodeKey")
        values.append(code_key(code))
    db.execute(f'INSERT INTO "{table}" ({",".join(columns)}) VALUES ({",".join("?" for _ in values)})',
               values)


def execute_statements(db, script, parameters=None):
    # executescript 会隐式提交；逐条执行才能测试 C# 显式事务所用脚本的回滚。
    for statement in script.split(";"):
        if statement.strip():
            db.execute(statement, parameters or {})


def six_column_row(db):
    db.execute(
        """INSERT INTO demo_records (Id,Code,Name,Remark,Version,CreatedAtUtc)
        VALUES ('old-id','OLD-1','旧数据','保留备注',7,'2024-02-29 23:59:58.1234567')"""
    )
    db.commit()


def schema(db):
    result = []
    for (sql,) in db.execute(
        """SELECT sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'
        AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock') ORDER BY type,name"""
    ):
        sql = re.sub(r"\s+", " ", sql).strip().rstrip(";")
        if sql.startswith("CREATE TABLE "):
            start, end = sql.index("("), sql.rindex(")")
            columns = sorted(column.strip() for column in sql[start + 1:end].split(","))
            sql = sql[:start].strip() + "(" + ",".join(columns) + ")"
        result.append(sql)
    return result


def insert(connection, code):
    connection.execute(
        """INSERT INTO demo_records
        (Id,Code,Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy)
        VALUES (?, ?, 'name', '', 0, '2026-11-01', '', '2026-11-01', '')""",
        (code, code),
    )
    connection.commit()


class SqliteMigrationSmoke(unittest.TestCase):
    def test_notes_upgrade_preserves_demo_and_enforces_unique_code(self):
        with managed_connect(":memory:") as db:
            db.executescript(DDL)
            insert(db, "KEEP")
            db.executescript(NOTES_DDL)
            db.execute("""INSERT INTO notes_entries
                (Id,Code,Text,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy)
                VALUES ('note-id','default','便笺',1,'2026-11-01','tester','2026-11-01','tester')""")
            with self.assertRaises(sqlite3.IntegrityError):
                db.execute("""INSERT INTO notes_entries
                    SELECT 'second','DEFAULT',Text,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy
                    FROM notes_entries""")
            self.assertEqual(db.execute("SELECT Code FROM demo_records").fetchall(), [("KEEP",)])
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchall(), [("ok",)])

    def test_snapshot_and_new_frozen_target_match(self):
        # 静态检查不代替 EF HasPendingModelChanges 或 C# 编译。
        snapshot = SOURCE.with_name("AppDbContextModelSnapshot.cs").read_text()
        pattern = r"protected override void %s\(ModelBuilder modelBuilder\)\s*\{(.*?)\n    \}"
        migration_model = re.search(pattern % "BuildTargetModel", IDENTITY_SOURCE, re.S).group(1)
        snapshot_model = re.search(pattern % "BuildModel", snapshot, re.S).group(1)
        self.assertEqual(re.sub(r"\s+", " ", migration_model).strip(),
                         re.sub(r"\s+", " ", snapshot_model).strip())
        self.assertEqual(migration_model.count('entity.HasIndex("CodeKey").IsUnique();'), 2)
        self.assertEqual(migration_model.count(
            'entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("TEXT");'), 2)
        self.assertNotIn("UseCollation", migration_model)
        self.assertNotIn('HasIndex("Code")', migration_model)
        self.assertNotIn("BuildInitialModel", migration_model)
        self.assertNotIn("BuildNotesModel", migration_model)
        self.assertIn("throw new NotSupportedException", IDENTITY_SOURCE)

    def test_identity_upgrade_preserves_every_old_value_and_adds_only_keys(self):
        codes = ["  AbC  ", "É", "é", "Σ", "σ", "ς", "İ", "i", "ß", "SS",
                 "Ａ", "ａ", "中文😀Z", "e\u0301", "", "\tA\n", "中" * 50]
        with managed_connect(":memory:") as db:
            db.executescript(DDL + NOTES_DDL)
            for table in TABLES:
                for index, code in enumerate(codes):
                    insert_record(db, table, str(index), code)
            db.commit()
            old_columns = {table: [row[1] for row in db.execute(f"PRAGMA table_info({table})")]
                           for table in TABLES}
            before = {table: db.execute(f'SELECT * FROM "{table}" ORDER BY Id').fetchall()
                      for table in TABLES}
            with db:
                db.execute("BEGIN IMMEDIATE")
                execute_statements(db, IDENTITY_SQL)
            for table in TABLES:
                self.assertEqual(db.execute(
                    f'SELECT {",".join(old_columns[table])} FROM "{table}" ORDER BY Id').fetchall(),
                    before[table])
                actual = dict(db.execute(f'SELECT Code,CodeKey FROM "{table}"'))
                self.assertEqual(actual, {code: code_key(code) for code in codes})
                self.assertEqual(actual["  AbC  "], "20206162632020")
                self.assertEqual(actual["中文😀Z"], "E4B8ADE69687F09F98807A")
                self.assertEqual(len(actual["中" * 50]), 300)
                self.assertNotEqual(actual["É"], actual["é"])
                self.assertNotEqual(actual["e\u0301"], actual["é"])
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchall(), [("ok",)])

    def test_sql_backfill_matches_ascii_only_strict_utf8_reference(self):
        # 穷举 ASCII 字符与两侧非 ASCII，防止误用 trim、Unicode lower/casefold 或小写 hex。
        samples = [chr(value) for value in range(128)] + [
            "  A  ", "ÉÉ", "éé", "İIıi", "ẞßSS", "Σσς", "Ａａ", "😀AZ中文", "e\u0301"]
        with managed_connect(":memory:") as db:
            for code in samples:
                with self.subTest(code=repr(code)):
                    actual = db.execute("SELECT hex(CAST(lower(?) AS BLOB))", (code,)).fetchone()[0]
                    self.assertEqual(actual, code_key(code))
        for invalid in ("\ud800", "\udfff", "A\ud800Z"):
            with self.subTest(invalid=repr(invalid)), self.assertRaises(UnicodeEncodeError):
                code_key(invalid)

    def test_identity_schema_uses_required_unique_keys_and_binary_code(self):
        with managed_connect(":memory:") as db:
            db.executescript(DDL + NOTES_DDL + IDENTITY_SQL)
            for table in TABLES:
                with self.subTest(table=table):
                    columns = {row[1]: row for row in db.execute(f"PRAGMA table_info({table})")}
                    self.assertEqual(columns["CodeKey"][2:4], ("TEXT", 1))
                    indexes = db.execute(f"PRAGMA index_list({table})").fetchall()
                    self.assertNotIn(f"IX_{table}_Code", [row[1] for row in indexes])
                    self.assertIn((f"IX_{table}_CodeKey", 1), [(row[1], row[2]) for row in indexes])
                    indexed = db.execute(f'PRAGMA index_xinfo("IX_{table}_CodeKey")').fetchall()
                    self.assertEqual([(row[2], row[4]) for row in indexed if row[5]], [("CodeKey", "BINARY")])
                    insert_record(db, table, "one", "AbC", keyed=True)
                    with self.assertRaises(sqlite3.IntegrityError):
                        insert_record(db, table, "two", "aBc", keyed=True)
                    for index, code in enumerate(("É", "é", " AbC ", "Σ", "σ", "😀")):
                        insert_record(db, table, "unicode-" + str(index), code, keyed=True)
                    self.assertEqual(db.execute(
                        f'SELECT count(*) FROM "{table}" WHERE Code = ?', ("abc",)).fetchone()[0], 0)
                    with self.assertRaises(sqlite3.IntegrityError):
                        db.execute(f'UPDATE "{table}" SET CodeKey=NULL WHERE Id=?', ("one",))

    def test_non_utf8_database_cannot_run_identity_migration_directly(self):
        for encoding in ("UTF-16le", "UTF-16be"):
            with self.subTest(encoding=encoding), managed_connect(":memory:") as db:
                db.execute(f"PRAGMA encoding='{encoding}'")
                db.executescript(DDL + NOTES_DDL)
                insert_record(db, "demo_records", "one", "AbC")
                db.commit()
                before = schema(db)
                db.execute("BEGIN IMMEDIATE")
                with self.assertRaises(sqlite3.IntegrityError):
                    execute_statements(db, IDENTITY_SQL)
                db.rollback()
                self.assertEqual(schema(db), before)
                self.assertEqual(db.execute("SELECT Code FROM demo_records").fetchall(), [("AbC",)])

    def test_identity_failure_rolls_back_both_rebuilds_and_history(self):
        for failing_table in TABLES:
            with self.subTest(failing_table=failing_table), managed_connect(":memory:") as db:
                db.executescript(DDL + NOTES_DDL)
                execute_statements(db, BASELINE_SQL, {"id": BASELINE_ID})
                db.execute('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)', (NOTES_ID, "10.0.14"))
                for table in TABLES:
                    insert_record(db, table, "one", "AbC")
                # 模拟历史唯一索引丢失后的 ASCII 重复值，使新唯一索引创建真实失败。
                db.execute(f'DROP INDEX "IX_{failing_table}_Code"')
                insert_record(db, failing_table, "two", "aBc")
                db.commit()
                old_schema = schema(db)
                before = {table: db.execute(f'SELECT * FROM "{table}" ORDER BY Id').fetchall()
                          for table in TABLES}
                db.execute("BEGIN IMMEDIATE")
                # 历史写入也必须与迁移一起回滚。
                db.execute('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)', (IDENTITY_ID, "10.0.14"))
                with self.assertRaises(sqlite3.IntegrityError):
                    execute_statements(db, IDENTITY_SQL)
                db.rollback()
                self.assertEqual(schema(db), old_schema)
                for table in TABLES:
                    self.assertEqual(db.execute(f'SELECT * FROM "{table}" ORDER BY Id').fetchall(),
                                     before[table])
                self.assertEqual(db.execute('SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY MigrationId')
                                 .fetchall(), [(BASELINE_ID,), (NOTES_ID,)])
                self.assertEqual(db.execute("PRAGMA integrity_check").fetchall(), [("ok",)])

    def test_six_column_chain_reaches_identity_schema_on_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "original.db"
            candidate_path = pathlib.Path(directory) / "candidate.db"
            with managed_connect(str(path)) as original:
                original.executescript(SIX_DDL)
                six_column_row(original)
                before = original.execute("SELECT * FROM demo_records").fetchall()
                with managed_connect(str(candidate_path)) as candidate:
                    original.backup(candidate)
            original_digest = hashlib.sha256(path.read_bytes()).digest()
            with managed_connect(str(candidate_path)) as candidate:
                with candidate:
                    candidate.execute("BEGIN IMMEDIATE")
                    execute_statements(candidate, UPGRADE_SQL)
                    execute_statements(candidate, BASELINE_SQL, {"id": BASELINE_ID})
                for migration_id, sql in ((NOTES_ID, NOTES_DDL), (IDENTITY_ID, IDENTITY_SQL)):
                    with candidate:
                        candidate.execute("BEGIN IMMEDIATE")
                        execute_statements(candidate, sql)
                        candidate.execute('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)',
                                          (migration_id, "10.0.14"))
                self.assertEqual(candidate.execute(
                    "SELECT Id,Code,Name,Remark,Version,CreatedAtUtc FROM demo_records").fetchall(), before)
                self.assertEqual(candidate.execute(
                    "SELECT CodeKey,CreatedBy,UpdatedAtUtc,UpdatedBy FROM demo_records").fetchone(),
                    (code_key("OLD-1"), "unknown", before[0][5], "unknown"))
                self.assertEqual(candidate.execute('SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY MigrationId')
                                 .fetchall(), [(BASELINE_ID,), (NOTES_ID,), (IDENTITY_ID,)])
                with managed_connect(":memory:") as expected:
                    expected.executescript(DDL + NOTES_DDL + IDENTITY_SQL)
                    self.assertEqual(schema(candidate), schema(expected))
                self.assertEqual(candidate.execute("PRAGMA integrity_check").fetchall(), [("ok",)])
            self.assertEqual(hashlib.sha256(path.read_bytes()).digest(), original_digest)

    def test_identity_schema_comparison_detects_drift_despite_version_claim(self):
        # 使用与初始化器相同的固定 SQL 链建立预期结构；本测试不执行 C# 初始化器。
        initializer = INITIALIZER.read_text()
        self.assertIn("applied[^1] == CodeIdentityKeys.MigrationId", initializer)
        self.assertIn("create.CommandText += CodeIdentityKeys.UpgradeSql;", initializer)
        with managed_connect(":memory:") as expected:
            expected.executescript(DDL + NOTES_DDL + IDENTITY_SQL)
            target = schema(expected)
            changes = [
                "",  # 历史 schema 即使声称新版也不匹配。
                IDENTITY_SQL + 'DROP INDEX "IX_notes_entries_CodeKey";',
                IDENTITY_SQL + 'ALTER TABLE demo_records ADD COLUMN Unexpected TEXT;',
                IDENTITY_SQL + 'CREATE INDEX extra_code ON demo_records(Code);',
            ]
            for sql in changes:
                with self.subTest(sql=sql), managed_connect(":memory:") as actual:
                    actual.executescript(DDL + NOTES_DDL + sql)
                    execute_statements(actual, BASELINE_SQL, {"id": BASELINE_ID})
                    for migration_id in (NOTES_ID, IDENTITY_ID):
                        actual.execute('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)',
                                       (migration_id, "10.0.14"))
                    self.assertNotEqual(schema(actual), target)

    def test_six_column_upgrade_preserves_source_and_matches_fixed_full_schema(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "six.db"
            with managed_connect(str(path)) as original:
                original.executescript(SIX_DDL)
                six_column_row(original)
                old_values = original.execute("SELECT * FROM demo_records").fetchall()
                before = hashlib.sha256(path.read_bytes()).digest()
                safety_path = pathlib.Path(directory) / "safety.db"
                with managed_connect(str(safety_path)) as safety:
                    original.backup(safety)
            candidate_path = pathlib.Path(directory) / "candidate.db"
            shutil.copyfile(str(safety_path), str(candidate_path))
            with managed_connect(str(candidate_path)) as candidate:
                candidate.execute("BEGIN IMMEDIATE")
                execute_statements(candidate, UPGRADE_SQL)
                execute_statements(candidate, BASELINE_SQL, {"id": BASELINE_ID})
                candidate.commit()
                self.assertEqual(candidate.execute(
                    "SELECT Id,Code,Name,Remark,Version,CreatedAtUtc FROM demo_records").fetchall(), old_values)
                self.assertEqual(candidate.execute(
                    "SELECT CreatedBy,UpdatedAtUtc,UpdatedBy FROM demo_records").fetchone(),
                    ("unknown", old_values[0][5], "unknown"))
                self.assertEqual(candidate.execute(
                    'SELECT MigrationId FROM "__EFMigrationsHistory"').fetchall(), [(BASELINE_ID,)])
                self.assertEqual(candidate.execute("PRAGMA integrity_check").fetchall(), [("ok",)])
                self.assertTrue(all(row[4] is None for row in candidate.execute("PRAGMA table_info(demo_records)")))
                with managed_connect(":memory:") as expected:
                    expected.executescript(DDL)
                    self.assertEqual(schema(candidate), schema(expected))
                with self.assertRaises(sqlite3.IntegrityError):
                    candidate.execute("""INSERT INTO demo_records
                        SELECT 'new-id','old-1',Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy
                        FROM demo_records""")
            self.assertEqual(hashlib.sha256(path.read_bytes()).digest(), before)
            with managed_connect(str(safety_path)) as safety:
                self.assertEqual(len(safety.execute("PRAGMA table_info(demo_records)").fetchall()), 6)
                self.assertEqual(safety.execute("SELECT * FROM demo_records").fetchall(), old_values)

    def test_failed_baselining_rolls_back_audit_upgrade_and_preserves_six_column_data(self):
        with managed_connect(":memory:") as candidate:
            candidate.executescript(SIX_DDL)
            six_column_row(candidate)
            before = candidate.execute("SELECT * FROM demo_records").fetchall()
            candidate.execute("BEGIN IMMEDIATE")
            execute_statements(candidate, UPGRADE_SQL)
            execute_statements(candidate, BASELINE_SQL, {"id": BASELINE_ID})
            with self.assertRaises(sqlite3.IntegrityError):
                candidate.execute('INSERT INTO "__EFMigrationsHistory" VALUES (?, ?)', (BASELINE_ID, "10.0.14"))
            candidate.rollback()
            self.assertEqual(candidate.execute("SELECT * FROM demo_records").fetchall(), before)
            self.assertEqual(len(candidate.execute("PRAGMA table_info(demo_records)").fetchall()), 6)
            self.assertEqual(candidate.execute(
                "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory'").fetchone()[0], 0)

    def test_partial_audit_or_unknown_schema_is_not_a_known_historical_shape(self):
        with managed_connect(":memory:") as old, managed_connect(":memory:") as full:
            old.executescript(SIX_DDL)
            full.executescript(DDL)
            fixed_shapes = [schema(old), schema(full)]
            old.execute("ALTER TABLE demo_records ADD COLUMN CreatedBy TEXT NOT NULL DEFAULT 'unknown'")
            self.assertNotIn(schema(old), fixed_shapes)

    def test_fixed_migration_ddl_integrity_and_case_insensitive_unique_code(self):
        with managed_connect(":memory:") as db:
            db.executescript(DDL)
            insert(db, "A1")
            with self.assertRaises(sqlite3.IntegrityError):
                insert(db, "a1")
            self.assertEqual(db.execute("PRAGMA integrity_check").fetchall(), [("ok",)])
            columns = {row[1] for row in db.execute("PRAGMA table_info(demo_records)")}
            self.assertEqual(columns, {"Id", "Code", "Name", "Remark", "Version",
                                       "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy"})

    def test_online_backup_includes_committed_wal_without_modifying_live_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "live.db"
            writer = sqlite3.connect(str(path))
            try:
                writer.execute("PRAGMA journal_mode=WAL")
                writer.execute("PRAGMA wal_autocheckpoint=0")
                writer.executescript(DDL)
                insert(writer, "IN-WAL")
                self.assertGreater(pathlib.Path(str(path) + "-wal").stat().st_size, 0)
                with managed_connect(str(path) + ".copy") as backup:
                    writer.backup(backup)
                    self.assertEqual(backup.execute("SELECT Code FROM demo_records").fetchall(), [("IN-WAL",)])
                    self.assertEqual(backup.execute("PRAGMA integrity_check").fetchall(), [("ok",)])
                self.assertEqual(writer.execute("SELECT Code FROM demo_records").fetchall(), [("IN-WAL",)])
                self.assertEqual(writer.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()[0], 0)
            finally:
                writer.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
