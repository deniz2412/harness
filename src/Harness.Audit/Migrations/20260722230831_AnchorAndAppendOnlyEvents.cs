using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harness.Audit.Migrations
{
    /// <inheritdoc />
    public partial class AnchorAndAppendOnlyEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- Chain-head anchor (STRIDE M-1): records where each run's chain must terminate, so
            // deleting the tail (or all) of a run's events is detectable instead of leaving a
            // shorter, internally consistent chain. Written by AuditEmitter in step with each event.
            migrationBuilder.AddColumn<string>(
                name: "HeadHash",
                table: "Runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HeadSeq",
                table: "Runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // --- F4: make the Events table append-only AT THE DATABASE, not just by convention.
            //
            // The app only ever INSERTs events (AuditEmitter) and SELECTs them (ChainVerifier, the
            // /events endpoint, ReadNodeOutputsAsync); nothing in src/ UPDATEs or DELETEs an Events
            // row — only Runs and Approvals are updated — so this trigger cannot break a legitimate
            // code path. It raises on any UPDATE or DELETE against Events.
            //
            // HONEST LIMIT: the app connects as the table OWNER, and a Postgres owner or superuser
            // can bypass a trigger (DROP TRIGGER, or SET session_replication_role = 'replica'). So
            // this stops ACCIDENTAL mutation and any NON-owner role, but NOT a malicious owner. Full
            // resistance needs a separate least-privilege runtime role holding only INSERT/SELECT on
            // Events (and no UPDATE on the Runs head anchor) — graduation / infra work
            // (threat-model F4, M4), not something a trigger alone delivers.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION harness_events_append_only()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION
                        'Events is append-only: % on "Events" is not permitted', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER events_append_only
                BEFORE UPDATE OR DELETE ON "Events"
                FOR EACH ROW EXECUTE FUNCTION harness_events_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS events_append_only ON \"Events\";");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS harness_events_append_only();");

            migrationBuilder.DropColumn(
                name: "HeadHash",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "HeadSeq",
                table: "Runs");
        }
    }
}
