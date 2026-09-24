/* ═══════════════════════════════════════════════════════════════════════════
   25 — THE ORDER DESK LANDS ON PACKING, LOSES CLAIMS, AND A LINE REMEMBERS
        WHAT WAS ACTUALLY SENT
   ═══════════════════════════════════════════════════════════════════════════

   The owner, 23 September, about the Order Department panel only -- nothing
   here touches any other role.

   ─────────────────── 1. SIGNING IN OPENS PACKING, NOT THE DASHBOARD ─────────

   "Whenever a user with the Order Department role logs in, the first page
    they should see is the Packing page ... the Dashboard page should not
    render post-login."

   "Role"."HomePath" already exists and is exactly what the login screen reads
   (app/login/page.tsx: `router.replace(... user.homePath ...)`) -- this needed
   no code at all, on either side, once this one row changes. Setup > Roles
   also edits this column by hand, so the owner can move it back in two clicks.

   ────────────────────────── 2. CLAIMS IS GONE FOR THEM ──────────────────────

   "remove Claims from it. There is no need for a Claims page in the Order
    Department panel."

   Order Department loses claims.view, claims.receive and claims.settle.
   Nobody else's grant changes -- the Accountant and the Super Admin still
   have all three, because ClaimsController is guarded by BOTH the BackOffice
   policy AND (this session's code change) an explicit role list that no
   longer names order-dept, and proxy.ts's /claims rule drops order-dept from
   its roles array. Three places said "order-dept may see Claims"; now none of
   them do.

   ───────────────────── 3. A LINE REMEMBERS WHAT WAS SENT ────────────────────

   The Packing screen lets the order desk send LESS than a line asked for
   ("The Order Department can reduce the requested quantity but cannot
   increase it") and the invoice's own PDF has to say so afterwards. Nothing
   before this recorded the difference -- "Quantity" was always ONE number,
   what was ordered, and dispatching never touched it.

   "SalesOrderItem"."DispatchedQty" is that second number: NULL until the line
   is actually dispatched, then the quantity that truly left the shelf. A full
   dispatch (the ordinary case, no reduction) sets it equal to "Quantity" --
   this is not only for shortages, it is the honest record of "what actually
   went out" for every dispatch from here on. Existing dispatched orders are
   left NULL: nobody asked for their history rewritten, and NULL already reads
   correctly as "dispatched before this column existed" rather than as zero.

   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

/* ── 1. sign-in lands on Packing ─────────────────────────────────────────── */
UPDATE "Role" SET "HomePath" = '/packing' WHERE "RoleKey" = 'order-dept';

/* ── 2. Claims taken from the order desk ─────────────────────────────────── */
DELETE FROM "RolePermission" rp
 USING "Role" r, "Permission" p
 WHERE rp."RoleId" = r."RoleId"
   AND rp."PermissionId" = p."PermissionId"
   AND r."RoleKey" = 'order-dept'
   AND p."PermissionKey" IN ('claims.view', 'claims.receive', 'claims.settle');

/* ── 3. a line remembers what actually left ──────────────────────────────── */
ALTER TABLE "SalesOrderItem" ADD COLUMN IF NOT EXISTS "DispatchedQty" INT NULL;
COMMENT ON COLUMN "SalesOrderItem"."DispatchedQty" IS
  'What actually left the shelf for this line, set when the order is dispatched. NULL for a line never dispatched (including every one dispatched before this column existed). Never more than "Quantity" -- the order desk may reduce what a salesperson asked for, never raise it.';

/* ── what it did ──────────────────────────────────────────────────────────── */
DO $$
DECLARE
    n_home  int;
    n_perm  int;
    n_col   int;
BEGIN
    SELECT count(*) INTO n_home FROM "Role" WHERE "RoleKey" = 'order-dept' AND "HomePath" = '/packing';
    SELECT count(*) INTO n_perm
      FROM "RolePermission" rp
      JOIN "Role" r USING ("RoleId")
      JOIN "Permission" p USING ("PermissionId")
     WHERE r."RoleKey" = 'order-dept' AND p."PermissionKey" LIKE 'claims%';
    SELECT count(*) INTO n_col FROM information_schema.columns
     WHERE table_name = 'SalesOrderItem' AND column_name = 'DispatchedQty';

    RAISE NOTICE 'order-dept HomePath is /packing: % (want 1)', n_home;
    RAISE NOTICE 'order-dept still holding a claims permission: % (want 0)', n_perm;
    RAISE NOTICE 'SalesOrderItem.DispatchedQty exists: % (want 1)', n_col;

    IF n_home <> 1 OR n_perm <> 0 OR n_col <> 1 THEN
        RAISE EXCEPTION 'migration 25 did not land as intended -- rolled back';
    END IF;
END $$;

COMMIT;
