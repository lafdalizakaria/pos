# Avancement

## Phase 1 — Fondations ✅ (validée)

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

## Phase 2 — API centrale + back-office ✅ (en attente de validation)

### Livré

**Cas d'usage partagés** (`Newrest.Pos.Application`, utilisés par l'API et le back-office)
- `AccessControl` : rôles Entra ID (`Pos.Admin`, `Pos.Manager`, `Pos.Accountant`, `Pos.Viewer`) + périmètres société/site
  (`UserAccessScopes`, nouvelle table). `AuditTrail` : audit dans la même transaction que la modification.
- Organisation : sociétés, sites, points de vente, caisses (préfixe immuable, **clé d'appareil** affichée une fois,
  rotation, révocation), opérateurs (PIN haché, rôles, déverrouillage), droits d'accès.
- Catalogue : catégories, articles (code immuable, prix/TVA audités), **photos de référence** (JPEG/PNG/WebP, 5 Mo,
  `IFileStorage` sur disque), listes de prix par société / site / point de vente, prix effectifs d'un point de vente.
- Menus du jour : création avec prix résolus, ajout/retrait/prix/disponibilité, publication, **copie vers d'autres dates**
  et **copie de semaine type**.
- Clients : clients B2B, contrats (points de vente acceptés), règles de subvention (création, clôture — jamais modifiées).
- Convives : fiche, badges (attribution, **badge perdu → remplacement**, blocage), recherche par badge, **import CSV / Excel**
  (simulation, rapport d'erreurs par ligne, aucune modification partielle d'une ligne rejetée).
- Comptes : liste, solde, paramètres (type, découvert), historique paginé, **recharge** (idempotente), **correction**,
  **contre-passation**, **remboursement** — tous via le ledger verrouillé et audités.
- Journal d'audit consultable (finance / administrateurs, filtré par périmètre).

**API REST** `/api/v1` : 83 endpoints, OpenAPI, ProblemDetails avec `code` stable, authentification Entra ID (JWT) pour
les utilisateurs et **jetons caisse** (clé d'appareil → JWT 15 min, limitation 20 req/min/IP), sélection du schéma
par émetteur.

**Back-office Blazor Server** (français, mise en page prête pour le RTL) : 13 écrans — accueil, sociétés & sites,
points de vente & caisses, opérateurs, articles (photos), catégories, tarifs, menus (vue semaine), clients & contrats &
subventions, convives & badges & import, comptes & opérations, journal d'audit, droits d'accès. Connexion Entra ID
(OpenID Connect) ou connexion de développement (refusée en Production). Chaque opération utilise un `DbContext` neuf.

### Vérifications effectuées

| Vérification | Résultat |
|---|---|
| Build complet Release, avertissements = erreurs, `dotnet format` | ✅ |
| Modèle EF ↔ migrations (nouvelle migration `AddUserAccessScopesAndDeviceKeys`) | ✅ |
| Tests unitaires Domain | ✅ 103/103 |
| Tests d'intégration SQL Server | ✅ 13/13 |
| Tests API (WebApplicationFactory + SQL Server 2022) | ✅ 24/24 — jetons absents/forgés/expirés, utilisateur sans rôle, clé caisse (échange, rotation, révocation, autre caisse), limitation de débit, cloisonnement site/société, rôles en écriture, organisation, opérateurs (PIN jamais audité), catalogue + audit des prix, photos, tarifs, menus (copie jour/semaine/écrasement), contrats, subventions, badges perdus, import CSV (dont champs entre guillemets) et Excel, recharge idempotente, conflit de clé, corrections/contre-passations/remboursements |
| Couverture lignes | ✅ Domain 90,3 %, Application 94,1 % (seuils CI 80 % chacun) |
| Parcours navigateur réel (Playwright, `tests/e2e`) sur SQL Server + back-office lancé | ✅ 24/24 : toutes les pages, clé de caisse, recharge, création de menu et ajout d'article, modification de prix, badge perdu, upload de photo, import CSV avec erreur de ligne, responsable de site limité à son site, refus de l'audit |
| Connexion Entra ID réelle | ⚠️ non testée (aucun tenant disponible) : configuration documentée dans `docs/runbook.md`, à valider avec la DSI |

Bugs trouvés et corrigés pendant la vérification : message de confirmation effacé après rechargement, page « accès
refusé » manquante (404), prix des menus non affichés (virgule décimale dans un champ numérique), type MIME des photos,
modification partielle possible sur une ligne d'import rejetée, dépendance du migrateur aux services applicatifs.

### Décisions prises
Voir `docs/assumptions.md` A31–A48. Points saillants :
- Rôles dans Entra ID, **périmètres dans l'application** (administrables sans DSI) ; clients/convives/comptes cloisonnés par société.
- Le back-office appelle directement les cas d'usage (même processus) au lieu de l'API HTTP : une seule implémentation
  des droits et de l'audit, pas de double authentification.
- Une consommation ne se contre-passe pas : elle s'annule par un avoir (phase 3).

### Reste à faire / points ouverts
- Valider la connexion Entra ID sur le tenant Newrest (inscription d'application, rôles d'application).
- Écrans tickets / avoirs / clôtures / vérification de chaîne, facturation mensuelle et tableau de bord : ils
  dépendent des tickets synchronisés par les caisses → livrés avec les phases 3 et suivantes.
- Synchronisation descendante pour les caisses (catalogue, menus publiés, badges, soldes) : phase 3.
- Stockage des photos sur blob storage, si retenu par la DSI : nouvelle implémentation de `IFileStorage`.

## Phase 3 — Caisse WPF (à venir)
Session, vente manuelle, paiements, badge, subvention, impression (simulateur), clôture Z, SQLite + outbox + sync,
mode hors ligne, endpoints de synchronisation caisse.
