/* ═══════════════════════════════════════════════════════════════════════════
   23 — THE ORDER DESK LOSES THE CATALOGUE AND PURCHASES, AND SALES TAX GOES TO 0
   ═══════════════════════════════════════════════════════════════════════════

   Two things the owner asked for on 21 September, in one file because they
   ship with one build.

   ─────────────────── 1. WHAT THE ORDER DEPARTMENT MAY DO ────────────────────

   "in order department panel in stock section he cannot access Items,
    categories, brands and cannot create any Items, categories, brands. however
    he can have access to see Stock in Hand, make and see Transfers and can do
    Stock Correction and can see stock history."

   "order department panel cannot access any purchases page means from aside
    remove completely Purchases section and order department can never see
    purchases"

   THE ORDER DESK HELD FOUR RIGHTS THAT CONTRADICT THAT:

       products.manage    Add & edit items        -> Items, Categories, Brands
       purchases.view     See purchases           -> the whole Purchases group
       receipts.stock     Receive stock           -> "Stock Received", under it
       (and stock.view, which they KEEP -- see below)

   The first three are taken from them.

   stock.view IS THE PROBLEM. It is what opens Stock in Hand and Stock History,
   both of which they keep -- but the Items list hung off the same right. So a
   right of its own is added for the catalogue:

       products.view      See items          (NEW)

   granted to everybody who could open Items before EXCEPT the order desk:
   the Super Admin, the Accountant and the Warehouse Keeper. Nobody else
   changes. Setup > Roles shows it as a tick, so the owner can hand it to the
   order desk again later if they ever want to.

   KEPT, exactly as they were: stock.view, stock.transfer, stock.correct.

   NOBODY HAS TO SIGN IN AGAIN, and the reason is worth knowing because it is
   not obvious. Rights reach the web app's MENU from the database on every page
   load, so the order desk's sidebar changes the moment they refresh. They reach
   the API's own checks through the sign-in token, which lives eight hours --
   so the API refuses the order desk by ROLE on every purchases, item, category
   and brand endpoint (InventoryController, PurchasesController), which takes
   effect the instant the new build is deployed and cannot be defeated by a token
   issued yesterday. That was tested with tokens minted before this migration: an
   order-desk token still carrying the old rights is refused on all of it, and an
   accountant's old token, which has no products.view in it, still reaches Items.

   ───────────────────────── 2. SALES TAX: 18% -> 0% ──────────────────────────

   "Sales tax rate in many pages is set to 18% however i want it must be 0%."

   THERE IS NO SETTING FOR IT. Each product carries its own rate
   ("Product"."TaxRatePercent") and every order, invoice and counter screen
   starts from it. The live catalogue had

       36 products at 18%  (34 active)
        2 products at 10%

   -- which is where every "18%" came from. All 38 go to 0.

   THE TWO AT 10% ARE INCLUDED, and that is a reading of the request: the aim
   is a sales tax of zero, and leaving two items at 10% would put a tax line on
   an order only when those two are in it, which is the kind of thing nobody
   spots until a customer asks. If they were meant to stay, the undo below
   restores them by id.

   NOTHING ALREADY ISSUED IS TOUCHED. Orders, invoices and their lines keep the
   rate they were made at -- a document that has been sent to a shop is a
   record, and rewriting what it charged would make the ledger disagree with the
   paper. Only the catalogue, which decides what NEW documents start at.

   ─────────────────────────────── AN UNDO ────────────────────────────────────

   Both halves are reversible, and the figures the second half needs are
   written into the file below rather than left to memory:

     -- tax back to what it was (36 x 18, 2 x 10):
        see 23_undo_values.txt beside this file.
     -- the order desk's rights back:
        INSERT INTO "RolePermission" ("RoleId","PermissionId")
        SELECT 3, "PermissionId" FROM "Permission"
         WHERE "PermissionKey" IN ('products.manage','purchases.view','receipts.stock');

   ═══════════════════════════════════════════════════════════════════════════ */

BEGIN;

/* ── 1a. the new right ──────────────────────────────────────────────────── */
INSERT INTO "Permission" ("PermissionKey", "Label", "GroupName")
SELECT 'products.view', 'See items', 'Stock'
 WHERE NOT EXISTS (SELECT 1 FROM "Permission" WHERE "PermissionKey" = 'products.view');

/* ── 1b. who gets it: everybody who could open Items, except the order desk ─ */
INSERT INTO "RolePermission" ("RoleId", "PermissionId")
SELECT r."RoleId", p."PermissionId"
  FROM "Role" r, "Permission" p
 WHERE r."RoleKey" IN ('super-admin', 'accountant', 'warehouse-keeper')
   AND p."PermissionKey" = 'products.view'
   AND NOT EXISTS (SELECT 1 FROM "RolePermission" x
                    WHERE x."RoleId" = r."RoleId" AND x."PermissionId" = p."PermissionId");

/* ── 1c. what the order desk loses ──────────────────────────────────────── */
DELETE FROM "RolePermission" rp
 USING "Role" r, "Permission" p
 WHERE rp."RoleId" = r."RoleId"
   AND rp."PermissionId" = p."PermissionId"
   AND r."RoleKey" = 'order-dept'
   AND p."PermissionKey" IN ('products.manage', 'purchases.view', 'receipts.stock');

/* ── 2. sales tax to zero on the catalogue ──────────────────────────────── */
UPDATE "Product" SET "TaxRatePercent" = 0 WHERE "TaxRatePercent" <> 0;

/* ── what it did, in the output ─────────────────────────────────────────── */
DO $$
DECLARE
    n_view      int;
    n_desk      int;
    n_taxed     int;
    n_desk_has  text;
BEGIN
    SELECT count(*) INTO n_view
      FROM "RolePermission" rp JOIN "Permission" p USING ("PermissionId")
     WHERE p."PermissionKey" = 'products.view';

    SELECT count(*) INTO n_desk
      FROM "RolePermission" rp
      JOIN "Role" r USING ("RoleId")
      JOIN "Permission" p USING ("PermissionId")
     WHERE r."RoleKey" = 'order-dept'
       AND p."PermissionKey" IN ('products.manage', 'purchases.view', 'receipts.stock', 'products.view');

    SELECT string_agg(p."PermissionKey", ', ' ORDER BY p."PermissionKey") INTO n_desk_has
      FROM "RolePermission" rp
      JOIN "Role" r USING ("RoleId")
      JOIN "Permission" p USING ("PermissionId")
     WHERE r."RoleKey" = 'order-dept' AND p."PermissionKey" ~ '^(stock|products|purchases|receipts)';

    SELECT count(*) INTO n_taxed FROM "Product" WHERE "TaxRatePercent" <> 0;

    RAISE NOTICE 'products.view held by % roles (want 3)', n_view;
    RAISE NOTICE 'order desk still holding a removed or withheld right: % (want 0)', n_desk;
    RAISE NOTICE 'order desk stock/product rights now: %', n_desk_has;
    RAISE NOTICE 'products still carrying a tax rate: % (want 0)', n_taxed;

    IF n_view <> 3 OR n_desk <> 0 OR n_taxed <> 0 THEN
        RAISE EXCEPTION 'migration 23 did not land as intended -- rolled back';
    END IF;
END $$;

COMMIT;
