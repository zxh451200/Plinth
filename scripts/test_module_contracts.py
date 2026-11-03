"""Fixtures exercise architecture diagnostics, not compiled C# semantics."""
from pathlib import Path
import tempfile
import unittest

from module_contracts import check_migration_entities, check_modules, service_errors


class ModuleContractTests(unittest.TestCase):
    def scan(self, files):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name, content in files.items():
                path = root / "src/Plinth.App/Modules" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            return check_modules(root)

    def test_own_module_and_shared_services_are_allowed(self):
        self.assertEqual([], self.scan({"One/Services/Reader.cs": """
            using Plinth.App.Modules.One.Data;
            using Plinth.App.Services;
            // using Plinth.App.Modules.Two.Data;
            class Reader { string example = "Plinth.App.Modules.Two.Data"; }
        """}))

    def test_alias_and_fully_qualified_cross_module_references_are_rejected(self):
        for source in ("using Other = Plinth.App.Modules.Two.Data;",
                       "class A { Plinth.App.Modules.Two.Data.Record value; }"):
            with self.subTest(source=source):
                errors = self.scan({"One/Data/A.cs": source})
                self.assertTrue(any("cross-module reference to Two" in error for error in errors))

    def test_xaml_cross_module_reference_is_rejected_but_comments_are_ignored(self):
        self.assertEqual([], self.scan({"One/Views/A.axaml": '<!-- using:Plinth.App.Modules.Two.Views -->'}))
        self.assertTrue(self.scan({"One/Views/A.axaml":
                                  '<UserControl xmlns:other="using:Plinth.App.Modules.Two.Views" />'}))

    def test_entity_needs_explicit_configuration_key_and_version(self):
        entity = "public sealed class Record : AuditableEntity {}"
        mapping = """
            class Mapping : IEntityTypeConfiguration<Record> {
                public void Configure(EntityTypeBuilder<Record> builder) {
                    builder.HasKey(record => record.Id);
                    builder.Property(record => record.Version).IsConcurrencyToken();
                }
            }
        """
        self.assertEqual([], self.scan({"One/Data/Record.cs": entity + mapping}))
        self.assertIn("IEntityTypeConfiguration", self.scan({"One/Data/Record.cs": entity})[0])
        for fragment in ("builder.HasKey(record => record.Id);",
                         "builder.Property(record => record.Version).IsConcurrencyToken();"):
            with self.subTest(fragment=fragment):
                self.assertTrue(self.scan({"One/Data/Record.cs": entity + mapping.replace(fragment, "")}))

    def test_guarded_inline_write_and_private_helper_are_allowed(self):
        for action in ("await db.SaveChangesAsync(ct);", "await SaveCoreAsync(ct);"):
            source = """
                public class Service(SensitiveOperationService operations) {
                    public Task SaveAsync() => operations.ExecuteAsync("one:save", "One", "save",
                        async ct => { ACTION return true; });
                    private Task SaveCoreAsync(CancellationToken ct) { return db.SaveChangesAsync(ct); }
                }
            """.replace("ACTION", action)
            self.assertEqual([], service_errors(source, "One"))

    def test_public_helper_bypass_is_rejected(self):
        source = """
            class Service(SensitiveOperationService operations) {
                public Task SaveAsync() => operations.ExecuteAsync("one:save", "One", "save",
                    async ct => { return await SaveCoreAsync(ct); });
                public Task SaveCoreAsync(CancellationToken ct) { return db.SaveChangesAsync(ct); }
            }
        """
        self.assertTrue(any("SaveCoreAsync reaches a write" in error for _, error in service_errors(source, "One")))

    def test_confirmed_entry_keeps_the_same_authorization_boundaries(self):
        guarded = """
            class Service(SensitiveOperationService operations) {
                public Task SaveAsync() => operations.ExecuteConfirmedAsync("one:save", "One", "save",
                    async ct => { return await db.SaveChangesAsync(ct); });
            }
        """
        self.assertEqual([], service_errors(guarded, "One"))
        for source in (
            guarded.replace('"one:save"', '"two:save"'),
            guarded.replace('"one:save"', 'permission'),
            guarded.replace("SensitiveOperationService", "OtherExecutor"),
            guarded.replace("return await db.SaveChangesAsync(ct);", "return await SaveCoreAsync(ct);")
            .replace("\n            }", """
                public Task SaveCoreAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
            }"""),
        ):
            with self.subTest(source=source):
                self.assertTrue(service_errors(source, "One"))
        sibling = """
            class Service(SensitiveOperationService operations) {
                public async Task SaveAsync() {
                    await operations.ExecuteConfirmedAsync("one:save", "One", "save", async ct => true);
                    await db.SaveChangesAsync();
                }
            }
        """
        self.assertTrue(service_errors(sibling, "One"))

    def test_sibling_write_is_not_authorized_by_an_unrelated_guard(self):
        source = """
            class Service(SensitiveOperationService operations) {
                public async Task SaveAsync() {
                    await operations.ExecuteAsync("one:save", "One", "save", async ct => true);
                    await db.SaveChangesAsync();
                }
            }
        """
        self.assertTrue(service_errors(source, "One"))

    def test_unprotected_helper_chain_is_rejected(self):
        source = """
            class Service {
                public Task ImportAsync() => ParseAsync();
                private Task ParseAsync() => PersistAsync();
                private Task PersistAsync() => db.SaveChangesAsync();
            }
        """
        self.assertTrue(service_errors(source, "One"))

    def test_comments_and_string_literals_do_not_create_write_calls(self):
        self.assertEqual([], service_errors("""
            class Service {
                public string Read() {
                    // db.SaveChangesAsync();
                    return "db.ExecuteDeleteAsync()";
                }
            }
        """, "One"))

    def test_foreign_or_dynamic_permission_is_rejected(self):
        for permission in ('"two:save"', "permission"):
            source = """
                class Service(AuthorizedOperation operations) {
                    public Task SaveAsync() => operations.ExecuteAsync(PERMISSION, "One", "save",
                        async ct => { return await db.SaveChangesAsync(ct); });
                }
            """.replace("PERMISSION", permission)
            self.assertTrue(any("permission must" in error for _, error in service_errors(source, "One")))

    def test_execute_async_on_an_unrelated_receiver_does_not_authorize(self):
        source = """
            class Service(OtherExecutor operations) {
                public Task SaveAsync() => operations.ExecuteAsync("one:save", "One", "save",
                    async ct => { return await db.SaveChangesAsync(ct); });
            }
        """
        self.assertTrue(service_errors(source, "One"))

    def test_repository_diagnostics_include_path_and_line(self):
        errors = self.scan({"One/Services/Writer.cs": """
            class Writer(IDbContextFactory<AppDbContext> factory) {
                public Task SaveAsync() => db.SaveChangesAsync();
            }
        """})
        self.assertTrue(any("One/Services/Writer.cs:3:" in error for error in errors))

    def test_portable_linq_and_like_are_allowed(self):
        self.assertEqual([], self.scan({"One/Services/Reader.cs": """
            class Reader {
                object Read() => db.Set<Record>().AsNoTracking()
                    .Where(r => EF.Functions.Like(r.Name, pattern))
                    .OrderBy(r => r.CodeKey).ThenBy(r => r.Id).Take(50);
            }
        """}))

    def test_provider_types_and_aliases_are_rejected_with_locations(self):
        for source in ("using Database = Microsoft.Data.Sqlite;",
                       "class A { SqliteConnection connection; }",
                       "class A { void Read() { try {} catch (SqliteException) {} } }"):
            with self.subTest(source=source):
                errors = self.scan({"One/Data/Reader.cs": "\n" + source})
                self.assertTrue(any("One/Data/Reader.cs:2:" in error and "provider dependency" in error
                                    for error in errors))

    def test_provider_mapping_and_raw_sql_calls_are_rejected(self):
        for call in ('options.UseSqlite(connection)', 'options.UseNpgsql(connection)',
                     'options.UseMySql(connection)', 'options.UseSqlServer(connection)',
                     'property.UseCollation("NOCASE")', 'property.HasColumnType("TEXT")',
                     'query.FromSqlRaw("SELECT 1")', 'db.Database.ExecuteSqlRawAsync("SELECT 1")',
                     'db.Database.SqlQueryRaw<int>("SELECT 1")',
                     'query.FromSqlInterpolated($"SELECT {value}")'):
            with self.subTest(call=call):
                self.assertTrue(any("nonportable EF call" in error for error in
                                    self.scan({"One/Data/Mapping.cs": "class A { void Read() { " + call + "; } }"})))

    def test_portability_examples_in_comments_and_literals_are_ignored(self):
        self.assertEqual([], self.scan({"One/Data/Reader.cs": '''
            // using Microsoft.Data.Sqlite;
            /* builder.UseCollation("NOCASE"); */
            class A {
                string example = "SqliteConnection query.FromSqlRaw()";
                string verbatim = @"property.HasColumnType(""TEXT"")";
                string raw = """options.UseNpgsql();""";
            }
        '''}))

    def test_module_subfolder_cannot_opt_out_of_portability_checks(self):
        for folder in ("Infrastructure", "Migrations", "Tests"):
            with self.subTest(folder=folder):
                self.assertTrue(self.scan({f"One/{folder}/Reader.cs": "using Microsoft.Data.Sqlite;"}))

    def test_host_infrastructure_and_migrations_are_outside_module_scan(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for relative in ("Data/Reader.cs", "Migrations/Old.cs", "Services/BackupService.cs"):
                path = root / "src/Plinth.App" / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("using Microsoft.Data.Sqlite; class A { void Read() { db.UseSqlite(); } }")
            self.assertEqual([], check_modules(root))

    def test_entity_requires_both_snapshot_and_migration_target_declarations(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            app = root / "src/Plinth.App"
            entity = app / "Modules/One/Data/Record.cs"
            entity.parent.mkdir(parents=True)
            entity.write_text("namespace Plinth.App.Modules.One.Data; class Record : AuditableEntity {}")
            self.assertEqual(2, len(check_migration_entities(root)))
            migrations = app / "Migrations"
            migrations.mkdir()
            declaration = 'modelBuilder.Entity("Plinth.App.Modules.One.Data.Record", entity => {});'
            (migrations / "AppDbContextModelSnapshot.cs").write_text(declaration)
            self.assertIn("migration target", check_migration_entities(root)[0])
            (migrations / "Initial.cs").write_text(declaration)
            self.assertEqual([], check_migration_entities(root))


if __name__ == "__main__":
    unittest.main()
