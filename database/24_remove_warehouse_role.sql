/* ═══════════════════════════════════════════════════════════════════════════
   24 — THE WAREHOUSE ROLE IS DELETED; WAREHOUSES STAY AS LOCATIONS
   ═══════════════════════════════════════════════════════════════════════════

   The owner, 22 September:

     "completely delete role of Warehouse and all pages of warehouse panel,
      there is no need of warehouse role, however warehouse would be used as a
      location for transfers and stock will keep at warehouse so that data
      will remain in system but warehouse separately panel is not required."

   TWO DIFFERENT THINGS NAMED "WAREHOUSE" IN THIS SYSTEM, AND ONLY ONE GOES:

     "warehouse-keeper"   a ROLE. A job title somebody signs in as. Reads
                          orders.warehouse and stock.view/transfer, and never
                          moved an order in the chain (OrderWorkflow.cs already
                          said so: "/warehouse is a picking list to read, not a
                          queue to click through"). This is what is deleted.

     "Karachi Warehouse"  a LOCATION, kind "warehouse" ("LocationKind" id 1).
                          Where stock physically sits, what a transfer moves
                          goods to or from, what dispatch can send out of. This
                          is UNTOUCHED. "Location" and "LocationKind" are not
                          touched by this migration -- the owner's own words,
                          "warehouse would be used as a location ... so that
                          data will remain in system", are exactly this: the
                          two warehouse locations and every stock row, transfer
                          and movement against them are left exactly as they
                          were.

   ONE ACCOUNT HELD THE ROLE: "muhammadtalhabinsuhail@gmail.com" (Talha),
   UserId 41, PrimaryLocationId 1 (Karachi Warehouse) -- his own account for
   testing the role and its panel, not a real warehouse worker's. Moved to
   ORDER DEPARTMENT rather than left with no role: the order desk is the role
   that absorbed the warehouse's physical-stock work in this chain (Invoiced ->
   AtOrderDept -> Dispatched, all worked by order-dept), so it is the nearest
   real job to what the account was testing. Location moved to LocationId 2,
   Karachi Order Department -- order-dept is place-bound to a "department"
   location, and 1 is a "warehouse", which order-dept may not hold
   (AdminUsersController.ValidatePlace). Talha or the owner can change either
   at Setup > Users in two clicks if this was not the intended landing spot.

   THE PERMISSION "orders.warehouse" (Prepare and send order stock) goes with
   the role. It was held only by warehouse-keeper and, trivially, by
   super-admin (who holds every permission by definition); nothing else reads
   it once GetWarehouseQueue is removed from the API (this session's code
   change).

   NOTHING ELSE REFERENCES ROLE 9: checked every foreign key into "Role"
   (User.RoleId, RolePermission.RoleId, DeliveryChannel.ConfirmedByRoleId) --
   only User and RolePermission had rows for it, both handled below.

   ─────────────────────────────────────────────────────────────────────────
   PURCHASE RETURNS -- the other half of the same request, NO DATABASE CHANGE

   "completely remove purchase return scenario in the system however sales
    return will remain exists in the system."

   This is a CODE-ONLY change (PurchasesController lost the three endpoints
   that create and browse one; the three frontend pages and the sidebar link
   are deleted). The tables "PurchaseReturn" and "PurchaseReturnItem" are NOT
   touched here, on purpose: nine real returns already exist (the earliest
   from 28 April 2026), each with a journal entry against it -- that is
   accounting history, not test data, and dropping it would make the ledger
   disagree with what was actually posted. ProductHistoryController still
   folds them into a product's stock ledger and DocumentsController can still
   print or share the PDF of one that already exists; only making a NEW one,
   and the dedicated screen to browse them, are gone. If the owner wants the
   nine historical rows purged as well, say so and it is a separate, explicit
   DROP -- not bundled into a role-and-panel migration.
   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

/* ── 1. move the one account off the role, to a role that still exists ───── */
UPDATE "User"
   SET "RoleId" = (SELECT "RoleId" FROM "Role" WHERE "RoleKey" = 'order-dept'),
       "PrimaryLocationId" = 2   -- Karachi Order Department
 WHERE "RoleId" = (SELECT "RoleId" FROM "Role" WHERE "RoleKey" = 'warehouse-keeper');

/* ── 2. drop every grant that named the role, or the permission it alone used ─ */
DELETE FROM "RolePermission"
 WHERE "RoleId" = (SELECT "RoleId" FROM "Role" WHERE "RoleKey" = 'warehouse-keeper')
    OR "PermissionId" = (SELECT "PermissionId" FROM "Permission" WHERE "PermissionKey" = 'orders.warehouse');

/* ── 3. the permission ─────────────────────────────────────────────────── */
DELETE FROM "Permission" WHERE "PermissionKey" = 'orders.warehouse';

/* ── 4. the role itself ───────────────────────────────────────────────── */
DELETE FROM "Role" WHERE "RoleKey" = 'warehouse-keeper';

/* ── what it did ──────────────────────────────────────────────────────── */
DO $$
DECLARE
    n_role  int;
    n_perm  int;
    n_users int;
    talha   text;
BEGIN
    SELECT count(*) INTO n_role FROM "Role" WHERE "RoleKey" = 'warehouse-keeper';
    SELECT count(*) INTO n_perm FROM "Permission" WHERE "PermissionKey" = 'orders.warehouse';
    SELECT count(*) INTO n_users FROM "User" u JOIN "Role" r USING ("RoleId") WHERE r."RoleKey" = 'warehouse-keeper';
    SELECT r."RoleKey" || ', location ' || u."PrimaryLocationId" INTO talha
      FROM "User" u JOIN "Role" r USING ("RoleId") WHERE u."Email" = 'muhammadtalhabinsuhail@gmail.com';

    RAISE NOTICE 'warehouse-keeper role rows left: % (want 0)', n_role;
    RAISE NOTICE 'orders.warehouse permission rows left: % (want 0)', n_perm;
    RAISE NOTICE 'accounts still on the deleted role: % (want 0)', n_users;
    RAISE NOTICE 'Talha is now: %', talha;

    IF n_role <> 0 OR n_perm <> 0 OR n_users <> 0 THEN
        RAISE EXCEPTION 'migration 24 did not land as intended -- rolled back';
    END IF;
END $$;

COMMIT;
