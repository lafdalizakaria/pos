# Avancement

## Phase 1 — Fondations ✅ (en attente de validation)

### Livré

**Solution et outillage**
- Solution `Newrest.Pos.slnx` (.NET 10 LTS), 10 projets `src/` + 2 projets de tests, gestion centralisée des versions
  NuGet (`Directory.Packages.props`), analyse .NET + style imposés au build, avertissements bloquants en CI.
- La caisse WPF (`net10.0-windows`) compile aussi sous Linux (`EnableWindowsTargeting`) : toute la solution est
  construite en CI sur `ubuntu-latest`.
- CI GitHub Actions (`.github/workflows/ci.yml`) : restore, `dotnet format --verify-no-changes`, build Release,
  vérification que le modèle EF correspond aux migrations, tests unitaires avec couverture, tests d'intégration
  SQL Server (Testcontainers), seuil de couverture Domain ≥ 80 %. CodeQL (C#) dans `codeql.yml`.
- `.env.example`, `deploy/docker-compose.dev.yml` (SQL Server de dev), `deploy/sql/least-privilege.sql`.

**Domain** (`Newrest.Pos.Domain`, sans dépendance)
- Organisation : `Company`, `Site`, `PointOfSale`, `Register`, `Operator` (PIN PBKDF2-SHA256 600 k itérations,
  verrouillage après échecs).
- Catalogue : `Category`, `Article` (TVA, photos de référence, description visuelle, subventionnable), `PriceList` +
  `PriceOverride` (société / site / point de vente, validité) et `PriceResolver`.
- Menus : `DailyMenu` / `DailyMenuItem` (disponibilité, publication, copie vers une autre date).
- Clients : `ClientCompany`, `Contract` (points de vente acceptés), `SubsidyRule`, `Diner`, `Badge` (perdu → remplacé,
  le solde reste au compte).
- Ledger : `Account` (prépayé / postpayé / mixte, découvert, cache), `AccountMovement` immuable, contre-passation.
- Subvention : `SubsidyCalculator` (%, forfait, plafond repas, plafond jour multi-points de vente, nb repas/jour,
  sélection de règle par catégorie). Exemple de référence vérifié : 45 MAD, 50 % plafonné 20 MAD/jour → 20 / 25.
- Ventes : `Ticket` scellé (ventes, avoirs totaux), `TicketLine`, `Payment` (espèces + rendu, TPE + référence
  d'autorisation, compte, mixte), numérotation `TicketNumber`, chaîne `TicketHasher` / `TicketChainVerifier`,
  `CashSession`, `ZReport` + `ZReportCalculator` (moyens de paiement, TVA, subventions, recharges hors CA, écart).
- Vision : `RecognitionConfidencePolicy` (0,60 / 0,90), `RecognitionLog`. Audit : `AuditLog`.

**Infrastructure**
- `PosDbContext` (schéma `pos`), migration `InitialCreate`, index uniques métier (séquence par caisse, idempotence,
  contre-passation unique, une session ouverte par caisse…), contraintes CHECK, `rowversion` sur les données de
  référence (curseur de sync), aucune cascade sur les données métier.
- `PosSaveChangesInterceptor` : horodatages techniques, refus de toute modification/suppression d'un enregistrement immuable.
- `AccountLedger` (`IAccountLedger`) : débit atomique sous `UPDLOCK, ROWLOCK`, idempotence, détection de conflit de clé,
  contre-passation unique, rejeu hors ligne marqué, contrôle de dérive du cache.
- `DemoDataSeeder` : 2 sociétés, 3 sites, 5 points de vente, 6 caisses, 5 opérateurs, 30 articles (dont couscous
  viande `CSC-VND` et couscous poulet `CSC-PLT`), 2 listes de prix, menus du jour publiés, 2 clients fictifs, contrats,
  3 règles de subvention, 8 convives avec badges et comptes approvisionnés. Idempotent.
- `Newrest.Pos.Migrator` : applique les migrations, `--seed-demo` (refusé en Production), `--list`, `--script`.
- Squelettes hébergés : API (`/health/live`, `/health/ready`, `/api/v1/ping`, OpenAPI, Serilog, OpenTelemetry),
  back-office Blazor Server en français, caisse WPF MVVM (fenêtre d'accueil).

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| `dotnet build` solution complète, `CI=true` (avertissements = erreurs) | ✅ 0 avertissement |
| `dotnet format --verify-no-changes` | ✅ |
| Tests unitaires Domain | ✅ 96/96 |
| Tests d'intégration SQL Server 2022 (Testcontainers) | ✅ 13/13 dont **50 débits concurrents** (25 acceptés exactement, solde = −découvert, cache cohérent) et 20 soumissions simultanées d'une même clé (1 seul mouvement) |
| Couverture lignes Domain | ✅ 90,1 % en Release (seuil 80 %) |
| Modèle EF ↔ migrations | ✅ aucune modification en attente |
| Migrateur + seed sur SQL Server réel, re-seed idempotent, script SQL idempotent | ✅ |
| API et back-office démarrés : `/health/ready` = Healthy, `/api/v1/ping`, `/openapi/v1.json`, page d'accueil FR | ✅ |
| `least-privilege.sql` sur SQL Server réel : `pos_api` peut mettre à jour `Accounts`, reçoit un refus `UPDATE`/`DELETE` sur `AccountMovements`, `Tickets`, `ZReportLines`, et n'a aucun droit DDL | ✅ |
| Workflows GitHub Actions | ⚠️ écrits mais pas encore exécutés sur GitHub (pas de push effectué) |

### Décisions prises
Voir `docs/assumptions.md` (A1–A30) et `docs/architecture.md`. Points saillants :
- Verrou pessimiste `UPDLOCK, ROWLOCK` (plutôt que retry optimiste) pour le débit : plus simple à raisonner, sérialise
  uniquement le compte concerné ; `rowversion` conservé en filet de sécurité.
- La subvention est portée par le ticket ; seul le reste à charge est débité du compte.
- Une vente hors ligne déjà réalisée est acceptée à la synchronisation même au-delà du découvert, mais marquée
  (`ExceededOverdraft`) — **à valider par le métier/finance** (A18).
- Séquence de tickets partagée ventes/avoirs ; avoir total uniquement en phase 1 (A6, A7).
- FluentAssertions figé en 7.x pour éviter la licence commerciale de la v8 (A29).

### Reste à faire / points ouverts
- Questions fiscales listées dans `docs/compliance.md` (mentions obligatoires, taux de TVA, traitement de la subvention,
  conservation, éventuelle certification du logiciel de caisse).
- `Newrest.Pos.Application` ne contient encore que l'interface du ledger : les cas d'usage arrivent en phase 2 ;
  le seuil de couverture 80 % lui sera appliqué dès qu'il contiendra du code.
- Job CI Python ajouté en phase 4 avec le service vision.

## Phase 2 — API centrale + back-office (à venir)
Authentification (caisses : clé par appareil / certificat ; utilisateurs : Entra ID + rôles, JWT courts), endpoints
catalogue, prix, menus, clients, convives (import CSV/Excel), badges, comptes (recharge, correction, remboursement),
audit, cloisonnement par société/site, écrans Blazor correspondants, vérification de la chaîne dans le back-office.
